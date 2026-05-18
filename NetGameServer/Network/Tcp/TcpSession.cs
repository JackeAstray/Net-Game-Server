using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Network.Tcp;

public class TcpSession : ISession
{
    private readonly TcpClient tcpClient;

    public TcpSession(TcpClient tcpClient)
    {
        this.tcpClient = tcpClient;
        SessionId = SessionIdGenerator.Next();
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
                var payload = EnsureLengthPrefixed(data.Span);
                tcpClient.GetStream().Write(payload);
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

    private static ReadOnlySpan<byte> EnsureLengthPrefixed(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
            if (declaredLength == data.Length - 4)
            {
                return data;
            }
        }

        byte[] framed = new byte[data.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), data.Length);
        data.CopyTo(framed.AsSpan(4));
        return framed;
    }
}
