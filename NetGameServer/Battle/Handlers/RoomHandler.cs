using System;
using System.Linq;
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
            try
            {
                // 获取请求的类型，这里默认客户端在加入请求时通过 SceneType 或是默认根据包含 World 处理，也可以像 Center 时带入 CategoryId
                bool isWorldMap = request.RoomId.Contains("World");
                string templateId = string.IsNullOrEmpty(request.SceneType) ? (isWorldMap ? "World" : "PVP") : request.SceneType;

                // 查表获取场景模板配置
                if (!Configs.ConfigManager.SceneTemplates.TryGetValue(templateId, out var templateConfig))
                {
                    // 如果找不到先给个默认的回退处理，实际情况可能直接拒绝进入
                    templateConfig = Configs.ConfigManager.SceneTemplates.Values.FirstOrDefault();
                    Shared.Log.Warning($"Battle 房间模板未找到，使用默认模板 TemplateId:{templateId} RoomId:{request.RoomId}");
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
                if (scene.Config.MaxPlayers > 0 && scene.EntityManager.GetAllSessionIds().Count() >= scene.Config.MaxPlayers)
                {
                    return Task.FromResult(new BattleJoinResponse
                    {
                        Success = false,
                        Message = "房间人数已满"
                    });
                }

                // 将玩家绑定到该场景
                sceneManager.BindPlayerToScene(clientSessionId, request.RoomId);

                // 基于实体框架创建玩家实体（属性脏标记 + 增量同步）
                var newPlayerEntity = Battle.Entities.PlayerEntityDef.Create(clientSessionId);

                // 崩溃恢复：若存在持久化数据，恢复玩家属性（对标 KBE restore_entity_handler）
                bool recovered = false;
                try
                {
                    var persisted = Battle.BattleServerApp.RestorePersistedPlayers()
                        .FirstOrDefault(e => e.EntityId == clientSessionId);
                    if (persisted != null)
                    {
                        newPlayerEntity = persisted;
                        recovered = true;
                        Shared.Log.Info($"玩家实体从持久化恢复 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"玩家实体恢复失败，使用默认属性 ClientSessionId:{clientSessionId} Exception:{ex.Message}");
                }

                // 通知游戏逻辑脚本：实体创建（脚本可覆写初始属性/绑定玩法）
                Battle.BattleServerApp.NotifyEntityCreated(newPlayerEntity);

                // 触发进入事件，进行数据广播（全量快照 + AOI 登记）
                entitySyncHandler.OnPlayerEnter(clientSessionId, newPlayerEntity, gatewaySession);
                Battle.BattleServerApp.SyncRoomPlayerCount(request.RoomId);

                return Task.FromResult(new BattleJoinResponse
                {
                    Success = true,
                    Message = $"加入场景 {scene.Config.Name} (类型: {scene.Config.SceneType}) 成功. AOI启用: {isWorldMap}{(recovered ? " [已恢复存档]" : "")}"
                });
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"Battle 加入场景失败 ClientSessionId:{clientSessionId} RoomId:{request?.RoomId} Exception:{ex}");
                return Task.FromResult(new BattleJoinResponse
                {
                    Success = false,
                    Message = "加入场景失败"
                });
            }
        }

        public Task<BattleLeaveRoomResponse> HandleLeaveRoomRequestAsync(long clientSessionId, BattleLeaveRoomRequest request, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(clientSessionId);
            if (scene == null)
            {
                return Task.FromResult(new BattleLeaveRoomResponse
                {
                    Success = false,
                    RoomId = request?.RoomId ?? string.Empty,
                    Message = "玩家当前不在房间中"
                });
            }

            string roomId = scene.SceneId;
            if (!string.IsNullOrWhiteSpace(request?.RoomId) && !string.Equals(roomId, request.RoomId, StringComparison.Ordinal))
            {
                return Task.FromResult(new BattleLeaveRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = "请求房间与当前所在房间不一致"
                });
            }

            // 离开前持久化保存玩家属性（对标 KBE 实体落库，崩溃后可恢复）
            var leavingEntity = scene.EntityManager.GetEntity(clientSessionId);
            if (leavingEntity != null)
            {
                Battle.BattleServerApp.PersistPlayer(leavingEntity);
                Battle.BattleServerApp.NotifyEntityDestroyed(leavingEntity);
            }

            entitySyncHandler.OnPlayerLeave(clientSessionId, gatewaySession);
            Battle.BattleServerApp.SyncRoomPlayerCount(roomId);
            Battle.BattleServerApp.SyncRoomMemberLeave(roomId, clientSessionId);

            return Task.FromResult(new BattleLeaveRoomResponse
            {
                Success = true,
                RoomId = roomId,
                Message = "已退出房间"
            });
        }

        /// <summary>
        /// 处理客户端断开连接：根据会话 ID 从场景解绑玩家并执行离开或清理逻辑。
        /// </summary>
        /// <remarks>如果玩家仍在场景内，调用 OnPlayerLeave 触发离开同步；否则解除玩家与场景的绑定并记录信息日志。</remarks>
        /// <param name="clientSessionId">断开连接的玩家会话 ID。</param>
        /// <param name="gatewaySession">对应的网关会话（实现 Network.ISession），用于执行离场通知和清理操作。</param>
        public void HandleDisconnect(long clientSessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(clientSessionId);
            if (scene != null)
            {
                string roomId = scene.SceneId;
                var entity = scene.EntityManager.GetEntity(clientSessionId);
                if (entity != null)
                {
                    // 断开时持久化保存（崩溃/断线后可恢复）
                    Battle.BattleServerApp.PersistPlayer(entity);
                    Battle.BattleServerApp.NotifyEntityDestroyed(entity);
                }
                entitySyncHandler.OnPlayerLeave(clientSessionId, gatewaySession);
                Battle.BattleServerApp.SyncRoomPlayerCount(roomId);
                Battle.BattleServerApp.SyncRoomMemberLeave(roomId, clientSessionId);
            }
            else
            {
                sceneManager.UnbindPlayer(clientSessionId);
            }

            Shared.Log.Info($"玩家 {clientSessionId} 已从场景解绑并清理");
        }
    }
}
