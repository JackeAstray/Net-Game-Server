using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;

namespace Network.WebSockets;

public class WebSocketSession : ISession
{
    private readonly WebSocket webSocket;

    public WebSocketSession(WebSocket webSocket, EndPoint? remoteEndPoint)
    {
        this.webSocket = webSocket;
        RemoteEndPoint = remoteEndPoint;
        SessionId = SessionIdGenerator.Next();
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint { get; }

    public bool IsConnected => webSocket.State == WebSocketState.Open;

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    public void Send(ReadOnlyMemory<byte> data)
    {
        if (!IsConnected)
        {
            return;
        }

        byte[] payload = EnsureLengthPrefixed(data.Span);
        _ = SendAsyncInternal(payload);
    }

    /// <summary>
    /// 异步将二进制有效负载发送到 WebSocket，并在成功时更新最后活动时间。
    /// </summary>
    /// <remarks>在发送失败时记录警告并关闭会话。</remarks>
    /// <param name="payload">要发送的二进制有效负载。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SendAsyncInternal(byte[] payload)
    {
        try
        {
            await webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
            LastActivityTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"WebSocketSession Send Error: {ex.Message}");
            Close();
        }
    }

    public void Close()
    {
        if (IsConnected)
        {
            _ = webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
        }
    }

    /// <summary>
    /// 返回以 4 字节小端序长度前缀的字节数组。
    /// </summary>
    /// <remarks>当 data.Length >= 4 且前四字节按小端序解析的整数等于 data.Length - 4
    /// 时，视为已帧化。长度前缀表示有效载荷的长度（不包含前缀本身）。返回值始终为新数组。</remarks>
    /// <param name="data">要处理的有效载荷；如果已包含与实际长度匹配的 4 字节小端长度前缀，将被视为已帧化。</param>
    /// <returns>返回一个新分配的字节数组，保证以 4 字节小端序表示的长度前缀开头；如果输入已正确帧化则返回其副本，否则在前面添加长度前缀。</returns>
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
}
