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

        /// <summary>
        /// 注册消息处理函数：将消息 ID 与对应的处理函数关联起来。
        /// </summary>
        /// <param name="msgId">消息 ID。</param>
        /// <param name="handler">处理函数。</param>
        public void RegisterHandler(int msgId, Func<ISession, ReadOnlyMemory<byte>, Task> handler)
        {
            handlers[msgId] = handler;
        }

        /// <summary>
        /// 处理原始数据：从数据中解析出消息 ID，并调用对应的处理函数。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="data">接收到的原始数据。</param>
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