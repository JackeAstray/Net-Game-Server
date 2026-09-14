using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Logger;

/// <summary>
/// 日志聚合服务器核心（对标 KBE logger）：
/// 监听 UDP 端口接收各服务器上报的日志，按节点分文件落盘 + 控制台输出。
/// 独立进程（Program）或测试进程均可承载。
/// P2 加固（防日志洪泛打满磁盘/CPU、防伪造日志注入）：
/// - 可选 HMAC 鉴权（authSecret 非空时校验报文尾部 32 字节标签）
/// - 每节点 + 全局固定窗口限流
/// - 单文件大小上限 + 滚动，防单节点无限增长
/// </summary>
public sealed class LoggerServer : IDisposable
{
    private readonly UdpClient udp;
    private readonly string logDir;
    private readonly byte[]? authKey;
    private readonly CancellationTokenSource cts = new();
    private Task? receiveTask;

    // P2 修复：限流（接收循环单线程，计数器无需加锁）
    private static readonly int RateWindowMs = 1000;
    private const int MaxPacketsPerNodePerSecond = 1000;
    private const int MaxPacketsGlobalPerSecond = 20000;
    private const int MaxDistinctNodes = 1024;
    private readonly Dictionary<string, (long WindowStartTicks, int Count)> nodeRates = new();
    private long globalWindowStartTicks = Environment.TickCount64;
    private int globalWindowCount;
    private long droppedByRateLimit;
    private long rejectedAuth;
    private long lastDropLogTicks;

    // P2 修复（性能）：单文件大小上限（默认 256MB），超限滚动，最多保留 3 个滚动文件
    private const long MaxFileSizeBytes = 256L * 1024 * 1024;
    private const int MaxRolloverFiles = 3;

    /// <summary>日志落盘目录。</summary>
    public string LogDir => logDir;

    /// <summary>收到日志事件（level, nodeId, message），供测试断言。</summary>
    public event Action<string, string, string>? LogReceived;

    /// <param name="port">监听端口（默认 31320）</param>
    /// <param name="logDir">日志落盘目录（默认 ./logs/Logger）</param>
    /// <param name="authSecret">可选鉴权密钥；非空时要求报文尾部带 HMAC-SHA256 标签（发送端 RemoteLogClient 同密钥）。</param>
    public LoggerServer(int port = 31320, string? logDir = null, string? authSecret = null)
    {
        this.logDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "logs", "Logger");
        Directory.CreateDirectory(this.logDir);
        udp = new UdpClient(port);
        if (!string.IsNullOrWhiteSpace(authSecret))
        {
            authKey = Encoding.UTF8.GetBytes(authSecret);
        }
    }

    /// <summary>启动接收循环（后台任务）。</summary>
    public void Start()
    {
        receiveTask = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                ReadOnlySpan<byte> span = result.Buffer;

                // P2 鉴权：报文尾部 32 字节为 HMAC-SHA256(密钥, 报文体)
                if (authKey != null)
                {
                    if (span.Length < 34) continue;
                    int bodyLen = span.Length - 32;
                    using var hmac = new HMACSHA256(authKey);
                    byte[] expected = hmac.ComputeHash(span.Slice(0, bodyLen).ToArray());
                    byte[] actual = span.Slice(bodyLen).ToArray();
                    if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    {
                        long rejected = Interlocked.Increment(ref rejectedAuth);
                        LogDropWarning("鉴权失败丢弃（密钥不匹配或伪造报文）", rejected);
                        continue;
                    }
                    span = span.Slice(0, bodyLen);
                }

                // 格式：[nodeIdLen(2)][nodeId][level\t timestamp\t message]
                if (span.Length < 2) continue;

                int nodeLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
                if (span.Length < 2 + nodeLen) continue;

                string nodeId = Encoding.UTF8.GetString(span.Slice(2, nodeLen));
                string message = Encoding.UTF8.GetString(span.Slice(2 + nodeLen));

                // P2 限流：丢弃超窗报文（含伪造的高频注入）
                if (!AllowRate(nodeId))
                {
                    long dropped = Interlocked.Increment(ref droppedByRateLimit);
                    LogDropWarning("超出限流阈值丢弃", dropped);
                    continue;
                }

                string level = "INFO";
                var parts = message.Split('\t');
                if (parts.Length >= 2)
                {
                    level = parts[0];
                }

                LogReceived?.Invoke(level, nodeId, message);

                // 按节点分文件落盘（滚动按天 + 单文件大小上限滚动）
                string fileName = Path.Combine(logDir, $"{Sanitize(nodeId)}.{DateTime.UtcNow:yyyyMMdd}.log");
                AppendToFileWithRotation(fileName, $"{DateTime.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Logger 接收异常: {ex.Message}");
            }
        }
    }

    private void LogDropWarning(string reason, long total)
    {
        long now = Environment.TickCount64;
        if (now - lastDropLogTicks < 5000) return;
        lastDropLogTicks = now;
        Console.Error.WriteLine($"Logger 丢弃日志（{reason}，累计 {total} 条）");
    }

    /// <summary>固定窗口限流：全局 + 每节点（接收循环单线程，直接读写即可）。</summary>
    private bool AllowRate(string nodeId)
    {
        long now = Environment.TickCount64;

        if (now - globalWindowStartTicks >= RateWindowMs)
        {
            globalWindowStartTicks = now;
            globalWindowCount = 0;
        }
        if (++globalWindowCount > MaxPacketsGlobalPerSecond) return false;

        if (!nodeRates.TryGetValue(nodeId, out var entry) || now - entry.WindowStartTicks >= RateWindowMs)
        {
            // 防 nodeRates 被伪造 nodeId 撑爆：超过上限的新节点直接丢弃
            if (!nodeRates.ContainsKey(nodeId) && nodeRates.Count >= MaxDistinctNodes) return false;
            nodeRates[nodeId] = (now, 1);
            return true;
        }

        if (entry.Count >= MaxPacketsPerNodePerSecond) return false;
        // ValueTuple 是值类型，必须整体写回，否则 Count++ 只改副本、限流永不生效
        nodeRates[nodeId] = (entry.WindowStartTicks, entry.Count + 1);
        return true;
    }

    /// <summary>
    /// 追加写入并按大小滚动。
    /// </summary>
    /// <remarks>
    /// 已知性能取舍（**有意保留现状**）：现在每条日志做一次 <c>File.AppendAllText</c>（open/close）
    /// 与一次 <c>new FileInfo(...).Length</c>（stat）；限流上限为 1000/节点/秒，洪泛时开销量可观。
    /// 曾尝试改为持久 <c>StreamWriter</c> + 字节计数（与 Tools/Machine、Tools/Supervisor 的 AppendLog 对齐），
    /// 但 Windows 文件共享语义下会**破坏读取方**：
    ///   写入方持有句柄（access=Write, share=Read）时，读取方 <c>File.ReadAllText</c>（access=Read, share=Read）
    ///   要求写入方的 Write 权限 ⊆ 自己的 share（Read）→ 不成立 → 抛 IOException
    ///   “The process cannot access the file because it is being used by another process”。
    /// 而 <c>HttpLogViewer</c> 与 <c>LoggerServer</c> **运行在同一进程**（且外部还可能用 Get-Content/编辑器读），
    /// 故修复需 writer + viewer + 外部读取方三处协同改用 <c>FileShare.ReadWrite</c>，风险远大于收益。
    /// 若要真正做到，建议直接改用成熟日志库的滚动文件 sink（自带共享语义处理）。
    /// </remarks>
    private void AppendToFileWithRotation(string fileName, string line)
    {
        try
        {
            if (File.Exists(fileName) && new FileInfo(fileName).Length >= MaxFileSizeBytes)
            {
                if (!RotateFile(fileName))
                {
                    Console.Error.WriteLine($"Logger 文件滚动达上限，丢弃写入: {fileName}");
                    return;
                }
            }
            File.AppendAllText(fileName, line);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Logger 落盘异常 {fileName}: {ex.Message}");
        }
    }

    private static bool RotateFile(string fileName)
    {
        for (int i = MaxRolloverFiles; i >= 1; i--)
        {
            string src = i == 1 ? fileName : $"{fileName}.{i - 1}";
            string dst = $"{fileName}.{i}";
            if (File.Exists(src))
            {
                try
                {
                    File.Move(src, dst, overwrite: true);
                }
                catch
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string Sanitize(string nodeId)
    {
        var sb = new StringBuilder(nodeId.Length);
        foreach (var c in nodeId)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        cts.Cancel();
        udp.Close();
        udp.Dispose();
    }
}
