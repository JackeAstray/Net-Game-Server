using System;
using System.Threading.Tasks;
using Shared.Messages.Center;
using Shared.Messages;

namespace Battle.Handlers
{
    public class BattleMainHandler
    {
        private readonly SceneManager sceneManager;

        public BattleMainHandler(SceneManager sceneManager)
        {
            this.sceneManager = sceneManager;
        }

        /// <summary>
        /// 处理中心服务器的创建场景请求，基于请求参数创建一个新的场景，并返回结果给中心服务器。
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public Task<CenterCreateSceneResponse> HandleCreateSceneRequestAsync(CenterCreateSceneRequest request)
        {
            try
            {
                var sceneConfig = new SceneConfig
                {
                    SceneId = request.RoomId,
                    Name = $"Scene_{request.SceneType}",
                    SceneType = request.SceneType,
                    UseAoi = request.SceneType.Contains("World", StringComparison.OrdinalIgnoreCase), // 示例：世界地图使用AOI
                    GridSize = 50.0f,
                    MaxPlayers = 100,
                    IsPrivate = request.IsPrivate
                };

                sceneManager.GetOrCreateScene(sceneConfig);

                return Task.FromResult(new CenterCreateSceneResponse
                {
                    Success = true,
                    RoomId = request.RoomId,
                    SceneId = request.RoomId,
                    BattleNodeId = Shared.ConfigHelper.GetConfig<string>("ServerId") ?? "Battle_1"
                });
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"创建场景失败 {request.RoomId}: {ex.Message}");
                return Task.FromResult(new CenterCreateSceneResponse
                {
                    Success = false,
                    RoomId = request.RoomId,
                    SceneId = ""
                });
            }
        }
    }
}