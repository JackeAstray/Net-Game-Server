using System;
using System.Buffers;

namespace Network;

/// <summary>
/// 打包缓冲发送助手：TcpSession / TcpClientWrapper 走零拷贝直传（缓冲所有权移交会话，写入后自动归还）；
/// 其他会话回退"拷贝发送 + 立即归还"。调用方统一不再手动 Return 池化缓冲。
/// </summary>
public static class PacketSender
{
    /// <summary>
    /// 发送已打包的池化缓冲（PacketBuilder.BuildPacket 产物）。
    /// </summary>
    /// <param name="session">目标会话。</param>
    /// <param name="packet">池化缓冲（调用方不再归还）。</param>
    /// <param name="totalLength">有效字节数（含长度前缀与消息头）。</param>
    public static void Send(ISession session, byte[] packet, int totalLength)
    {
        if (session is Tcp.TcpSession tcp)
        {
            tcp.SendFromPool(packet, totalLength);
            return;
        }
        if (session is Tcp.TcpClientWrapper wrapper)
        {
            wrapper.SendFromPool(packet, totalLength);
            return;
        }

        try
        {
            session.Send(packet.AsSpan(0, totalLength).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <summary>向 TCP 客户端包装器（服务间连接）发送打包缓冲：零拷贝直传。</summary>
    public static void Send(Tcp.TcpClientWrapper wrapper, byte[] packet, int totalLength)
    {
        wrapper.SendFromPool(packet, totalLength);
    }
}
