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

    /// <summary>实体唯一 ID（场景内/节点内）。</summary>
    public long EntityId { get; }

    /// <summary>实体类型定义。</summary>
    public EntityDef Def => def;

    /// <summary>实体类型名。</summary>
    public string TypeName => def.Name;

    /// <summary>当前是否有未同步的脏属性。</summary>
    public bool IsDirty => dirty.Count > 0;

    /// <summary>
    /// 所属客户端会话 ID（0 = 无属主）。
    /// 用于 OWN_CLIENT 权限属性的定向广播与归属判定（对标 KBE 实体属主）。
    /// </summary>
    public long OwnerClientId { get; set; }

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
    /// </summary>
    public void Set<T>(string name, T value)
    {
        if (!def.TryGetProperty(name, out var prop))
        {
            Log.Warn($"Entity[{TypeName}:{EntityId}] 尝试设置未声明的属性 {name}，已忽略。");
            return;
        }

        if (values.TryGetValue(name, out var old) && Equals(old, value))
        {
            return; // 值未变化，不标记脏
        }

        values[name] = value;
        if (prop.SyncToClient)
        {
            lock (dirty)
            {
                dirty.Add(name);
            }
        }

        // 属性变更事件（脚本层 OnPropertyChanged 回调，对标 KBE onPropertyChange）
        PropertyChanged?.Invoke(name, old, value);
    }

    /// <summary>
    /// 写入属性但不标记脏（用于全量快照初始化、服务端权威回写等不应再次广播的场景）。
    /// </summary>
    public void SetSilent<T>(string name, T value)
    {
        if (!def.TryGetProperty(name, out _))
        {
            return;
        }
        values[name] = value;
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
