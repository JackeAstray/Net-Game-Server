using Shared;
using Log = Shared.Log;

namespace Battle
{
    /// <summary>
    /// 战斗/房间服务器入口：解析 machine 注入参数 → 初始化日志与远程日志上报 → 启动网络服务。
    /// 启动参数（NodeLaunchArgs 统一解析，KBE machine 化，迭代 20）：
    ///   Battle.exe --port 31307 --host 127.0.0.1 --center-host 127.0.0.1 --center-port 31306
    ///              --node-id Battle-127.0.0.1:31307 --instance-id "Battle-1#1"
    ///              --machine-id machine-A --supervised-by machine
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数：把 --port 等写进 ConfigHelper 运行时覆盖
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("BattlePort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("BattleHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/Battle.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");
            Log.Info("战斗/房间服务器(Battle Server)正在启动...");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            // nodeId 优先级：machine 注入 > 按 host:port 派生
            string nodeId = launch.NodeId
                ?? $"Battle-{ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("BattlePort")}";
            RemoteLog.Initialize(nodeId);
            Log.Info($"Battle 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            await BattleServerApp.StartNetworkAsync();

            Log.Info("战斗服务器启动完成...");

            // 健康检查 + 优雅关闭（迭代 21）：/healthz 存活、/readyz 就绪，关服时 flush 实体持久化
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31307 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(BattleServerApp.ShutdownAsync);
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
        }
    }
}
