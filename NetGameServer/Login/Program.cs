using Shared;
using Log = Shared.Log;

namespace Login
{
    /// <summary>
    /// 登录服务器入口：解析 machine 注入参数 → 初始化日志与远程日志上报 → 启动网络服务与 HTTP API。
    /// 启动参数（NodeLaunchArgs 统一解析，KBE machine 化，迭代 20）。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数（迭代 20）
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("LoginPort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("LoginHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/Login.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            string nodeId = launch.NodeId
                ?? $"Login-{ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("LoginPort")}";
            RemoteLog.Initialize(nodeId);
            Log.Info($"Login 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("登录服务器正在启动...");

            await LoginServerApp.StartNetworkAsync();
            await LoginServerApp.StartWebApiAsync(args);

            // 健康检查 + 优雅关闭（迭代 21）
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31302 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(LoginServerApp.ShutdownAsync);
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
        }
    }
}
