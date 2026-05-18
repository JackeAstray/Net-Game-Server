using System;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public class RoomHandler
    {
        private readonly SceneManager sceneManager;
        private readonly EntitySyncHandler entitySyncHandler;

        public RoomHandler(SceneManager sceneManager, EntitySyncHandler entitySyncHandler)
        {
            this.sceneManager = sceneManager;
            this.entitySyncHandler = entitySyncHandler;
        }

        /// <summary>
        /// 处理玩家加入请求，动态创建或获取场景，并将玩家绑定到场景中。
        /// 根据请求中的 SceneType 或 RoomId 判断是否启用 AOI，并根据配置表获取场景模板参数进行场景创建。
        /// </summary>
        /// <param name="clientSessionId"></param>
        /// <param name="request"></param>
        /// <param name="gatewaySession"></param>
        /// <returns></returns>
        public Task<BattleJoinResponse> HandleJoinRequestAsync(long clientSessionId, BattleJoinRequest request, Network.ISession gatewaySession)
        {
            // 获取请求的类型，这里默认客户端在加入请求时通过 SceneType 或是默认根据包含 World 处理，也可以像 Center 时带入 CategoryId
            bool isWorldMap = request.RoomId.Contains("World");
            string templateId = string.IsNullOrEmpty(request.SceneType) ? (isWorldMap ? "World" : "PVP") : request.SceneType;

            // 查表获取场景模板配置
            if (!Configs.ConfigManager.SceneTemplates.TryGetValue(templateId, out var templateConfig))
            {
                // 如果找不到先给个默认的回退处理，实际情况可能直接拒绝进入
                templateConfig = Configs.ConfigManager.SceneTemplates.Values.FirstOrDefault();
            }

            var sceneConfig = new SceneConfig
            {
                SceneId = request.RoomId,
                Name = templateConfig?.Name ?? "默认场景",
                SceneType = templateConfig?.SceneType ?? "Room",
                UseAoi = templateConfig?.UseAoi ?? false,
                GridSize = templateConfig?.GridSize ?? 50.0f,
                MaxPlayers = request.MaxPlayers > 0 ? request.MaxPlayers : (templateConfig?.MaxPlayers ?? 100),
                CustomRules = templateConfig?.CustomRules ?? new System.Collections.Generic.Dictionary<string, string>()
            };

            // 获取或创建场景
            var scene = sceneManager.GetOrCreateScene(sceneConfig);

            // 将玩家绑定到该场景
            sceneManager.BindPlayerToScene(clientSessionId, request.RoomId);

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
            entitySyncHandler.OnPlayerEnter(clientSessionId, newPlayerState, gatewaySession);

            return Task.FromResult(new BattleJoinResponse
            {
                Success = true,
                Message = $"加入场景 {scene.Config.Name} (类型: {scene.Config.SceneType}) 成功. AOI启用: {isWorldMap}"
            });
        }

        /// <summary>
        /// 处理玩家断线清理
        /// </summary>
        /// <param name="clientSessionId">玩家客户端会话 ID</param>
        public void HandleDisconnect(long clientSessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(clientSessionId);
            if (scene != null)
            {
                entitySyncHandler.OnPlayerLeave(clientSessionId, gatewaySession);
            }
            else
            {
                sceneManager.UnbindPlayer(clientSessionId);
            }

            Shared.Log.Info($"玩家 {clientSessionId} 已从场景解绑并清理");
        }
    }
}
