using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Framework.Entity
{
    /// <summary>
    /// 实体管理器：管理节点/场景内的实体集合，并负责跨进程远程调用的本地分发。
    /// </summary>
    public class EntityManager
    {
        /// <summary>所有实体，Key = 实体 ID</summary>
        private readonly ConcurrentDictionary<long, Entity> entities = new();

        /// <summary>添加或更新实体。</summary>
        public void AddOrUpdateEntity(long entityId, Entity entity)
        {
            entities[entityId] = entity;
        }

        /// <summary>移除实体。</summary>
        public void RemoveEntity(long entityId)
        {
            entities.TryRemove(entityId, out _);
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
