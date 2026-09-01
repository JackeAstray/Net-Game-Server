namespace Framework.Entity;

/// <summary>
/// 实体持久化服务（对标 KBE dbmgr entity_table 自动存取 + GeekServer State 透明持久化）：
/// - 用 EntityDef 属性声明驱动序列化（不需要手写 SQL/字段映射）
/// - 存储介质可插拔：<see cref="IEntityPersistenceStore"/>（文件默认 / MySQL / PostgreSQL / Redis）
/// - 支持按实体 ID 单条保存/加载（属性级），或全量快照保存/恢复（崩溃恢复）
/// - 批量落库（迭代 21+）：周期把"有属性变更的在线实体"成批异步写入存储（移出主循环），
///   对标 GeekServer 脏状态自动保存——不再只在下线时落库，在线期崩溃最多丢一个落库周期。
///
/// 线程约定：实体状态的快照必须在单线程 tick 内完成（<see cref="Entity.CopyValues"/> 脱离活实体），
/// 序列化与存储写入在后台任务执行，不与 tick 线程竞争。
/// </summary>
public sealed class EntityPersistenceService : IDisposable
{
    private readonly IEntityPersistenceStore store;
    private readonly Func<long, Entity>? entityFactory; // 按 ID 重建空实体骨架（恢复用）
    private readonly List<EntityManager> managers = new(); // 周期批量落库的实体来源（对标 EntityBackupService）
    private readonly long flushIntervalMs;
    private readonly int flushBatchSize;
    private readonly object sync = new();
    private long lastFlushAt;
    /// <summary>批量落库串行门闩（P3 加固：同一时刻只允许一个批量落库，防并发写重排导致旧快照覆盖新数据）。</summary>
    private readonly SemaphoreSlim flushGate = new(1, 1);
    /// <summary>关闭标记（P3 加固：Dispose 后不再启动新批量落库，防在途写入被中断丢失）。</summary>
    private volatile bool disposed;

    /// <summary>当前存储后端名称。</summary>
    public string StoreName => store.Name;

    /// <summary>最近一次批量落库快照的实体数（统计用）。</summary>
    public int LastFlushedCount { get; private set; }

    /// <summary>累计批量落库次数（统计用）。</summary>
    public long TotalFlushes { get; private set; }

    /// <summary>已挂载的实体管理器数量（统计用）。</summary>
    public int ManagerCount { get { lock (sync) { return managers.Count; } } }

    /// <summary>创建文件后端 + 默认批量落库参数（兼容旧调用方，见旧签名）。</summary>
    /// <param name="storageDir">持久化目录</param>
    /// <param name="entityFactory">按实体 ID 创建空实体骨架的回调（恢复时用；null 则无法加载单实体）</param>
    public EntityPersistenceService(string storageDir, Func<long, Entity>? entityFactory = null)
        : this(new FileEntityPersistenceStore(storageDir), entityFactory, flushIntervalMs: 5000, flushBatchSize: 256)
    {
    }

    /// <param name="store">持久化存储后端（可插拔）</param>
    /// <param name="entityFactory">按实体 ID 创建空实体骨架的回调（恢复时用；null 则无法加载单实体）</param>
    /// <param name="flushIntervalMs">批量落库最小间隔（毫秒）</param>
    /// <param name="flushBatchSize">单次批量落库最多快照的实体数（超出部分留给下轮，控制主循环耗时上界）</param>
    public EntityPersistenceService(
        IEntityPersistenceStore store,
        Func<long, Entity>? entityFactory = null,
        long flushIntervalMs = 5000,
        int flushBatchSize = 256)
    {
        this.store = store;
        this.entityFactory = entityFactory;
        this.flushIntervalMs = Math.Max(1, flushIntervalMs);
        this.flushBatchSize = Math.Max(1, flushBatchSize);
        lastFlushAt = Environment.TickCount64;
    }

    // ===== 批量落库（对标 GeekServer 脏状态自动保存）=====

    /// <summary>注册实体管理器（周期批量落库的实体来源；幂等）。</summary>
    public EntityPersistenceService AttachManager(EntityManager manager)
    {
        lock (sync)
        {
            if (!managers.Contains(manager))
            {
                managers.Add(manager);
            }
        }
        return this;
    }

    /// <summary>注销实体管理器（场景销毁时调用，防只增不减）。</summary>
    public EntityPersistenceService RemoveManager(EntityManager manager)
    {
        lock (sync)
        {
            managers.Remove(manager);
        }
        return this;
    }

    /// <summary>
    /// 每 tick 调用：到达落库间隔且有脏实体时，触发一次后台批量落库。
    /// 非阻塞——快照在调用线程（tick）完成，序列化 + 存储写入在后台任务执行。
    /// </summary>
    public void FlushDirtyIfDue()
    {
        if (disposed) return; // 关闭后不再启动新批量落库
        long now = Environment.TickCount64;
        if (now - lastFlushAt < flushIntervalMs)
        {
            return;
        }
        if (SnapshotDirtyCount() == 0)
        {
            lastFlushAt = now; // 无脏实体也推进时间，避免空转
            return;
        }
        lastFlushAt = now;
        _ = FlushDirtyCoreAsync();
    }

    /// <summary>立即触发一次批量落库（关服/手动调用；异步执行并返回 Task）。</summary>
    public Task FlushDirtyAsync() => FlushDirtyCoreAsync();

    private sealed record Snapshot(long EntityId, string EntityType, EntityDef Def, Dictionary<string, object?> Props, Entity? Source);

    /// <summary>统计当前待落库实体数（快照前轻量遍历）。</summary>
    private int SnapshotDirtyCount()
    {
        int count = 0;
        List<EntityManager> snapshotManagers;
        lock (sync) { snapshotManagers = new List<EntityManager>(managers); }
        foreach (var manager in snapshotManagers)
        {
            foreach (var entity in manager.GetAllEntities())
            {
                if (entity.IsPersistDirty)
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// 批量落库核心（P3 加固）：
    /// - 用串行门闩保证同一时刻只有一个批量落库，杜绝并发写重排（旧快照覆盖新数据）。
    /// - 快照在调用线程（tick）完成 + MarkPersisted（确认已快照）。
    /// - 写入失败时 ForcePersistDirty 重新置位脏标记，下个周期重试——不再"写入失败但脏标记已清"造成静默数据丢失。
    /// </summary>
    private async Task FlushDirtyCoreAsync()
    {
        await flushGate.WaitAsync();
        try
        {
            if (disposed) return;
            await FlushDirtyLockedCoreAsync();
        }
        finally
        {
            flushGate.Release();
        }
    }

    /// <summary>批量落库本体（假定已持有 flushGate；供 Dispose 复用，避免在持有门闩时死锁）。</summary>
    private async Task FlushDirtyLockedCoreAsync()
    {
        List<Snapshot> batch = new(flushBatchSize);
        List<EntityManager> snapshotManagers;
        lock (sync) { snapshotManagers = new List<EntityManager>(managers); }

        foreach (var manager in snapshotManagers)
        {
            foreach (var entity in manager.GetAllEntities())
            {
                if (!entity.IsPersistDirty)
                {
                    continue;
                }
                if (batch.Count >= flushBatchSize)
                {
                    break;
                }
                // 快照在调用线程完成（单线程约定），随后 MarkPersisted 防重复落库；
                // 快照携带 Source（实体引用），写入失败时 ForcePersistDirty 重试。
                batch.Add(new Snapshot(entity.EntityId, entity.TypeName, entity.Def, entity.CopyValues(), entity));
                entity.MarkPersisted();
            }
            if (batch.Count >= flushBatchSize)
            {
                break;
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        LastFlushedCount = batch.Count;
        TotalFlushes++;
        var storeRef = store;
        // 串行写入（已在 gate 内，保持提交顺序）。
        await Task.Run(() =>
        {
            foreach (var s in batch)
            {
                try
                {
                    // 与 SaveEntity 语义一致：只序列化 SyncToClient 属性（CELL_PRIVATE 为服务端瞬时状态，不落库）
                    byte[] props = PropertyCodec.SerializeAllValues(s.Props, s.Def, onlySyncToClient: true);
                    storeRef.Save(s.EntityType, s.EntityId, props);
                }
                catch (Exception ex)
                {
                    // P3 加固：写入失败重新置位脏标记，下个周期重试，避免变更静默丢失。
                    s.Source?.ForcePersistDirty();
                    Framework.Core.Log.Error(ex, $"实体批量落库失败 EntityId:{s.EntityId} Type:{s.EntityType}");
                }
            }
        });
    }

    // ===== 单条/全量存取（原有 API，语义保持不变）=====

    /// <summary>
    /// 保存单个实体全部属性（对标 KBE 实体落库）。立即写存储。
    /// </summary>
    public void SaveEntity(Entity entity)
    {
        byte[] props = PropertyCodec.SerializeAll(entity);
        store.Save(entity.TypeName, entity.EntityId, props);
        entity.MarkPersisted();
    }

    /// <summary>
    /// 异步保存单个实体（不阻塞调用线程）。
    /// 先在调用线程快照（脱离活实体），再于后台写入存储。
    /// P3 加固：写入失败时 ForcePersistDirty 重新置位脏标记，防静默数据丢失。
    /// </summary>
    public async Task SaveEntityAsync(Entity entity)
    {
        if (disposed) return;
        var snapshot = entity.CopyValues();
        var def = entity.Def;
        long entityId = entity.EntityId;
        string entityType = entity.TypeName;
        entity.MarkPersisted();
        var storeRef = store;
        try
        {
            await Task.Run(() =>
            {
                byte[] props = PropertyCodec.SerializeAllValues(snapshot, def);
                storeRef.Save(entityType, entityId, props);
            });
        }
        catch (Exception ex)
        {
            // P3 加固：写入失败重新置位脏标记，下个周期重试。
            entity.ForcePersistDirty();
            Framework.Core.Log.Error(ex, $"实体异步落库失败 EntityId:{entityId} Type:{entityType}");
        }
    }

    /// <summary>
    /// 加载单个实体属性到已重建的实体骨架。返回 true 表示加载成功。
    /// </summary>
    public bool LoadEntity(Entity entity)
    {
        byte[]? props = store.TryLoad(entity.TypeName, entity.EntityId);
        if (props == null)
        {
            return false;
        }
        PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
        return true;
    }

    /// <summary>
    /// 按 ID 重建并加载实体（需要 entityFactory）。
    /// </summary>
    public Entity? LoadEntityById(string entityType, long entityId)
    {
        if (entityFactory == null)
        {
            return null;
        }
        byte[]? props = store.TryLoad(entityType, entityId);
        if (props == null)
        {
            return null;
        }
        var entity = entityFactory(entityId);
        PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
        return entity;
    }

    /// <summary>删除单个实体持久化数据。</summary>
    public void DeleteEntity(string entityType, long entityId) => store.Delete(entityType, entityId);

    /// <summary>批量保存实体（自动按类型分目录）。</summary>
    public void SaveAll(IEnumerable<Entity> entities)
    {
        foreach (var entity in entities)
        {
            SaveEntity(entity);
        }
    }

    /// <summary>
    /// 全量恢复：扫描某类型的所有实体存储，用 entityFactory 重建并加载。
    /// 返回恢复的实体列表（对标 KBE restore_entity_handler）。
    /// </summary>
    public List<Entity> RestoreAll(string entityType)
    {
        var result = new List<Entity>();
        foreach (var stored in store.LoadAll(entityType))
        {
            if (entityFactory == null)
            {
                continue;
            }
            var entity = entityFactory(stored.EntityId);
            PropertyCodec.DeserializeInto(entity, stored.Props, applyDirty: false);
            result.Add(entity);
        }
        Framework.Core.Log.Info($"实体持久化恢复完成: {entityType} 共 {result.Count} 个（后端 {StoreName}）");
        return result;
    }

    /// <summary>统计某类型的持久化实体数。</summary>
    public int Count(string entityType) => store.Count(entityType);

    public void Dispose()
    {
        // P3 加固：置位关闭标记（此后 FlushDirtyIfDue/SaveEntityAsync 不再启动新写入），
        // 等待在途批量落库完成，再执行最终 flush，最后释放存储——防在途/剩余脏数据被丢弃。
        disposed = true;
        try
        {
            // 等待在途批量落库完成（获取 gate 即表示无在途写入）。
            if (!flushGate.Wait(TimeSpan.FromSeconds(10)))
            {
                Framework.Core.Log.Warning("实体持久化关服等待在途落库超时");
            }
            // 已持有 gate：执行最终 flush（FlushDirtyLockedCoreAsync 假定持有 gate，不会死锁）。
            // F4 修复：单次批量落库有上限（flushBatchSize），此前只 flush 一次——脏实体数超过上限时
            // 关服即静默丢数据。改为循环 flush 直到无脏实体（或达到安全轮次上限，防写入持续失败死循环）。
            const int MaxFinalFlushRounds = 1000;
            int finalFlushRounds = 0;
            while (finalFlushRounds < MaxFinalFlushRounds && SnapshotDirtyCount() > 0)
            {
                finalFlushRounds++;
                FlushDirtyLockedCoreAsync().GetAwaiter().GetResult();
            }
            if (SnapshotDirtyCount() > 0)
            {
                Framework.Core.Log.Warning($"实体持久化关服最终 flush 仍有 {SnapshotDirtyCount()} 个脏实体未落库（写入持续失败），请排查存储后端");
            }
        }
        catch (Exception ex)
        {
            Framework.Core.Log.Error(ex, "实体持久化关服 flush 失败");
        }
        finally
        {
            try { flushGate.Release(); } catch { /* 已释放则忽略 */ }
        }
        store.Dispose();
    }
}
