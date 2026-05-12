using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Network;
using Network.Tcp;
using Serilog;

namespace Login
{
    public static class LoginServerApp
    {
        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("LoginPort") == 0 ? 30002 : ConfigHelper.GetConfig<int>("LoginPort");

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session => Shared.Log.Info($"网关已连接: {session.RemoteEndPoint}");
            tcpServer.OnSessionDisconnected += (session, reason) => Shared.Log.Info($"网关断开连接，原因: {reason}");

            networkManager.RegisterServer("LoginTcp", tcpServer);
            networkManager.Router.UnbindServer(tcpServer);

            await networkManager.StartServerAsync("LoginTcp", port);
            Shared.Log.Info($"登录服务器已启动，监听端口: {port}");

            var dbClient = ConnectToDatabase();
            var loginHandler = new Login.Handlers.LoginHandler(dbClient);
            var messageHandlers = Login.Handlers.MessageRouter.BuildHandlers(loginHandler);

            tcpServer.OnDataReceived += async (session, data) =>
            {
                if (data.Length < 12) return;

                long clientSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                var payload = data.Slice(12);

                try
                {
                    if (messageHandlers.TryGetValue(msgId, out var handler))
                    {
                        await handler(payload, session, clientSessionId);
                    }
                    else
                    {
                        Shared.Log.Warning($"收到未处理的消息类型 MsgId: {msgId}");
                    }
                }
                catch (System.Exception ex)
                {
                    Shared.Log.Error($"处理消息 MsgId:{msgId} 时出现异常: {ex}");
                }
            };
        }

        private static TcpClientWrapper ConnectToDatabase()
        {
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);

            dbClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");

                var request = new Shared.Messages.Db.GetMaxUidRequest();
                byte[] data = Shared.Json.SerializeToUtf8Bytes(request);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1000);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            };

            dbClient.OnDataReceived += (session, data) =>
            {
                if (data.Length >= 4)
                {
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    if (msgId == 1000)
                    {
                        var response = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.GetMaxUidResponse>(data.Span.Slice(4));
                        if (response != null)
                        {
                            long currentMaxSequenceFromDB = response.MaxUid;
                            int currentRegionId = ConfigHelper.GetConfig<int>("RegionId") == 0 ? 1 : ConfigHelper.GetConfig<int>("RegionId");
                            Shared.UIDGenerator.Initialize(currentRegionId, currentMaxSequenceFromDB);
                            Shared.Log.Info($"UID 生成器初始化完成，区服ID:{currentRegionId}，当前同步的最大序列:{currentMaxSequenceFromDB}");
                        }
                    }
                }
            };

            dbClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 DB 服务器断开连接: {reason}");
            _ = dbClient.ConnectAsync();

            return dbClient;
        }

        public static async Task StartWebApiAsync(string[] args)
        {
            int apiPort = ConfigHelper.GetConfig<int>("ApiPort") == 0 ? 30003 : ConfigHelper.GetConfig<int>("ApiPort");

            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog();
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            var loginHandler = new Login.Handlers.LoginHandler(dbClient);

            builder.Services.AddSingleton<TcpClientWrapper>(dbClient);
            builder.Services.AddSingleton<Login.Handlers.LoginHandler>(loginHandler);

            string redisConnStr = ConfigHelper.GetConfig<string>("RedisConnectionString") ?? "127.0.0.1:6379";
            Shared.RedisHelper.Initialize(redisConnStr);
            Shared.Log.Info("Redis 初始化成功。");

            var app = builder.Build();

            app.UseSwagger(options =>
            {
                options.RouteTemplate = "api/swagger/{documentName}/swagger.json";
            });
            app.UseSwaggerUI(options =>
            {
                options.RoutePrefix = "api/swagger";
                options.SwaggerEndpoint("/api/swagger/v1/swagger.json", "Login API V1");
            });

            app.MapControllers();

            Shared.Log.Info($"ASP.NET API已启动，正在监听 HTTP 端口 {apiPort}");
            _ = app.RunAsync($"http://*:{apiPort}");
        }
    }
}
