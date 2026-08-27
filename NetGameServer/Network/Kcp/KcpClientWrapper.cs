using System.Buffers;
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

    public event SessionConnectedHandler? OnConnected;
    public event DataReceivedHandler? OnDataReceived;
    public event SessionDisconnectedHandler? OnDisconnected;

    public KcpClientWrapper(string host, int port, uint conv = 0x4B434550)
    {
        this.host = host;
        this.port = port;
        this.conv = conv;
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
                kcp?.Input(result.Buffer);
                var now = DateTimeOffset.UtcNow;
                kcp?.Update(ref now);

                while (kcp != null && kcp.TryRecv(recvWriter) > 0)
                {
                    OnDataReceived?.Invoke(null!, recvWriter.WrittenMemory.ToArray());
                    recvWriter.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpClientWrapper] 接收循环异常 {ex.Message}");
            OnDisconnected?.Invoke(null!, ex.Message);
        }
    }

    private async Task DriveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token);
                if (kcp != null)
                {
                    var now = DateTimeOffset.UtcNow;
                    kcp.Update(ref now);
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
            kcp.Send(data.Span, null);
            var now = DateTimeOffset.UtcNow;
            kcp.Update(ref now);
        }
        catch (Exception ex)
        {
            Shared.Log.Warning($"[KcpClientWrapper] 发送异常 {ex.Message}");
        }
    }

    public void Stop()
    {
        isRunning = false;
        cts?.Cancel();
        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
        kcp?.Dispose();
        kcp = null;
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
