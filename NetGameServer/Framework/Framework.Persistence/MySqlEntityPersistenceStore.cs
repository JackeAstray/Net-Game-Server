using Framework.Entity;

namespace Framework.Persistence;

/// <summary>
/// MySQL 实体持久化存储（MySqlConnector 官方驱动）。
/// 表结构（自动建表）：entity_persistence(entity_type, entity_id, props, updated_at)，主键 (entity_type, entity_id)。
/// props 列存储 PropertyCodec 序列化后的属性字节（LONGBLOB）。
/// 写语义：UPSERT（INSERT ... ON DUPLICATE KEY UPDATE），崩溃安全由数据库事务保证。
/// </summary>
public sealed class MySqlEntityPersistenceStore : IEntityPersistenceStore
{
    private readonly string connectionString;

    /// <summary>实例级建表仅执行一次（P1 修复：原 static 标记在分片/多库场景下第二个实例跳过建表导致 SQL 报错）。</summary>
    private bool tableEnsured;
    private static readonly object tableGate = new();

    public string Name => "MySql";

    public MySqlEntityPersistenceStore(string connectionString)
    {
        this.connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    private const string UpsertSql = """
        INSERT INTO entity_persistence (entity_type, entity_id, props, updated_at)
        VALUES (@type, @id, @props, UTC_TIMESTAMP(6))
        ON DUPLICATE KEY UPDATE props = VALUES(props), updated_at = UTC_TIMESTAMP(6)
        """;

    private const string SelectSql = "SELECT props FROM entity_persistence WHERE entity_type = @type AND entity_id = @id";

    private const string DeleteSql = "DELETE FROM entity_persistence WHERE entity_type = @type AND entity_id = @id";

    private const string SelectAllSql = "SELECT entity_id, props FROM entity_persistence WHERE entity_type = @type";

    private const string CountSql = "SELECT COUNT(*) FROM entity_persistence WHERE entity_type = @type";

    /// <summary>同步建表 DDL（P2 修复：原异步 + GetAwaiter().GetResult() 是同步阻塞异步反模式，改为与 PostgreSql 对齐）。</summary>
    private void EnsureTable(MySqlConnector.MySqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entity_persistence (
                entity_type VARCHAR(64) NOT NULL,
                entity_id BIGINT NOT NULL,
                props LONGBLOB NOT NULL,
                updated_at DATETIME(6) NOT NULL,
                PRIMARY KEY (entity_type, entity_id)
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>实例内只建表一次（double-checked locking）。</summary>
    private void EnsureTableOnce(MySqlConnector.MySqlConnection conn)
    {
        if (tableEnsured)
        {
            return;
        }
        lock (tableGate)
        {
            if (tableEnsured)
            {
                return;
            }
            EnsureTable(conn);
            tableEnsured = true;
        }
    }

    public void Save(string entityType, long entityId, byte[] serializedProps)
    {
        using var conn = new MySqlConnector.MySqlConnection(connectionString);
        conn.Open();
        EnsureTableOnce(conn);
        using var cmd = new MySqlConnector.MySqlCommand(UpsertSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.Parameters.AddWithValue("@props", serializedProps);
        cmd.ExecuteNonQuery();
    }

    public byte[]? TryLoad(string entityType, long entityId)
    {
        using var conn = new MySqlConnector.MySqlConnection(connectionString);
        conn.Open();
        using var cmd = new MySqlConnector.MySqlCommand(SelectSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        cmd.Parameters.AddWithValue("@id", entityId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return (byte[])reader.GetValue(0);
    }

    public void Delete(string entityType, long entityId)
    {
        using var conn = new MySqlConnector.MySqlConnection(connectionString);
        conn.Open();
        using var cmd = new MySqlConnector.MySqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<StoredEntity> LoadAll(string entityType)
    {
        using var conn = new MySqlConnector.MySqlConnection(connectionString);
        conn.Open();
        using var cmd = new MySqlConnector.MySqlCommand(SelectAllSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            byte[] props = (byte[])reader.GetValue(1);
            yield return new StoredEntity(id, props);
        }
    }

    public int Count(string entityType)
    {
        using var conn = new MySqlConnector.MySqlConnection(connectionString);
        conn.Open();
        using var cmd = new MySqlConnector.MySqlCommand(CountSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
    }
}
