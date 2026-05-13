using System;
using System.Threading.Tasks;
using Shared;
using Log = Shared.Log;

namespace Battle
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Battle.log");
            Log.Info("战斗/房间服务器(Battle Server)正在启动...");

            await BattleServerApp.StartNetworkAsync();

            Log.Info("战斗服务器启动完成...");

            await Task.Delay(-1);
        }
    }
}
