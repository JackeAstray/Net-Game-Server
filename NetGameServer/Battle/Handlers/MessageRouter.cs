using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public static class MessageRouter
    {
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(RoomHandler roomHandler, EntitySyncHandler entitySyncHandler)
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

            return handlers;
        }

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
