using System.Buffers.Binary;
using System.Text;

namespace Framework.Entity;

/// <summary>
/// 实体持久化服务（对标 KBE dbmgr entity_table 的自动存取）：
/// - 用 EntityDef 属性声明驱动序列化（不需要手写 SQL/字段映射）
/// - 支持按实体 ID 单条保存/加载（属性级），或全量快照保存/恢复
/// - 存储介质可插拔（文件目录 / 自定义回调），Battle 接入后实现崩溃自动恢复
/// 文件布局：&lt;dir&gt;/&lt;EntityType&gt;/&lt;EntityId&gt;.bin
/// </summary>
public sealed class EntityPersistenceService
{
    private readonly string storageDir;
    private readonly Func<long, Entity>? entityFactory; // 按 ID 重建空实体骨架（恢复用）

    // 实体类型名白名单（防路径穿越：类型名只允许字母/数字/下划线）
    private static readonly System.Text.RegularExpressions.Regex EntityTypePattern =
        new("^[A-Za-z0-9_]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // 每实体写锁：同一实体的并发 SaveEntityAsync/SaveEntity 串行化，避免 FileMode.Create 冲突
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, object> saveLocks = new();

    /// <param name="storageDir">持久化目录</param>
    /// <param name="entityFactory">按实体 ID 创建空实体骨架的回调（恢复时用；null 则无法加载单实体）</param>
    public EntityPersistenceService(string storageDir, Func<long, Entity>? entityFactory = null)
    {
        this.storageDir = storageDir;
        this.entityFactory = entityFactory;
        Directory.CreateDirectory(storageDir);
    }

    /// <summary>校验实体类型名并返回规范化后的名称（防路径穿越）。</summary>
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

    /// <summary>实体文件路径。</summary>
    private string GetEntityPath(Entity entity) => ResolveSafePath(entity.TypeName, entity.EntityId);

    /// <summary>
    /// 原子写盘：先写同目录临时文件再 rename 覆盖，进程崩溃/写一半时不会留下半截损坏文件。
    /// </summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 保存单个实体全部属性（对标 KBE 实体落库）。
    /// 原子写 + 每实体串行化，可安全并发调用。
    /// </summary>
    public void SaveEntity(Entity entity)
    {
        lock (GetSaveLock(entity.EntityId))
        {
            byte[] props = PropertyCodec.SerializeAll(entity);
            string path = GetEntityPath(entity);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, props);
        }
    }

    /// <summary>
    /// 异步保存单个实体（不阻塞调用线程）。
    /// 先在调用线程快照（脱离活实体），再于后台按实体串行化写盘；
    /// 避免在非 tick 线程直接读活实体造成与 tick 写的数据竞争。
    /// </summary>
    public Task SaveEntityAsync(Entity entity)
    {
        // P3 修复：快照必须在"调用线程"完成（注释声称如此，原实现却放在 Task.Run 后台线程里，
        // 后台线程直接读活实体，与 tick 线程写实体存在数据竞争）。锁只串行化写盘，不保护实体读取。
        var snapshot = entity.CopyValues();
        var def = entity.Def;
        long entityId = entity.EntityId;
        return Task.Run(() =>
        {
            lock (GetSaveLock(entityId))
            {
                byte[] props = PropertyCodec.SerializeAllValues(snapshot, def);
                string path = GetEntityPath(entity);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAtomic(path, props);
            }
        });
    }

    private object GetSaveLock(long entityId) => saveLocks.GetOrAdd(entityId, static _ => new object());

    /// <summary>
    /// 加载单个实体属性到已重建的实体骨架。返回 true 表示加载成功。
    /// </summary>
    public bool LoadEntity(Entity entity)
    {
        string path = GetEntityPath(entity);
        if (!File.Exists(path))
        {
            return false;
        }
        byte[] props = File.ReadAllBytes(path);
        PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
        return true;
    }

    /// <summary>
    /// 按 ID 重建并加载实体（需要 entityFactory）。
    /// </summary>
    public Entity? LoadEntityById(string entityType, long entityId)
    {
        if (entityFactory == null)
        {
            return null;
        }
        var entity = entityFactory(entityId);
        // 类型名可能不一致，直接按传入类型加载
        string path = ResolveSafePath(entityType, entityId);
        if (!File.Exists(path))
        {
            return null;
        }
        byte[] props = File.ReadAllBytes(path);
        PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
        return entity;
    }

    /// <summary>删除单个实体持久化数据。</summary>
    public void DeleteEntity(string entityType, long entityId)
    {
        string path = ResolveSafePath(entityType, entityId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>批量保存实体（自动按类型分目录）。</summary>
    public void SaveAll(IEnumerable<Entity> entities)
    {
        foreach (var entity in entities)
        {
            SaveEntity(entity);
        }
    }

    /// <summary>
    /// 全量恢复：扫描目录中某类型的所有实体文件，用 entityFactory 重建并加载。
    /// 返回恢复的实体列表（对标 KBE restore_entity_handler）。
    /// </summary>
    public List<Entity> RestoreAll(string entityType)
    {
        var result = new List<Entity>();
        string dir = Path.Combine(storageDir, ValidateEntityType(entityType));
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(dir, "*.bin"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!long.TryParse(name, out long entityId) || entityFactory == null)
            {
                continue;
            }
            var entity = entityFactory(entityId);
            byte[] props = File.ReadAllBytes(file);
            PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
            result.Add(entity);
        }

        Framework.Core.Log.Info($"实体持久化恢复完成: {entityType} 共 {result.Count} 个");
        return result;
    }

    /// <summary>统计某类型的持久化实体数。</summary>
    public int Count(string entityType)
    {
        string dir = Path.Combine(storageDir, ValidateEntityType(entityType));
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin").Length : 0;
    }
}
