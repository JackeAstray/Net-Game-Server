using System;
using System.Collections.Generic;
using System.Linq;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public static class MessageRouter
    {
        /// <summary>ScriptAction 单次调用参数个数上限（防大列表反序列化 DoS）。</summary>
        private const int MaxScriptActionArgs = 32;
        /// <summary>
        /// 构建基于 MessageDispatcher 的强类型处理器（对标 KBE 自动生成的处理器注册表）。
        /// 使用生成的协议消息类 + MemoryPack 二进制序列化（JSON 兼容回退），
        /// 彻底消灭手写 MsgId 分支与手动反序列化。
        /// 未注册的 MsgId 由调用方回退旧逻辑。
        /// </summary>
        public static Framework.Protocol.MessageDispatcher BuildDispatcher(
            RoomHandler roomHandler,
            EntitySyncHandler entitySyncHandler,
            BattleMainHandler battleMainHandler,
            FrameSyncManager? frameSyncManager,
            TimeSyncManager? timeSyncManager = null)
        {
            var dispatcher = new Framework.Protocol.MessageDispatcher();

            // 加入房间（双格式兼容：旧客户端 JSON / 新客户端 MemoryPack）
            dispatcher.RegisterSync<Framework.Protocol.Generated.BattleJoin>(
                (ctx, msg) =>
                {
                    var req = new BattleJoinRequest
                    {
                        RoomId = msg.RoomId,
                        SceneName = msg.SceneName,
                        SceneType = msg.SceneType,
                        MaxPlayers = msg.MaxPlayers,
                        CustomRules = msg.CustomRules
                    };
                    var gatewaySession = ((BattleSessionContext)ctx).GatewaySession;
                    var res = roomHandler.HandleJoinRequestAsync(ctx.ClientSessionId, req, gatewaySession).GetAwaiter().GetResult();
                    var resMsg = new Framework.Protocol.Generated.BattleJoinResult
                    {
                        Success = res.Success,
                        Message = res.Message
                    };
                    ctx.Send(resMsg);
                },
                jsonFallback: true);

            // 离开房间
            dispatcher.RegisterSync<Framework.Protocol.Generated.BattleLeaveRoom>(
                (ctx, msg) =>
                {
                    var req = new BattleLeaveRoomRequest { RoomId = msg.RoomId };
                    var gatewaySession = ((BattleSessionContext)ctx).GatewaySession;
                    var res = roomHandler.HandleLeaveRoomRequestAsync(ctx.ClientSessionId, req, gatewaySession).GetAwaiter().GetResult();
                    var resMsg = new Framework.Protocol.Generated.BattleLeaveRoomResult
                    {
                        Success = res.Success,
                        RoomId = res.RoomId,
                        Message = res.Message
                    };
                    ctx.Send(resMsg);
                },
                jsonFallback: true);

            // 实体状态同步（位置/朝向上报 → 增量广播）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntitySync>(
                (ctx, msg) =>
                {
                    var req = new EntitySyncRequest
                    {
                        Position = new Vector3 { X = msg.Position?.X ?? 0, Y = msg.Position?.Y ?? 0, Z = msg.Position?.Z ?? 0 },
                        Rotation = new Vector3 { X = msg.Rotation?.X ?? 0, Y = msg.Rotation?.Y ?? 0, Z = msg.Rotation?.Z ?? 0 }
                    };
                    var gatewaySession = ((BattleSessionContext)ctx).GatewaySession;
                    entitySyncHandler.HandleEntitySyncRequestAsync(ctx.ClientSessionId, req, gatewaySession).GetAwaiter().GetResult();
                },
                jsonFallback: true);

            // 帧同步输入上报（tick 引擎驱动）
            if (frameSyncManager != null)
            {
                dispatcher.RegisterSync<Framework.Protocol.Generated.BattleFrameSync>(
                    (ctx, msg) =>
                    {
                        frameSyncManager.EnqueueInput(ctx.ClientSessionId, msg);
                    },
                    jsonFallback: true);
            }

            // 玩家断线通知（网关内部消息）
            dispatcher.RegisterSync<Framework.Protocol.Generated.PlayerDisconnect>(
                (ctx, msg) =>
                {
                    var gatewaySession = ((BattleSessionContext)ctx).GatewaySession;
                    roomHandler.HandleDisconnect(ctx.ClientSessionId, gatewaySession);
                },
                jsonFallback: true);

            // 通用实体脚本动作：客户端按实体 ID 调用脚本 OnMessage
            // （如 TakeDamage / CastSkill / Pickup / UseItem / QueryProgress，参数为 int32 列表）
            // 鉴权在 BattleServerApp.DispatchEntityScriptAction 内完成（场景归属 + 属主/白名单）。
            dispatcher.RegisterSync<Framework.Protocol.Generated.ScriptAction>(
                (ctx, msg) =>
                {
                    var raw = msg.Args;
                    if (raw != null && raw.Count > MaxScriptActionArgs)
                    {
                        Shared.Log.Warning($"实体脚本动作参数过多被拒绝 SessionId:{ctx.ClientSessionId} EntityId:{msg.EntityId} Method:{msg.Method} Args:{raw.Count}");
                        return;
                    }
                    var args = (raw ?? new List<int>()).Select(a => (object)a).ToArray();
                    Battle.BattleServerApp.DispatchEntityScriptAction(ctx.ClientSessionId, msg.EntityId, msg.Method, args);
                },
                jsonFallback: true);

            // 时间同步（KBE-Gap-Review D7）：客户端发 ClientTimeSync → 服务端回 ServerTimeSync
            // 客户端按 NTP 公式估算 RTT/offset，多点采样取中位数更稳。
            if (timeSyncManager != null)
            {
                dispatcher.RegisterSync<Framework.Protocol.Generated.ClientTimeSync>(
                    (ctx, msg) =>
                    {
                        var res = timeSyncManager.HandleSync(msg);
                        ctx.Send(res);
                    },
                    jsonFallback: true);
            }

            // 断线重连恢复（网关内部消息）：取消实体挂起，恢复在线（对标 KBE 断线恢复）
            dispatcher.RegisterSync<Framework.Protocol.Generated.PlayerSessionResume>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.ResumePlayer(ctx.ClientSessionId);
                },
                jsonFallback: true);

            // ==== 实体在线迁移（C2 第二阶段：冻结-序列化-搬迁-恢复） ====
            // 迁移入（Center 中继的 91003）：目标 Battle 恢复实体并回 91004
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityMigrateRequest>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.HandleEntityMigrateIn(msg);
                });

            // 迁移出结果（Center 回源的 91004）：成功移除本地实体，失败回滚解冻
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityMigrateResult>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.HandleEntityMigrateOutResult(msg);
                });

            // 迁移命令（Center 下发的 91006）：触发指定会话迁往目标节点（负载均衡/管理侧）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityMigrateCommand>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.StartEntityMigration(msg.ClientSessionId, msg.TargetNodeId);
                });

            // 迁移路由完成（Center 广播 91005）：更新调用方路由缓存，指向实体新所在节点（对标 ET Location）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityMigrateRouted>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.OnEntityMigratedRouted(msg.ClientSessionId, msg.NewNodeId);
                });

            // ==== 实体远程调用（EntityCall：91001 入 / 91002 回执） ====
            // 远程调用入（Center 中继或 Battle 直达的 91001）：本地执行，非 0 CallId 回 91002 到来源会话
            // （Center 中继来的 → 回 Center 回源；Battle 直达会话来的 → 直接回目标 Battle）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityRemoteCall>(
                (ctx, msg) =>
                {
                    var result = Battle.BattleServerApp.HandleEntityRemoteCallIn(msg);
                    if (result != null)
                    {
                        Battle.BattleServerApp.SendEntityRemoteCallResult(result, ((BattleSessionContext)ctx).GatewaySession);
                    }
                });

            // 远程调用回执（Center 回源的 91002）：关联完成调用方回调（回执/超时）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityRemoteCallResult>(
                (ctx, msg) =>
                {
                    Framework.Entity.EntityCallHubRegistry.Default.HandleResult(msg);
                });

            // 实体位置查询响应（Center 回 91010）：更新路由缓存；直达开启时预热直连会话
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityLocateResponse>(
                (ctx, msg) =>
                {
                    Battle.BattleServerApp.HandleEntityLocationResponse(msg);
                });

            // 创建场景（Center 内部消息 90003，迁移自旧 JSON 路由）：回 90004 到 Center
            dispatcher.RegisterSync<Framework.Protocol.Generated.CenterCreateScene>(
                (ctx, msg) =>
                {
                    var req = new Shared.Messages.Center.CenterCreateSceneRequest
                    {
                        RoomId = msg.RoomId,
                        RoomName = msg.RoomName,
                        SceneType = msg.SceneType,
                        IsPrivate = msg.IsPrivate,
                        MaxPlayers = msg.MaxPlayers
                    };
                    var res = battleMainHandler.HandleCreateSceneRequestAsync(req).GetAwaiter().GetResult();
                    SendRawToSession(((BattleSessionContext)ctx).GatewaySession, MessageIds.CenterCreateSceneRes,
                        new Framework.Protocol.Generated.CenterCreateSceneResult
                        {
                            Success = res.Success,
                            RoomId = res.RoomId,
                            SceneId = res.SceneId,
                            BattleNodeId = res.BattleNodeId,
                            Message = res.Success ? string.Empty : "创建场景失败"
                        });
                },
                jsonFallback: true);

            // 销毁场景（Center 内部消息 90006，迁移自旧 JSON 路由）：回 90007 到 Center
            dispatcher.RegisterSync<Framework.Protocol.Generated.CenterDestroyScene>(
                (ctx, msg) =>
                {
                    var req = new Shared.Messages.Center.CenterDestroySceneRequest { RoomId = msg.RoomId };
                    var res = battleMainHandler.HandleDestroySceneRequestAsync(req).GetAwaiter().GetResult();
                    SendRawToSession(((BattleSessionContext)ctx).GatewaySession, MessageIds.CenterDestroySceneRes,
                        new Framework.Protocol.Generated.CenterDestroySceneResult
                        {
                            Success = res.Success,
                            RoomId = res.RoomId,
                            Message = res.Message,
                            AffectedSessionIds = res.AffectedSessionIds.ToList()
                        });
                },
                jsonFallback: true);

            return dispatcher;
        }

        /// <summary>
        /// 向指定会话发送 MemoryPack 序列化的内部消息包（不带客户端路由元数据，Center 内部消息回包用）。
        /// </summary>
        /// <param name="session">目标会话（如 Center 节点的连接会话）。</param>
        /// <param name="msgId">消息标识符。</param>
        /// <param name="message">要序列化并发送的消息对象。</param>
        private static void SendRawToSession(Network.ISession session, int msgId, Framework.Protocol.IGameMessage message)
        {
            byte[] payload = message.Serialize();
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
            Network.PacketSender.Send(session, packet, totalLength);
        }
    }
}