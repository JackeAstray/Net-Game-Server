using Framework.Entity;

namespace Battle.Entities;

/// <summary>
/// 玩家实体定义（对标 KBE entity_defs 中的 Avatar.def）。
/// 属性声明在此集中管理，驱动属性增量同步（Witness 式）。
/// </summary>
public static class PlayerEntityDef
{
    private static readonly EntityDef Instance = new EntityDef { Name = "Player" }
        .Add("Nickname", EntityPropertyType.String)
        .Add("Position", EntityPropertyType.Float3)
        .Add("Rotation", EntityPropertyType.Float3)
        .Add("Hp", EntityPropertyType.Int32)
        .Add("MaxHp", EntityPropertyType.Int32)
        .Add("Score", EntityPropertyType.Int32)
        // 背包/装备为玩家私有：仅广播给属主客户端（对标 KBE OWN_CLIENT）
        .Add("Equipment", EntityPropertyType.Int32List, syncToClient: true, scope: EntitySyncScope.OwnClient);

    /// <summary>获取玩家实体定义（单例）。</summary>
    public static EntityDef Def => Instance;

    /// <summary>创建玩家实体。</summary>
    public static Framework.Entity.Entity Create(long entityId)
    {
        var entity = Instance.CreateEntity(entityId);
        entity.Set("Nickname", $"Player_{entityId % 1000}");
        entity.Set("Hp", 100);
        entity.Set("MaxHp", 100);
        entity.Set("Score", 0);
        entity.Set("Position", new Float3(0, 0, 0));
        entity.Set("Rotation", new Float3(0, 0, 0));
        entity.Set("Equipment", new List<int>());
        return entity;
    }
}
