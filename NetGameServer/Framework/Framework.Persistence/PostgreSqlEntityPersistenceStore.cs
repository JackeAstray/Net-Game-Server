using Framework.Entity;

namespace Framework.Persistence;

/// <summary>
/// PostgreSQL 实体持久化存储（Npgsql 官方驱动）。
/// 表结构（自动建表）：entity_persistence(entity_type, entity_id, props, updated_at)，主键 (entity_type, entity_id)。
/// props 列存储 PropertyCodec 序列化后的属性字节（BYTEA）。
/// 写语义：UPSERT（INSERT ... ON CONFLICT ... DO UPDATE），崩溃安全由数据库事务保证。
/// </summary>
public sealed class PostgreSqlEntityPersistenceStore : IEntityPersistenceStore
{
    private readonly string connectionString;

    public string Name => "PostgreSql";

    public PostgreSqlEntityPersistenceStore(string connectionString)
    {
        this.connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    private const string UpsertSql = """
        INSERT INTO entity_persistence (entity_type, entity_id, props, updated_at)
        VALUES (@type, @id, @props, now())
        ON CONFLICT (entity_type, entity_id) DO UPDATE
            SET props = EXCLUDED.props, updated_at = now()
        """;

    private const string SelectSql = "SELECT props FROM entity_persistence WHERE entity_type = @type AND entity_id = @id";

    private const string DeleteSql = "DELETE FROM entity_persistence WHERE entity_type = @type AND entity_id = @id";

    private const string SelectAllSql = "SELECT entity_id, props FROM entity_persistence WHERE entity_type = @type";

    private const string CountSql = "SELECT COUNT(*) FROM entity_persistence WHERE entity_type = @type";

    private void EnsureTable(Npgsql.NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entity_persistence (
                entity_type VARCHAR(64) NOT NULL,
                entity_id BIGINT NOT NULL,
                props BYTEA NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL,
                PRIMARY KEY (entity_type, entity_id)
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void Save(string entityType, long entityId, byte[] serializedProps)
    {
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        EnsureTable(conn);
        using var cmd = new Npgsql.NpgsqlCommand(UpsertSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.Parameters.AddWithValue("@props", serializedProps);
        cmd.ExecuteNonQuery();
    }

    public byte[]? TryLoad(string entityType, long entityId)
    {
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new Npgsql.NpgsqlCommand(SelectSql, conn);
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
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new Npgsql.NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        cmd.Parameters.AddWithValue("@id", entityId);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<StoredEntity> LoadAll(string entityType)
    {
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new Npgsql.NpgsqlCommand(SelectAllSql, conn);
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
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new Npgsql.NpgsqlCommand(CountSql, conn);
        cmd.Parameters.AddWithValue("@type", entityType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
    }
}
