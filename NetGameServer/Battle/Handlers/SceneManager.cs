using System.Collections.Concurrent;
using System.Linq;

namespace Battle.Handlers
{
    /// <summary>
    /// 统一管理战斗节点上的所有 Scene（房间/大地图）。
    /// 负责场景的创建、查询、移除，以及玩家与场景的绑定关系管理。
    /// </summary>
    public class SceneManager
    {
        /// <summary>
        /// 存储当前节点上所有场景的集合，键为 SceneId，值为对应的 BattleScene 实例。
        /// 使用并发字典以支持多线程读写。
        /// </summary>
        private readonly ConcurrentDictionary<string, BattleScene> scenes = new();

        /// <summary>场景创建事件（宿主用于生成场景级玩法实体、注册脚本实体管理器等）。</summary>
        public event Action<BattleScene>? SceneCreated;

        /// <summary>场景销毁事件（宿主用于注销脚本实体管理器/备份管理器、清理帧同步字典、通知脚本 OnDestroy 取消定时器）。</summary>
        public event Action<BattleScene>? SceneDestroyed;

        /// <summary>
        /// 玩家会话 Id 到所在场景 Id 的映射。
        /// 用于快速根据玩家路由到其所在场景进行消息处理。
        /// </summary>
        private readonly ConcurrentDictionary<long, string> playerToSceneBinding = new();

        /// <summary>
        /// 场景 Id -> 绑定该场景的玩家会话集合（反索引，对标迭代 8 三-15 修正）：
        /// GetPlayerCount / GetPlayerSessionIds / UnbindPlayersInScene 由 O(全体玩家) 全表扫描
        /// 降为 O(该场景玩家数)，Bind/Unbind 维护本反索引。
        /// </summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, byte>> sceneToPlayers = new(StringComparer.Ordinal);

        /// <summary>
        /// 实体 Id -> 所在场景 Id 反索引（修复：FindSceneByEntityId 原为 O(场景数×每场景实体表) 逐场景扫描，
        /// 在脚本动作/EntityCall 热路径上每帧调用；改为订阅各场景 EntityManager 增删事件维护本表，
        /// 查询降为 O(1)）。
        /// </summary>
        private readonly ConcurrentDictionary<long, string> entityToSceneBinding = new();

        /// <summary>
        /// 根据场景配置获取已有场景或创建新场景。
        /// 如果指定 SceneId 的场景已存在则返回该场景，否则创建并返回新的 BattleScene 实例。
        /// 新场景创建后会触发 SceneCreated 事件（场景级玩法实体生成钩子）。
        /// 创建后立即订阅其 EntityManager 的实体增删事件，维护 entityId→sceneId 反索引
        /// （必须在 SceneCreated 之前订阅，以捕获宿主生成场景级玩法实体的注册）。
        /// </summary>
        /// <param name="config">用于创建场景的配置信息，必须包含 SceneId。</param>
        /// <returns>对应的 BattleScene 实例。</returns>
        public BattleScene GetOrCreateScene(SceneConfig config)
        {
            return scenes.GetOrAdd(config.SceneId, id =>
            {
                var scene = new BattleScene(config);
                // 维护实体反索引：新增/更新→记录，移除→清除。
                scene.EntityManager.EntityAdded += (entityId, _) => entityToSceneBinding[entityId] = id;
                scene.EntityManager.EntityRemoved += (entityId, _) => entityToSceneBinding.TryRemove(entityId, out string? _);
                SceneCreated?.Invoke(scene);
                return scene;
            });
        }

        /// <summary>
        /// 根据实体 ID 查找其所在场景（跨场景实体路由/脚本动作分发用）。
        /// 修复：由逐场景扫描降为 O(1) 反索引查询（命中场景再 O(1) 确认实体仍存在）。
        /// </summary>
        public BattleScene? FindSceneByEntityId(long entityId)
        {
            if (entityToSceneBinding.TryGetValue(entityId, out var sceneId)
                && scenes.TryGetValue(sceneId, out var scene)
                && scene.EntityManager.GetEntity(entityId) != null)
            {
                return scene;
            }
            return null;
        }

        /// <summary>
        /// 根据 SceneId 获取场景实例。
        /// 若场景不存在则返回 null。
        /// </summary>
        /// <param name="sceneId">场景的唯一标识符。</param>
        /// <returns>对应的 BattleScene 实例或 null。</returns>
        public BattleScene? GetScene(string sceneId)
        {
            scenes.TryGetValue(sceneId, out var scene);
            return scene;
        }

        /// <summary>
        /// 将玩家（通过 sessionId）绑定到指定场景 Id。
        /// 若玩家已存在绑定关系则会被覆盖为新场景（同时维护反索引）。
        /// </summary>
        /// <param name="sessionId">玩家的会话 Id（唯一标识）。</param>
        /// <param name="sceneId">要绑定的场景 Id。</param>
        public void BindPlayerToScene(long sessionId, string sceneId)
        {
            if (playerToSceneBinding.TryGetValue(sessionId, out var oldSceneId) && oldSceneId == sceneId)
            {
                return; // 已是目标场景
            }

            playerToSceneBinding[sessionId] = sceneId;

            if (oldSceneId != null)
            {
                RemoveFromSceneSet(oldSceneId, sessionId);
            }
            sceneToPlayers.GetOrAdd(sceneId, _ => new ConcurrentDictionary<long, byte>())[sessionId] = 0;
        }

        /// <summary>
        /// 解除玩家与场景的绑定关系。
        /// 通常在玩家断开连接或离开场景时调用。
        /// </summary>
        /// <param name="sessionId">要解绑的玩家会话 Id。</param>
        public void UnbindPlayer(long sessionId)
        {
            if (playerToSceneBinding.TryRemove(sessionId, out var sceneId))
            {
                RemoveFromSceneSet(sceneId, sessionId);
            }
        }

        /// <summary>从场景反索引中移除玩家（集合为空时清理该场景条目）。</summary>
        private void RemoveFromSceneSet(string sceneId, long sessionId)
        {
            if (sceneToPlayers.TryGetValue(sceneId, out var set))
            {
                set.TryRemove(sessionId, out _);
                if (set.IsEmpty)
                {
                    sceneToPlayers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<long, byte>>(sceneId, set));
                }
            }
        }

        /// <summary>
        /// 根据玩家的会话 Id 获取其所在的场景实例。
        /// 若玩家未绑定到任何场景或场景不存在则返回 null。
        /// </summary>
        /// <param name="sessionId">玩家的会话 Id。</param>
        /// <returns>玩家所在的 BattleScene 实例或 null。</returns>
        public BattleScene? GetSceneByPlayer(long sessionId)
        {
            if (playerToSceneBinding.TryGetValue(sessionId, out var sceneId))
            {
                return GetScene(sceneId);
            }
            return null;
        }

        /// <summary>
        /// 从管理集合中移除指定 SceneId 的场景。
        /// 移除成功时触发 <see cref="SceneDestroyed"/> 事件并记录日志（宿主负责场景内资源的清理）。
        /// </summary>
        /// <param name="sceneId">要移除的场景 Id。</param>
        public void RemoveScene(string sceneId)
        {
            if (scenes.TryRemove(sceneId, out var removedScene))
            {
                // 清理实体反索引中指向该场景的条目（场景销毁时实体可能未逐条走 RemoveEntity）。
                foreach (var kv in entityToSceneBinding)
                {
                    if (kv.Value == sceneId)
                    {
                        entityToSceneBinding.TryRemove(kv.Key, out _);
                    }
                }
                SceneDestroyed?.Invoke(removedScene);
                Shared.Log.Info($" 场景已移除并清理干净: {sceneId}");
            }
        }

        /// <summary>
        /// 获取指定场景中当前绑定的玩家会话数量（O(1)，反索引）。
        /// </summary>
        /// <param name="sceneId">场景标识。</param>
        /// <returns>绑定到该场景的玩家数。</returns>
        public int GetPlayerCount(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                return 0;
            }

            return sceneToPlayers.TryGetValue(sceneId, out var set) ? set.Count : 0;
        }

        /// <summary>
        /// 清理指定场景上的所有玩家绑定（O(该场景玩家数)，反索引）。
        /// </summary>
        /// <param name="sceneId">场景标识。</param>
        /// <returns>被清理的玩家数量。</returns>
        public int UnbindPlayersInScene(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                return 0;
            }

            if (!sceneToPlayers.TryRemove(sceneId, out var set))
            {
                return 0;
            }

            int removed = 0;
            foreach (var sessionId in set.Keys)
            {
                if (playerToSceneBinding.TryRemove(sessionId, out _))
                {
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// 获取指定场景当前绑定的玩家会话标识列表（O(该场景玩家数)，反索引）。
        /// </summary>
        /// <param name="sceneId">场景标识。</param>
        /// <returns>当前绑定到该场景的玩家会话标识数组。</returns>
        public long[] GetPlayerSessionIds(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                return System.Array.Empty<long>();
            }

            if (sceneToPlayers.TryGetValue(sceneId, out var set) && set.Count > 0)
            {
                return set.Keys.ToArray();
            }

            return System.Array.Empty<long>();
        }

        /// <summary>
        /// 获取当前场景集合中的场景数。
        /// </summary>
        /// <remarks>基于内部集合的 Count 值，为 O(1) 操作；如果集合在调用后被修改，结果可能随之改变。</remarks>
        /// <returns>表示场景数量的整数。</returns>
        public int GetSceneCount()
        {
            return scenes.Count;
        }

        /// <summary>获取全部场景（脚本 tick 驱动用）。</summary>
        public IEnumerable<BattleScene> GetAllScenes()
        {
            return scenes.Values;
        }

        /// <summary>
        /// 获取当前绑定到场景的玩家数量。
        /// </summary>
        /// <returns>已绑定玩家的数量。</returns>
        public int GetBoundPlayerCount()
        {
            return playerToSceneBinding.Count;
        }
    }
}
