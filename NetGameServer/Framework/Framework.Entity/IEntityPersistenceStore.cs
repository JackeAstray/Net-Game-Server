namespace Framework.Entity;

/// <summary>
/// 已持久化的实体属性块（实体 ID + PropertyCodec 序列化后的属性字节）。
/// 存储实现只搬运"属性字节块"，不理解业务字段——序列化/反序列化由
/// <see cref="EntityPersistenceService"/> / <see cref="PropertyCodec"/> 负责。
/// </summary>
public readonly record struct StoredEntity(long EntityId, byte[] Props);

/// <summary>
/// 实体持久化存储抽象（对标 KBE dbmgr 的可插拔后端：文件 / MySQL / PostgreSQL / Redis）。
/// 职责边界：
/// - 只负责按 (entityType, entityId) 读写/删除"属性字节块"，介质无关；
/// - 实现必须线程安全（<see cref="EntityPersistenceService"/> 会在后台线程批量写入）；
/// - 实现应当具备崩溃安全语义（写入原子或可恢复），文件实现用"临时文件 + rename"。
/// 各后端实现见：
/// - 文件：<see cref="FileEntityPersistenceStore"/>（Framework.Entity，零依赖，默认/测试用）
/// - SQL/Redis：Framework.Persistence（MySql/PostgreSql/Redis 官方驱动）
/// </summary>
public interface IEntityPersistenceStore : IDisposable
{
    /// <summary>存储后端名称（日志/统计用），如 "File" / "MySql" / "PostgreSql" / "Redis"。</summary>
    string Name { get; }

    /// <summary>保存（覆盖）单条实体属性块。</summary>
    void Save(string entityType, long entityId, byte[] serializedProps);

    /// <summary>按 ID 加载单条实体属性块；不存在返回 null。</summary>
    byte[]? TryLoad(string entityType, long entityId);

    /// <summary>删除单条实体属性块（不存在时静默成功）。</summary>
    void Delete(string entityType, long entityId);

    /// <summary>枚举某类型的全部实体属性块（崩溃恢复用，对标 KBE restore_entity_handler）。</summary>
    IEnumerable<StoredEntity> LoadAll(string entityType);

    /// <summary>某类型的实体数。</summary>
    int Count(string entityType);
}
