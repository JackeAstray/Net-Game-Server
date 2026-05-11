using System.Net;
using System.Net.Sockets;

namespace Network.Tcp;

public class TcpSession : ISession
{
    private readonly TcpClient tcpClient;
    private static long sessionCounter = 0;

    public TcpSession(TcpClient tcpClient)
    {
        this.tcpClient = tcpClient;
        SessionId = Interlocked.Increment(ref sessionCounter);
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => tcpClient.Client.RemoteEndPoint;

    public bool IsConnected => tcpClient.Connected;

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    public void Send(ReadOnlyMemory<byte> data)
    {
        if (IsConnected)
        {
            try
            {
                tcpClient.GetStream().Write(data.Span);
                LastActivityTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // 日志记录发送异常
                Shared.Log.Warning($"TcpSession Send Error: {ex.Message}");
                Close();
            }
        }
    }

    public void Close()
    {
        tcpClient.Close();
    }
}
