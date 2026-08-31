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
