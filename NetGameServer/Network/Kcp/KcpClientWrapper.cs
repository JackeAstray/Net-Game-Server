using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Sockets.Kcp;

namespace Network.Kcp;

/// <summary>
/// KCP 客户端封装（对标 KBE 客户端 KCP 通道）：
/// - 与 KcpServer 配合，提供可靠有序的 UDP 传输
/// - 内部一个 UdpClient + 一个 Kcp 实例，驱动线程周期 Update
/// </summary>
public class KcpClientWrapper : INetworkClient
{
    private readonly string host;
    private readonly int port;
    private readonly uint conv;
    private UdpClient? udpClient;
    private Kcp<KcpSegment>? kcp;
    private CancellationTokenSource? cts;
    private bool isRunning;
    private readonly ArrayBufferWriter<byte> recvWriter = new(1024);
    /// <summary>KCP 状态互斥锁（P1 三线程并发修复）：接收线程 Input、驱动线程 Update、Send 并发触碰同一 Kcp 实例。</summary>
    private readonly object kcpGate = new();
    /// <summary>客户端侧会话代理（事件回调的 ISession 参数；P1 修复：不再向事件派发 null）。</summary>
    private KcpClientSessionProxy? sessionProxy;

    /// <summary>网关下发的会话令牌（UDP/KCP 会话身份绑定）：收到 GatewaySessionAuthPush 后填充，
    /// Send 时插入到 [MsgId(4)][Token(8)][Payload]；未收到（连裸 KcpServer 等无令牌服务端）时原样发送。</summary>
    private byte[]? sessionAuthToken;
    private readonly TaskCompletionSource<bool> authReceivedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>网关会话令牌推送消息 ID（与 Shared.Messages.MessageIds.GatewaySessionAuthPush 一致；避免 Network 层依赖 Shared.Messages 大表）。</summary>
    private const int SessionAuthPushMsgId = 70001;

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public KcpClientWrapper(string host, int port, uint? conv = null)
    {
        this.host = host;
        this.port = port;
        // P1 conv 随机化：不再使用可猜测的固定转换号（防会话固定攻击），每次连接随机生成
        this.conv = conv ?? CreateRandomConv();
    }

    /// <summary>生成随机非零 KCP 转换号（高位置位确保永不为 0，避免与"任意转换号"语义冲突）。</summary>
    private static uint CreateRandomConv()
    {
        return (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue) | 0x80000000u;
    }

    public async Task ConnectAsync()
    {
        isRunning = true;
        udpClient = new UdpClient();
        // 绑定本地随机端口（UDP 接收需要绑定才能 ReceiveAsync）
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var remoteEndPoint = new IPEndPoint(
            IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host))[0],
            port);

        kcp = new Kcp<KcpSegment>(conv,
            new KcpOutputCallback(data =>
            {
                try
                {
                    udpClient?.Send(data.ToArray(), data.Length, remoteEndPoint);
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"[KcpClientWrapper] UDP 发送异常 {ex.Message}");
                }
            }),
            PooledRentable.Instance);
        kcp.SegmentManager = new SimpleSegManager();
        kcp.NoDelay(1, 10, 2, 1);
        kcp.WndSize(128, 128);
        kcp.SetMtu(1400);

        cts = new CancellationTokenSource();
        var session = new KcpClientSessionProxy(this, remoteEndPoint);
        sessionProxy = session;
        Shared.Log.Info($"[KcpClientWrapper] KCP 连接成功 {host}:{port} conv=0x{conv:X8}");
        OnConnected?.Invoke(session);

        _ = ReceiveLoopAsync(cts.Token);
        _ = DriveLoopAsync(cts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && udpClient != null)
            {
                var result = await udpClient.ReceiveAsync(token);
                List<byte[]>? packets = null;
                lock (kcpGate)
                {
                    kcp?.Input(result.Buffer);
                    var now = DateTimeOffset.UtcNow;
                    kcp?.Update(in now);

                    while (kcp != null && kcp.TryRecv(recvWriter) > 0)
                    {
                        // P2 修复：锁内只收集，锁外回调（与 KcpSession.Input 一致，防持锁触发
                        // 应用层回调导致跨会话锁序死锁/长时间阻塞 KCP 驱动）
                        (packets ??= new List<byte[]>(2)).Add(recvWriter.WrittenMemory.ToArray());
                        recvWriter.Clear();
                    }
                }
                if (packets != null)
                {
                    foreach (var packet in packets)
                    {
                        // 传输层会话令牌推送：解析存储，不下发应用层（与消息协议无关）
                        if (packet.Length >= 12 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, 4)) == SessionAuthPushMsgId)
                        {
                            byte[] authToken = new byte[8];
                            packet.AsSpan(4, 8).CopyTo(authToken);
                            lock (kcpGate)
                            {
                                sessionAuthToken = authToken;
                            }
                            authReceivedTcs.TrySetResult(true);
                            Shared.Log.Info($"[KcpClientWrapper] 已接收网关会话令牌 {host}:{port}");
                            continue;
                        }
                        OnDataReceived?.Invoke(sessionProxy!, packet);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpClientWrapper] 接收循环异常 {ex.Message}");
            OnDisconnected?.Invoke(sessionProxy!, ex.Message);
        }
    }

    private async Task DriveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token);
                lock (kcpGate)
                {
                    if (kcp != null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        kcp.Update(in now);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Send(ReadOnlyMemory<byte> data)
    {
        if (kcp == null || data.Length == 0) return;
        try
        {
            lock (kcpGate)
            {
                kcp.Send(PrepareFrame(data).Span, null);
                var now = DateTimeOffset.UtcNow;
                kcp.Update(in now);
            }
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpClientWrapper] 发送异常 {ex.Message}");
        }
    }

    /// <summary>
    /// 等待网关会话令牌下发完成（UDP/KCP 身份绑定握手）。连接启用令牌校验的网关（Gateway 默认启用）后，
    /// 业务消息必须携带令牌，调用方应 await 本方法后再发业务流量；连裸 KcpServer（无令牌）时立即返回 true。
    /// </summary>
    /// <param name="timeoutMs">等待上限，默认 5000ms。</param>
    /// <returns>在超时前收到令牌返回 true；超时返回 false（网关未启用令牌校验时也返回 true——无令牌即视为免校验）。</returns>
    public async Task<bool> WaitForSessionAuthAsync(int timeoutMs = 5000)
    {
        lock (kcpGate)
        {
            if (sessionAuthToken != null)
            {
                return true;
            }
        }
        var timeout = Task.Delay(timeoutMs);
        var completed = await Task.WhenAny(authReceivedTcs.Task, timeout);
        return completed == authReceivedTcs.Task;
    }

    /// <summary>
    /// 组装发送帧：未收到令牌（无令牌服务端）原样返回；收到令牌后插入 [MsgId(4)][Token(8)][Payload]。
    /// </summary>
    private ReadOnlyMemory<byte> PrepareFrame(ReadOnlyMemory<byte> data)
    {
        byte[]? token = sessionAuthToken;
        if (token == null)
        {
            return data;
        }
        var buf = new byte[data.Length + 8];
        data.Span.Slice(0, 4).CopyTo(buf);
        token.CopyTo(buf, 4);
        data.Span.Slice(4).CopyTo(buf.AsSpan(12));
        return buf;
    }

    public void Stop()
    {
        isRunning = false;
        cts?.Cancel();
        // 持锁 dispose：避免与接收/驱动/Send 线程并发触碰 Kcp 实例（P2-10 拆卸竞态修复）
        lock (kcpGate)
        {
            udpClient?.Close();
            udpClient?.Dispose();
            udpClient = null;
            kcp?.Dispose();
            kcp = null;
        }
    }

    /// <summary>客户端侧会话代理（ISession 适配）。</summary>
    private sealed class KcpClientSessionProxy : ISession
    {
        private readonly KcpClientWrapper owner;
        public long SessionId { get; } = Framework.Core.Security.SessionIdGenerator.Next();
        public EndPoint? RemoteEndPoint { get; }
        public bool IsConnected => owner.isRunning;
        public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
        public object? UserData { get; set; }

        public KcpClientSessionProxy(KcpClientWrapper owner, EndPoint? remote)
        {
            this.owner = owner;
            RemoteEndPoint = remote;
        }

        public void Send(ReadOnlyMemory<byte> data) => owner.Send(data);
        public void Close() => owner.Stop();
    }
}
