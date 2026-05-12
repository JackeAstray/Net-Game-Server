using Network;
using Network.Tcp;
using Shared;

namespace Game
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Game.log");
            Log.Info("游戏服务器正在启动...");

            await GameServerApp.StartNetworkAsync();
            GameServerApp.ConnectToDatabase();

            Log.Info("服务器启动流程完成。按 Ctrl+C 退出。");
            await Task.Delay(Timeout.Infinite);
        }
    }
}