using Network;
using Network.Tcp;
using Shared;

namespace Game
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数（迭代 20）
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("GamePort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("GameHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/Game.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            string nodeId = launch.NodeId
                ?? $"Game-{ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("GamePort")}";
            Shared.RemoteLog.Initialize(nodeId);
            Log.Info($"Game 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("游戏服务器正在启动...");

            await GameServerApp.StartNetworkAsync();
            GameServerApp.ConnectToDatabase();

            Log.Info("服务器启动流程完成。按 Ctrl+C 退出。");

            // 健康检查 + 优雅关闭（迭代 21）
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31304 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            Shared.HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(() =>
            {
                Log.Info("Game 优雅关闭：断开后端连接（心跳超时自动摘除注册）。");
                return Task.CompletedTask;
            });
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
        }
    }
}
