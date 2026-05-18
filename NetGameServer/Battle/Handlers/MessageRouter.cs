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
                var req = Shared.Json.DeserializeFromUtf8Bytes<BattleJoinRequest>(payload.Span);
                if (req != null)
                {
                    var res = await roomHandler.HandleJoinRequestAsync(clientSessionId, req, session);
                    SendToGateway(session, clientSessionId, MessageIds.BattleJoinRes, res);
                }
            };

            handlers[MessageIds.EntitySyncReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<EntitySyncRequest>(payload.Span);
                if (req != null)
                {
                    await entitySyncHandler.HandleEntitySyncRequestAsync(clientSessionId, req, session);
                }
            };

            handlers[MessageIds.PlayerDisconnectNotif] = async (payload, session, clientSessionId) =>
            {
                roomHandler.HandleDisconnect(clientSessionId, session);
                await Task.CompletedTask;
            };

            handlers[MessageIds.CenterCreateSceneReq] = async (payload, session, clientSessionId) =>
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
            };

            return handlers;
        }

        /// <summary>
        /// 发送响应消息到网关服务器，统一协议 [MsgId][Payload]，路由信息通过 payload 元数据传递。
        /// </summary>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(responsePayload, clientSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
            try
            {
                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}