using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Network.Udp;

public class UdpSession : ISession
{
    private readonly UdpClient udpClient;
    private readonly IPEndPoint remoteEndPoint;

    public UdpSession(UdpClient udpClient, IPEndPoint remoteEndPoint)
    {
        this.udpClient = udpClient;
        this.remoteEndPoint = remoteEndPoint;
        SessionId = SessionIdGenerator.Next();
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => remoteEndPoint;

    public bool IsConnected => true; // UDP 是无连接的，这里只能表示一个有效的逻辑会话

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    public void Send(ReadOnlyMemory<byte> data)
    {
        try
        {
            byte[] payload = EnsureLengthPrefixed(data.Span);
            udpClient.Send(payload, payload.Length, remoteEndPoint);
            LastActivityTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // 对于UDP发送异常只做日志记录，不用断开
             Shared.Log.Warning($"UdpSession Send Error: {ex.Message}");
        }
    }

    private static byte[] EnsureLengthPrefixed(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
            if (declaredLength == data.Length - 4)
            {
                return data.ToArray();
            }
        }

        byte[] framed = new byte[data.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), data.Length);
        data.CopyTo(framed.AsSpan(4));
        return framed;
    }

    public void Close()
    {
        // UDP无明确连接断开行为，逻辑上的移除处理留给上层(或长时心跳机制管理)进行操作
    }
}