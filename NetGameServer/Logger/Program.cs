namespace Logger;

/// <summary>
/// 日志聚合进程（对标 KBE logger）：
/// - 监听 UDP 端口（默认 31320），接收各服务器 RemoteLogClient 上报的日志
/// - 按节点分文件落盘（logs/Logger/<NodeId>.log），滚动按天
/// - 控制台实时输出
/// 启动顺序：Logger 可在任意时刻启动（其他服务器上报失败会自动降级，启动后恢复）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int port = args.Length > 1 && args[0] == "--port" && int.TryParse(args[1], out var p) ? p : 31320;

        Console.WriteLine($"Logger 日志聚合进程启动，监听 UDP 端口: {port}");
        Console.WriteLine("各服务器配置 LoggerHost/LoggerPort 后自动上报（默认 127.0.0.1:31320）");

        using var server = new LoggerServer(port);
        server.LogReceived += (level, nodeId, message) =>
        {
            Console.WriteLine($"[{nodeId}] {message}");
        };
        server.Start();

        // 保持运行直到 Ctrl+C
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        await tcs.Task;
        return 0;
    }
}
