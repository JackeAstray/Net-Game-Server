using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Framework.Core;

/// <summary>
/// 远程日志上报客户端（对标 KBE logger 的日志聚合）：
/// - 订阅 Log.LogSink，把本服务器日志异步批量上报到 Logger 进程
/// - 批量发送（每 500ms 或满 64 条），UDP 无连接语义，失败静默降级（不影响业务）
/// 报文格式：[NodeId(4)][level(4)][message(N)] UTF-8 文本
/// 可选鉴权：authSecret 非空时，报文尾部追加 32 字节 HMAC-SHA256(密钥, 报文体) 标签，
/// Logger 进程配置同密钥后校验（防伪造日志注入）。
/// </summary>
public sealed class RemoteLogClient : IDisposable
{
    private const int MaxBatchSize = 64;
    private const int MaxPendingLogs = 4096;
    private const int HmacTagLength = 32;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly string nodeId;
    private readonly System.Net.IPEndPoint loggerEndpoint;
    private readonly byte[]? authKey;
    // P3 优化：ThreadLocal 复用 HMAC 实例（OnLog 在业务热路径同步调用，每条日志 new HMACSHA256 会产生可观 GC）
    private readonly ThreadLocal<System.Security.Cryptography.HMACSHA256>? hmacPerThread;
    private readonly ConcurrentQueue<byte[]> pending = new();
    private readonly UdpClient udp;
    private readonly CancellationTokenSource cts = new();
    private Task? flushTask;
    private bool started;
    private int droppedLogs;

    /// <param name="nodeId">节点标识（如 "Battle-127.0.0.1:31307"）</param>
    /// <param name="loggerHost">Logger 进程地址</param>
    /// <param name="loggerPort">Logger 进程端口（默认 31320）</param>
    /// <param name="authSecret">可选鉴权密钥；与 LoggerServer 一致时启用 HMAC 标签。</param>
    public RemoteLogClient(string nodeId, string loggerHost, int loggerPort = 31320, string? authSecret = null)
    {
        this.nodeId = nodeId;
        // P2 修复：主机名解析失败（DNS 故障/配置错误）不得让节点启动崩溃——回退回环并告警，日志聚合降级为本地。
        System.Net.IPAddress loggerIp;
        if (System.Net.IPAddress.TryParse(loggerHost, out var parsedIp))
        {
            loggerIp = parsedIp;
        }
        else
        {
            try
            {
                var addresses = System.Net.Dns.GetHostAddresses(loggerHost);
                loggerIp = addresses.Length > 0 ? addresses[0] : throw new System.Net.Sockets.SocketException();
            }
            catch (Exception ex)
            {
                Log.Warn($"远程日志主机解析失败 LoggerHost:{loggerHost}，回退 127.0.0.1，远程日志聚合不可用: {ex.Message}");
                loggerIp = System.Net.IPAddress.Loopback;
            }
        }
        loggerEndpoint = new System.Net.IPEndPoint(loggerIp, loggerPort);
        if (!string.IsNullOrWhiteSpace(authSecret))
        {
            authKey = Encoding.UTF8.GetBytes(authSecret);
            hmacPerThread = new ThreadLocal<System.Security.Cryptography.HMACSHA256>(
                () => new System.Security.Cryptography.HMACSHA256(authKey));
        }
        udp = new UdpClient();
        udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
    }

    /// <summary>启动：订阅日志事件并启动批量冲刷任务。</summary>
    public void Start()
    {
        if (started) return;
        started = true;
        Log.LogSink += OnLog;
        flushTask = Task.Run(FlushLoopAsync);
        Log.Info($"RemoteLogClient 启动: {nodeId} -> {loggerEndpoint}");
    }

    private void OnLog(string level, string message)
    {
        // [nodeIdLen(2)][nodeId][level(1)][timestamp(8)][message][hmac(32) 可选]
        string line = $"{level}\t{DateTime.UtcNow:O}\t{message}";
        byte[] payload = Encoding.UTF8.GetBytes(line);
        int hmacLen = authKey != null ? HmacTagLength : 0;
        byte[] packet = new byte[2 + Encoding.UTF8.GetByteCount(nodeId) + payload.Length + hmacLen];
        int offset = 0;
        var nodeBytes = Encoding.UTF8.GetBytes(nodeId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)nodeBytes.Length);
        offset = 2;
        nodeBytes.CopyTo(packet, offset);
        offset += nodeBytes.Length;
        payload.CopyTo(packet, offset);

        // P2 鉴权：报文尾部追加 HMAC-SHA256 标签（Logger 同密钥校验）
        if (hmacPerThread != null)
        {
            var hmac = hmacPerThread.Value!; // ThreadLocal.Value 可空标注；工厂保证非空
            hmac.ComputeHash(packet, 0, 2 + nodeBytes.Length + payload.Length)
               .CopyTo(packet, 2 + nodeBytes.Length + payload.Length);
        }

        // V19 修复：上报队列有界——Logger 不可达/积压时丢弃超额日志并低频率告警，防无界内存增长。
        // UDP 日志聚合为尽力而为语义，丢弃不影响业务。
        if (pending.Count >= MaxPendingLogs)
        {
            int dropped = Interlocked.Increment(ref droppedLogs);
            if (dropped == 1 || (dropped & 1023) == 0)
            {
                Serilog.Log.Warning($"远程日志上报队列已满，丢弃日志（累计已丢 {dropped} 条）");
            }
            return;
        }
        pending.Enqueue(packet);
    }

    private async Task FlushLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, cts.Token);
                Flush();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 上报失败静默降级（本地日志已记录，不阻塞业务）
                Serilog.Log.Warning($"远程日志上报异常: {ex.Message}");
            }
        }
        Flush();
    }

    private void Flush()
    {
        int count = 0;
        while (count < MaxBatchSize && pending.TryDequeue(out var packet))
        {
            udp.Send(packet, packet.Length, loggerEndpoint);
            count++;
        }
    }

    public void Dispose()
    {
        Log.LogSink -= OnLog;
        cts.Cancel();
        udp.Close();
        udp.Dispose();
        hmacPerThread?.Dispose();
    }
}
