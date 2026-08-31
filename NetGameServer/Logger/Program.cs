using System;
using System.Threading.Tasks;

namespace Logger
{
    /// <summary>
    /// 日志聚合进程（对标 KBE logger）：
    /// - 监听 UDP 端口（默认 31320），接收各服务器 RemoteLogClient 上报的日志
    /// - 按节点分文件落盘（logs/Logger/&lt;NodeId&gt;.log），滚动按天
    /// - 控制台实时输出
    /// 启动顺序：Logger 可在任意时刻启动（其他服务器上报失败会自动降级，启动后恢复）。
    /// </summary>
    internal class Program
    {
        public static async Task<int> Main(string[] args)
        {
            int port = 31320;
            if (args.Length > 1 && args[0] == "--port")
            {
                int parsedPort;
                if (int.TryParse(args[1], out parsedPort))
                {
                    port = parsedPort;
                }
            }

            Console.WriteLine("Logger 日志聚合进程启动，监听 UDP 端口: " + port);
            Console.WriteLine("各服务器配置 LoggerHost/LoggerPort 后自动上报（默认 127.0.0.1:31320）");

            // P2 鉴权：与各节点同密钥（各节点经 ConfigHelper 读取 LoggerAuthSecret，含环境变量）。
            string? authSecret = Environment.GetEnvironmentVariable("LoggerAuthSecret");
            if (!string.IsNullOrWhiteSpace(authSecret))
            {
                Console.WriteLine("Logger 已启用 HMAC 鉴权（LoggerAuthSecret）");
            }

            using (LoggerServer server = new LoggerServer(port, authSecret: authSecret))
            {
                server.LogReceived += OnLogReceived;
                server.Start();

                // 保持运行直到 Ctrl+C
                TaskCompletionSource<bool> exitSignal = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
                {
                    e.Cancel = true;
                    exitSignal.TrySetResult(true);
                };
                await exitSignal.Task;
            }
            return 0;
        }

        /// <summary>收到日志事件：控制台实时输出。</summary>
        private static void OnLogReceived(string level, string nodeId, string message)
        {
            Console.WriteLine("[" + nodeId + "] " + message);
        }
    }
}
