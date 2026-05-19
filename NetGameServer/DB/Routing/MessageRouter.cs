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
        /// 异步解析原始数据库协议数据，读取小端序的消息 ID（前 4 字节）与可选请求 ID，并将剩余负载转发给已注册的消息处理器。
        /// </summary>
        /// <remarks>当数据长度不足 4 字节或消息 ID 未注册时记录错误。会尝试从负载中提取请求 ID；若提取成功，会用带有该请求 ID 的 RequestContextSession
        /// 调用处理器。内部捕获并记录处理器或解析过程中的异常，不会将异常抛出给调用者。</remarks>
        /// <param name="session">用于创建请求上下文的会话对象，作为消息处理器的目标会话。</param>
        /// <param name="data">包含消息 ID（前 4 字节，小端序）后接有效负载的原始字节序列。</param>
        private async void HandleRawData(ISession session, ReadOnlyMemory<byte> data)
        {
            try
            {
                if (data.Length < 4)
                {
                    Shared.Log.Error($"[MessageRouter] 收到非法 DB 协议包，长度不足 4，实际: {data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                ReadOnlyMemory<byte> payload = data.Slice(4);

                long requestId = 0;
                if (Shared.RouteMetadata.TryExtractRequestId(payload, out long extractedRequestId, out var cleanPayload))
                {
                    requestId = extractedRequestId;
                    payload = cleanPayload;
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
            catch (Exception ex)
            {
                Shared.Log.Error($"[MessageRouter] 处理原始 DB 数据时发生异常: {ex}");
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

            /// <summary>
            /// 发送指定字节数据：若 requestId<=0 或 数据长度小于 4 字节则透传原始数据；否则将前 4 字节按小端解析为消息 ID，向负载附加请求 ID，构建路由数据包并发送。
            /// </summary>
            /// <remarks>使用 Shared.RouteMetadata.AttachRequestId 向负载附加请求 ID，并通过
            /// Network.Routing.PacketBuilder.BuildPacket 构建数据包。仅发送 BuildPacket 返回的 totalLength 字节，并在 finally 中将租用的数组归还到
            /// ArrayPool<byte>.Shared。对于 requestId<=0 或短包直接调用内部发送器。</remarks>
            /// <param name="data">要发送的只读字节缓冲区；前 4 字节（小端）为消息 ID，其余为负载；长度小于 4 字节时按原样透传。</param>
            public void Send(ReadOnlyMemory<byte> data)
            {
                if (requestId <= 0)
                {
                    inner.Send(data);
                    return;
                }

                if (data.Length < 4)
                {
                    inner.Send(data);
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                byte[] payload = data.Slice(4).ToArray();
                byte[] payloadWithRequestId = Shared.RouteMetadata.AttachRequestId(payload, requestId);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadWithRequestId, out int totalLength);
                try
                {
                    inner.Send(packet.AsSpan(0, totalLength).ToArray());
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                }
            }

            public void Close() => inner.Close();
        }
    }
}