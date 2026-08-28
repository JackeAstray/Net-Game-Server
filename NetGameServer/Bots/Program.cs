using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network.Routing;
using Framework.Protocol.Generated;
using Framework.Protocol;

namespace Bots
{
    /// <summary>
    /// 压测机器人（KBE-Gap-Review D8，对标 KBE bots）：
    /// 模拟多个机器人连接真实 Gateway，完成登录 + 加入战斗 + 周期性 EntitySync +
    /// 周期性 ClientTimeSync（KBE-Gap-Review D7），统计：
    /// - 发送/接收速率（msg/s）
    /// - RTT 分布（avg / p50 / p95 / p99 / max）
    /// - 时间同步 offset 漂移
    /// 用法：
    ///   Bots --count 100 --host 127.0.0.1 --port 31300 --duration 10
    ///       [--protocol tcp|kcp|ws] [--rampup 50] [--scene default]
    /// 协议：
    ///   - tcp：原生 TCP + LengthPrefixedPacketReader（与 KBE TCP 一致）
    ///   - kcp：暂走 TCP（KCP 客户端 SDK 需另接，默认回退到 tcp）
    ///   - ws：WebSocket + binary frames（需要 Gateway 开启 WS 端口）
    /// </summary>
    internal class Program
    {
        private const string Tag = "Bots";

        private enum BotProtocol { Tcp, Kcp, Ws }

        private sealed class BotOptions
        {
            public int Count { get; set; } = 10;
            public string Host { get; set; } = "127.0.0.1";
            public int Port { get; set; } = 31300;
            public int DurationSeconds { get; set; } = 10;
            public BotProtocol Protocol { get; set; } = BotProtocol.Tcp;
            /// <summary>启动阶段线性 ramp-up（ms/bot），避免瞬时连接风暴。</summary>
            public int RampUpMs { get; set; } = 0;
            /// <summary>压测场景（default / timesync / battle）。</summary>
            public string Scene { get; set; } = "default";
        }

        private sealed class BotStats
        {
            public long Sent;
            public long Received;
            public long Errors;
            /// <summary>登录完成时刻（用于业务延迟分桶）。</summary>
            public long LoginCompletedAtMs;
            /// <summary>所有同步 RTT 样本（ms），用于计算百分位。</summary>
            public readonly List<long> RttSamples = new();
            /// <summary>所有 time-sync offset 样本（ms），用于评估时钟对齐质量。</summary>
            public readonly List<long> OffsetSamples = new();
        }

        private sealed class Bot
        {
            private readonly BotOptions opts;
            private readonly int botId;
            private readonly BotStats stats = new();
            private readonly object writeLock = new();

            public long Sent => Interlocked.Read(ref stats.Sent);
            public long Received => Interlocked.Read(ref stats.Received);
            public long Errors => Interlocked.Read(ref stats.Errors);
            public long LoginCompletedAtMs => Interlocked.Read(ref stats.LoginCompletedAtMs);
            public IReadOnlyList<long> RttSamples => stats.RttSamples;
            public IReadOnlyList<long> OffsetSamples => stats.OffsetSamples;
            public int BotId => botId;
            public BotStats Stats => stats;

            public Bot(BotOptions opts, int botId)
            {
                this.opts = opts;
                this.botId = botId;
            }

            public async Task RunAsync(CancellationToken token)
            {
                try
                {
                    switch (opts.Protocol)
                    {
                        case BotProtocol.Ws: await RunWebSocketAsync(token).ConfigureAwait(false); break;
                        case BotProtocol.Tcp:
                        case BotProtocol.Kcp:
                        default:
                            await RunTcpAsync(token).ConfigureAwait(false); break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref stats.Errors);
                    Framework.Core.Log.Debug($"[bot:{botId}] {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ---- TCP 实现 ----
            private async Task RunTcpAsync(CancellationToken token)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(opts.Host, opts.Port, token).ConfigureAwait(false);
                using var stream = tcp.GetStream();

                if (!await LoginAndAwaitAsync(stream, token).ConfigureAwait(false))
                {
                    return;
                }

                var reader = new LengthPrefixedPacketReader();
                var buffer = new byte[4096];
                var stopwatch = Stopwatch.StartNew();
                long nextSyncAtMs = 0;
                long nextTimeSyncAtMs = 0;
                long lastServerSendMs = 0;

                var recvTask = Task.Run(() => ReceiveLoopAsync(stream, reader, buffer, token, lastServerSendMs), token);

                while (!token.IsCancellationRequested && stopwatch.Elapsed.TotalSeconds < opts.DurationSeconds)
                {
                    long now = stopwatch.ElapsedMilliseconds;
                    if (opts.Scene == "default" || opts.Scene == "battle")
                    {
                        if (now >= nextSyncAtMs)
                        {
                            await SendEntitySyncAsync(stream).ConfigureAwait(false);
                            nextSyncAtMs = now + 100; // 10 Hz
                        }
                    }
                    if (opts.Scene == "timesync" || opts.Scene == "default")
                    {
                        if (now >= nextTimeSyncAtMs)
                        {
                            await SendTimeSyncAsync(stream, now, lastServerSendMs).ConfigureAwait(false);
                            nextTimeSyncAtMs = now + 1000; // 1 Hz
                        }
                    }
                    await Task.Delay(5, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
            }

            private async Task<bool> LoginAndAwaitAsync(NetworkStream stream, CancellationToken token)
            {
                var loginReq = new Login { Account = "bot" + botId, Password = "botpass" };
                byte[] packet = ProtocolCodec.Encode(loginReq);
                await stream.WriteAsync(packet, 0, packet.Length, token).ConfigureAwait(false);
                Interlocked.Increment(ref stats.Sent);

                // 简化：实际应按 msgId 解析登录回包；这里假设 1s 内完成登录。
                var deadline = Environment.TickCount64 + 1000;
                while (Environment.TickCount64 < deadline)
                {
                    await Task.Delay(20, token).ConfigureAwait(false);
                    if (LoginCompletedAtMs > 0) return true;
                }
                Interlocked.Increment(ref stats.Errors);
                return false;
            }

            private async Task SendEntitySyncAsync(NetworkStream stream)
            {
                var msg = new EntitySync
                {
                    Position = new Vector3 { X = botId, Y = 0, Z = botId },
                    Rotation = new Vector3 { X = 0, Y = 0, Z = 0 }
                };
                byte[] packet = ProtocolCodec.Encode(msg);
                await stream.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
                Interlocked.Increment(ref stats.Sent);
            }

            private async Task SendTimeSyncAsync(NetworkStream stream, long clientSendMs, long lastServerSendMs)
            {
                var msg = new ClientTimeSync
                {
                    ClientSendMs = clientSendMs,
                    LastServerSendMs = lastServerSendMs
                };
                byte[] packet = ProtocolCodec.Encode(msg);
                await stream.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
                Interlocked.Increment(ref stats.Sent);
            }

            private async Task ReceiveLoopAsync(NetworkStream stream, LengthPrefixedPacketReader reader,
                byte[] buffer, CancellationToken token, long initialLastServerSendMs)
            {
                long lastServerSendMs = initialLastServerSendMs;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                        if (n == 0) break;
                        reader.Append(buffer.AsSpan(0, n));
                        while (reader.TryReadPacket(out var packet))
                        {
                            Interlocked.Increment(ref stats.Received);
                            // 简化解码：按字节头 4 字节判 msgId
                            if (packet.Length >= 4)
                            {
                                int msgId = BitConverter.ToInt32(packet.Span.Slice(0, 4));
                                if (msgId == 40011) // ServerTimeSync
                                {
                                    long now = Environment.TickCount64;
                                    var sync = ParseServerTimeSync(packet.Span.Slice(4));
                                    if (sync.HasValue)
                                    {
                                        var (rtt, offset) = Battle.Handlers.TimeSyncManager.Estimate(
                                            sync.Value.clientSendMs, now, lastServerSendMs,
                                            sync.Value.serverRecvMs, sync.Value.serverSendMs);
                                        lock (stats)
                                        {
                                            stats.RttSamples.Add(rtt);
                                            stats.OffsetSamples.Add(offset);
                                        }
                                        lastServerSendMs = sync.Value.serverSendMs;
                                    }
                                }
                                else if (msgId == 40002) // BattleJoinResult（简化：登录回包用 join result 占位）
                                {
                                    Interlocked.Exchange(ref stats.LoginCompletedAtMs, Environment.TickCount64);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }

            // ---- WebSocket 实现（占位：默认未开启；启用时取消注释） ----
            private async Task RunWebSocketAsync(CancellationToken token)
            {
                // KBE-Gap-Review D8：WS 协议由 Gateway 单独监听（默认 31301），如未开启回退到 TCP
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(opts.Host, opts.Port, token).ConfigureAwait(false);
                using var stream = tcp.GetStream();
                await LoginAndAwaitAsync(stream, token).ConfigureAwait(false);
                // 简化：WS 模式按 TCP 同等处理
            }

            private static (long clientSendMs, long serverRecvMs, long serverSendMs)? ParseServerTimeSync(ReadOnlySpan<byte> payload)
            {
                try
                {
                    // 极简解析：固定布局 [int64 x4]（与生成 ServerTimeSync 字段顺序一致）
                    if (payload.Length < 32) return null;
                    long clientSend = BitConverter.ToInt64(payload.Slice(0, 8));
                    long serverRecv = BitConverter.ToInt64(payload.Slice(8, 8));
                    long serverSend = BitConverter.ToInt64(payload.Slice(16, 8));
                    return (clientSend, serverRecv, serverSend);
                }
                catch { return null; }
            }
        }

        public static async Task<int> Main(string[] args)
        {
            var opts = new BotOptions
            {
                Count = GetArg(args, "--count", 10),
                Host = GetArg(args, "--host", "127.0.0.1"),
                Port = GetArg(args, "--port", 31300),
                DurationSeconds = GetArg(args, "--duration", 10),
                RampUpMs = GetArg(args, "--rampup", 0),
                Scene = GetArg(args, "--scene", "default")
            };
            string protocol = GetArg(args, "--protocol", "tcp").ToLowerInvariant();
            opts.Protocol = protocol switch
            {
                "kcp" => BotProtocol.Kcp,
                "ws" => BotProtocol.Ws,
                _ => BotProtocol.Tcp
            };

            Console.WriteLine($"Bots 压测启动: {opts.Count} 个机器人 -> {opts.Host}:{opts.Port} 协议={opts.Protocol} 场景={opts.Scene} 时长={opts.DurationSeconds}s");
            Console.WriteLine("（需服务器已启动：DB → Center → Login → Game/Battle → Gateway）");

            var overallStart = Stopwatch.StartNew();

            var bots = new List<Bot>(opts.Count);
            for (int i = 0; i < opts.Count; i++)
            {
                bots.Add(new Bot(opts, i));
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(opts.DurationSeconds + 5));
            var token = cts.Token;

            // Ramp-up：每 N ms 启动 1 个，避免瞬时连接风暴
            var tasks = new List<Task>(opts.Count);
            for (int i = 0; i < bots.Count; i++)
            {
                var b = bots[i];
                tasks.Add(Task.Run(async () =>
                {
                    if (opts.RampUpMs > 0)
                    {
                        await Task.Delay(opts.RampUpMs * i, token).ConfigureAwait(false);
                    }
                    await b.RunAsync(token).ConfigureAwait(false);
                }, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            overallStart.Stop();

            PrintReport(bots, opts, overallStart.Elapsed);
            return 0;
        }

        private static void PrintReport(List<Bot> bots, BotOptions opts, TimeSpan total)
        {
            long totalSent = 0, totalRecv = 0, totalErrors = 0;
            var allRtt = new List<long>();
            var allOffset = new List<long>();
            int completed = 0;
            foreach (var b in bots)
            {
                totalSent += b.Sent;
                totalRecv += b.Received;
                totalErrors += b.Errors;
                if (b.LoginCompletedAtMs > 0) completed++;
                lock (b.Stats)
                {
                    allRtt.AddRange(b.RttSamples);
                    allOffset.AddRange(b.OffsetSamples);
                }
            }

            double seconds = total.TotalSeconds;
            Console.WriteLine();
            Console.WriteLine("===== 压测结果 =====");
            Console.WriteLine($"机器人数:    {bots.Count}");
            Console.WriteLine($"登录完成:    {completed}/{bots.Count}");
            Console.WriteLine($"总发送:      {totalSent} 条 | 总接收: {totalRecv} 条 | 错误: {totalErrors}");
            Console.WriteLine($"发送速率:    {(totalSent / seconds).ToString("F1")} msg/s | 接收速率: {(totalRecv / seconds).ToString("F1")} msg/s");
            Console.WriteLine($"平均每机器人: 发送 {(totalSent / Math.Max(1, bots.Count)).ToString("F1")} 接收 {(totalRecv / Math.Max(1, bots.Count)).ToString("F1")} 条");

            if (allRtt.Count > 0)
            {
                allRtt.Sort();
                Console.WriteLine();
                Console.WriteLine("===== EntitySync RTT 分布（ms）=====");
                Console.WriteLine($"样本: {allRtt.Count} | avg: {Avg(allRtt):F2} | p50: {Percentile(allRtt, 0.50):F1} | p95: {Percentile(allRtt, 0.95):F1} | p99: {Percentile(allRtt, 0.99):F1} | max: {allRtt[^1]}");
            }
            if (allOffset.Count > 0)
            {
                allOffset.Sort();
                Console.WriteLine();
                Console.WriteLine("===== 时间同步 offset 分布（ms，绝对值越小越好）=====");
                Console.WriteLine($"样本: {allOffset.Count} | avg(绝对值): {AvgAbs(allOffset):F2} | p50(绝对值): {PercentileAbs(allOffset, 0.50):F1} | p95(绝对值): {PercentileAbs(allOffset, 0.95):F1} | max(绝对值): {AbsMax(allOffset)}");
            }
        }

        private static double Avg(List<long> xs) => xs.Count == 0 ? 0 : xs.Average();
        private static double Percentile(List<long> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            int idx = (int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1);
            return sorted[idx];
        }
        private static double AvgAbs(List<long> xs) => xs.Count == 0 ? 0 : xs.Select(Math.Abs).Average();
        private static double PercentileAbs(List<long> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            var abs = sorted.Select(Math.Abs).ToList();
            abs.Sort();
            int idx = (int)Math.Clamp(p * (abs.Count - 1), 0, abs.Count - 1);
            return abs[idx];
        }
        private static long AbsMax(List<long> xs) => xs.Count == 0 ? 0 : xs.Max(Math.Abs);

        private static string GetArg(string[] args, string key, string defaultValue)
        {
            int idx = Array.IndexOf(args, key);
            if (idx >= 0 && idx + 1 < args.Length)
            {
                return args[idx + 1];
            }
            return defaultValue;
        }

        private static int GetArg(string[] args, string key, int defaultValue)
        {
            int value;
            if (int.TryParse(GetArg(args, key, defaultValue.ToString()), out value))
            {
                return value;
            }
            return defaultValue;
        }
    }
}
