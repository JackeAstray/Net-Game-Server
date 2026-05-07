using System.Net;
using System.Net.Sockets;

namespace Network.Udp;

public class UdpSession : ISession
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _remoteEndPoint;
    private static long _sessionCounter = 0;

    public UdpSession(UdpClient udpClient, IPEndPoint remoteEndPoint)
    {
        _udpClient = udpClient;
        _remoteEndPoint = remoteEndPoint;
        SessionId = Interlocked.Increment(ref _sessionCounter);
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => _remoteEndPoint;

    public bool IsConnected => true; // UDP 是无连接的，这里只能表示一个有效的逻辑会话

    public object? UserData { get; set; }

    public void Send(byte[] data)
    {
        _udpClient.Send(data, data.Length, _remoteEndPoint);
    }

    public void Close()
    {
        // UDP无明确连接断开行为，逻辑上的移除处理留给上层(或长时心跳机制管理)进行操作
    }
}