using Shared;
using Log = Shared.Log;

namespace Battle
{
    /// <summary>
    /// 战斗/房间服务器入口：初始化日志与远程日志上报，启动网络服务（tick 引擎/场景/帧同步）。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Battle.log");
            Log.Info("战斗/房间服务器(Battle Server)正在启动...");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            RemoteLog.Initialize($"Battle-{ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("BattlePort")}");

            await BattleServerApp.StartNetworkAsync();

            Log.Info("战斗服务器启动完成...");

            await Task.Delay(-1);
        }
    }
}
