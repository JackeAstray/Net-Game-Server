using Network;
using Network.Tcp;
using Shared;

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
            Log.Configure(true, "Login.log");
            Log.Info("登录服务器正在启动...");

            int port = ConfigHelper.GetConfig<int>("LoginPort");
            if (port == 0) port = 8182;

            int apiPort = ConfigHelper.GetConfig<int>("ApiPort");
            if (apiPort == 0) apiPort = 5000;

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) => Log.Info($"接收到数据，长度: {data.Length}");
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("LoginTcp", tcpServer);

            await networkManager.StartServerAsync("LoginTcp", port);
            Log.Info($"登录服务器已启动，监听端口: {port}");

            // 连接 DB
            int dbPort = ConfigHelper.GetConfig<int>("DBPort");
            if (dbPort == 0) dbPort = 8083;
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");
            _ = dbClient.ConnectAsync();

            // WebAPI for HTTP requests
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddSingleton<Login.Handlers.LoginHandler>();

            var app = builder.Build();
            app.MapControllers();

            Log.Info($"ASP.NET API已启动，正在监听 HTTP 端口 {apiPort}");
            _ = app.RunAsync($"http://*:{apiPort}");

            await Task.Delay(-1);
        }
    }
}