using Network;

namespace DB.Routing
{
    /// <summary>
    /// 带请求上下文的会话包装（ISession 适配）：
    /// 在 Send 时自动为出包负载附加路由元数据 RequestId（先写后读关联），
    /// 使业务处理器无需感知请求 ID 细节，与旧 MessageRouter 行为保持一致。
    /// 从 MessageRouter 私有嵌套类提升为公共类，供 MessageDispatcher 迁移路径复用。
    /// </summary>
    public sealed class RequestContextSession : ISession
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
        /// ArrayPool&lt;byte&gt;.Shared。对于 requestId&lt;=0 或短包直接调用内部发送器。</remarks>
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
