using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Sockets.Kcp;

namespace Network.Kcp;

/// <summary>
/// KCP 会话（对标 KBE 的 KCP 通道）：一个远端端点对应一个会话。
/// 封装 Kcp 2.7.0 库：Send 走 KCP 可靠传输，收包由 KcpServer 驱动 Input + TryRecv。
/// 注意：KCP 的 Update 必须由 KcpServer 定期驱动（发送/重传）。
/// </summary>
public sealed class KcpSession : ISession
{
    private readonly UdpClient udpClient;
    private readonly IPEndPoint remoteEndPoint;
    private readonly Kcp<KcpSegment> kcp;
    private readonly KcpOutputCallback outputCallback;
    private readonly ArrayBufferWriter<byte> recvWriter = new(1024);
    /// <summary>KCP 状态互斥锁（P1 三线程并发修复）：接收线程 Input、驱动线程 Update、应用线程 Send 并发触碰同一 Kcp 实例会破坏收发窗口。</summary>
    private readonly object kcpGate = new();
    /// <summary>告警限频锁（P2 加固：防止恶意洪泛触发日志风暴）。</summary>
    private readonly object warnGate = new();
    private DateTime lastRecvSizeWarnUtc = DateTime.MinValue;
    private DateTime lastSendQueueWarnUtc = DateTime.MinValue;

    /// <summary>接收单个应用消息的最大字节数（P2 加固：KCP 直通不经过 LengthPrefixedPacketReader 的 64KB 上限，
    /// 此处补齐；防止超大帧被转发到后端共享连接触发 InvalidDataException 导致全网关断线 DoS）。</summary>
    internal const int MaxRecvMessageBytes = 64 * 1024;
    /// <summary>发送队列+发送缓冲的最大分段数（P2 加固：攻击者置 rmt_wnd=0 或静默不 ACK 时
    /// snd_queue 无界增长导致原生 OOM；分段数超限即丢弃，避免内存被拖垮）。</summary>
    internal const int MaxPendingSegments = 2048;

    /// <summary>标记需要由 KcpServer 立即移除并关闭的会话（P2 加固：超大消息/协议异常，防止死链路残留与重复利用）。</summary>
    internal volatile bool MarkedForClose;

    public long SessionId { get; }

    public EndPoint? RemoteEndPoint => remoteEndPoint;

    public bool IsConnected => true; // UDP 无连接语义，逻辑会话一直有效直到超时清理

    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    public object? UserData { get; set; }

    /// <summary>KCP 转换号（客户端与服务端约定，用于区分连接）。</summary>
    public uint Conv { get; }

    internal KcpSession(UdpClient udpClient, IPEndPoint remoteEndPoint, uint conv)
    {
        this.udpClient = udpClient;
        this.remoteEndPoint = remoteEndPoint;
        Conv = conv;

        outputCallback = new KcpOutputCallback(data =>
        {
            try
            {
                udpClient.Send(data.ToArray(), data.Length, remoteEndPoint);
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"[KcpSession] UDP 发送异常 SessionId:{SessionId} Remote:{remoteEndPoint} Exception:{ex.Message}");
            }
        });

        kcp = new Kcp<KcpSegment>(conv, outputCallback, PooledRentable.Instance);
        kcp.SegmentManager = new SimpleSegManager();
        // 快速模式：无延迟、10ms 间隔、2 次快速重传、关闭流控
        kcp.NoDelay(1, 10, 2, 1);
        kcp.WndSize(128, 128);
        kcp.SetMtu(1400);

        SessionId = Framework.Core.Security.SessionIdGenerator.Next();
    }

    /// <summary>KCP 更新（由 KcpServer 每 tick 调用，驱动发送/重传/超时）。</summary>
    internal void Update()
    {
        lock (kcpGate)
        {
            var now = DateTimeOffset.UtcNow;
            kcp.Update(in now);
        }
    }

    /// <summary>把收到的 UDP 数据报送入 KCP，并尝试取出完整应用数据包。</summary>
    /// <returns>true 表示至少取出一个完整包并触发了 OnDataReceived</returns>
    internal bool Input(ReadOnlySpan<byte> data)
    {
        LastActivityTime = DateTime.UtcNow;
        List<byte[]>? packets = null;
        bool gotPacket = false;
        lock (kcpGate)
        {
            kcp.Input(data);
            var now = DateTimeOffset.UtcNow;
            kcp.Update(in now);

            while (kcp.TryRecv(recvWriter) > 0)
            {
                // P2 加固：单条应用消息大小上限（KCP 绕过 LengthPrefixedPacketReader 的 64KB 上限，
                // 超大帧若转发到后端会打爆共享后端连接）。超限即丢弃该消息并将会话标记为待关闭。
                if (recvWriter.WrittenCount > MaxRecvMessageBytes)
                {
                    if (ShouldLog(ref lastRecvSizeWarnUtc))
                        Shared.Log.Warning($"[KcpSession] 收到超大应用消息 {recvWriter.WrittenCount} 字节，超过上限 {MaxRecvMessageBytes}，会话将被关闭 SessionId:{SessionId} Remote:{remoteEndPoint}");
                    recvWriter.Clear();
                    MarkedForClose = true;
                    break;
                }
                gotPacket = true;
                (packets ??= new List<byte[]>(2)).Add(recvWriter.WrittenMemory.ToArray());
                recvWriter.Clear();
            }
        }
        // 锁外回调：避免持锁触发应用层（应用回调可能向其他会话 Send，防止跨会话锁序死锁）
        if (packets != null)
        {
            foreach (var packet in packets)
            {
                OnDataReceived?.Invoke(this, packet);
            }
        }
        return gotPacket;
    }

    /// <summary>通过 KCP 发送应用数据（可靠、有序）。</summary>
    public void Send(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0) return;
        try
        {
            lock (kcpGate)
            {
                // P2 加固：发送队列+发送缓冲分段数上限。攻击者置 rmt_wnd=0 或停止 ACK 时，
                // 发送窗口收缩为 0，snd_queue 无限累积导致 OOM；超限丢弃该消息（对端已停滞）。
                if (kcp.WaitSnd >= MaxPendingSegments)
                {
                    if (ShouldLog(ref lastSendQueueWarnUtc))
                        Shared.Log.Warning($"[KcpSession] 发送队列已满(分段数:{kcp.WaitSnd}，上限:{MaxPendingSegments})，丢弃消息（对端可能 rmt_wnd=0 或停止 ACK） SessionId:{SessionId} Remote:{remoteEndPoint}");
                    return;
                }
                kcp.Send(data.Span, null);
                LastActivityTime = DateTime.UtcNow;
                // 立即驱动一次发送（否则要等下一个 Update tick）；Update 内部再取锁（Monitor 可重入）
                Update();
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpSession] 发送异常 SessionId:{SessionId} Remote:{remoteEndPoint} Exception:{ex.Message}");
        }
    }

    /// <summary>告警限频：同一会话同类告警每 <paramref name="minIntervalSeconds"/> 秒最多输出一次，防止恶意洪泛触发日志风暴。</summary>
    private bool ShouldLog(ref DateTime lastUtc, int minIntervalSeconds = 10)
    {
        lock (warnGate)
        {
            var now = DateTime.UtcNow;
            if ((now - lastUtc).TotalSeconds < minIntervalSeconds) return false;
            lastUtc = now;
            return true;
        }
    }

    /// <summary>关闭会话（释放资源）。</summary>
    public void Close()
    {
        try
        {
            kcp.Dispose();
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpSession] 关闭异常 SessionId:{SessionId} Exception:{ex.Message}");
        }
    }

    public event DataReceivedHandler? OnDataReceived;
}
