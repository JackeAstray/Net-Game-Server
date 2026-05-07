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

    public object? UserData { get; set; }

    public void Send(byte[] data)
    {
        if (IsConnected)
        {
            tcpClient.GetStream().Write(data, 0, data.Length);
        }
    }

    public void Close()
    {
        tcpClient.Close();
    }
}
