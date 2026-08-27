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

        /// <summary>添加或更新实体。</summary>
        public void AddOrUpdateEntity(long entityId, Entity entity)
        {
            entities[entityId] = entity;
            var byType = entitiesByType.GetOrAdd(entity.TypeName, _ => new ConcurrentDictionary<long, Entity>());
            byType[entityId] = entity;
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

            object?[] args = ArgCodec.Deserialize(call.Args);
            return entity.InvokeMethod(call.MethodName, args);
        }
    }
}
