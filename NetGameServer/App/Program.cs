using Network;
using Network.Tcp;
using Shared;

namespace App
{
    /// <summary>
    /// 应用节点（AppServer）入口 —— "游戏服务器 + 应用服务器"并存承载节点。
    /// 与游戏节点一致：内部 TCP 接收网关消息（[MsgId][Payload] + 路由元数据）、注册 Center、
    /// HMAC 内部认证、优雅关闭；额外承载 ASP.NET Core REST API（应用面）。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("AppPort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("AppHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/App.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效）
            string nodeId = launch.NodeId
                ?? $"App-{ConfigHelper.GetConfig<string>("AppHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("AppPort")}";
            Shared.RemoteLog.Initialize(nodeId);
            Log.Info($"App 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("应用服务器正在启动...");

            await AppServerApp.StartNetworkAsync();
            await AppServerApp.StartHttpAsync(args);

            Log.Info("服务器启动流程完成。按 Ctrl+C 退出。");

            // 健康检查 + 优雅关闭
            int healthPort = ConfigHelper.GetConfig<int>("HealthPort") == 0 ? 31308 + 10000 : ConfigHelper.GetConfig<int>("HealthPort");
            Shared.HealthServer.Start(healthPort, nodeId);
            NodeLifecycle.Default.RegisterShutdownHook(AppServerApp.ShutdownAsync);
            await NodeLifecycle.Default.WaitForShutdownAsync();
            await NodeLifecycle.Default.RunShutdownAsync();
        }
    }
}
