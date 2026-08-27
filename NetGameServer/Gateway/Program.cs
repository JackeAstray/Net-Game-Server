using Shared;
using Log = Shared.Log;

namespace Gateway
{
    /// <summary>
    /// 网关服务器入口：初始化日志与远程日志上报，启动客户端接入网络服务与反向代理。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Gateway.log");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            RemoteLog.Initialize($"Gateway-{ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("GatewayPort")}");
            Log.Info("网关服务器正在启动...");

            await GatewayServerApp.StartNetworkAsync();
            await GatewayServerApp.StartReverseProxyAsync(args);

            await Task.Delay(-1);
        }
    }
}
