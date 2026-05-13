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
        // 存储当前在这个 Battle 节点上的所有实体状态，Key = 客户端 OriginalSessionId
        private readonly ConcurrentDictionary<long, EntityState> _entities = new();

        /// <summary>
        /// 当玩家加入房间/场景时，创建实体状态。
        /// </summary>
        public void AddOrUpdateEntity(long sessionId, EntityState state)
        {
            _entities[sessionId] = state;
        }

        public void RemoveEntity(long sessionId)
        {
            _entities.TryRemove(sessionId, out _);
        }

        public EntityState? GetEntity(long sessionId)
        {
            if (_entities.TryGetValue(sessionId, out var state))
            {
                return state;
            }
            return null;
        }

        public IEnumerable<EntityState> GetAllEntities()
        {
            return _entities.Values;
        }

        public IEnumerable<long> GetAllSessionIds()
        {
            return _entities.Keys;
        }
    }
}