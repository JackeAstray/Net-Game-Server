using Shared;
using Log = Shared.Log;

namespace Center
{
    /// <summary>
    /// 中心服务器（调度服务器）入口：解析 machine 注入参数 → 初始化日志与远程日志上报 → 启动网络服务与节点注册管理。
    /// 启动参数（NodeLaunchArgs 统一解析，KBE machine 化，迭代 20）。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数（迭代 20）
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("CenterPort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("CenterHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/Center.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            string nodeId = launch.NodeId
                ?? $"Center-{ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("CenterPort")}";
            RemoteLog.Initialize(nodeId);
            Log.Info($"Center 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("中心服务器(Center Server)正在启动...");

            await CenterServerApp.StartNetworkAsync();
            // 管理台 HTTP 服务（Kestrel 阻塞运行；NodeLifecycle 关闭钩子会优雅停止它）
            var httpTask = CenterHttpServer.StartAsync(args);

            // 健康检查 + 优雅关闭（迭代 21）
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31306 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(CenterHttpServer.StopAsync);
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
            // 等 Kestrel 停止后 StartAsync 返回（优雅停服）
            await httpTask;
        }
    }
}
