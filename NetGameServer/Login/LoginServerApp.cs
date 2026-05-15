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
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<byte[]>> PendingRequests = new System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<byte[]>>();

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

            // 注册并启动服务器
            networkManager.RegisterServer("LoginTcp", tcpServer);
            networkManager.Router.UnbindServer(tcpServer);

            await networkManager.StartServerAsync("LoginTcp", port);
            Shared.Log.Info($"登录服务器已启动，监听端口: {port}");

            // 初始化与 DB 的连接（用于 UID 同步或持久化操作）
            var dbClient = ConnectToDatabase();
            var loginHandler = new Login.Handlers.LoginHandler(dbClient);
            Login.Managers.SessionManager.Instance.OnUserOfflineAction = (userId) => { _ = loginHandler.HandleOfflineAsync(userId); };

            // 构建消息处理器字典，按 MsgId 分发
            var messageHandlers = Login.Handlers.MessageRouter.BuildHandlers(loginHandler);

            // 给 SessionManager 设置 SendToGatewayAction，使它可以广播特殊包(例如踢人)到网关，这通过向任意活动网关session发送来完成，因为包头带了真实客户端长ID
            // 如果我们需要支持多个网关的话，我们可能需要跟踪注册的网关会话，但由于LoginServer没有在 tcpServer 上公开已连接会话的集合，这里我们需要一个集合或者把 session存在某处
            // 在此为了简化，我们在 tcpServer.OnSessionConnected 中存储所有活跃的网关会话，然后选取一个发送。
            var activeGatewaySessions = new System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession>();

            tcpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"网关已连接: {session.RemoteEndPoint}");
                activeGatewaySessions[session.SessionId] = session;
            };
            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Shared.Log.Info($"网关断开连接，原因: {reason}");
                activeGatewaySessions.TryRemove(session.SessionId, out _);
            };

            Login.Managers.SessionManager.Instance.SendToGatewayAction = (clientSessionId, packetData) =>
            {
                // 发送到负责的网关。
                foreach (var session in activeGatewaySessions.Values)
                {
                    // [SessionId(8)][MsgId(4)][Payload]
                    // 但网关直接转发到客户端，SendToGatewayAction 的 packetData 并没有加客户端 SessionId，我们需要包装一下
                    byte[] wrapperMsg = new byte[8 + packetData.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(wrapperMsg.AsSpan(0, 8), clientSessionId);
                    packetData.CopyTo(wrapperMsg.AsSpan(8));

                    session.Send(wrapperMsg);
                    break; // 假设所有网关都可以互通，或者按某种逻辑路由。一般只需转给连过来的那个网关
                }
            };

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

        /// <summary>
        /// 建立与 DB 服务器的 TCP 连接，并在连接成功后请求当前最大 UID 用于 UID 生成器的初始化。
        /// </summary>
        /// <returns>返回已连接的 TcpClientWrapper 实例。</returns>
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

                    if (data.Length >= 12)
                    {
                        long requestId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(4, 8));
                        if (PendingRequests.TryRemove(requestId, out var tcs))
                        {
                            try
                            {
                                tcs.TrySetResult(data.Span.Slice(12).ToArray());
                            }
                            catch (Exception ex)
                            {
                                Shared.Log.Error($"反序列化响应异常: {ex}");
                            }
                            return;
                        }
                    }

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

        /// <summary>
        /// 启动 ASP.NET Core Web API 服务，提供登录相关的 HTTP 接口（如注册、登录、修改密码等）。
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>一个表示异步操作的任务</returns>
        public static async Task StartWebApiAsync(string[] args)
        {
            int apiPort = ConfigHelper.GetConfig<int>("ApiPort") == 0 ? 30003 : ConfigHelper.GetConfig<int>("ApiPort");

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(apiPort);
            });

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
            _ = app.RunAsync();
        }
    }
}
