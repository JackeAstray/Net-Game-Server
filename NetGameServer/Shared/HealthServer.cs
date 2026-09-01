using System.Net;
using System.Net.Sockets;
using System.Text;
using Framework.Core;

namespace Shared;

/// <summary>
/// 轻量健康检查 HTTP 服务（对标 GeekServer/K8s 健康探针经验，迭代 21）。
/// 用原始 TcpListener 实现极简 HTTP/1.1，零依赖、免 URL ACL、跨平台：
/// - GET /healthz → 200（存活探针，进程活着即 200）
/// - GET /readyz  → 200（就绪探针）；排空/关闭中（NodeLifecycle.IsDraining）→ 503
/// - GET /        → 200 节点元信息（nodeId / 端口 / 后端）
/// 其余路径 → 404。响应 JSON，Connection: close，逐个连接处理（健康探针频率低，足够）。
/// </summary>
public sealed class HealthServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly string nodeId;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptLoop;

    public int Port { get; }
    public bool IsDraining => NodeLifecycle.Default.IsDraining;

    public HealthServer(int port, string nodeId)
    {
        this.nodeId = nodeId;
        Port = port;
        listener = new TcpListener(IPAddress.Loopback, port);
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// 启动健康检查服务（后台运行，不阻塞调用线程）。
    /// 配置优先：HealthPort；未配置时由调用方传默认端口（一般 = 业务端口 + 10000）。
    /// </summary>
    public static HealthServer Start(int port, string nodeId)
    {
        return new HealthServer(port, nodeId);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            listener.Start();
            Log.Info($"健康检查服务已启动: http://127.0.0.1:{Port}（/healthz 存活, /readyz 就绪, node={nodeId}）");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"健康检查服务启动失败端口:{Port}");
            return;
        }

        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "健康检查 accept 异常");
                continue;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                // 只读请求行即可（健康探针 GET /healthz HTTP/1.1）
                byte[] requestLine = await ReadRequestLineAsync(stream);
                string path = ParsePath(requestLine);

                (int status, string body) = BuildResponse(path);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 503 ? "Service Unavailable" : "Not Found")}\r\n" +
                    $"Content-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n" +
                    "\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("健康检查连接处理异常: {Message}", ex.Message);
        }
    }

    private (int Status, string Body) BuildResponse(string path)
    {
        switch (path)
        {
            case "/healthz":
                return (200, "{\"status\":\"ok\",\"service\":\"liveness\",\"node\":\"" + JsonEscape(nodeId) + "\"}");
            case "/readyz":
                if (IsDraining)
                {
                    return (503, "{\"status\":\"draining\",\"service\":\"readiness\",\"node\":\"" + JsonEscape(nodeId) + "\"}");
                }
                return (200, "{\"status\":\"ready\",\"service\":\"readiness\",\"node\":\"" + JsonEscape(nodeId) + "\"}");
            case "/":
                return (200, "{\"status\":\"ok\",\"service\":\"net-game-server\",\"node\":\"" + JsonEscape(nodeId) + "\",\"port\":" + Port + "}");
            default:
                return (404, "{\"status\":\"not_found\"}");
        }
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<byte[]> ReadRequestLineAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var list = new List<byte>(128);
        int read;
        // 读到 CRLF 结束请求行（简单防御：最多 1024 字节）
        while (list.Count < 1024 && (read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                list.Add(buffer[i]);
                if (list.Count >= 2 && list[^2] == (byte)'\r' && list[^1] == (byte)'\n')
                {
                    return list.ToArray();
                }
            }
        }
        return list.ToArray();
    }

    private static string ParsePath(byte[] requestLine)
    {
        // "GET /healthz HTTP/1.1"
        string line = Encoding.ASCII.GetString(requestLine);
        int sp1 = line.IndexOf(' ');
        if (sp1 < 0)
        {
            return "/";
        }
        int sp2 = line.IndexOf(' ', sp1 + 1);
        string path = sp2 < 0 ? line[(sp1 + 1)..] : line.Substring(sp1 + 1, sp2 - sp1 - 1);
        // 去掉 query string
        int q = path.IndexOf('?');
        return q >= 0 ? path[..q] : path;
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            listener.Stop();
        }
        catch
        {
            // ignore
        }
    }
}
