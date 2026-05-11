using Network;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DB.Routing
{
    public class MessageRouter
    {
        private readonly ConcurrentDictionary<int, Func<ISession, ReadOnlyMemory<byte>, Task>> handlers = new();

        public void BindServer(INetworkServer server)
        {
            server.OnDataReceived -= HandleRawData;
            server.OnDataReceived += HandleRawData;
        }

        public void RegisterHandler(int msgId, Func<ISession, ReadOnlyMemory<byte>, Task> handler)
        {
            handlers[msgId] = handler;
        }

        private async void HandleRawData(ISession session, ReadOnlyMemory<byte> data)
        {
            if (data.Length < 4) return;

            int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));

            if (handlers.TryGetValue(msgId, out var handler))
            {
                try
                {
                    await handler(session, data.Slice(4));
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"[MessageRouter] MsgId {msgId} 处理异常: {ex}");
                }
            }
            else
            {
                Shared.Log.Error($"未知的消息 ID: {msgId}");
            }
        }
    }
}