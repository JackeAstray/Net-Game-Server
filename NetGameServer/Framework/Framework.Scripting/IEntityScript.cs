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
}
