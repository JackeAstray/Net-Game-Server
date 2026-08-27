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

    /// <param name="storageDir">持久化目录</param>
    /// <param name="entityFactory">按实体 ID 创建空实体骨架的回调（恢复时用；null 则无法加载单实体）</param>
    public EntityPersistenceService(string storageDir, Func<long, Entity>? entityFactory = null)
    {
        this.storageDir = storageDir;
        this.entityFactory = entityFactory;
        Directory.CreateDirectory(storageDir);
    }

    /// <summary>实体文件路径。</summary>
    private string GetEntityPath(Entity entity) =>
        Path.Combine(storageDir, entity.TypeName, $"{entity.EntityId}.bin");

    /// <summary>
    /// 保存单个实体全部属性（对标 KBE 实体落库）。
    /// </summary>
    public void SaveEntity(Entity entity)
    {
        byte[] props = PropertyCodec.SerializeAll(entity);
        string path = GetEntityPath(entity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, props);
    }

    /// <summary>
    /// 异步保存单个实体（不阻塞调用线程）。
    /// </summary>
    public Task SaveEntityAsync(Entity entity) => Task.Run(() => SaveEntity(entity));

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
        string path = Path.Combine(storageDir, entityType, $"{entityId}.bin");
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
        string path = Path.Combine(storageDir, entityType, $"{entityId}.bin");
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
        string dir = Path.Combine(storageDir, entityType);
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
        string dir = Path.Combine(storageDir, entityType);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin").Length : 0;
    }
}
