using System.Buffers.Binary;
using Framework.Core;

namespace Framework.Entity;

/// <summary>
/// 实体备份服务（对标 KBE baseapp/backuper 的平滑分摊算法）：
/// - 每 tick 只备份 entitiesCount/periodInTicks 个实体，避免周期性 IO 尖峰
/// - 备份格式：[magic(4)][entityId(8)][props(PropertyCodec 全量块)]...
/// - 支持落盘（archiver 语义）与内存快照（恢复用）
///
/// 迭代 8（三-10 修正）：序列化与落盘全部移入 OrderedTaskQueue 后台线程，主循环只做
/// O(快照) 的浅拷贝脱离（Entity.CopyValues），不再阻塞主循环；全量实体列表按总数缓存，
/// 仅当实体数量变化时重建，避免每 tick O(总实体数) 的列表分配。
/// </summary>
public sealed class EntityBackupService : IDisposable
{
    private const uint BackupMagic = 0x424B5054; // "BKPT" -> Backup
    private const int HeaderSize = 16; // magic(4) + entityId(8) + propsLength(4)

    private readonly List<EntityManager> managers = new();
    private readonly OrderedTaskQueue taskQueue;
    private readonly string? backupFilePath;
    private readonly int periodInTicks;
    private long tick;
    private float backupRemainder;
    private int cursor;

    // 全量实体列表缓存：仅当实体总数变化时重建（避免每 tick 全量 List 分配）
    private readonly List<Entity> allEntities = new();
    private int cachedTotal = -1;

    /// <summary>最近一次备份的实体数（统计用）。</summary>
    public long LastBackedUpCount { get; private set; }

    /// <summary>最近一次备份耗时（毫秒，统计用）。</summary>
    public long LastBackupElapsedMs { get; private set; }

    /// <summary>已脱离活实体的备份快照（后台线程只读此快照，不与 tick 线程竞争）。</summary>
    private sealed class BackupEntitySnapshot
    {
        public required long EntityId;
        public required EntityDef Def;
        public required Dictionary<string, object?> Props;
    }

    /// <param name="backupFilePath">落盘路径（null 表示仅内存备份）</param>
    /// <param name="periodInTicks">完整备份一轮所需的 tick 数（对标 KBE backupPeriod）</param>
    public EntityBackupService(string? backupFilePath = null, int periodInTicks = 100)
    {
        this.backupFilePath = backupFilePath;
        this.periodInTicks = Math.Max(1, periodInTicks);
        taskQueue = new OrderedTaskQueue("EntityBackup");
    }

    /// <summary>
    /// 注册实体管理器（可注册多个，统一备份）。
    /// 幂等：同一管理器重复注册会被忽略，防止调用方在每 tick 循环内注册导致列表无限增长
    /// （曾存在 BattleServerApp.OnTick 每 tick 每场景调用本方法的泄漏；现在重复调用无副作用，
    /// 且新场景创建后仍会被自动纳入备份）。
    /// </summary>
    public EntityBackupService AddManager(EntityManager manager)
    {
        lock (managers)
        {
            if (!managers.Contains(manager))
            {
                managers.Add(manager);
            }
        }
        return this;
    }

    /// <summary>
    /// 注销实体管理器（场景销毁时调用，防 managers 只增不减导致备份文件无限增长）。
    /// 移除后重置实体总数缓存，下一轮 Tick 会重新计算实体集合。
    /// </summary>
    public EntityBackupService RemoveManager(EntityManager manager)
    {
        lock (managers)
        {
            if (managers.Remove(manager))
            {
                cachedTotal = -1;
                allEntities.Clear();
            }
        }
        return this;
    }

    /// <summary>
    /// 每 tick 调用一次：按平滑分摊算法备份部分实体。
    /// 主循环只做：总数计算（O(管理器数)）+ 需要时重建缓存列表 + O(快照) 浅拷贝脱离；
    /// 序列化（UTF8 编码）与文件写入在 OrderedTaskQueue 后台线程执行。
    /// </summary>
    public void Tick()
    {
        tick++;

        // 全量列表缓存：仅当实体总数变化时重建，避免每 tick O(总实体数) 分配
        int total = 0;
        foreach (var manager in managers)
        {
            total += manager.Count;
        }
        if (total != cachedTotal)
        {
            allEntities.Clear();
            foreach (var manager in managers)
            {
                foreach (var entity in manager.GetAllEntities())
                {
                    allEntities.Add(entity);
                }
            }
            cachedTotal = total;
            cursor = 0; // 实体集合变化后从头重新轮转
        }

        if (allEntities.Count == 0)
        {
            return;
        }

        // 对标 KBE backuper：numToBackup = entitiesCount / periodInTicks + backupRemainder
        float numToBackupFloat = (float)allEntities.Count / periodInTicks + backupRemainder;
        int numToBackup = (int)Math.Floor(numToBackupFloat);
        backupRemainder = numToBackupFloat - numToBackup;

        if (numToBackup <= 0)
        {
            return;
        }

        // 从当前游标位置取 numToBackup 个实体（轮转），并浅拷贝脱离活实体（O(快照)，主循环线程安全）
        var snapshots = new List<BackupEntitySnapshot>(numToBackup);
        for (int i = 0; i < numToBackup && cursor < allEntities.Count; i++)
        {
            var entity = allEntities[cursor++];
            snapshots.Add(new BackupEntitySnapshot
            {
                EntityId = entity.EntityId,
                Def = entity.Def,
                Props = entity.CopyValues()
            });
        }
        if (cursor >= allEntities.Count)
        {
            cursor = 0; // 一轮完成，重置游标
        }

        LastBackedUpCount = snapshots.Count;

        if (backupFilePath == null)
        {
            return; // 仅内存模式：快照已生成（可扩展为内存环形缓冲）
        }

        // 序列化 + 落盘均移入后台队列（对标 KBE archiver 写库：不阻塞主循环）
        taskQueue.Enqueue("backup-file", () =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] snapshot = SerializeSnapshot(snapshots);
                File.AppendAllBytes(backupFilePath, snapshot);
                sw.Stop();
                LastBackupElapsedMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "实体备份落盘失败");
            }
        });
    }

    /// <summary>把一批已脱离的备份快照序列化为备份块（PropertyCodec 全量属性 + 长度前缀）。</summary>
    private static byte[] SerializeSnapshot(List<BackupEntitySnapshot> snapshots)
    {
        using var ms = new MemoryStream(256);
        // CA2014 修复：stackalloc 提到循环外——循环内 stackalloc 会随迭代逐次压栈，量大时可能栈溢出。
        Span<byte> header = stackalloc byte[HeaderSize];
        foreach (var snapshot in snapshots)
        {
            byte[] props = PropertyCodec.SerializeAllValues(snapshot.Props, snapshot.Def);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), BackupMagic);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(4, 8), snapshot.EntityId);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), props.Length);
            ms.Write(header);
            ms.Write(props);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 从备份文件恢复实体到各注册管理器（对标 KBE restore_entity_handler）。
    /// 返回恢复的实体数。
    /// </summary>
    public int RestoreFromFile()
    {
        if (string.IsNullOrEmpty(backupFilePath) || !File.Exists(backupFilePath))
        {
            return 0;
        }

        byte[] data = File.ReadAllBytes(backupFilePath);
        int offset = 0;
        int restored = 0;

        while (offset + HeaderSize <= data.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (magic != BackupMagic)
            {
                break;
            }
            long entityId = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 4, 8));
            int propsLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 12, 4));
            offset += HeaderSize;

            if (propsLength < 0 || offset + propsLength > data.Length)
            {
                Log.Warn($"实体备份块长度非法，停止恢复 offset={offset} propsLength={propsLength}");
                break;
            }

            // 在已注册管理器中查找该实体（实体需已由业务层重建）
            foreach (var manager in managers)
            {
                var entity = manager.GetEntity(entityId);
                if (entity != null)
                {
                    PropertyCodec.DeserializeInto(entity, data.AsSpan(offset, propsLength), applyDirty: false);
                    restored++;
                    break;
                }
            }
            offset += propsLength;
        }

        Log.Info($"实体备份恢复完成，恢复实体数: {restored}");
        return restored;
    }

    public void Dispose()
    {
        taskQueue.SweepIdle(TimeSpan.Zero);
    }
}
