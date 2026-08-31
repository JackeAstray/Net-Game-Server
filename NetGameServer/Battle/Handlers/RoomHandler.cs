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

        /// <summary>房间人数硬上限（服务端权威）：客户端可请求小于该值的容量，但不能无限制放大房间。</summary>
        private const int HardMaxPlayers = 200;

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
                // P3 修复：RoomId 可能为 null（校验不完整路径），null 上调用 Contains 抛 NRE。
                // 统一用局部非空变量，避免在 `?.` 后编译器把属性标记为可空而触发下游可空告警。
                string roomId = request.RoomId ?? string.Empty;

                // 获取请求的类型，这里默认客户端在加入请求时通过 SceneType 或是默认根据包含 World 处理，也可以像 Center 时带入 CategoryId
                bool isWorldMap = roomId.Contains("World");
                string templateId = string.IsNullOrEmpty(request.SceneType) ? (isWorldMap ? "World" : "PVP") : request.SceneType;

                // 查表获取场景模板配置
                if (!Configs.ConfigManager.SceneTemplates.TryGetValue(templateId, out var templateConfig))
                {
                    // 如果找不到先给个默认的回退处理，实际情况可能直接拒绝进入
                    templateConfig = Configs.ConfigManager.SceneTemplates.Values.FirstOrDefault();
                    Shared.Log.Warning($"Battle 房间模板未找到，使用默认模板 TemplateId:{templateId} RoomId:{request.RoomId}");
                }

                // 安全修复：客户端可请求容量但不能超过服务端硬上限（防 int.MaxValue 房间导致无界内存）
                int requestedMax = request.MaxPlayers > 0 ? request.MaxPlayers : (templateConfig?.MaxPlayers ?? 100);
                int cappedMax = Math.Min(requestedMax, HardMaxPlayers);
                if (cappedMax != requestedMax)
                {
                    Shared.Log.Warning($"Battle 房间容量被服务端上限钳制 RoomId:{request.RoomId} Requested:{requestedMax} Cap:{HardMaxPlayers}");
                }

                var sceneConfig = new SceneConfig
                {
                    SceneId = roomId,
                    Name = templateConfig?.Name ?? "默认场景",
                    SceneType = templateConfig?.SceneType ?? "Room",
                    UseAoi = templateConfig?.UseAoi ?? false,
                    GridSize = templateConfig?.GridSize ?? 50.0f,
                    MaxPlayers = cappedMax,
                    CustomRules = templateConfig?.CustomRules ?? new System.Collections.Generic.Dictionary<string, string>()
                };

                // 获取或创建场景
                var scene = sceneManager.GetOrCreateScene(sceneConfig);
                // 人数校验只统计真实玩家（场景反索引），玩法实体（NPC/Quest/Skill/Item）
                // 不占用玩家名额——此前 GetAllSessionIds() 会把玩法实体计入，导致 PVP(10)
                // 房间实际只能容纳 2 名玩家（4 场景实体 + 每人 2 个私有玩法实体）。
                if (scene.Config.MaxPlayers > 0 && sceneManager.GetPlayerCount(scene.Config.SceneId) >= scene.Config.MaxPlayers)
                {
                    return Task.FromResult(new BattleJoinResponse
                    {
                        Success = false,
                        Message = "房间人数已满"
                    });
                }

                // 将玩家绑定到该场景
                sceneManager.BindPlayerToScene(clientSessionId, roomId);

                // 基于实体框架创建玩家实体（属性脏标记 + 增量同步）
                var newPlayerEntity = Battle.Entities.PlayerEntityDef.Create(clientSessionId);

                // 崩溃恢复：若存在持久化数据，恢复玩家属性（对标 KBE restore_entity_handler）。
                // 单条加载（O(1)），替代全量目录扫描——玩家量大时加入路径不再随存档数线性变慢。
                bool recovered = false;
                try
                {
                    var persisted = Battle.BattleServerApp.LoadPersistedPlayer(clientSessionId);
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

                // 玩家实体绑定属主（OWN_CLIENT 权限属性的定向广播用）
                newPlayerEntity.OwnerClientId = clientSessionId;

                // 通知游戏逻辑脚本：实体创建（脚本可覆写初始属性/绑定玩法）
                Battle.BattleServerApp.NotifyEntityCreated(newPlayerEntity);

                // 玩家私有玩法实体（Skill/Item）：生成并绑定属主（对标 KBE 脚本 createEntity）
                Battle.BattleServerApp.SpawnPlayerGameplayEntities(scene, clientSessionId);

                // 触发进入事件，进行数据广播（全量快照 + AOI 登记）
                entitySyncHandler.OnPlayerEnter(clientSessionId, newPlayerEntity, gatewaySession);
                Battle.BattleServerApp.SyncRoomPlayerCount(roomId);

                return Task.FromResult(new BattleJoinResponse
                {
                    Success = true,
                    Message = $"加入场景 {scene.Config.Name} (类型: {scene.Config.SceneType}) 成功. AOI启用: {isWorldMap}{(recovered ? " [已恢复存档]" : "")}"
                });
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"Battle 加入场景失败 ClientSessionId:{clientSessionId} RoomId:{request?.RoomId} Exception:{ex}");
                // A1 修复：加入失败时回滚玩家-场景绑定（此前 BindPlayerToScene 在实体创建之前执行，
                // 后续任何一步抛异常都会留下"已绑定但无实体"的死绑定，泄漏进 sceneToPlayers 反索引）。
                sceneManager.UnbindPlayer(clientSessionId);
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

            // D4 孤儿回收：玩家主动离房，属主玩法实体（Skill/Item）回收防泄漏
            Battle.BattleServerApp.RecycleOwnedEntities(scene, clientSessionId);

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
        /// 处理客户端断开连接：优先走"断线挂起"（实体保留，宽限期内可重连恢复），
        /// 未启用重连或宽限超时后完整离场。
        /// </summary>
        public void HandleDisconnect(long clientSessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(clientSessionId);
            if (scene == null)
            {
                sceneManager.UnbindPlayer(clientSessionId);
                Shared.Log.Info($"玩家 {clientSessionId} 已从场景解绑并清理");
                return;
            }

            // 断线重连（对标 KBE 断线恢复）：实体挂起保留宽限期，重连后无缝续接
            if (Battle.BattleServerApp.SuspendPlayerOnDisconnect(scene, clientSessionId, gatewaySession))
            {
                return;
            }

            // 无重连支持（配置关闭）：完整离场
            Battle.BattleServerApp.LeaveScene(scene, clientSessionId, gatewaySession);
            Shared.Log.Info($"玩家 {clientSessionId} 已从场景解绑并清理");
        }
    }
}
