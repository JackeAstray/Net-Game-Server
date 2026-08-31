using EntityObj = Framework.Entity.Entity;

namespace Framework.Scripting;

/// <summary>
/// 游戏逻辑脚本接口（对标 KBE 的 Python 脚本实体）。
/// 游戏开发者实现此接口编写玩法逻辑，与底层框架物理分离：
/// - 底层（Network/Entity/Protocol）不再随游戏逻辑改动而重新编译
/// - 脚本通过 Entity 属性/方法 API 与框架交互
/// </summary>
public interface IEntityScript
{
    /// <summary>脚本绑定的实体类型名（与 EntityDef.Name 对应）。</summary>
    string EntityType { get; }

    /// <summary>实体创建（进入场景/初始化）时调用。</summary>
    void OnCreate(EntityObj entity);

    /// <summary>实体销毁时调用。</summary>
    void OnDestroy(EntityObj entity);

    /// <summary>每 tick 调用（由 TickEngine 驱动）。</summary>
    void OnTick(EntityObj entity, long frame);

    /// <summary>收到客户端消息时调用（method 为消息名，args 为解码参数）。</summary>
    void OnMessage(EntityObj entity, string method, object?[] args);

    /// <summary>
    /// 实体属性变更时调用（Entity.Set 触发；SetSilent 不触发）。
    /// 事件驱动替代轮询（对标 KBE 属性 change 回调链 / onPropertyChange）。
    /// </summary>
    void OnPropertyChanged(EntityObj entity, string name, object? oldValue, object? newValue) { }

    /// <summary>
    /// 全局共享数据变更时调用（ScriptHost.SetGlobal 触发）。
    /// 对标 KBE KBEngine.globalData 的订阅式协作，替代 tick 轮询。
    /// 注意：脚本实例按实体类型共享，本回调会对该类型下的每个实体各调用一次。
    /// </summary>
    void OnGlobalChanged(EntityObj entity, string key, object? value) { }

    /// <summary>
    /// 脚本热更新后调用（KBE-Gap-Review S4：显式状态迁移钩子）。
    /// 旧实例 state 由 ScriptHost 透传（持久化过的数据），脚本可在此把
    /// 已变更的内部字段重新同步到实体属性，避免热更新后状态漂移。
    /// 返回值会被 <see cref="ScriptHost"/> 用作下次热更新透传的 state。
    /// </summary>
    void OnReload(EntityObj entity, object? oldState) { }

    /// <summary>
    /// 脚本版本号（KBE-Gap-Review S4）：任何状态相关变更都应 bump 这个号，
    /// 框架把它与 entity 属性一起做版本判定，热更新有破坏性变更时便于诊断。
    /// </summary>
    int ScriptVersion => 1;
}

/// <summary>
/// 游戏逻辑脚本基类（方便脚本只覆写需要的回调）。
/// 注意：基类只暴露薄包装的 <see cref="Log"/>，避免脚本与底层日志实现耦合。
/// </summary>
public abstract class EntityScriptBase : IEntityScript
{
    public abstract string EntityType { get; }

    public virtual void OnCreate(EntityObj entity) { }
    public virtual void OnDestroy(EntityObj entity) { }
    public virtual void OnTick(EntityObj entity, long frame) { }
    public virtual void OnMessage(EntityObj entity, string method, object?[] args) { }
    public virtual void OnPropertyChanged(EntityObj entity, string name, object? oldValue, object? newValue) { }
    public virtual void OnGlobalChanged(EntityObj entity, string key, object? value) { }
    public virtual void OnReload(EntityObj entity, object? oldState) { }
    public virtual int ScriptVersion => 1;

    /// <summary>
    /// 脚本层结构化日志门面（KBE-Gap-Review S1）。
    /// 用法：Log.Info("Script", "Avatar {Id} created", entity.EntityId);
    /// 默认实现转发到 <see cref="Framework.Core.Log"/>（Serilog），按 LogLevel 过滤，
    /// 进程统一配置级别；脚本层不再使用 Console.WriteLine。
    /// </summary>
    protected ScriptLogger Log => ScriptLogger.Instance;

    /// <summary>
    /// 脚本层定时器接入（KBE-Gap-Review S2：回血改框架定时器）。
    /// 用法：AddTimer(entity, 1000, () => DoHeal(), repeat: true)；
    /// 返回 <see cref="Framework.Tick.TimerHandle"/>，可用 handle.Cancel() 取消（典型如 entity 销毁前）。
    /// 底层未注入 TickEngine 时返回 null。public 以让 csx 脚本访问。
    /// 句柄同时登记到实例级清单，热更新时由框架统一取消旧实例定时器（P1：防新旧定时器叠加/旧实例泄漏）。
    /// </summary>
    public Framework.Tick.TimerHandle? AddTimer(EntityObj entity, int intervalMs, Action callback, bool repeat = false)
    {
        var engine = ScriptHost.Current?.TickEngine;
        if (engine == null) return null;
        var handle = engine.AddTimer(intervalMs, WrapWithEntity(entity, callback), repeat);
        lock (_timers)
        {
            _timers.Add(handle);
        }
        return handle;
    }

    private readonly object _timersGate = new();
    private readonly System.Collections.Generic.List<Framework.Tick.TimerHandle> _timers = new();

    /// <summary>取消本实例创建的全部定时器（热更新迁移时由 ScriptHost 调用，供跨实例定时器收尾）。</summary>
    internal void CancelAllTimers()
    {
        lock (_timersGate)
        {
            foreach (var handle in _timers)
            {
                handle.Cancel();
            }
            _timers.Clear();
        }
    }

    private static Action WrapWithEntity(EntityObj entity, Action callback)
    {
        // 简单的实体上下文包装：保留 entity 参数便于 caller 使用；callback 内自己处理空指针。
        // 注意：脚本层常见模式是脚本 OnDestroy 时遍历自己的 timer 句柄统一 Cancel；
        // 也可由 Battle 节点在 NotifyDestroy 之前清理（按 entity 持有的句柄表）。
        return () =>
        {
            try { callback(); }
            catch (Exception ex) { Framework.Core.Log.Error(ex, "脚本定时器回调异常 EntityId:" + entity.EntityId); }
        };
    }

    /// <summary>
    /// 脚本层边界钳制（KBE-Gap-Review S3：防负值/溢出/超上限）。
    /// 用法：MathClampSet(entity, "Hp", newHp, 0, maxHp)。
    /// 钳制后属性值落在 [min, max] 内。返回是否实际发生改变。public 让 csx 跨程序集访问。
    /// </summary>
    public static bool MathClampSet(EntityObj entity, string name, int value, int min, int max)
    {
        int clamped = value < min ? min : (value > max ? max : value);
        int old = entity.Get<int>(name);
        if (old == clamped) return false;
        entity.Set(name, clamped);
        return true;
    }

    /// <summary>
    /// 脚本层安全加减（KBE-Gap-Review S3）：在 [min, max] 内累加（典型如扣血/回血）。
    /// 返回新值。public 让 csx 脚本跨程序集访问（Roslyn 脚本对 protected 跨 assembly 限制严格）。
    /// </summary>
    public static int MathClampAdd(EntityObj entity, string name, int delta, int min, int max)
    {
        // P2 修复：内部用 long 累加，防止 Get<int> + delta 整数溢出回绕（恶意大 delta 可导致结果跳变）。
        long newValue = (long)entity.Get<int>(name) + delta;
        if (newValue < min) newValue = min;
        else if (newValue > max) newValue = max;
        entity.Set(name, (int)newValue);
        return (int)newValue;
    }
}

/// <summary>
/// 脚本层结构化日志门面（KBE-Gap-Review S1，对标 KBE 脚本层 KBEngine.INFO/DEBUG 接口）。
/// 设计要点：
/// - 单例（无状态），不依赖 ScriptHost，便于脚本静态访问；
/// - 模板形式（{Field} 占位）避免字符串插值的固定开销；
/// - 命中级别被禁用时由 <see cref="Framework.Core.Log"/> 直接 short-circuit，
///   模板和参数都不会被求值；
/// - 支持按 Tag 维度过滤（进程级配置），便于单玩法类别静音。
/// </summary>
public sealed class ScriptLogger
{
    /// <summary>单例（脚本中可安全 Log.Info 调用）。</summary>
    public static readonly ScriptLogger Instance = new();

    private ScriptLogger() { }

    /// <summary>进程级 tag 过滤（空集合表示不过滤）。可在 Program.Main 启动时配置。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _enabledTags
        = new(System.StringComparer.Ordinal);

    /// <summary>启用/禁用指定 tag 的日志（默认全部启用；调用 DisabledTag 可静音某玩法脚本）。</summary>
    public static void EnableTag(string tag) => _enabledTags[tag] = 0;
    public static void DisableTag(string tag) => _enabledTags.TryRemove(tag, out _);
    public static void ClearTagFilter() => _enabledTags.Clear();

    public void Trace(string tag, string template, params object?[] values)
        => Emit("TRACE", tag, template, values);
    public void Debug(string tag, string template, params object?[] values)
        => Emit("DEBUG", tag, template, values);
    public void Info(string tag, string template, params object?[] values)
        => Emit("INFO", tag, template, values);
    public void Warn(string tag, string template, params object?[] values)
        => Emit("WARN", tag, template, values);
    public void Error(string tag, string template, params object?[] values)
        => Emit("ERROR", tag, template, values);
    public void Error(string tag, System.Exception ex, string template, params object?[] values)
        => Framework.Core.Log.Error(ex, "[script:{Tag}] " + template, MergeTag(tag, values));

    private static void Emit(string level, string tag, string template, object?[] values)
    {
        // 进程级 tag 过滤：未启用则丢弃
        if (_enabledTags.Count > 0 && !_enabledTags.ContainsKey(tag))
        {
            return;
        }
        var prefixed = "[script:{Tag}] " + template;
        var merged = MergeTag(tag, values);
        switch (level)
        {
            case "TRACE": Framework.Core.Log.Trace(prefixed, merged); break;
            case "DEBUG": Framework.Core.Log.Debug(prefixed, merged); break;
            case "INFO":  Framework.Core.Log.Info(prefixed, merged);  break;
            case "WARN":  Framework.Core.Log.Warn(prefixed, merged);  break;
            case "ERROR": Framework.Core.Log.Error(prefixed, merged); break;
        }
    }

    private static object?[] MergeTag(string tag, object?[] values)
    {
        if (values == null || values.Length == 0)
        {
            return new object?[] { tag };
        }
        var merged = new object?[values.Length + 1];
        merged[0] = tag;
        Array.Copy(values, 0, merged, 1, values.Length);
        return merged;
    }
}
