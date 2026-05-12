using Network;
using Network.Tcp;
using Shared;
using Serilog;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Log = Shared.Log;

namespace Gateway
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Gateway.log");
            Log.Info("网关服务器正在启动...");

            await GatewayServerApp.StartNetworkAsync();
            await GatewayServerApp.StartReverseProxyAsync(args);

            await Task.Delay(-1);
        }
    }
}