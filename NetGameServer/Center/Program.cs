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
            await CenterHttpServer.StartAsync(args);
        }
    }
}
