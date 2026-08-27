using Shared;
using Log = Shared.Log;

namespace Center
{
    /// <summary>
    /// 中心服务器/调度服务器
    /// 负责管理整个集群的状态，记录玩家所在节点，处理跨服社交等调度任务。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Center.log");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            Shared.RemoteLog.Initialize($"Center-{ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("CenterPort")}");
            Log.Info("中心服务器(Center Server)正在启动...");

            await CenterServerApp.StartNetworkAsync();
            await CenterHttpServer.StartAsync(args);
        }
    }
}
