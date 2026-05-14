using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared.Messages.Battle;
using Shared.Messages;

namespace Battle.Handlers
{
    /// <summary>
    /// 管理单个场景内所有的活跃实体连接以及其状态。
    /// </summary>
    public class EntityManager
    {
        /// <summary>
        /// 存储当前在这个 Battle 节点上的所有实体状态，Key = 客户端 OriginalSessionId
        /// </summary>
        private readonly ConcurrentDictionary<long, EntityState> entities = new();

        /// <summary>
        /// 当玩家加入房间/场景时，创建或更新该玩家的实体状态。
        /// 如果已存在相同 sessionId 的实体，则会被新的 state 覆盖。
        /// </summary>
        /// <param name="sessionId">玩家会话ID（客户端 OriginalSessionId）</param>
        /// <param name="state">玩家的实体状态对象</param>
        public void AddOrUpdateEntity(long sessionId, EntityState state)
        {
            entities[sessionId] = state;
        }

        /// <summary>
        /// 从管理器中移除指定会话ID的实体状态，用于玩家离开或断开时清理。
        /// </summary>
        /// <param name="sessionId">要移除的玩家会话ID</param>
        public void RemoveEntity(long sessionId)
        {
            entities.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// 根据会话ID获取对应的实体状态。
        /// </summary>
        /// <param name="sessionId">玩家会话ID</param>
        /// <returns>找到则返回对应的 EntityState，否则返回 null</returns>
        public EntityState? GetEntity(long sessionId)
        {
            if (entities.TryGetValue(sessionId, out var state))
            {
                return state;
            }
            return null;
        }

        /// <summary>
        /// 获取当前管理器中所有活跃的实体状态集合。
        /// 注意：返回的是集合的实时视图，调用方如果需要一致性快照应自行复制。
        /// </summary>
        /// <returns>所有实体状态的枚举</returns>
        public IEnumerable<EntityState> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>
        /// 获取当前管理器中所有实体对应的会话ID集合。
        /// </summary>
        /// <returns>所有会话ID的枚举</returns>
        public IEnumerable<long> GetAllSessionIds()
        {
            return entities.Keys;
        }
    }
}