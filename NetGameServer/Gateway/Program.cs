using Shared;
using Log = Shared.Log;

namespace Gateway
{
    /// <summary>
    /// 网关服务器入口：解析 machine 注入参数 → 初始化日志与远程日志上报 → 启动客户端接入网络服务与反向代理。
    /// 启动参数（NodeLaunchArgs 统一解析，KBE machine 化，迭代 20）。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 解析并应用 machine 注入参数（迭代 20）
            var launch = NodeLaunchArgs.Parse(args);
            if (launch.Port.HasValue) ConfigHelper.SetRuntimeOverride("GatewayPort", launch.Port.Value.ToString());
            if (!string.IsNullOrEmpty(launch.Host)) ConfigHelper.SetRuntimeOverride("GatewayHost", launch.Host);
            NodeLaunchArgs.ApplyToConfigHelper(launch);

            Log.Configure(true, "Logs/Gateway.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            string nodeId = launch.NodeId
                ?? $"Gateway-{ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("GatewayPort")}";
            RemoteLog.Initialize(nodeId);
            Log.Info($"Gateway 节点标识: {nodeId} (instance={launch.InstanceId ?? "-"}, machine={launch.MachineId ?? "-"}, supervisedBy={launch.SupervisedBy ?? "none"})");

            Log.Info("网关服务器正在启动...");

            await GatewayServerApp.StartNetworkAsync();
            await GatewayServerApp.StartReverseProxyAsync(args);

            await Task.Delay(-1);
        }
    }
}
