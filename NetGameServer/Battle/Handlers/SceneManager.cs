using System.Collections.Concurrent;

namespace Battle.Handlers
{
    /// <summary>
    /// 统一管理战斗节点上的所有 Scene(房间/大地图)
    /// </summary>
    public class SceneManager
    {
        private readonly ConcurrentDictionary<string, BattleScene> _scenes = new();

        // 玩家到场景的映射，用来快速路由玩家消息到所在场景
        private readonly ConcurrentDictionary<long, string> _playerToSceneBinding = new();

        public BattleScene GetOrCreateScene(string sceneId, bool useAoi = true, float gridSize = 50.0f)
        {
            return _scenes.GetOrAdd(sceneId, id => new BattleScene(id, useAoi, gridSize));
        }

        public BattleScene? GetScene(string sceneId)
        {
            _scenes.TryGetValue(sceneId, out var scene);
            return scene;
        }

        public void BindPlayerToScene(long sessionId, string sceneId)
        {
            _playerToSceneBinding[sessionId] = sceneId;
        }

        public void UnbindPlayer(long sessionId)
        {
            _playerToSceneBinding.TryRemove(sessionId, out _);
        }

        public BattleScene? GetSceneByPlayer(long sessionId)
        {
            if (_playerToSceneBinding.TryGetValue(sessionId, out var sceneId))
            {
                return GetScene(sceneId);
            }
            return null;
        }
    }
}