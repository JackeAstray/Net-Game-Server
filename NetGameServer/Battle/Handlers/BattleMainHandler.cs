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
        /// 处理创建场景请求，基于请求参数构造场景配置并通过场景管理器获取或创建场景，返回创建结果。
        /// </summary>
        /// <remarks>内部捕获并记录异常，方法通过 Task.FromResult 返回同步完成的任务；实际场景由 sceneManager.GetOrCreateScene
        /// 获取或创建。</remarks>
        /// <param name="request">包含场景创建所需的信息（例如 RoomId、SceneType、IsPrivate），用于构建 SceneConfig 并决定是否使用 AOI 与私有设置。</param>
        /// <returns>表示 CenterCreateSceneResponse 的任务，Success 指示操作是否成功；成功时包含 RoomId、SceneId 与 BattleNodeId，失败时 Success 为 false 且
        /// SceneId 为空。</returns>
        public Task<CenterCreateSceneResponse> HandleCreateSceneRequestAsync(CenterCreateSceneRequest request)
        {
            try
            {
                var sceneConfig = new SceneConfig
                {
                    SceneId = request.RoomId,
                    Name = string.IsNullOrWhiteSpace(request.RoomName) ? $"Scene_{request.SceneType}" : request.RoomName,
                    SceneType = request.SceneType,
                    UseAoi = request.SceneType.Contains("World", StringComparison.OrdinalIgnoreCase),
                    GridSize = 50.0f,
                    MaxPlayers = request.MaxPlayers > 0 ? request.MaxPlayers : 100,
                    IsPrivate = request.IsPrivate
                };

                sceneManager.GetOrCreateScene(sceneConfig);

                return Task.FromResult(new CenterCreateSceneResponse
                {
                    Success = true,
                    RoomId = request.RoomId,
                    SceneId = request.RoomId,
                    BattleNodeId = Battle.BattleServerApp.CurrentNodeId
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

        public Task<CenterDestroySceneResponse> HandleDestroySceneRequestAsync(CenterDestroySceneRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.RoomId))
                {
                    return Task.FromResult(new CenterDestroySceneResponse
                    {
                        Success = false,
                        Message = "RoomId 不能为空"
                    });
                }

                string roomId = request.RoomId.Trim();
                long[] affectedSessionIds = sceneManager.GetPlayerSessionIds(roomId);
                int removedPlayers = sceneManager.UnbindPlayersInScene(roomId);
                sceneManager.RemoveScene(roomId);

                return Task.FromResult(new CenterDestroySceneResponse
                {
                    Success = true,
                    RoomId = roomId,
                    Message = $"房间已销毁，清理玩家数: {removedPlayers}",
                    AffectedSessionIds = affectedSessionIds
                });
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"销毁场景失败 {request.RoomId}: {ex.Message}");
                return Task.FromResult(new CenterDestroySceneResponse
                {
                    Success = false,
                    RoomId = request.RoomId,
                    Message = "房间销毁失败",
                    AffectedSessionIds = Array.Empty<long>()
                });
            }
        }
    }
}