using Network;
using Network.Tcp;

namespace Network;

/// <summary>
/// 内部连接认证扩展：所有服务间连接（Login/Game/Battle -> Center/DB）在连接建立后
/// 必须发送认证握手（InternalAuth），服务端验证通过后才处理业务消息。
/// </summary>
public static class InternalAuthExtensions
{
    /// <summary>
    /// 连接建立后调用：发送带 HMAC 签名的认证握手包。
    /// </summary>
    /// <param name="client">已建立的 TCP 客户端连接</param>
    /// <param name="sharedSecret">共享密钥（CenterNodeSharedSecret）</param>
    /// <param name="nodeId">本节点标识，如 "Login-127.0.0.1:31302"</param>
    public static void SendInternalAuthHandshake(this TcpClientWrapper client, string sharedSecret, string nodeId)
    {
        var filter = new Framework.Core.Security.InternalAuthFilter(sharedSecret, nodeId);
        byte[] authPacket = filter.BuildAuthPacket();
        // 帧长度修复（P1）：auth 包为裸 [MsgId][payload]，显式加长度头再发送，
        // 避免裸包触发 TcpSession.Send 的长度启发式误判（MsgId 恰等于负载长度时漏加前缀）。
        byte[] payload = authPacket.AsSpan(4).ToArray();
        byte[] framed = Network.Routing.PacketBuilder.BuildPacket(
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(authPacket.AsSpan(0, 4)),
            payload, out int totalLength);
        client.SendFromPool(framed, totalLength);
    }
}
