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
                // P3 加固：Center 创建场景同样校验 RoomId 非空 + 容量钳制到服务端硬上限，
                // 防止经 Center 路径创建 MaxPlayers=int.MaxValue 的房间使 RoomHandler 的 200 上限失效。
                const int HardMaxPlayers = 200;
                const int MaxScenesPerNode = 500;
                if (string.IsNullOrWhiteSpace(request.RoomId))
                {
                    return Task.FromResult(new CenterCreateSceneResponse { Success = false, RoomId = "", SceneId = "" });
                }
                if (request.RoomId.Length > 64)
                {
                    Shared.Log.Warning($"Battle 拒绝超长 RoomId Length:{request.RoomId.Length}（上限 64）");
                    return Task.FromResult(new CenterCreateSceneResponse { Success = false, RoomId = "", SceneId = "" });
                }
                // P3 修复：Center 创建场景路径同样受场景数上限约束（与 RoomHandler 两条路径一致，
                // 防经匹配流程绕过 MaxScenesPerNode 无限创建场景）。
                if (sceneManager.GetSceneCount() >= MaxScenesPerNode)
                {
                    Shared.Log.Warning($"Battle 场景数已达上限({MaxScenesPerNode})，拒绝 Center 创建场景 RoomId:{request.RoomId}");
                    return Task.FromResult(new CenterCreateSceneResponse { Success = false, RoomId = "", SceneId = "" });
                }

                var sceneConfig = new SceneConfig
                {
                    SceneId = request.RoomId,
                    Name = string.IsNullOrWhiteSpace(request.RoomName) ? $"Scene_{request.SceneType}" : request.RoomName,
                    SceneType = request.SceneType,
                    UseAoi = request.SceneType.Contains("World", StringComparison.OrdinalIgnoreCase),
                    GridSize = 50.0f,
                    MaxPlayers = request.MaxPlayers > 0 ? Math.Min(request.MaxPlayers, HardMaxPlayers) : 100,
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
                // 受影响会话含观战者（只读广播目标，同样需要收到场景销毁通知）
                long[] affectedSessionIds = sceneManager.GetSceneSessionIds(roomId);
                // P3 修复：销毁前落库场景内玩家（RemoveScene 只做脚本/备份注销，不落库，
                // 此前在线玩家未保存进度随场景销毁丢失）。
                var scene = sceneManager.GetScene(roomId);
                if (scene != null)
                {
                    foreach (var sessionId in sceneManager.GetPlayerSessionIds(roomId))
                    {
                        var entity = scene.EntityManager.GetEntity(sessionId);
                        if (entity != null)
                        {
                            Battle.BattleServerApp.PersistPlayer(entity);
                        }
                    }
                }
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