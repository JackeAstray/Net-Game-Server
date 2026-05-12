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
    /// <summary>
    /// 登录服务器程序入口类。
    /// 负责启动登录相关的网络服务（TCP 网关连接）以及 HTTP API 服务，并初始化与数据库/Redis 的连接。
    /// </summary>
    public static class LoginServerApp
    {
        /// <summary>
        /// 启动用于接收网关连接的 TCP 服务并处理来自网关的数据包。
        /// 数据包结构为: [SessionId(8)][MsgId(4)][Payload]
        /// 该方法会:
        /// - 启动 NetworkManager 与 TcpServer
        /// - 绑定连接/断开/接收事件
        /// - 初始化与 DB 的连接并构建消息处理器映射
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 从配置读取监听端口，若未配置则使用默认 30002
            int port = ConfigHelper.GetConfig<int>("LoginPort") == 0 ? 30002 : ConfigHelper.GetConfig<int>("LoginPort");

            // 创建网络管理器与 TCP 服务器（用于网关连接）
            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            // 连接与断开事件日志
            tcpServer.OnSessionConnected += session => Shared.Log.Info($"网关已连接: {session.RemoteEndPoint}");
            tcpServer.OnSessionDisconnected += (session, reason) => Shared.Log.Info($"网关断开连接，原因: {reason}");

            // 注册并启动服务器
            networkManager.RegisterServer("LoginTcp", tcpServer);
            networkManager.Router.UnbindServer(tcpServer);

            await networkManager.StartServerAsync("LoginTcp", port);
            Shared.Log.Info($"登录服务器已启动，监听端口: {port}");

            // 初始化与 DB 的连接（用于 UID 同步或持久化操作）
            var dbClient = ConnectToDatabase();
            var loginHandler = new Login.Handlers.LoginHandler(dbClient);
            // 构建消息处理器字典，按 MsgId 分发
            var messageHandlers = Login.Handlers.MessageRouter.BuildHandlers(loginHandler);

            // 处理收到的数据: 先解析 SessionId 与 MsgId，再将 Payload 交给对应的处理器
            tcpServer.OnDataReceived += async (session, data) =>
            {
                if (data.Length < 12) return; // 无效包

                long clientSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                var payload = data.Slice(12);

                try
                {
                    if (messageHandlers.TryGetValue(msgId, out var handler))
                    {
                        // 调用对应的消息处理器，传入 payload、会话对象和客户端会话 ID
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
            // 从配置读取 DB 连接信息，若未配置则使用默认值
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);

            // 当与 DB 建立连接时，向 DB 请求当前最大 UID（用于 UID 生成器初始化）
            dbClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");

                var request = new Shared.Messages.Db.GetMaxUidRequest();
                byte[] data = Shared.Json.SerializeToUtf8Bytes(request);
                // 包格式: [MsgId(4) | Payload]
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1000);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            };

            // 处理从 DB 返回的数据，用于解析 MsgId 并处理 GetMaxUidResponse
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
                            // 初始化全局 UID 生成器
                            Shared.UIDGenerator.Initialize(currentRegionId, currentMaxSequenceFromDB);
                            Shared.Log.Info($"UID 生成器初始化完成，区服ID:{currentRegionId}，当前同步的最大序列:{currentMaxSequenceFromDB}");
                        }
                    }
                }
            };

            // DB 断线日志并开始异步连接
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
