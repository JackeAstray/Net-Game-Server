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
}

/// <summary>
/// 游戏逻辑脚本基类（方便脚本只覆写需要的回调）。
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
}
