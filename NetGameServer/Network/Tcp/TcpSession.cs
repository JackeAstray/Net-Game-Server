using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Network.Tcp;

/// <summary>
/// TCP 会话（写侧改造：非阻塞发送队列，对标 KBE 发送缓冲）。
/// - Send 只入队（复制一次），由单一写者任务异步冲刷，调用线程（tick/收包线程）不再被慢对端阻塞
/// - 每个包原子入队，多线程并发 Send 不会出现字节交错损坏帧（旧实现并发 Write 存在该风险）
/// - NoDelay=true 禁用 Nagle：20Hz 小包（位置同步/帧同步）延迟显著降低
/// - SendFromPool 零拷贝直传：直接接管 ArrayPool 缓冲（写入完成后归还），调用方不得再 Return
/// - 背压：队列满时丢弃新包并告警（节流）+ 关闭连接（慢客户端保护，对端重连恢复）
/// </summary>
public class TcpSession : ISession
{
    private readonly TcpClient tcpClient;
    private readonly Channel<QueuedPacket> sendChannel;
    private readonly CancellationTokenSource writerCts = new();
    private readonly object writerGate = new();
    private Task? writerTask;
    private long droppedPackets;
    private long lastDropWarnTick;

    /// <summary>发送队列上限（包数）。超过视为对端消费过慢。</summary>
    private const int MaxQueuedPackets = 8192;

    private readonly struct QueuedPacket
    {
        public readonly byte[] Buffer;
        public readonly int Length;
        public readonly bool Pooled;

        public QueuedPacket(byte[] buffer, int length, bool pooled)
        {
            Buffer = buffer;
            Length = length;
            Pooled = pooled;
        }
    }

    public TcpSession(TcpClient tcpClient)
    {
        this.tcpClient = tcpClient;
        tcpClient.NoDelay = true; // 禁用 Nagle：游戏小包低延迟
        SessionId = SessionIdGenerator.Next();
        sendChannel = Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(MaxQueuedPackets)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite // 满时 TryWrite 返回 false，由 Enqueue 处理
        });
    }

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => tcpClient.Client.RemoteEndPoint;

    public bool IsConnected => tcpClient.Connected;

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    /// <summary>
    /// 发送数据（非阻塞）：复制入队，由写者任务异步写盘。调用线程不阻塞。
    /// </summary>
    public void Send(ReadOnlyMemory<byte> data)
    {
        if (!IsConnected)
        {
            Shared.Log.Warning($"[TcpSession] 发送失败，连接未建立 SessionId:{SessionId} Remote:{RemoteEndPoint} DataLength:{data.Length}");
            return;
        }

        try
        {
            var payload = EnsureLengthPrefixed(data.Span);
            // 复制进队（与旧实现同一次拷贝；发送本身不再阻塞）
            Enqueue(payload.ToArray(), payload.Length, pooled: false);
            LastActivityTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[TcpSession] 发送异常 SessionId:{SessionId} Remote:{RemoteEndPoint} DataLength:{data.Length} Exception:{ex}");
            Close();
        }
    }

    /// <summary>
    /// 零拷贝发送：直接接管 ArrayPool 缓冲（写入完成后自动归还）。
    /// 调用方不得再将该缓冲归还 ArrayPool；对不支持直传的会话请使用 PacketSender。
    /// </summary>
    /// <param name="pooledBuffer">PacketBuilder.BuildPacket 等池化来源的缓冲。</param>
    /// <param name="count">实际有效字节数（含长度前缀与消息头）。</param>
    public void SendFromPool(byte[] pooledBuffer, int count)
    {
        if (!IsConnected)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pooledBuffer);
            Shared.Log.Warning($"[TcpSession] 发送失败（池化缓冲已归还），连接未建立 SessionId:{SessionId} DataLength:{count}");
            return;
        }

        Enqueue(pooledBuffer, count, pooled: true);
        LastActivityTime = DateTime.UtcNow;
    }

    private void Enqueue(byte[] buffer, int length, bool pooled)
    {
        StartWriter();
        if (!sendChannel.Writer.TryWrite(new QueuedPacket(buffer, length, pooled)))
        {
            // 背压：队列满 = 对端消费过慢，丢包 + 节流告警 + 关闭连接（慢客户端保护）
            if (pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
            long dropped = Interlocked.Increment(ref droppedPackets);
            long now = Environment.TickCount64;
            long last = Volatile.Read(ref lastDropWarnTick);
            if (now - last > 5000 && Interlocked.CompareExchange(ref lastDropWarnTick, now, last) == last)
            {
                Shared.Log.Warning($"[TcpSession] 发送队列已满（上限 {MaxQueuedPackets}，累计丢弃 {dropped} 包）——对端消费过慢，关闭连接 SessionId:{SessionId} Remote:{RemoteEndPoint}");
                Close();
            }
        }
    }

    private void StartWriter()
    {
        if (writerTask != null) return;
        lock (writerGate)
        {
            if (writerTask == null)
            {
                writerTask = WriteLoopAsync();
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var stream = tcpClient.GetStream();
            await foreach (var packet in sendChannel.Reader.ReadAllAsync(writerCts.Token))
            {
                await stream.WriteAsync(packet.Buffer.AsMemory(0, packet.Length), writerCts.Token);
                if (packet.Pooled)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[TcpSession] 发送循环异常退出 SessionId:{SessionId} Exception:{ex}");
            TryCloseInternal();
        }
        finally
        {
            // 归还队列中残留的池化缓冲，避免连接关闭后泄漏
            while (sendChannel.Reader.TryRead(out var leftover))
            {
                if (leftover.Pooled)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(leftover.Buffer);
                }
            }
        }
    }

    private void TryCloseInternal()
    {
        try
        {
            tcpClient.Close();
        }
        catch
        {
        }
    }

    /// <summary>关闭连接：取消写者循环、关闭底层套接字（读侧会触发断开事件）。</summary>
    public void Close()
    {
        writerCts.Cancel();
        sendChannel.Writer.TryComplete();
        TryCloseInternal();
    }

    /// <summary>
    /// 返回以 4 字节小端整数表示长度前缀的 ReadOnlySpan&lt;byte&gt;。
    /// 如果输入已有与数据长度匹配的前缀则返回原切片，否则返回包含前缀的新分配缓冲区的切片。
    /// </summary>
    /// <remarks>前缀的判定为前 4 字节按小端解析为 Int32，并与 data.Length - 4 比较。仅在前缀缺失或不匹配时分配新的字节数组。</remarks>
    /// <param name="data">要检查并确保带有 4 字节小端长度前缀的字节切片。</param>
    /// <returns>包含 4 字节小端长度前缀的 ReadOnlySpan&lt;byte&gt;；可能为原始切片或新分配的字节数组的切片。</returns>
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
