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
        /// 针对 ISession 将业务消息打包成系统数据包发送的快捷扩展。
        /// </summary>
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