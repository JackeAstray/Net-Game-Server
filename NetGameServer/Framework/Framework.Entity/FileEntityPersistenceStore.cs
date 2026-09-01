using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Framework.Entity;

/// <summary>
/// 文件型实体持久化存储（默认实现，零第三方依赖；供开发/测试/单机部署使用）。
/// 布局：&lt;dir&gt;/&lt;EntityType&gt;/&lt;EntityId&gt;.bin
/// 特性：
/// - 防路径穿越：类型名白名单（字母/数字/下划线）+ 解析后路径必须位于根目录之内（纵深防御）；
/// - 原子写盘：先写同目录临时文件再 rename 覆盖，进程崩溃/写一半不会留下半截损坏文件；
/// - 每实体写锁：同一实体并发 Save 串行化，避免 FileMode.Create 冲突。
/// 生产可替换为 SQL/Redis 后端（见 Framework.Persistence），接口 <see cref="IEntityPersistenceStore"/>。
/// </summary>
public sealed class FileEntityPersistenceStore : IEntityPersistenceStore
{
    private readonly string storageDir;

    /// <summary>实体类型名白名单（防路径穿越：类型名只允许字母/数字/下划线）。</summary>
    private static readonly Regex EntityTypePattern =
        new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>每实体写锁：同一实体的并发 Save 串行化，避免 FileMode.Create 冲突。</summary>
    private readonly ConcurrentDictionary<long, object> saveLocks = new();

    public string Name => "File";

    public FileEntityPersistenceStore(string storageDir)
    {
        this.storageDir = storageDir;
        Directory.CreateDirectory(storageDir);
    }

    private static string ValidateEntityType(string entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType) || !EntityTypePattern.IsMatch(entityType))
        {
            throw new ArgumentException($"非法实体类型名（仅允许字母/数字/下划线）: {entityType ?? "<null>"}");
        }
        return entityType;
    }

    /// <summary>确保解析后的完整路径仍位于 storageDir 之下（纵深防御）。</summary>
    private string ResolveSafePath(string entityType, long entityId)
    {
        string dir = Path.Combine(storageDir, ValidateEntityType(entityType));
        string full = Path.GetFullPath(Path.Combine(dir, $"{entityId}.bin"));
        string root = Path.GetFullPath(storageDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"实体路径越界: {full}");
        }
        return full;
    }

    /// <summary>原子写盘：先写同目录临时文件再 rename 覆盖。</summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private object GetSaveLock(long entityId) => saveLocks.GetOrAdd(entityId, static _ => new object());

    public void Save(string entityType, long entityId, byte[] serializedProps)
    {
        lock (GetSaveLock(entityId))
        {
            string path = ResolveSafePath(entityType, entityId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, serializedProps);
        }
    }

    public byte[]? TryLoad(string entityType, long entityId)
    {
        string path = ResolveSafePath(entityType, entityId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Delete(string entityType, long entityId)
    {
        string path = ResolveSafePath(entityType, entityId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IEnumerable<StoredEntity> LoadAll(string entityType)
    {
        string dir = Path.Combine(storageDir, ValidateEntityType(entityType));
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (var file in Directory.GetFiles(dir, "*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!long.TryParse(name, out long entityId))
            {
                continue;
            }
            yield return new StoredEntity(entityId, File.ReadAllBytes(file));
        }
    }

    public int Count(string entityType)
    {
        string dir = Path.Combine(storageDir, ValidateEntityType(entityType));
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin").Length : 0;
    }

    public void Dispose()
    {
        // 文件存储无可释放资源；保留空实现以满足接口。
    }
}
