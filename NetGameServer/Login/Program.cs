using Network;
using Network.Tcp;
using Shared;
using Serilog;
using Log = Shared.Log;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;

namespace Login
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Login.log");
            Log.Info("登录服务器正在启动...");

            await LoginServerApp.StartNetworkAsync();
            await LoginServerApp.StartWebApiAsync(args);

            await Task.Delay(-1);
        }
    }
}