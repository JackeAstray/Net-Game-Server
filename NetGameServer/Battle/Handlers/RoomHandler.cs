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

            // 获取或创建场景（例如 World_01 对应一个大的网格地图，而 Room_XXX 对局用小房间无 AOI）
            var scene = _sceneManager.GetOrCreateScene(request.RoomId, useAoi: isWorldMap, gridSize: 50.0f);

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
                Message = $"Joined scene {request.RoomId} successfully. AoiEnabled: {isWorldMap}"
            });
        }
    }
}
