using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Network;
using Network.Routing;
using Framework.Protocol.Generated;
using Framework.Protocol;

namespace Bots;

/// <summary>
/// 压测机器人（对标 KBE bots）：模拟多个客户端连接 Gateway，发送登录/实体同步消息，
/// 统计吞吐与延迟。用法：
///   Bots --count 100 --host 127.0.0.1 --port 31300 --duration 10
/// </summary>
public static class Program
{
    private sealed class Bot
    {
        private readonly TcpClient tcp;
        private readonly string host;
        private readonly int port;
        private readonly int botId;
        private readonly LengthPrefixedPacketReader reader = new();
        private readonly byte[] buffer = new byte[4096];
        public long Sent { get; private set; }
        public long Received { get; private set; }

        public Bot(string host, int port, int botId)
        {
            this.host = host;
            this.port = port;
            this.botId = botId;
            tcp = new TcpClient();
        }

        public async Task RunAsync(CancellationToken token, int durationMs)
        {
            await tcp.ConnectAsync(host, port);
            var stream = tcp.GetStream();

            // 1. 发送登录请求（JSON 旧协议，走网关 → Login）
            var loginReq = new Framework.Protocol.Generated.Login
            {
                Account = $"bot{botId}",
                Password = "botpass"
            };
            byte[] loginPacket = ProtocolCodec.Encode(loginReq);
            await stream.WriteAsync(loginPacket.AsMemory(0, loginPacket.Length), token);

            // 2. 持续发送实体同步消息（JSON 旧协议，走网关 → Battle/Game 路由）
            var syncReq = new Framework.Protocol.Generated.EntitySync
            {
                Position = new Framework.Protocol.Generated.Vector3 { X = botId, Y = 0, Z = botId },
                Rotation = new Framework.Protocol.Generated.Vector3 { X = 0, Y = 0, Z = 0 }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long interval = 100; // 每 100ms 发一条
            long nextSend = 0;

            var recvTask = ReceiveLoopAsync(stream, token);

            while (!token.IsCancellationRequested && sw.ElapsedMilliseconds < durationMs)
            {
                long now = sw.ElapsedMilliseconds;
                if (now >= nextSend)
                {
                    byte[] packet = ProtocolCodec.Encode(syncReq);
                    await stream.WriteAsync(packet.AsMemory(0, packet.Length), token);
                    Sent++;
                    nextSend = now + interval;
                }
                await Task.Delay(10, token);
            }

            await recvTask;
            tcp.Close();
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (n == 0) break;
                    reader.Append(buffer.AsSpan(0, n));
                    while (reader.TryReadPacket(out var packet))
                    {
                        Received++;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public static async Task<int> Main(string[] args)
    {
        // 简易参数解析
        int count = GetArg(args, "--count", 10);
        string host = GetArg(args, "--host", "127.0.0.1");
        int port = GetArg(args, "--port", 31300);
        int duration = GetArg(args, "--duration", 10);

        Console.WriteLine($"Bots 压测启动: {count} 个机器人 -> {host}:{port}，持续 {duration}s");
        Console.WriteLine("（需要服务器已启动：DB → Center → Login → Game/Battle → Gateway）");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 5));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var bots = Enumerable.Range(0, count).Select(i => new Bot(host, port, i)).ToList();
        await Task.WhenAll(bots.Select(b => b.RunAsync(cts.Token, duration)));

        sw.Stop();
        long totalSent = bots.Sum(b => b.Sent);
        long totalReceived = bots.Sum(b => b.Received);
        double seconds = sw.Elapsed.TotalSeconds;

        Console.WriteLine();
        Console.WriteLine("===== 压测结果 =====");
        Console.WriteLine($"机器人数: {count}");
        Console.WriteLine($"总发送: {totalSent} 条 | 总接收: {totalReceived} 条");
        Console.WriteLine($"发送速率: {totalSent / seconds:F1} msg/s | 接收速率: {totalReceived / seconds:F1} msg/s");
        Console.WriteLine($"平均每机器人: 发送 {totalSent / Math.Max(1, count):F1} 接收 {totalReceived / Math.Max(1, count):F1} 条");
        return 0;
    }

    private static string GetArg(string[] args, string key, string defaultValue)
    {
        int idx = Array.IndexOf(args, key);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultValue;
    }

    private static int GetArg(string[] args, string key, int defaultValue)
        => int.TryParse(GetArg(args, key, defaultValue.ToString()), out var v) ? v : defaultValue;
}
