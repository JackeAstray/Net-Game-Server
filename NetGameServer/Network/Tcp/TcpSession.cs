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

    /// <summary>
    /// 返回以 4 字节小端整数表示长度前缀的 ReadOnlySpan<byte>。如果输入已有与数据长度匹配的前缀则返回原切片，否则返回包含前缀的新分配缓冲区的切片。
    /// </summary>
    /// <remarks>前缀的判定为前 4 字节按小端解析为 Int32，并与 data.Length - 4 比较。仅在前缀缺失或不匹配时分配新的字节数组。</remarks>
    /// <param name="data">要检查并确保带有 4 字节小端长度前缀的字节切片。</param>
    /// <returns>包含 4 字节小端长度前缀的 ReadOnlySpan<byte>；可能为原始切片或新分配的字节数组的切片。</returns>
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