using Network;
using Network.Tcp;
using Shared;

namespace Game
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Game.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            Shared.RemoteLog.Initialize($"Game-{ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("GamePort")}");
            Log.Info("游戏服务器正在启动...");

            await GameServerApp.StartNetworkAsync();
            GameServerApp.ConnectToDatabase();

            Log.Info("服务器启动流程完成。按 Ctrl+C 退出。");
            await Task.Delay(Timeout.Infinite);
        }
    }
}