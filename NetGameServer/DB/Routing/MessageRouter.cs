using Network;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DB.Routing
{
    public class MessageRouter
    {
        private readonly ConcurrentDictionary<int, Func<ISession, ReadOnlyMemory<byte>, Task>> handlers = new();

        /// <summary>
        /// 绑定服务器：将消息路由器绑定到网络服务器的事件上，以便在接收到数据时能够正确地处理消息。
        /// </summary>
        /// <param name="server"></param>
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
        /// 处理原始数据：严格按统一协议 [MsgId(4)][RequestId(8)][Payload] 解析并分发。
        /// 响应统一回写为 [MsgId(4)][RequestId(8)][Payload]。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="data">接收到的原始数据。</param>
        private async void HandleRawData(ISession session, ReadOnlyMemory<byte> data)
        {
            if (!Network.Routing.PacketBuilder.TryParseDbPacket(data, out int msgId, out long requestId, out ReadOnlyMemory<byte> payload))
            {
                Shared.Log.Error($"[MessageRouter] 收到非法 DB 协议包，长度不足 12，实际: {data.Length}");
                return;
            }

            if (handlers.TryGetValue(msgId, out var handler))
            {
                try
                {
                    var targetSession = new RequestContextSession(session, requestId);
                    await handler(targetSession, payload);
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

        private sealed class RequestContextSession : ISession
        {
            private readonly ISession inner;
            private readonly long requestId;

            public RequestContextSession(ISession inner, long requestId)
            {
                this.inner = inner;
                this.requestId = requestId;
            }

            public long SessionId => inner.SessionId;
            public System.Net.EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
            public bool IsConnected => inner.IsConnected;
            public DateTime LastActivityTime => inner.LastActivityTime;
            public object? UserData
            {
                get => inner.UserData;
                set => inner.UserData = value;
            }

            public void Send(ReadOnlyMemory<byte> data)
            {
                if (data.Length < 4)
                {
                    inner.Send(data);
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                var payload = data.Span.Slice(4);

                byte[] packet = Network.Routing.PacketBuilder.BuildDbRequestPacket(msgId, requestId, payload);
                inner.Send(packet);
            }

            public void Close() => inner.Close();
        }
    }
}
