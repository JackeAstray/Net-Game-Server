using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;

namespace Network.WebSockets;

public class WebSocketSession : ISession
{
    private readonly WebSocket webSocket;

    // 串行化发送：同一 WebSocket 实例并发 SendAsync 会抛 InvalidOperationException 导致连接被拆
    private readonly SemaphoreSlim sendLock = new(1, 1);

    // P3 修复：有界发送积压计数。慢消费者时不再无界堆积 fire-and-forget 任务，
    // 超过上限即丢弃新包并告警（保护服务器内存/线程）。
    private const int MaxQueuedSends = 4096;
    private int pendingSends;

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
            Shared.Log.Warning($"[WebSocketSession] 发送失败，连接未建立 SessionId:{SessionId} Remote:{RemoteEndPoint} DataLength:{data.Length}");
            return;
        }

        // 有界积压：慢消费者/断网时丢弃新包并告警，防止 fire-and-forget 发送任务无界堆积
        if (Interlocked.Increment(ref pendingSends) > MaxQueuedSends)
        {
            Interlocked.Decrement(ref pendingSends);
            Shared.Log.Warning($"[WebSocketSession] 发送积压超过上限 {MaxQueuedSends}，丢弃数据包（慢消费者）SessionId:{SessionId} Remote:{RemoteEndPoint}");
            return;
        }

        byte[] payload = EnsureLengthPrefixed(data.Span);
        Shared.Log.Debug($"[WebSocketSession] 发送数据 SessionId:{SessionId} Remote:{RemoteEndPoint} InputLength:{data.Length} FramedLength:{payload.Length}");
        _ = SendAsyncInternal(payload);
    }

    /// <summary>
    /// 异步将二进制有效负载发送到 WebSocket，并在成功时更新最后活动时间。
    /// </summary>
    /// <remarks>通过信号量串行化同一实例的并发发送（WebSocket 不支持并发 SendAsync）；发送失败时记录警告并关闭会话。</remarks>
    /// <param name="payload">要发送的二进制有效负载。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SendAsyncInternal(byte[] payload)
    {
        await sendLock.WaitAsync();
        try
        {
            if (!IsConnected)
            {
                return;
            }
            await webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
            LastActivityTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[WebSocketSession] 发送异常 SessionId:{SessionId} Remote:{RemoteEndPoint} PayloadLength:{payload.Length} Exception:{ex}");
            Close();
        }
        finally
        {
            Interlocked.Decrement(ref pendingSends);
            sendLock.Release();
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
