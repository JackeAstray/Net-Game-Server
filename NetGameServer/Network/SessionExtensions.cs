using Network.Routing;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace Network
{
    public static class SessionExtensions
    {
        /// <summary>
        /// 将指定的消息序列化为 JSON，并构建带有消息标识和长度前缀的二进制包，通过指定会话发送。
        /// </summary>
        /// <remarks>序列化使用 Shared.Json 将对象转换为 UTF‑8 字节，按 长度(4) + MsgId(4) + Payload 的包格式构建数据，发送后将临时缓冲区归还给
        /// ArrayPool 以降低 GC 压力。</remarks>
        /// <typeparam name="T">要序列化并发送的消息的类型。</typeparam>
        /// <param name="session">用于发送数据的会话实例。</param>
        /// <param name="msgId">用于路由和解析的消息标识符。</param>
        /// <param name="message">要序列化为 UTF‑8 JSON 并随包发送的消息对象。</param>
        public static void SendJsonMessage<T>(this ISession session, int msgId, T message)
        {
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(message);

            // BuildPacket 保证了外包装结构: 长度(4) + MsgId(4) + payload
            byte[] buffer = PacketBuilder.BuildPacket(msgId, payload, out int totalLength);

            try
            {
                // Send 目前只接 byte[]，可以通过截断或者重载其能够用 ReadOnlySpan 的功能
                var validData = buffer.AsSpan(0, totalLength).ToArray();
                session.Send(validData);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer); // 免GC核心。
            }
        }
    }
}