using System.Collections.Concurrent;

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

        /// <summary>
        /// 玩家会话 Id 到所在场景 Id 的映射。
        /// 用于快速根据玩家路由到其所在场景进行消息处理。
        /// </summary>
        private readonly ConcurrentDictionary<long, string> playerToSceneBinding = new();

        /// <summary>
        /// 根据场景配置获取已有场景或创建新场景。
        /// 如果指定 SceneId 的场景已存在则返回该场景，否则创建并返回新的 BattleScene 实例。
        /// </summary>
        /// <param name="config">用于创建场景的配置信息，必须包含 SceneId。</param>
        /// <returns>对应的 BattleScene 实例。</returns>
        public BattleScene GetOrCreateScene(SceneConfig config)
        {
            return scenes.GetOrAdd(config.SceneId, _ => new BattleScene(config));
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
        /// 若玩家已存在绑定关系则会被覆盖为新场景。
        /// </summary>
        /// <param name="sessionId">玩家的会话 Id（唯一标识）。</param>
        /// <param name="sceneId">要绑定的场景 Id。</param>
        public void BindPlayerToScene(long sessionId, string sceneId)
        {
            playerToSceneBinding[sessionId] = sceneId;
        }

        /// <summary>
        /// 解除玩家与场景的绑定关系。
        /// 通常在玩家断开连接或离开场景时调用。
        /// </summary>
        /// <param name="sessionId">要解绑的玩家会话 Id。</param>
        public void UnbindPlayer(long sessionId)
        {
            playerToSceneBinding.TryRemove(sessionId, out _);
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
        /// 移除成功时会记录日志，注意调用者需负责场景内资源的清理（若有必要）。
        /// </summary>
        /// <param name="sceneId">要移除的场景 Id。</param>
        public void RemoveScene(string sceneId)
        {
            if (scenes.TryRemove(sceneId, out var removedScene))
            {
                Shared.Log.Info($" 场景已移除并清理干净: {sceneId}");
            }
        }

        public int GetSceneCount()
        {
            return scenes.Count;
        }

        public int GetBoundPlayerCount()
        {
            return playerToSceneBinding.Count;
        }
    }
}
