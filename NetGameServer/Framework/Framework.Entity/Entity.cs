using Framework.Core;

namespace Framework.Entity;

/// <summary>
/// 实体基类（对标 KBE Entity）：属性存储 + 脏标记。
/// 游戏逻辑通过 Set/Get 访问属性；被修改且 SyncToClient 的属性会进入脏集合，
/// 由 Witness（同步器）在下一同步周期只发送变更部分。
/// 设计要点：Entity 本身不持有锁——单线程 tick 内访问（对标 KBE 主循环串行模型）。
/// </summary>
public sealed class Entity
{
    private readonly EntityDef def;
    private readonly Dictionary<string, object?> values;
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityMethodHandler> methods = new(StringComparer.Ordinal);

    // 持久化脏标记（对标 GeekServer State 透明持久化）：
    // 任何属性实际变更（Entity.Set 值变化）都会置位，由 EntityPersistenceService 周期批量落库后 MarkPersisted 清除。
    // 与 dirty（客户端增量同步）分离——持久化关心"所有服务端属性变化"，dirty 只关心 SyncToClient 属性。
    private bool persistDirty;

    /// <summary>实体唯一 ID（场景内/节点内）。</summary>
    public long EntityId { get; }

    /// <summary>实体类型定义。</summary>
    public EntityDef Def => def;

    /// <summary>实体类型名。</summary>
    public string TypeName => def.Name;

    /// <summary>当前是否有未同步的脏属性。</summary>
    public bool IsDirty => dirty.Count > 0;

    /// <summary>是否有未落库的属性变更（持久化周期批量落库用，对标 GeekServer 脏状态自动保存）。</summary>
    public bool IsPersistDirty => persistDirty;

    /// <summary>清除持久化脏标记（批量落库完成快照后调用；随后发生的属性变更会重新置位）。</summary>
    public void MarkPersisted()
    {
        lock (dirty)
        {
            persistDirty = false;
        }
    }

    /// <summary>
    /// 重新置位持久化脏标记（P3 加固：批量落库/异步落库写入失败后调用）。
    /// 保证"已快照但写入失败"的变更不会被静默丢弃——下个落库周期会重试。
    /// </summary>
    public void ForcePersistDirty()
    {
        lock (dirty)
        {
            persistDirty = true;
        }
    }

    /// <summary>
    /// 所属客户端会话 ID（0 = 无属主）。
    /// 用于 OWN_CLIENT 权限属性的定向广播与归属判定（对标 KBE 实体属主）。
    /// </summary>
    public long OwnerClientId { get; set; }

    private EntityMailbox? mailbox;

    /// <summary>
    /// 实体 Mailbox（对标 KBE entityMailbox / cellMailbox）：
    /// 脚本层入口，通过 <see cref="EntityMailbox.Call"/> / <see cref="EntityMailbox.CallAsync"/>
    /// 调用本实体方法（Local 同步）或远端实体方法（Remote 异步回执）。
    /// 由 <see cref="EntityManager.AddOrUpdateEntity"/> 在注册时挂载 Local Mailbox；宿主可显式
    /// <see cref="AttachMailbox"/> 替换为 Remote Mailbox（迁移后源节点视角）。
    /// 未挂载时访问会抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public EntityMailbox Mailbox => mailbox ?? throw new InvalidOperationException(
        $"Entity[{TypeName}:{EntityId}] Mailbox 未挂载（须经 EntityManager.AddOrUpdateEntity 注册）");

    /// <summary>
    /// 显式挂载 Mailbox（迁移/跨节点场景使用）。
    /// 通常宿主在 <see cref="EntityManager.AddOrUpdateEntity"/> 时通过 <see cref="AttachMailboxIfAbsent"/>
    /// 自动挂 Local Mailbox；仅在需要替换为 Remote Mailbox 时显式调用 <see cref="AttachMailbox"/>。
    /// </summary>
    public void AttachMailbox(EntityMailbox newMailbox) => mailbox = newMailbox;

    /// <summary>
    /// 仅在 Mailbox 未挂载时挂载（供 <see cref="EntityManager.AddOrUpdateEntity"/> 自动挂 Local 用）。
    /// 不会覆盖宿主已显式挂的 Remote Mailbox。
    /// </summary>
    public void AttachMailboxIfAbsent(EntityMailbox newMailbox)
    {
        if (mailbox == null)
        {
            mailbox = newMailbox;
        }
    }

    /// <summary>
    /// 属性变更事件（Entity.Set 触发；SetSilent 不触发）。
    /// 参数：属性名、旧值、新值。供脚本层事件总线（OnPropertyChanged）消费，替代轮询。
    /// </summary>
    public event Action<string, object?, object?>? PropertyChanged;

    internal Entity(EntityDef def, long entityId)
    {
        this.def = def;
        EntityId = entityId;
        values = new Dictionary<string, object?>(def.Properties.Count, StringComparer.Ordinal);
        foreach (var (name, prop) in def.Properties)
        {
            values[name] = DefaultValue(prop.Type);
        }
    }

    /// <summary>
    /// 读取属性。实体构造时所有属性都有默认值，因此不会返回 null
    /// （引用类型可能为 null，调用方自行处理）。
    /// </summary>
    public T Get<T>(string name)
    {
        if (values.TryGetValue(name, out var v) && v is T typed)
        {
            return typed;
        }
        return default!;
    }

    /// <summary>
    /// 写入属性并标记脏（若值有变化且属性 SyncToClient）。
    /// 属性未在 Def 中声明时记录警告并忽略（防拼写错误）。
    /// V15 修复：values 写入与脏标记放入同一把锁（dirty 同时充当 values 写互斥），
    /// 消除"dirty 加锁而 values 裸写"的不一致；单线程 tick 下无锁竞争开销。
    /// </summary>
    public void Set<T>(string name, T value)
    {
        if (!def.TryGetProperty(name, out var prop))
        {
            Log.Warn($"Entity[{TypeName}:{EntityId}] 尝试设置未声明的属性 {name}，已忽略。");
            return;
        }

        lock (dirty)
        {
            if (values.TryGetValue(name, out var old) && Equals(old, value))
            {
                return; // 值未变化，不标记脏
            }

            values[name] = value;
            if (prop.SyncToClient)
            {
                dirty.Add(name);
            }

            // 持久化脏标记：任何属性变更都需周期落库（与客户端增量 dirty 分离）。
            persistDirty = true;

            // 属性变更事件（脚本层 OnPropertyChanged 回调，对标 KBE onPropertyChange）
            PropertyChanged?.Invoke(name, old, value);
        }
    }

    /// <summary>
    /// 写入属性但不标记脏（用于全量快照初始化、服务端权威回写等不应再次广播的场景）。
    /// V15 修复：与 Set 使用同一把 values 写互斥锁，保证与 CopyValues/TakeDirty 的一致性。
    /// </summary>
    public void SetSilent<T>(string name, T value)
    {
        if (!def.TryGetProperty(name, out _))
        {
            return;
        }
        lock (dirty)
        {
            values[name] = value;
        }
    }

    /// <summary>
    /// 注册实体方法（对标 KBE 脚本实体方法）：供本进程直接调用或跨进程 EntityCall 远程调用。
    /// </summary>
    public void RegisterMethod(string methodName, EntityMethodHandler handler)
    {
        methods[methodName] = handler;
    }

    /// <summary>是否存在指定方法。</summary>
    public bool HasMethod(string methodName) => methods.ContainsKey(methodName);

    /// <summary>
    /// 调用本实体方法（参数已解码）。返回 (成功, 结果对象)。
    /// </summary>
    public (bool Success, object? Result) InvokeMethod(string methodName, object?[] args)
    {
        if (!methods.TryGetValue(methodName, out var handler))
        {
            Log.Warn($"Entity[{TypeName}:{EntityId}] 未注册方法 {methodName}，调用被忽略。");
            return (false, null);
        }

        try
        {
            return (true, handler(args));
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Entity[{TypeName}:{EntityId}] 方法 {methodName} 执行异常");
            return (false, null);
        }
    }

    /// <summary>
    /// 取出并清空脏属性集合（同步器调用）。返回属性名快照。
    /// </summary>
    public string[] TakeDirtyProperties()
    {
        lock (dirty)
        {
            if (dirty.Count == 0)
            {
                return Array.Empty<string>();
            }
            var snapshot = dirty.ToArray();
            dirty.Clear();
            return snapshot;
        }
    }

    /// <summary>
    /// 拷贝全部属性为独立字典（备份服务用，对标迭代 8 三-10 修正）：
    /// 在 values 写互斥锁下做快照，把序列化要读的数据脱离活实体，
    /// 后台队列线程再对快照做 UTF8 编码/写盘，既不阻塞主循环，也不与写线程竞争。
    /// V15 修复：对可变引用属性（List/数组）做深拷贝，防止后台读与 tick 写竞争同一实例。
    /// </summary>
    public Dictionary<string, object?> CopyValues()
    {
        lock (dirty)
        {
            var copy = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                copy[key] = DeepCopyForBackup(value);
            }
            return copy;
        }
    }

    /// <summary>备份快照深拷贝：仅深拷贝可变容器（List/数组），其余不可变值直接引用。</summary>
    private static object? DeepCopyForBackup(object? value)
    {
        return value switch
        {
            List<int> list => new List<int>(list),
            List<string> stringList => new List<string>(stringList),
            int[] intArray => (int[])intArray.Clone(),
            string[] stringArray => (string[])stringArray.Clone(),
            float[] floatArray => (float[])floatArray.Clone(),
            _ => value
        };
    }

    private static object? DefaultValue(EntityPropertyType type) => type switch
    {
        EntityPropertyType.Int32 => 0,
        EntityPropertyType.Int64 => 0L,
        EntityPropertyType.Float => 0f,
        EntityPropertyType.Double => 0d,
        EntityPropertyType.Bool => false,
        EntityPropertyType.String => string.Empty,
        EntityPropertyType.Float3 => new Float3(0, 0, 0),
        EntityPropertyType.Int32List => new List<int>(),
        _ => null
    };
}

/// <summary>3 分量浮点（Vector3 值语义）。</summary>
public readonly struct Float3 : IEquatable<Float3>
{
    public readonly float X, Y, Z;
    public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public bool Equals(Float3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Float3 f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}
