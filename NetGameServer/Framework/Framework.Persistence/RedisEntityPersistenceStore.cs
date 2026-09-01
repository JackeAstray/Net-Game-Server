using Framework.Entity;
using StackExchange.Redis;

namespace Framework.Persistence;

/// <summary>
/// Redis 实体持久化存储（StackExchange.Redis）。
/// 布局：Hash 键 "entity_persistence:{entityType}"，field = entityId，value = 属性字节（原始二进制）。
/// - LoadAll：单次 HGETALL 拉全量（崩溃恢复用）
/// - Count：HLEN O(1)
/// 注：Redis 非强持久介质（取决于 AOF/RDB 配置），适合缓存/会话恢复场景；
/// 强一致持久化请用 MySql / PostgreSql 后端。
/// </summary>
public sealed class RedisEntityPersistenceStore : IEntityPersistenceStore
{
    private readonly ConnectionMultiplexer redis;
    private readonly int database;

    public string Name => "Redis";

    public RedisEntityPersistenceStore(string connectionString, int database = 0)
    {
        this.redis = ConnectionMultiplexer.Connect(connectionString);
        this.database = database;
    }

    private static string Key(string entityType) => $"entity_persistence:{entityType}";

    private IDatabase Db => redis.GetDatabase(database);

    public void Save(string entityType, long entityId, byte[] serializedProps)
    {
        Db.HashSet(Key(entityType), entityId.ToString(), serializedProps);
    }

    public byte[]? TryLoad(string entityType, long entityId)
    {
        var value = Db.HashGet(Key(entityType), entityId.ToString());
        return value.IsNull ? null : (byte[])value!;
    }

    public void Delete(string entityType, long entityId)
    {
        Db.HashDelete(Key(entityType), entityId.ToString());
    }

    public IEnumerable<StoredEntity> LoadAll(string entityType)
    {
        HashEntry[] entries = Db.HashGetAll(Key(entityType));
        foreach (var entry in entries)
        {
            // 显式转 string，避免 RedisValue 隐式转换与 long.TryParse 重载产生二义性
            if (long.TryParse((string)entry.Name!, out long id))
            {
                yield return new StoredEntity(id, (byte[])entry.Value!);
            }
        }
    }

    public int Count(string entityType) => (int)Db.HashLength(Key(entityType));

    public void Dispose()
    {
        redis.Dispose();
    }
}
