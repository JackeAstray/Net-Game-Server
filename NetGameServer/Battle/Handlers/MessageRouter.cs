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
                roomHandler.HandleDisconnect(clientSessionId);
                await Task.CompletedTask;
            };

            handlers[MessageIds.CenterCreateSceneReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Center.CenterCreateSceneRequest>(payload.Span);
                if (req != null)
                {
                    var res = await battleMainHandler.HandleCreateSceneRequestAsync(req);
                    // 0 is for internal server communication
                    byte[] resPayload = Shared.Json.SerializeToUtf8Bytes(res);
                    byte[] packet = Network.Routing.PacketBuilder.BuildSessionWrapperPacket(0, MessageIds.CenterCreateSceneRes, resPayload);
                    session.Send(packet);
                }
            };

            return handlers;
        }

        /// <summary>
        /// 发送响应消息到网关服务器，消息格式为：前8字节为客户端SessionId，接着4字节为消息ID，剩余部分为JSON序列化的响应数据
        /// </summary>
        /// <typeparam name="T">响应数据类型</typeparam>
        /// <param name="gatewaySession">网关会话实例</param>
        /// <param name="clientSessionId">客户端会话ID</param>
        /// <param name="msgId">消息ID</param>
        /// <param name="response">响应数据</param>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] packet = new byte[12 + responsePayload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), clientSessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), msgId);
            responsePayload.CopyTo(packet.AsSpan(12));
            gatewaySession.Send(packet);
        }
    }
}