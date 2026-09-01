using Framework.Entity;

namespace Framework.Persistence;

/// <summary>实体持久化后端配置（由宿主节点从配置读取后构造）。</summary>
public sealed class EntityPersistenceOptions
{
    /// <summary>后端类型：File（默认）| MySql | PostgreSql | Redis。</summary>
    public string Provider { get; set; } = "File";

    /// <summary>File 后端存储目录。</summary>
    public string? Directory { get; set; }

    /// <summary>MySql/PostgreSql/Redis 连接字符串。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Redis 使用的逻辑数据库号。</summary>
    public int RedisDatabase { get; set; }

    /// <summary>批量落库最小间隔（毫秒）。</summary>
    public long FlushIntervalMs { get; set; } = 5000;

    /// <summary>单次批量落库最多快照实体数。</summary>
    public int FlushBatchSize { get; set; } = 256;
}

/// <summary>
/// 持久化后端工厂：按配置创建 <see cref="IEntityPersistenceStore"/> 实现。
/// 使 Battle 等节点无需关心后端细节，仅按配置切换（对标 KBE dbmgr 可插拔数据库后端）。
/// </summary>
public static class PersistenceStoreFactory
{
    /// <summary>创建后端。Provider 未知时抛异常（fail-fast，不静默回退到文件）。</summary>
    public static IEntityPersistenceStore Create(EntityPersistenceOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        switch (options.Provider.Trim().ToLowerInvariant())
        {
            case "file":
                string dir = string.IsNullOrWhiteSpace(options.Directory)
                    ? Path.Combine(AppContext.BaseDirectory, "persist", "entities")
                    : options.Directory;
                return new FileEntityPersistenceStore(dir);

            case "mysql":
                return new MySqlEntityPersistenceStore(
                    RequireConnectionString(options, "MySql"));

            case "postgresql":
            case "postgres":
            case "pgsql":
                return new PostgreSqlEntityPersistenceStore(
                    RequireConnectionString(options, "PostgreSql"));

            case "redis":
                return new RedisEntityPersistenceStore(
                    RequireConnectionString(options, "Redis"), options.RedisDatabase);

            default:
                throw new ArgumentException(
                    $"未知实体持久化后端 Provider: {options.Provider}（支持 File / MySql / PostgreSql / Redis）");
        }
    }

    private static string RequireConnectionString(EntityPersistenceOptions options, string provider)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                $"实体持久化后端 {provider} 需要配置 ConnectionString（EntityPersistence:ConnectionString）。");
        }
        return options.ConnectionString;
    }
}
