using System;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public class RoomHandler
    {
        private readonly SceneManager _sceneManager;
        private readonly EntitySyncHandler _entitySyncHandler;

        public RoomHandler(SceneManager sceneManager, EntitySyncHandler entitySyncHandler)
        {
            _sceneManager = sceneManager;
            _entitySyncHandler = entitySyncHandler;
        }

        public Task<BattleJoinResponse> HandleJoinRequestAsync(long clientSessionId, BattleJoinRequest request, Network.ISession gatewaySession)
        {
            // 判断是否包含"World"以此区分是大世界还是普通对局对战，实际可通过 request 的参数配置
            bool isWorldMap = request.RoomId.Contains("World");

            // 通过配置类来初始化或获取场景信息
            var sceneConfig = new SceneConfig
            {
                SceneId = request.RoomId,
                Name = string.IsNullOrEmpty(request.SceneName) ? (isWorldMap ? "默认大世界" : "默认房间") : request.SceneName,
                SceneType = string.IsNullOrEmpty(request.SceneType) ? (isWorldMap ? "World" : "PVP") : request.SceneType,
                UseAoi = isWorldMap,
                GridSize = 50.0f,
                MaxPlayers = request.MaxPlayers > 0 ? request.MaxPlayers : 100,
                CustomRules = request.CustomRules ?? new Dictionary<string, string>()
            };

            // 获取或创建场景
            var scene = _sceneManager.GetOrCreateScene(sceneConfig);

            // 将玩家绑定到该场景
            _sceneManager.BindPlayerToScene(clientSessionId, request.RoomId);

            var newPlayerState = new EntityState
            {
                EntityId = clientSessionId,
                Nickname = $"Player_{clientSessionId % 1000}", 
                Hp = 100,
                MaxHp = 100,
                Score = 0,
                Position = new Vector3(0, 0, 0),
                Rotation = new Vector3(0, 0, 0)
            };

            // 触发进入事件，进行数据广播
            _entitySyncHandler.OnPlayerEnter(clientSessionId, newPlayerState, gatewaySession);

            return Task.FromResult(new BattleJoinResponse
            {
                Success = true,
                Message = $"Joined scene {scene.Config.Name} (Type: {scene.Config.SceneType}) successfully. AoiEnabled: {isWorldMap}"
            });
        }
    }
}
