using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Routing;
using Network.Tcp;
using Serilog;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Login
{
    /// <summary>
    /// 登录服务器程序入口类。
    /// 负责启动登录相关的网络服务（TCP 网关连接）以及 HTTP API 服务，并初始化与数据库/Redis 的连接。
    /// </summary>
    public static class LoginServerApp
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<byte[]>> PendingRequests = new System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<byte[]>>();
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;
        private static readonly object sharedLoginSync = new object();
        private static TcpClientWrapper? sharedDbClient;
        private static Login.Handlers.LoginHandler? sharedLoginHandler;

        /// <summary>
        /// 启动用于接收网关连接的 TCP 服务并处理来自网关的数据包。
        /// 数据包结构为: [MsgId(4)][Payload]，路由信息通过 payload 中的 RouteMetadata（如 __clientSessionId）传递。
        /// 该方法会:
        /// - 启动 NetworkManager 与 TcpServer
        /// - 绑定连接/断开/接收事件
        /// - 初始化与 DB 的连接并构建消息处理器映射
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 从配置读取监听端口，若未配置则使用默认 31302
            int port = ConfigHelper.GetConfig<int>("LoginPort") == 0 ? 31302 : ConfigHelper.GetConfig<int>("LoginPort");

            // 创建 TCP 服务器（用于网关连接）
            var tcpServer = new TcpServer();

            await tcpServer.StartAsync(port);
            Shared.Log.Info($"登录服务器已启动，监听端口: {port}");

            // 初始化与 DB 的连接（用于 UID 同步或持久化操作）
            var loginHandler = GetOrCreateLoginHandler();
            Login.Managers.SessionManager.Instance.OnUserOfflineAction = (userId) => { _ = loginHandler.HandleOfflineAsync(userId); };

            // 构建消息处理器字典，按 MsgId 分发
            var messageHandlers = Login.Handlers.MessageRouter.BuildHandlers(loginHandler);

            // 跟踪所有活跃网关会话，并记录“客户端会话 -> 网关会话”的绑定，避免多网关场景下回包错路由。
            var activeGatewaySessions = new System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession>();
            var clientGatewayBindings = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();

            void RemoveClientGatewayBinding(long clientSessionId)
            {
                clientGatewayBindings.TryRemove(clientSessionId, out _);
            }

            void RemoveBindingsByGatewaySession(long gatewaySessionId)
            {
                foreach (var binding in clientGatewayBindings)
                {
                    if (binding.Value == gatewaySessionId)
                    {
                        clientGatewayBindings.TryRemove(binding.Key, out _);
                    }
                }
            }

            tcpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"网关已连接: {session.RemoteEndPoint}");
                activeGatewaySessions[session.SessionId] = session;
            };
            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Shared.Log.Info($"网关断开连接，原因: {reason}");
                activeGatewaySessions.TryRemove(session.SessionId, out _);
                RemoveBindingsByGatewaySession(session.SessionId);
            };

            Login.Managers.SessionManager.Instance.SendToGatewayAction = (clientSessionId, packetData) =>
            {
                if (packetData.Length < 4)
                {
                    Shared.Log.Warning("SendToGatewayAction 收到无效包（长度不足 4），已丢弃。");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packetData.AsSpan(0, 4));
                byte[] payload = packetData.AsSpan(4).ToArray();
                byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, clientSessionId);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
                byte[] outbound = packet.AsSpan(0, totalLength).ToArray();
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);

                if (clientGatewayBindings.TryGetValue(clientSessionId, out var gatewaySessionId))
                {
                    if (activeGatewaySessions.TryGetValue(gatewaySessionId, out var targetGatewaySession))
                    {
                        targetGatewaySession.Send(outbound);
                        return;
                    }

                    RemoveClientGatewayBinding(clientSessionId);
                }

                // 单网关时允许兜底重绑；多网关时不做广播，避免重复下发或错路由。
                if (activeGatewaySessions.Count == 1)
                {
                    foreach (var session in activeGatewaySessions.Values)
                    {
                        session.Send(outbound);
                        clientGatewayBindings[clientSessionId] = session.SessionId;
                        return;
                    }
                }

                if (activeGatewaySessions.Count > 1)
                {
                    Shared.Log.Error($"SendToGatewayAction 目标网关绑定缺失，且存在多网关连接，回包已丢弃以避免广播误投 ClientSessionId:{clientSessionId} 活跃网关数:{activeGatewaySessions.Count}");
                    return;
                }

                Shared.Log.Warning($"SendToGatewayAction 无可用网关会话，回包已丢弃 ClientSessionId:{clientSessionId}");
            };

            // 处理收到的数据: 统一协议 [MsgId][Payload]，路由元数据在 payload 内
            tcpServer.OnDataReceived += async (session, data) =>
            {
                if (data.Length < 4) return;

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                byte[] payload = data.Slice(4).ToArray();

                if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                {
                    Shared.Log.Warning($"Login 收到缺少路由元数据的消息 MsgId:{msgId}");
                    return;
                }

                clientGatewayBindings[clientSessionId] = session.SessionId;

                try
                {
                    if (messageHandlers.TryGetValue(msgId, out var handler))
                    {
                        await handler(cleanPayload, session, clientSessionId);

                        if (msgId == MessageIds.PlayerDisconnectNotif)
                        {
                            RemoveClientGatewayBinding(clientSessionId);
                        }
                    }
                    else
                    {
                        Shared.Log.Warning($"收到未处理的消息类型 MsgId: {msgId}");

                        if (msgId >= 10000 && msgId < 20000)
                        {
                            int responseMsgId = msgId switch
                            {
                                MessageIds.LoginReq => MessageIds.LoginRes,
                                MessageIds.RegisterReq => MessageIds.RegisterRes,
                                MessageIds.LogoutReq => MessageIds.LogoutRes,
                                MessageIds.ResetPasswordReq => MessageIds.ResetPasswordRes,
                                MessageIds.UpdateNicknameReq => MessageIds.UpdateNicknameRes,
                                MessageIds.FindPasswordWithCodeReq => MessageIds.FindPasswordWithCodeRes,
                                _ => 0
                            };

                            if (responseMsgId > 0)
                            {
                                string errorMessage = $"未支持的登录消息类型: {msgId}";
                                byte[] unknownPayload = responseMsgId switch
                                {
                                    MessageIds.LoginRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.LoginResponse
                                    {
                                        Success = false,
                                        Message = errorMessage,
                                        UserId = 0,
                                        Token = string.Empty
                                    }),
                                    MessageIds.RegisterRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.RegisterResponse
                                    {
                                        Success = false,
                                        Message = errorMessage
                                    }),
                                    MessageIds.LogoutRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.LogoutResponse
                                    {
                                        Success = false,
                                        Message = errorMessage
                                    }),
                                    MessageIds.ResetPasswordRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.ChangePasswordResponse
                                    {
                                        Success = false,
                                        Message = errorMessage
                                    }),
                                    MessageIds.UpdateNicknameRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.ChangeNicknameResponse
                                    {
                                        Success = false,
                                        Message = errorMessage
                                    }),
                                    MessageIds.FindPasswordWithCodeRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Login.FindPasswordResponse
                                    {
                                        Success = false,
                                        Message = errorMessage
                                    }),
                                    _ => Array.Empty<byte>()
                                };

                                if (unknownPayload.Length > 0)
                                {
                                    byte[] routedUnknownPayload = Shared.RouteMetadata.AttachClientSessionId(unknownPayload, clientSessionId);
                                    byte[] unknownPacket = Network.Routing.PacketBuilder.BuildPacket(responseMsgId, routedUnknownPayload, out int unknownLength);
                                    byte[] unknownOutbound = unknownPacket.AsSpan(0, unknownLength).ToArray();
                                    System.Buffers.ArrayPool<byte>.Shared.Return(unknownPacket);

                                    session.Send(unknownOutbound);
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Shared.Log.Error($"处理消息 MsgId:{msgId} 时出现异常: {ex}");
                }
            };

            ConnectToCenter(port, activeGatewaySessions);
        }

        private static Login.Handlers.LoginHandler GetOrCreateLoginHandler()
        {
            if (sharedLoginHandler != null)
            {
                return sharedLoginHandler;
            }

            lock (sharedLoginSync)
            {
                if (sharedLoginHandler != null)
                {
                    return sharedLoginHandler;
                }

                sharedDbClient = ConnectToDatabase();
                sharedLoginHandler = new Login.Handlers.LoginHandler(sharedDbClient);
                return sharedLoginHandler;
            }
        }

        /// <summary>
        /// 建立与 DB 服务器的 TCP 连接，并在连接成功后请求当前最大 UID 用于 UID 生成器的初始化。
        /// </summary>
        /// <returns>返回已连接的 TcpClientWrapper 实例。</returns>
        private static TcpClientWrapper ConnectToDatabase()
        {
            // 从配置读取 DB 连接信息，若未配置则使用默认值
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 31305 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);

            // 当与 DB 建立连接时，向 DB 请求当前最大 UID（用于 UID 生成器初始化）
            dbClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");

                var request = new Shared.Messages.Db.GetMaxUidRequest();
                byte[] data = Shared.Json.SerializeToUtf8Bytes(request);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbGetMaxUidReq, data, out int totalLength);
                session.Send(packet.AsSpan(0, totalLength).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            };

            // 处理从 DB 返回的数据，严格按 [MsgId(4)][RequestId(8)][Payload] 解析
            dbClient.OnDataReceived += (session, data) =>
            {
                if (data.Length < 4)
                {
                    Shared.Log.Error($"DB 返回协议异常，长度不足 4，实际: {data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                byte[] payload = data.Slice(4).ToArray();

                if (Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload)
                    && PendingRequests.TryRemove(requestId, out var tcs))
                {
                    try
                    {
                        tcs.TrySetResult(cleanPayload);
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Error($"反序列化响应异常: {ex}");
                    }
                    return;
                }

                if (msgId == Shared.Messages.MessageIds.DbGetMaxUidRes)
                {
                    var response = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.GetMaxUidResponse>(payload);
                    if (response != null)
                    {
                        long currentMaxSequenceFromDB = response.MaxUid;
                        int currentRegionId = ConfigHelper.GetConfig<int>("RegionId") == 0 ? 1 : ConfigHelper.GetConfig<int>("RegionId");
                        Shared.UIDGenerator.Initialize(currentRegionId, currentMaxSequenceFromDB);
                        Shared.Log.Info($"UID 生成器初始化完成，区服ID:{currentRegionId}，当前同步的最大序列:{currentMaxSequenceFromDB}");
                    }
                }
            };

            // DB 断线日志并开始异步连接
            dbClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 DB 服务器断开连接: {reason}");
            _ = dbClient.ConnectAsync();

            return dbClient;
        }

        private static void ConnectToCenter(int port, System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession> activeGatewaySessions)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string loginHost = ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1";
            string nodeId = $"Login-{loginHost}:{port}";
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                SendRegisterNode(centerClient, nodeId, "Login", loginHost, port, activeGatewaySessions.Count);

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts = new System.Threading.CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, activeGatewaySessions.Count);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, cancellationToken);
            };

            centerClient.OnDisconnected += (session, reason) =>
            {
                centerHeartbeatCts?.Cancel();
                Shared.Log.Warning($"与 Center 服务器断开连接: {reason}");
            };
            centerClient.OnDataReceived += (session, data) => Shared.Log.Info($"Login 收到 Center 消息，长度: {data.Length}");
            _ = centerClient.ConnectAsync();
        }

        private static void SendRegisterNode(TcpClientWrapper centerClient, string nodeId, string nodeType, string host, int port, int currentLoad)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signatureSource = $"{nodeId}|{nodeType}|{host}|{port}|{currentLoad}|{timestamp}";
            var registerRequest = new CenterRegisterNodeRequest
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(registerRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRegisterNodeReq, payload, out int totalLength);
            centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        private static void SendNodeStatus(TcpClientWrapper centerClient, string nodeId, int currentLoad)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signatureSource = $"{nodeId}|{currentLoad}|{timestamp}";
            var statusRequest = new CenterNodeStatusRequest
            {
                NodeId = nodeId,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(statusRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterNodeStatusReq, payload, out int totalLength);
            centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        private static string ComputeCenterSignature(string source)
        {
            string secret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 启动 ASP.NET Core Web API 服务，提供登录相关的 HTTP 接口（如注册、登录、修改密码等）。
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>一个表示异步操作的任务</returns>
        public static async Task StartWebApiAsync(string[] args)
        {
            int apiPort = ConfigHelper.GetConfig<int>("ApiPort") == 0 ? 31303 : ConfigHelper.GetConfig<int>("ApiPort");

            var builder = WebApplication.CreateBuilder(args);

            int httpsPort = ConfigHelper.GetConfig<int>("ApiHttpsPort") == 0 ? 31318 : ConfigHelper.GetConfig<int>("ApiHttpsPort");
            string? certificatePath = ConfigHelper.GetConfig<string>("ApiHttpsCertificatePath");
            string? certificatePassword = ConfigHelper.GetConfig<string>("ApiHttpsCertificatePassword");
            bool httpsEnabled = !string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(apiPort);

                if (httpsEnabled)
                {
                    options.ListenAnyIP(httpsPort, listenOptions =>
                    {
                        listenOptions.UseHttps(certificatePath!, certificatePassword);
                    });

                    Shared.Log.Info($"ASP.NET API 已启用 HTTPS 监听，端口 {httpsPort}，证书: {certificatePath}");
                }
                else
                {
                    Shared.Log.Warning("未配置有效的 API HTTPS 证书，Login API 仅启用 HTTP 监听。");
                }
            });

            builder.Host.UseSerilog();
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var loginHandler = GetOrCreateLoginHandler();
            var dbClient = sharedDbClient!;

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

            if (httpsEnabled)
            {
                app.UseHttpsRedirection();
            }

            app.MapControllers();

            Shared.Log.Info($"ASP.NET API已启动，正在监听 HTTP 端口 {apiPort}{(httpsEnabled ? $", HTTPS 端口 {httpsPort}" : string.Empty)}");
            _ = app.RunAsync();
        }
    }
}
