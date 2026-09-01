using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace DB
{
    internal class Program
    {
        public static ServiceProvider ServiceProvider => DbServerApp.ServiceProvider;


        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数（迭代 20）
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("DBPort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("DBHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/DB.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            string nodeId = launch.NodeId
                ?? $"DB-{ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("DBPort")}";
            Shared.RemoteLog.Initialize(nodeId);
            Log.Info($"DB 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("DB服务器正在启动...");

            await DbServerApp.InitializeDatabase();
            await DbServerApp.StartNetworkAsync();

            // 健康检查 + 优雅关闭（迭代 21）
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31305 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            Shared.HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(() =>
            {
                Log.Info("DB 优雅关闭：断开业务连接（心跳超时自动摘除注册）。");
                return Task.CompletedTask;
            });
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
        }
    }
}
