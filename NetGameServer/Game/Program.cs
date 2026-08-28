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
            await Task.Delay(Timeout.Infinite);
        }
    }
}
