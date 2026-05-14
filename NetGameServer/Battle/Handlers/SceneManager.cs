using System.Collections.Concurrent;

namespace Battle.Handlers
{
    /// <summary>
    /// 统一管理战斗节点上的所有 Scene(房间/大地图)
    /// </summary>
    public class SceneManager
    {
        private readonly ConcurrentDictionary<string, BattleScene> scenes = new();

        // 玩家到场景的映射，用来快速路由玩家消息到所在场景
        private readonly ConcurrentDictionary<long, string> playerToSceneBinding = new();

        public BattleScene GetOrCreateScene(SceneConfig config)
        {
            return scenes.GetOrAdd(config.SceneId, _ => new BattleScene(config));
        }

        public BattleScene? GetScene(string sceneId)
        {
            scenes.TryGetValue(sceneId, out var scene);
            return scene;
        }

        public void BindPlayerToScene(long sessionId, string sceneId)
        {
            playerToSceneBinding[sessionId] = sceneId;
        }

        public void UnbindPlayer(long sessionId)
        {
            playerToSceneBinding.TryRemove(sessionId, out _);
        }

        public BattleScene? GetSceneByPlayer(long sessionId)
        {
            if (playerToSceneBinding.TryGetValue(sessionId, out var sceneId))
            {
                return GetScene(sceneId);
            }
            return null;
        }

        public void RemoveScene(string sceneId)
        {
            if (scenes.TryRemove(sceneId, out var removedScene))
            {
                Shared.Log.Info($" scene removed and cleaned up: {sceneId}");
            }
        }
    }
}