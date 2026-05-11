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
            Log.Configure(true, "Logs/Login.log");
            Log.Info("登录服务器正在启动...");

            int port = ConfigHelper.GetConfig<int>("LoginPort");
            if (port == 0) port = 30002;

            int apiPort = ConfigHelper.GetConfig<int>("ApiPort");
            if (apiPort == 0) apiPort = 30003;

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
            if (dbPort == 0) dbPort = 30005;
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            dbClient.OnConnected += session =>
            {
                Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");

                // 向 DB 服务器请求当前最大 UID
                var request = new Shared.Messages.Db.GetMaxUidRequest();
                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1000); // 1000 是 GetMaxUidRequest 的 MsgId
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            };
            dbClient.OnDataReceived += (session, data) =>
            {
                if (data.Length >= 4)
                {
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    if (msgId == 1000) // 1000 是 GetMaxUidResponse 的 MsgId
                    {
                        var response = System.Text.Json.JsonSerializer.Deserialize<Shared.Messages.Db.GetMaxUidResponse>(data.Span.Slice(4));
                        if (response != null)
                        {
                            long currentMaxSequenceFromDB = response.MaxUid;
                            int currentRegionId = ConfigHelper.GetConfig<int>("RegionId") == 0 ? 1 : ConfigHelper.GetConfig<int>("RegionId");
                            Shared.UIDGenerator.Initialize(currentRegionId, currentMaxSequenceFromDB);
                            Log.Info($"UID 生成器初始化完成，区服ID:{currentRegionId}，当前同步的最大序列:{currentMaxSequenceFromDB}");
                        }
                    }
                }
            };
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");
            _ = dbClient.ConnectAsync();

            // WebAPI for HTTP requests
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddSingleton<TcpClientWrapper>(dbClient);
            builder.Services.AddSingleton<Login.Handlers.LoginHandler>();

            string redisConnStr = ConfigHelper.GetConfig<string>("RedisConnectionString") ?? "127.0.0.1:6379";
            Shared.RedisHelper.Initialize(redisConnStr);
            Log.Info("Redis 初始化成功。");

            var app = builder.Build();

            //// 配置HTTP请求管道
            //if (app.Environment.IsDevelopment() || true) // Enable swagger in all environments for testing
            //{
            app.UseSwagger(options =>
            {
                options.RouteTemplate = "api/swagger/{documentName}/swagger.json";
            });
            app.UseSwaggerUI(options =>
            {
                options.RoutePrefix = "api/swagger";
                options.SwaggerEndpoint("/api/swagger/v1/swagger.json", "Login API V1");
            });
            //}

            app.MapControllers();

            Log.Info($"ASP.NET API已启动，正在监听 HTTP 端口 {apiPort}");
            _ = app.RunAsync($"http://*:{apiPort}");

            await Task.Delay(-1);
        }
    }
}