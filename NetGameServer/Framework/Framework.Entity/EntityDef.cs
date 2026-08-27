using System.Buffers.Binary;

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
/// 实体属性描述：名称 + 类型。
/// 由 EntityDef 持有，驱动属性的二进制编解码（对齐 KBE PropertyDescription）。
/// </summary>
public sealed class EntityProperty
{
    public required string Name { get; init; }
    public required EntityPropertyType Type { get; init; }

    /// <summary>该属性是否参与脏标记增量同步（对标 KBE 的 client 可见属性）。</summary>
    public bool SyncToClient { get; init; } = true;

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
        properties[property.Name] = property;
        return this;
    }

    public EntityDef Add(string name, EntityPropertyType type, bool syncToClient = true)
        => Add(new EntityProperty { Name = name, Type = type, SyncToClient = syncToClient });

    public bool TryGetProperty(string name, out EntityProperty property) => properties.TryGetValue(name, out property!);

    /// <summary>创建该 Def 的一个实体实例。</summary>
    public Entity CreateEntity(long entityId) => new(this, entityId);
}
