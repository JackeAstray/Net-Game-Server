using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Logger;

/// <summary>
/// 日志聚合服务器核心（对标 KBE logger）：
/// 监听 UDP 端口接收各服务器上报的日志，按节点分文件落盘 + 控制台输出。
/// 独立进程（Program）或测试进程均可承载。
/// </summary>
public sealed class LoggerServer : IDisposable
{
    private readonly UdpClient udp;
    private readonly string logDir;
    private readonly CancellationTokenSource cts = new();
    private Task? receiveTask;

    /// <summary>日志落盘目录。</summary>
    public string LogDir => logDir;

    /// <summary>收到日志事件（level, nodeId, message），供测试断言。</summary>
    public event Action<string, string, string>? LogReceived;

    /// <param name="port">监听端口（默认 31320）</param>
    /// <param name="logDir">日志落盘目录（默认 ./logs/Logger）</param>
    public LoggerServer(int port = 31320, string? logDir = null)
    {
        this.logDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "logs", "Logger");
        Directory.CreateDirectory(this.logDir);
        udp = new UdpClient(port);
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
                // 格式：[nodeIdLen(2)][nodeId][level\t timestamp\t message]
                ReadOnlySpan<byte> span = result.Buffer;
                if (span.Length < 2) continue;

                int nodeLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
                if (span.Length < 2 + nodeLen) continue;

                string nodeId = Encoding.UTF8.GetString(span.Slice(2, nodeLen));
                string message = Encoding.UTF8.GetString(span.Slice(2 + nodeLen));
                string level = "INFO";
                var parts = message.Split('\t');
                if (parts.Length >= 2)
                {
                    level = parts[0];
                }

                LogReceived?.Invoke(level, nodeId, message);

                // 按节点分文件落盘（滚动按天）
                string fileName = Path.Combine(logDir, $"{Sanitize(nodeId)}.{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(fileName, $"{DateTime.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}");
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
