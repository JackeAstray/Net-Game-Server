using System.Buffers.Binary;
using System.Text;

namespace Framework.Entity;

/// <summary>实体属性类型（对标 KBE PropertyType）</summary>
public enum EntityPropertyType : byte
{
    Int32 = 1,
    Int64 = 2,
    Float = 3,
    Double = 4,
    Bool = 5,
    String = 6,
    Float3 = 7,   // Vector3（3 个 float）
    Int32List = 8,
}

/// <summary>
/// 属性同步权限分级（对标 KBE Witness 的 ALL_CLIENTS / OWN_CLIENT / CELL_PUBLIC / CELL_PRIVATE）：
/// 决定脏属性增量广播时哪些客户端可见，避免隐私属性（冷却、背包、任务内部状态）泄露给无关玩家。
/// </summary>
public enum EntitySyncScope : byte
{
    /// <summary>广播给所有视野内客户端（默认）。</summary>
    AllClients = 0,

    /// <summary>仅广播给实体属主客户端（Entity.OwnerClientId）。</summary>
    OwnClient = 1,

    /// <summary>同空间（cell）内所有客户端可见（与 AllClients 同为公开，预留区分语义）。</summary>
    CellPublic = 2,

    /// <summary>仅服务端内部使用，不参与客户端广播（等价 SyncToClient=false）。</summary>
    CellPrivate = 3,
}

/// <summary>
/// 实体属性描述：名称 + 类型 + 同步权限。
/// 由 EntityDef 持有，驱动属性的二进制编解码（对齐 KBE PropertyDescription）。
/// </summary>
public sealed class EntityProperty
{
    private byte[]? utf8Name;

    public required string Name { get; init; }
    public required EntityPropertyType Type { get; init; }

    /// <summary>该属性是否参与脏标记增量同步（对标 KBE 的 client 可见属性）。</summary>
    public bool SyncToClient { get; init; } = true;

    /// <summary>同步权限分级（默认 AllClients）。</summary>
    public EntitySyncScope SyncScope { get; init; } = EntitySyncScope.AllClients;

    /// <summary>
    /// 属性名的 UTF8 字节（懒加载缓存，对标迭代 8 三-8 修正）。
    /// 序列化热路径每属性一次编码改为一次缓存，避免每包同步都 Encoding.UTF8.GetBytes(prop.Name)。
    /// </summary>
    public byte[] Utf8Name => utf8Name ??= Encoding.UTF8.GetBytes(Name);

    public override string ToString() => $"{Name}:{Type}";
}

/// <summary>
/// 实体定义（对标 KBE ScriptDefModule）：
/// 一组属性描述 + 实体类型名。同一类型的实体共享一份 Def。
/// </summary>
public sealed class EntityDef
{
    public required string Name { get; init; }
    private readonly Dictionary<string, EntityProperty> properties = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, EntityProperty> Properties => properties;

    public EntityDef Add(EntityProperty property)
    {
        // P2-4 修复：CellPrivate 必须强制 SyncToClient=false。
        // 若声明者漏写 syncToClient:false，私有/内部状态会被 Entity.Set 标记脏并广播给其他客户端。
        if (property.SyncScope == EntitySyncScope.CellPrivate && property.SyncToClient)
        {
            property = new EntityProperty
            {
                Name = property.Name,
                Type = property.Type,
                SyncToClient = false,
                SyncScope = property.SyncScope
            };
        }
        properties[property.Name] = property;
        return this;
    }

    public EntityDef Add(string name, EntityPropertyType type, bool syncToClient = true)
        => Add(new EntityProperty { Name = name, Type = type, SyncToClient = syncToClient });

    public EntityDef Add(string name, EntityPropertyType type, bool syncToClient, EntitySyncScope scope)
        => Add(new EntityProperty { Name = name, Type = type, SyncToClient = syncToClient, SyncScope = scope });

    public bool TryGetProperty(string name, out EntityProperty property) => properties.TryGetValue(name, out property!);

    /// <summary>创建该 Def 的一个实体实例。</summary>
    public Entity CreateEntity(long entityId) => new(this, entityId);
}
