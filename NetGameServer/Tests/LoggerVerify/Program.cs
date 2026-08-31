using System.Net.Sockets;
using System.Text;
using Framework.Core;
using Logger;

namespace LoggerVerify;

/// <summary>
/// Logger 端到端验证入口。
/// 覆盖：LoggerServer 端到端接收 RemoteLogClient 上报 + 落盘校验。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        // ===== Logger 端到端验证：LoggerServer（线程内）+ RemoteLogClient 上报 =====
        // 1. 启动日志聚合服务器（线程内承载，等价于独立进程行为）
        int loggerPort = 31321;
        var server = new LoggerServer(loggerPort, logDir: Path.Combine(Path.GetTempPath(), $"logger_test_{Guid.NewGuid():N}"));
        var received = new List<(string level, string node, string msg)>();
        server.LogReceived += (level, node, msg) => received.Add((level, node, msg));
        server.Start();

        // 1.5 配置全局日志级别（真实服务器由各 Program.cs 的 Log.Configure 完成；
        // 级别门控要求 Logger 已配置，否则 IsEnabled 恒为 false，LogSink 不会触发）
        Framework.Core.Log.Configure(enableConsoleLog: false, logFilePath: Path.Combine(Path.GetTempPath(), $"loggertest_{Guid.NewGuid():N}.log"));

        // 2. RemoteLogClient 上报（模拟 Battle 服务器日志）
        var client = new RemoteLogClient("Battle-test-node", "127.0.0.1", loggerPort);
        client.Start();
        Log.Info("测试日志: Battle 服务器启动");
        Log.Warn("测试日志: 玩家连接异常");
        Log.Error("测试日志: 数据库连接失败");
        await Task.Delay(1500); // 等待批量冲刷（500ms 间隔）
        client.Dispose();

        // 3. 校验收到
        Console.WriteLine($"收到日志数: {received.Count} (期望 3)");
        foreach (var (level, node, msg) in received)
        {
            Console.WriteLine($"  [{level}] {node} {msg}");
        }

        bool hasInfo = received.Any(r => r.msg.Contains("Battle 服务器启动"));
        bool hasWarn = received.Any(r => r.msg.Contains("玩家连接异常"));
        bool hasError = received.Any(r => r.msg.Contains("数据库连接失败"));
        Console.WriteLine($"校验: Info={hasInfo} Warn={hasWarn} Error={hasError} (期望 True/True/True)");
        if (!hasInfo || !hasWarn || !hasError || received.Count < 3) return 1;

        // 4. 校验落盘文件（Sanitize 把 - 替换为 _）
        string logFile = Directory.GetFiles(server.LogDir, "Battle_test_node*.log").FirstOrDefault() ?? string.Empty;
        Console.WriteLine($"落盘文件: {Path.GetFileName(logFile)} 存在={File.Exists(logFile)}");
        if (!File.Exists(logFile)) return 1;
        string content = File.ReadAllText(logFile);
        Console.WriteLine($"落盘内容行数: {content.Split('\n').Length} (期望 >=3)");
        if (content.Split('\n').Length < 3) return 1;

        server.Dispose();
        Console.WriteLine("\n===== Logger 验证通过 =====");
        return 0;
    }
}
