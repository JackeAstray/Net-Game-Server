using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;
using GenFrameSync = Framework.Protocol.Generated.BattleFrameSync;

namespace Battle.Handlers
{
    public static class MessageRouter
    {
        /// <summary>
        /// 构建消息处理器字典，将消息ID映射到对应的处理函数
        /// </summary>
        /// <param name="roomHandler">房间处理器实例</param>
        /// <param name="entitySyncHandler">实体同步处理器实例</param>
        /// <param name="frameSyncManager">帧同步管理器（可为 null，禁用帧同步）</param>
        /// <returns>消息处理器字典</returns>
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(RoomHandler roomHandler, EntitySyncHandler entitySyncHandler, BattleMainHandler battleMainHandler, FrameSyncManager? frameSyncManager = null)
        {
            var handlers = new Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>();

            handlers[MessageIds.BattleJoinReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<BattleJoinRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await roomHandler.HandleJoinRequestAsync(clientSessionId, req, session);
                        SendToGateway(session, clientSessionId, MessageIds.BattleJoinRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"BattleJoinReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"BattleJoinReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.EntitySyncReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<EntitySyncRequest>(payload.Span);
                    if (req != null)
                    {
                        await entitySyncHandler.HandleEntitySyncRequestAsync(clientSessionId, req, session);
                    }
                    else
                    {
                        Shared.Log.Warning($"EntitySyncReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"EntitySyncReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            // 帧同步：客户端上报输入（若启用帧同步管理器）
            if (frameSyncManager != null)
            {
                handlers[MessageIds.BattleFrameSync] = (payload, session, clientSessionId) =>
                {
                    try
                    {
                        var req = Shared.Json.DeserializeFromUtf8Bytes<GenFrameSync>(payload.Span);
                        if (req != null)
                        {
                            frameSyncManager.EnqueueInput(clientSessionId, req);
                        }
                        else
                        {
                            Shared.Log.Warning($"BattleFrameSync 反序列化失败 ClientSessionId:{clientSessionId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Error($"BattleFrameSync 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                    }
                    return Task.CompletedTask;
                };
            }

            handlers[MessageIds.BattleLeaveRoomReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<BattleLeaveRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await roomHandler.HandleLeaveRoomRequestAsync(clientSessionId, req, session);
                        SendToGateway(session, clientSessionId, MessageIds.BattleLeaveRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"BattleLeaveRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"BattleLeaveRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.PlayerDisconnectNotif] = async (payload, session, clientSessionId) =>
            {
                roomHandler.HandleDisconnect(clientSessionId, session);
                await Task.CompletedTask;
            };

            handlers[MessageIds.CenterCreateSceneReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Center.CenterCreateSceneRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await battleMainHandler.HandleCreateSceneRequestAsync(req);
                        byte[] resPayload = Shared.Json.SerializeToUtf8Bytes(res);
                        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(MessageIds.CenterCreateSceneRes, resPayload, out int totalLength);
                        Network.PacketSender.Send(session, packet, totalLength);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCreateSceneReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCreateSceneReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterDestroySceneReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Center.CenterDestroySceneRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await battleMainHandler.HandleDestroySceneRequestAsync(req);
                        byte[] resPayload = Shared.Json.SerializeToUtf8Bytes(res);
                        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(MessageIds.CenterDestroySceneRes, resPayload, out int totalLength);
                        Network.PacketSender.Send(session, packet, totalLength);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterDestroySceneReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterDestroySceneReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            return handlers;
        }

        /// <summary>
        /// 构建基于 MessageDispatcher 的强类型处理器（对标 KBE 自动生成的处理器注册表）。
        /// 使用生成的协议消息类 + MemoryPack 二进制序列化（JSON 兼容回退），
        /// 彻底消灭手写 MsgId 分支与手动反序列化。
        /// 返回 dispatcher，未注册的 MsgId 由调用方回退旧逻辑。
        /// </summary>
        public static Framework.Protocol.MessageDispatcher BuildDispatcher(
            RoomHandler roomHandler,
            EntitySyncHandler entitySyncHandler,
            FrameSyncManager? frameSyncManager)
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
            dispatcher.RegisterSync<Framework.Protocol.Generated.ScriptAction>(
                (ctx, msg) =>
                {
                    var args = (msg.Args ?? new List<int>()).Select(a => (object)a).ToArray();
                    Battle.BattleServerApp.DispatchEntityScriptAction(msg.EntityId, msg.Method, args);
                },
                jsonFallback: true);

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

            // ==== 实体远程调用（EntityCall：91001 入 / 91002 回执，经 Center 中继） ====
            // 远程调用入（Center 中继的 91001）：本地执行，非 0 CallId 回 91002 到 Center
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityRemoteCall>(
                (ctx, msg) =>
                {
                    var result = Battle.BattleServerApp.HandleEntityRemoteCallIn(msg);
                    if (result != null)
                    {
                        Battle.BattleServerApp.SendEntityRemoteCallResult(result);
                    }
                });

            // 远程调用回执（Center 回源的 91002）：关联完成调用方回调（回执/超时）
            dispatcher.RegisterSync<Framework.Protocol.Generated.EntityRemoteCallResult>(
                (ctx, msg) =>
                {
                    Framework.Entity.EntityCallHub.HandleResult(msg);
                });

            return dispatcher;
        }

        /// <summary>
        /// 将 response 序列化为 UTF-8 JSON，附加目标客户端会话 ID，构建路由包并通过 gatewaySession 发送。
        /// </summary>
        /// <remarks>发送失败时记录错误并吞掉异常；完成后将临时缓冲区归还给 ArrayPool。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="gatewaySession">用于向网关发送已构建数据包的会话接口。</param>
        /// <param name="clientSessionId">目标客户端的会话标识符。</param>
        /// <param name="msgId">消息或路由标识符。</param>
        /// <param name="response">要序列化并发送的响应对象。</param>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, clientSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
            try
            {
                Network.PacketSender.Send(gatewaySession, packet, totalLength);
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"Battle 向网关发送响应失败 MsgId:{msgId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
        }
    }
}