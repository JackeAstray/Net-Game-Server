using Microsoft.AspNetCore.Builder;
using Shared;
using Log = Shared.Log;

namespace Center
{
    /// <summary>
    /// 中心服务器/调度服务器
    /// 负责管理整个集群的状态，记录玩家所在节点，处理跨服社交等调度任务。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Center.log");
            Log.Info("中心服务器(Center Server)正在启动...");

            await CenterServerApp.StartNetworkAsync();

            int httpPort = ConfigHelper.GetConfig<int>("CenterHttpPort") == 0 ? 31316 : ConfigHelper.GetConfig<int>("CenterHttpPort");
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(httpPort);
            });

            var app = builder.Build();

            app.MapGet("/health", () => Results.Ok(new
            {
                status = "ok",
                nodeCount = Center.Handlers.NodeManager.Instance.GetNodeCount(),
                timestamp = DateTime.UtcNow
            }));

            app.MapGet("/nodes", () => Results.Ok(Center.Handlers.NodeManager.Instance.GetNodeSnapshots()));

            app.MapGet("/summary", () =>
            {
                var nodes = Center.Handlers.NodeManager.Instance.GetNodeSnapshots();
                return Results.Ok(new
                {
                    total = nodes.Count,
                    battle = nodes.Count(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase)),
                    game = nodes.Count(n => n.NodeType.Equals("Game", StringComparison.OrdinalIgnoreCase)),
                    gateway = nodes.Count(n => n.NodeType.Equals("Gateway", StringComparison.OrdinalIgnoreCase)),
                    login = nodes.Count(n => n.NodeType.Equals("Login", StringComparison.OrdinalIgnoreCase)),
                    timestamp = DateTime.UtcNow
                });
            });

            Log.Info($"中心服务器启动完成，等待其他服务节点接入。监控 HTTP 端口: {httpPort}");
            await app.RunAsync();
        }
    }
}
