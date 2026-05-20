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
            Shared.Log.Debug($"[UdpSession] 发送数据 SessionId:{SessionId} Remote:{RemoteEndPoint} InputLength:{data.Length} FramedLength:{payload.Length}");
            udpClient.Send(payload, payload.Length, remoteEndPoint);
            LastActivityTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // 对于UDP发送异常只做日志记录，不用断开
            Shared.Log.Warning($"[UdpSession] 发送异常 SessionId:{SessionId} Remote:{RemoteEndPoint} DataLength:{data.Length} Exception:{ex}");
        }
    }

    /// <summary>
    /// 确保返回的字节数组以 4 字节小端整数作为长度前缀来表示其有效载荷长度。
    /// </summary>
    /// <remarks>长度以 32 位小端整数表示，值为随后字节的长度。不会修改输入的 ReadOnlySpan，结果为独立的字节数组副本。</remarks>
    /// <param name="data">要检查或封装的只读字节序列；若前四字节作为小端 32 位整数等于剩余字节数，则视为已包含前缀。</param>
    /// <returns>包含 4 字节小端长度前缀的字节数组；若输入已包含正确前缀则返回其副本，否则返回带前缀的新数组。</returns>
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