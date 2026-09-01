using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Entity
{
    /// <summary>
    /// 实体管理器：管理节点/场景内的实体集合，并负责跨进程远程调用的本地分发。
    /// 维护按实体类型（EntityDef.Name）的二级索引，脚本 tick/事件分发可按类型直达，
    /// 避免每 tick 全量遍历所有实体再按类型过滤（对标 KBE cell 的按类型实体表）。
    /// </summary>
    public class EntityManager
    {
        /// <summary>所有实体，Key = 实体 ID</summary>
        private readonly ConcurrentDictionary<long, Entity> entities = new();

        /// <summary>按实体类型索引：TypeName -> (EntityId -> Entity)</summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Entity>> entitiesByType = new(StringComparer.Ordinal);

        // 集合变更版本号：任何 Add/Remove 都自增。
        // 供外部（EntityBackupService 等缓存实体集合的组件）廉价检测"集合是否发生变更"——
        // 即使总数不变（增删抵消），版本号也会变化，避免陈旧缓存漏备份新实体。
        private long version;

        /// <summary>集合变更版本号（Add/Remove 自增）。</summary>
        public long Version => System.Threading.Interlocked.Read(ref version);

        /// <summary>实体新增/更新事件（外部建立反索引用，如 SceneManager 的 entityId→sceneId 路由表）。</summary>
        public event Action<long, Entity>? EntityAdded;

        /// <summary>实体移除事件（外部维护反索引用）。</summary>
        public event Action<long, string>? EntityRemoved;

        /// <summary>添加或更新实体。</summary>
        public void AddOrUpdateEntity(long entityId, Entity entity)
        {
            // D7 脚本层 Mailbox：注册时若实体未挂 Mailbox 则挂 Local Mailbox（不覆盖已显式挂的 Remote Mailbox）
            entity.AttachMailboxIfAbsent(EntityMailbox.Local(entityId, this));

            entities[entityId] = entity;
            var byType = entitiesByType.GetOrAdd(entity.TypeName, _ => new ConcurrentDictionary<long, Entity>());
            byType[entityId] = entity;
            System.Threading.Interlocked.Increment(ref version);
            EntityAdded?.Invoke(entityId, entity);
        }

        /// <summary>移除实体。</summary>
        public void RemoveEntity(long entityId)
        {
            if (!entities.TryRemove(entityId, out var entity))
            {
                return;
            }
            if (entitiesByType.TryGetValue(entity.TypeName, out var byType))
            {
                byType.TryRemove(entityId, out _);
            }
            System.Threading.Interlocked.Increment(ref version);
            EntityRemoved?.Invoke(entityId, entity.TypeName);
        }

        /// <summary>获取实体；不存在返回 null。</summary>
        public Entity? GetEntity(long entityId)
        {
            entities.TryGetValue(entityId, out var entity);
            return entity;
        }

        /// <summary>所有实体（实时视图）。</summary>
        public IEnumerable<Entity> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>所有实体 ID。</summary>
        public IEnumerable<long> GetAllEntityIds()
        {
            return entities.Keys;
        }

        /// <summary>
        /// 按实体类型获取全部实体（O(该类型实体数)，脚本 TickAll/全局数据事件按类型直达）。
        /// </summary>
        public IEnumerable<Entity> GetAllEntitiesByType(string typeName)
        {
            if (!entitiesByType.TryGetValue(typeName, out var byType))
            {
                return Array.Empty<Entity>();
            }
            return byType.Values;
        }

        /// <summary>实体数量。</summary>
        public int Count => entities.Count;

        /// <summary>
        /// 分发跨进程远程调用到本节点实体（对标 KBE 远端实体方法调用）。
        /// 返回 (是否找到实体并执行, 结果对象)。
        /// </summary>
        public (bool Handled, object? Result) DispatchRemoteCall(Framework.Protocol.Generated.EntityRemoteCall call)
        {
            if (!entities.TryGetValue(call.EntityId, out var entity))
            {
                return (false, null);
            }

            // E8 修复：畸形参数 payload 不能抛出接收循环，视为调用失败
            object?[] args;
            try
            {
                args = ArgCodec.Deserialize(call.Args);
            }
            catch (Exception ex)
            {
                Framework.Core.Log.Warn($"EntityCall 参数反序列化失败，视为调用失败 EntityId:{call.EntityId} Method:{call.MethodName} Err:{ex.Message}");
                return (false, null);
            }
            return entity.InvokeMethod(call.MethodName, args);
        }

        /// <summary>
        /// 执行远程调用并构造回执（对标 KBE EntityRemoteCall 回执）：
        /// - CallId == 0（fire-and-forget）→ 返回 null，无需回执
        /// - 否则返回携带同一 CallId 的 EntityRemoteCallResult（Success=实体/方法是否执行成功）
        /// 接收方把返回值经节点链路回传给调用方，调用方经 EntityCallHub.HandleResult 关联完成。
        /// </summary>
        public Framework.Protocol.Generated.EntityRemoteCallResult? ExecuteRemoteCall(Framework.Protocol.Generated.EntityRemoteCall call)
        {
            var (handled, result) = DispatchRemoteCall(call);
            if (call.CallId == 0)
            {
                return null;
            }

            return new Framework.Protocol.Generated.EntityRemoteCallResult
            {
                CallId = call.CallId,
                EntityId = call.EntityId,
                MethodName = call.MethodName,
                Success = handled,
                Result = handled ? ArgCodec.Serialize(new object?[] { result }) : Array.Empty<byte>()
            };
        }
    }
}
