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
        /// <remarks>兼容两种调用约定：
        /// 1) 旧处理器已用 PacketBuilder.BuildPacket 预打包的完整帧 [TotalLength(4)][MsgId(4)][Payload]（TotalLength == data.Length - 4）；
        /// 2) 未打包的 [MsgId(4)][Payload]。
        /// 判定为完整帧时从偏移 4 读取真实 MsgId、从偏移 8 取负载，避免"帧中套帧"导致对端（如 Login）解析失败。
        /// 使用 Shared.RouteMetadata.AttachRequestId 向负载附加请求 ID，并通过
        /// Network.Routing.PacketBuilder.BuildPacket 构建数据包。仅发送 BuildPacket 返回的 totalLength 字节，并在 finally 中将租用的数组归还到
        /// ArrayPool&lt;byte&gt;.Shared。对于 requestId&lt;=0 或短包直接调用内部发送器。</remarks>
        /// <param name="data">要发送的只读字节缓冲区；完整帧（带长度前缀）或 [MsgId][Payload]；长度小于 4 字节时按原样透传。</param>
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

            int headerMsgId;
            ReadOnlyMemory<byte> payload;
            // 判定完整帧 [TotalLength(4)][MsgId(4)][Payload] vs 未打包 [MsgId(4)][Payload]：
            // 两种形态 data.Length 相同（均为 4+payloadLen），唯一区分是前 4 字节语义（长度字段 vs MsgId）。
            // 收紧条件：长度字段必须 == data.Length-4，且偏移 4 读出的 MsgId 落在业务区间（>=1000）。
            // 否则未打包包在"msgId 恰好等于 payload 长度"时会被误判为完整帧（丢 4 字节 payload）。
            if (data.Length >= 8
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4)) == data.Length - 4
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(4, 4)) >= 1000)
            {
                // 完整帧：前 4 字节是 TotalLength，MsgId 在偏移 4，Payload 从偏移 8 开始。
                headerMsgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(4, 4));
                payload = data.Slice(8);
            }
            else
            {
                headerMsgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                payload = data.Slice(4);
            }

            byte[] payloadWithRequestId = Shared.RouteMetadata.AttachRequestId(payload, requestId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(headerMsgId, payloadWithRequestId, out int totalLength);
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
