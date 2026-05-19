using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public static class MessageRouter
    {
        /// <summary>
        /// 构建消息处理器字典，将消息ID映射到对应的处理函数
        /// </summary>
        /// <param name="roomHandler">房间处理器实例</param>
        /// <param name="entitySyncHandler">实体同步处理器实例</param>
        /// <returns>消息处理器字典</returns>
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(RoomHandler roomHandler, EntitySyncHandler entitySyncHandler, BattleMainHandler battleMainHandler)
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
                        try
                        {
                            session.Send(packet.AsSpan(0, totalLength).ToArray());
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                        }
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

            return handlers;
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
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(responsePayload, clientSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
            try
            {
                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"Battle 向网关发送响应失败 MsgId:{msgId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}