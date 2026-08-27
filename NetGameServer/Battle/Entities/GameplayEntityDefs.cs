using Framework.Entity;

namespace Battle.Entities;

/// <summary>
/// 玩法实体定义（对标 KBE entity_defs 的 Npc.def / Quest.def / Skill.def / Item.def）。
/// 与 GameLogic/scripts/*.csx 一一对应：脚本按 EntityType 绑定实体，属性由 Def 声明驱动
/// 脏标记增量同步。属性初始值由脚本 OnCreate 设置（本类只创建骨架）。
/// 同步权限演示：Quest 内部状态不广播（CELL_PRIVATE）、Skill 冷却与 Item 背包仅属主可见（OWN_CLIENT）。
/// </summary>
public static class GameplayEntityDefs
{
    /// <summary>野怪：Hp/MaxHp/Score 公开，Position 公开（巡逻移动广播给视野内玩家）。</summary>
    public static readonly EntityDef Npc = new EntityDef { Name = "Npc" }
        .Add("Hp", EntityPropertyType.Int32)
        .Add("MaxHp", EntityPropertyType.Int32)
        .Add("Score", EntityPropertyType.Int32)
        .Add("Position", EntityPropertyType.Float3);

    /// <summary>任务：内部状态（进度/目标）不参与客户端广播（对标 KBE CELL_PRIVATE）。</summary>
    public static readonly EntityDef Quest = new EntityDef { Name = "Quest" }
        .Add("Hp", EntityPropertyType.Int32, syncToClient: false)
        .Add("MaxHp", EntityPropertyType.Int32, syncToClient: false)
        .Add("Score", EntityPropertyType.Int32, syncToClient: false);

    /// <summary>技能：等级公开；冷却为属主私有（OWN_CLIENT）；释放次数服务端内部（不广播）。</summary>
    public static readonly EntityDef Skill = new EntityDef { Name = "Skill" }
        .Add("Level", EntityPropertyType.Int32)
        .Add("CooldownRemaining", EntityPropertyType.Int32, syncToClient: true, scope: EntitySyncScope.OwnClient)
        .Add("Casts", EntityPropertyType.Int32, syncToClient: false);

    /// <summary>物品：背包内容为属主私有（OWN_CLIENT）。</summary>
    public static readonly EntityDef Item = new EntityDef { Name = "Item" }
        .Add("ItemId", EntityPropertyType.Int32, syncToClient: true, scope: EntitySyncScope.OwnClient)
        .Add("Count", EntityPropertyType.Int32, syncToClient: true, scope: EntitySyncScope.OwnClient);

    /// <summary>创建玩法实体骨架（属性默认值），初始值由对应脚本 OnCreate 设置。</summary>
    public static Entity Create(string typeName, long entityId) => typeName switch
    {
        "Npc" => Npc.CreateEntity(entityId),
        "Quest" => Quest.CreateEntity(entityId),
        "Skill" => Skill.CreateEntity(entityId),
        "Item" => Item.CreateEntity(entityId),
        _ => throw new ArgumentException($"未知玩法实体类型: {typeName}")
    };
}
