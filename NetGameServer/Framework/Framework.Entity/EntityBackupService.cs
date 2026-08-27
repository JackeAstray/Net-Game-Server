using System.Buffers.Binary;
using System.Text;
using Framework.Core;

namespace Framework.Entity;

/// <summary>
/// 实体备份服务（对标 KBE baseapp/backuper 的平滑分摊算法）：
/// - 每 tick 只备份 entitiesCount/periodInTicks 个实体，避免周期性 IO 尖峰
/// - 备份格式：[magic(4)][entityId(8)][props(PropertyCodec 全量块)]...
/// - 支持落盘（archiver 语义）与内存快照（恢复用）
/// 配合 OrderedTaskQueue 异步写盘，不阻塞主循环。
/// </summary>
public sealed class EntityBackupService : IDisposable
{
    private const uint BackupMagic = 0x424B5054; // "BKPT" -> Backup
    private const int HeaderSize = 16; // magic(4) + entityId(8) + propsLength(4)

    private readonly List<EntityManager> managers = new();
    private readonly OrderedTaskQueue taskQueue;
    private readonly string backupFilePath;
    private readonly int periodInTicks;
    private long tick;
    private float backupRemainder;
    private int cursor;

    /// <summary>最近一次备份的实体数（统计用）。</summary>
    public long LastBackedUpCount { get; private set; }

    /// <summary>最近一次备份耗时（毫秒，统计用）。</summary>
    public long LastBackupElapsedMs { get; private set; }

    /// <param name="backupFilePath">落盘路径（null 表示仅内存备份）</param>
    /// <param name="periodInTicks">完整备份一轮所需的 tick 数（对标 KBE backupPeriod）</param>
    public EntityBackupService(string? backupFilePath = null, int periodInTicks = 100)
    {
        this.backupFilePath = backupFilePath;
        this.periodInTicks = Math.Max(1, periodInTicks);
        taskQueue = new OrderedTaskQueue("EntityBackup");
    }

    /// <summary>注册实体管理器（可注册多个，统一备份）。</summary>
    public EntityBackupService AddManager(EntityManager manager)
    {
        managers.Add(manager);
        return this;
    }

    /// <summary>
    /// 每 tick 调用一次：按平滑分摊算法备份部分实体。
    /// </summary>
    public void Tick()
    {
        tick++;

        var entities = new List<Framework.Entity.Entity>();
        foreach (var manager in managers)
        {
            entities.AddRange(manager.GetAllEntities());
        }
        if (entities.Count == 0)
        {
            return;
        }

        // 对标 KBE backuper：numToBackup = entitiesCount / periodInTicks + backupRemainder
        float numToBackupFloat = (float)entities.Count / periodInTicks + backupRemainder;
        int numToBackup = (int)Math.Floor(numToBackupFloat);
        backupRemainder = numToBackupFloat - numToBackup;

        if (numToBackup <= 0)
        {
            return;
        }

        // 从当前游标位置取 numToBackup 个实体（轮转）
        var slice = new List<Framework.Entity.Entity>(numToBackup);
        for (int i = 0; i < numToBackup && cursor < entities.Count; i++)
        {
            slice.Add(entities[cursor++]);
        }
        if (cursor >= entities.Count)
        {
            cursor = 0; // 一轮完成，重置游标
        }

        byte[] snapshot = SerializeSnapshot(slice);
        LastBackedUpCount = slice.Count;

        if (backupFilePath == null)
        {
            return; // 仅内存模式：快照已生成（可扩展为内存环形缓冲）
        }

        // 异步落盘（对标 KBE archiver 写库：不阻塞主循环）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        taskQueue.Enqueue("backup-file", () =>
        {
            try
            {
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

    /// <summary>序列化一批实体为备份块（PropertyCodec 全量属性 + 长度前缀）。</summary>
    private static byte[] SerializeSnapshot(List<Framework.Entity.Entity> entities)
    {
        using var ms = new MemoryStream(256);
        foreach (var entity in entities)
        {
            byte[] props = PropertyCodec.SerializeAll(entity);
            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), BackupMagic);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(4, 8), entity.EntityId);
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
