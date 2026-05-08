using System.Net;
using System.Net.Sockets;

namespace Network.Udp;

public class UdpSession : ISession
{
    private readonly UdpClient udpClient;
    private readonly IPEndPoint remoteEndPoint;
    private static long sessionCounter = 0;

    public UdpSession(UdpClient udpClient, IPEndPoint remoteEndPoint)
    {
        this.udpClient = udpClient;
        this.remoteEndPoint = remoteEndPoint;
        SessionId = Interlocked.Increment(ref sessionCounter);
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => remoteEndPoint;

    public bool IsConnected => true; // UDP 是无连接的，这里只能表示一个有效的逻辑会话

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    public void Send(ReadOnlyMemory<byte> data)
    {
        udpClient.Send(data.Span.ToArray(), data.Length, remoteEndPoint);
        LastActivityTime = DateTime.UtcNow;
    }

    public void Close()
    {
        // UDP无明确连接断开行为，逻辑上的移除处理留给上层(或长时心跳机制管理)进行操作
    }
}