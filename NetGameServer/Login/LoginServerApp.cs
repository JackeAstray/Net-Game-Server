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

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            var loginDispatcher = Login.Handlers.MessageRouter.BuildDispatcher(loginHandler);

            // 跟踪所有活跃网关会话，并记录“客户端会话 -> 网关会话”的绑定，避免多网关场景下回包错路由。
            var activeGatewaySessions = new System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession>();
            var clientGatewayBindings = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();

            // 内部连接认证：每个网关连接必须先通过认证握手（InternalAuth），
            // 未认证的连接发送的业务消息将被拒绝。密钥与 Center 节点注册共用。
            string authSecret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            var gatewayAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

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
                Shared.Log.Info($"Login <- Gateway 已连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                activeGatewaySessions[session.SessionId] = session;
                gatewayAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, $"Login-{ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1"}:{port}");
                Shared.Log.Info($"Login 当前活跃网关连接数:{activeGatewaySessions.Count}");
            };
            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Shared.Log.Info($"Login <- Gateway 断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Reason:{reason}");
                activeGatewaySessions.TryRemove(session.SessionId, out _);
                gatewayAuthFilters.TryRemove(session.SessionId, out _);
                RemoveBindingsByGatewaySession(session.SessionId);
                Shared.Log.Info($"Login 当前活跃网关连接数:{activeGatewaySessions.Count}");
            };

            Login.Managers.SessionManager.Instance.SendToGatewayAction = (clientSessionId, packetData) =>
            {
                if (packetData.Length < 4)
                {
                    Shared.Log.Warning("SendToGatewayAction 收到无效包（长度不足 4），已丢弃。");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packetData.AsSpan(0, 4));
                int payloadLength = packetData.Length - 4;
                Shared.Log.Debug("Login -> Gateway 准备回包 MsgId:{MsgId} ClientSessionId:{ClientSessionId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", msgId, clientSessionId, packetData.Length, payloadLength);
                byte[] payload = packetData.AsSpan(4).ToArray();
                byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, clientSessionId);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
                byte[] outbound = packet.AsSpan(0, totalLength).ToArray();
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);

                if (clientGatewayBindings.TryGetValue(clientSessionId, out var gatewaySessionId))
                {
                    if (activeGatewaySessions.TryGetValue(gatewaySessionId, out var targetGatewaySession))
                    {
                        Shared.Log.Debug("Login -> Gateway 定向发送成功 MsgId:{MsgId} ClientSessionId:{ClientSessionId} GatewaySessionId:{GatewaySessionId} OutboundLength:{OutboundLength}", msgId, clientSessionId, gatewaySessionId, outbound.Length);
                        targetGatewaySession.Send(outbound);
                        return;
                    }

                    Shared.Log.Warning($"Login -> Gateway 绑定失效，准备移除 ClientSessionId:{clientSessionId} GatewaySessionId:{gatewaySessionId}");
                    RemoveClientGatewayBinding(clientSessionId);
                }

                // 单网关时允许兜底重绑；多网关时不做广播，避免重复下发或错路由。
                if (activeGatewaySessions.Count == 1)
                {
                    foreach (var session in activeGatewaySessions.Values)
                    {
                        Shared.Log.Warning($"Login -> Gateway 启用单网关兜底发送 MsgId:{msgId} ClientSessionId:{clientSessionId} GatewaySessionId:{session.SessionId} OutboundLength:{outbound.Length}");
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
                if (data.Length < 4)
                {
                    Shared.Log.Warning($"Login <- Gateway 收到无效数据，长度不足4 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                int payloadLength = data.Length - 4;

                // 内部连接认证：未认证连接只接受认证握手消息，其余业务消息一律拒绝。
                if (gatewayAuthFilters.TryGetValue(session.SessionId, out var authFilter))
                {
                    if (!authFilter.IsAuthenticated)
                    {
                        if (Framework.Core.Security.InternalAuthFilter.IsAuthMessage(msgId))
                        {
                            byte[] authPayload = data.Slice(4).ToArray();
                            if (authFilter.TryAuthenticate(authPayload))
                            {
                                Shared.Log.Info($"Login <- Gateway 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            }
                            else
                            {
                                Shared.Log.Warning($"Login <- Gateway 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                session.Close();
                                return;
                            }
                            return; // 认证握手不进入业务分发
                        }

                        Shared.Log.Warning($"Login 拒绝未认证连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                        return;
                    }
                }
                else
                {
                    // 兼容：旧客户端/内部工具直连时不强制认证（过渡期），仅记录警告。
                    Shared.Log.Warning($"Login 收到无认证过滤器连接的消息 MsgId:{msgId} SessionId:{session.SessionId}（过渡期兼容模式）");
                }

                Shared.Log.Debug("Login <- Gateway 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);
                byte[] payload = data.Slice(4).ToArray();

                if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                {
                    Shared.Log.Warning($"Login 收到缺少路由元数据的消息 MsgId:{msgId}");
                    return;
                }

                clientGatewayBindings[clientSessionId] = session.SessionId;
                Shared.Log.Debug($"Login 路由绑定更新 ClientSessionId:{clientSessionId} -> GatewaySessionId:{session.SessionId}");

                try
                {
                    // 新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容）
                    bool dispatched = await loginDispatcher.TryDispatch(
                        new Login.Handlers.LoginSessionContext(session, clientSessionId), msgId, cleanPayload);
                    if (dispatched)
                    {
                        if (msgId == MessageIds.PlayerDisconnectNotif)
                        {
                            Shared.Log.Debug("Login 收到玩家断线通知，清理绑定 ClientSessionId:{ClientSessionId}", clientSessionId);
                            RemoveClientGatewayBinding(clientSessionId);
                        }
                    }
                    else if (messageHandlers.TryGetValue(msgId, out var handler))
                    {
                        Shared.Log.Debug("Login 开始处理消息 MsgId:{MsgId} ClientSessionId:{ClientSessionId} PayloadLength:{PayloadLength}", msgId, clientSessionId, cleanPayload.Length);
                        await handler(cleanPayload, session, clientSessionId);
                        Shared.Log.Debug("Login 完成处理消息 MsgId:{MsgId} ClientSessionId:{ClientSessionId}", msgId, clientSessionId);

                        if (msgId == MessageIds.PlayerDisconnectNotif)
                        {
                            Shared.Log.Debug("Login 收到玩家断线通知，清理绑定 ClientSessionId:{ClientSessionId}", clientSessionId);
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

        /// <summary>
        /// 返回用于处理登录的单例 LoginHandler；若尚未创建，则线程安全地初始化并建立数据库连接。
        /// </summary>
        /// <remarks>采用双重检查锁定保证延迟初始化的线程安全。在首次创建时调用 ConnectToDatabase 并将结果赋予
        /// sharedDbClient；数据库连接失败时可能抛出异常。</remarks>
        /// <returns>已初始化且可用于处理登录请求的 Login.Handlers.LoginHandler 实例。</returns>
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
                Shared.Log.Info($"Login -> DB 已连接 (Host:{dbHost} Port:{dbPort}) SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");

                // 内部连接认证：先发送认证握手，再发业务请求
                string dbAuthSecret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
                dbClient.SendInternalAuthHandshake(dbAuthSecret, $"Login-{ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1"}");

                var request = new Shared.Messages.Db.GetMaxUidRequest();
                byte[] data = Shared.Json.SerializeToUtf8Bytes(request);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbGetMaxUidReq, data, out int totalLength);
                Shared.Log.Info($"Login -> DB 发送获取最大UID请求 MsgId:{Shared.Messages.MessageIds.DbGetMaxUidReq} PacketLength:{totalLength}");
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
                int payloadLength = data.Length - 4;
                Shared.Log.Debug("Login <- DB 收到消息 MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} Remote:{Remote}", msgId, data.Length, payloadLength, session.RemoteEndPoint);
                byte[] payload = data.Slice(4).ToArray();

                if (Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload)
                    && PendingRequests.TryRemove(requestId, out var tcs))
                {
                    try
                    {
                        Shared.Log.Debug("Login <- DB 命中待处理请求 RequestId:{RequestId} MsgId:{MsgId} PayloadLength:{PayloadLength}", requestId, msgId, cleanPayload.Length);
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
            dbClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"Login 与 DB 服务器断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Reason:{reason}");
            _ = dbClient.ConnectAsync();

            return dbClient;
        }

        /// <summary>
        /// 连接到 Center 服务器，注册当前登录节点并在后台定期上报节点状态（心跳）。
        /// </summary>
        /// <remarks>从配置读取 CenterHost/CenterPort/LoginHost，使用 TcpClientWrapper 异步连接中心服务器；连接成功时注册节点并启动每 10
        /// 秒上报一次状态的后台心跳任务；断开连接时取消心跳。</remarks>
        /// <param name="port">用于生成节点标识和向 Center 注册的本地端口号。</param>
        /// <param name="activeGatewaySessions">当前活动的网关会话集合，用于上报会话数量作为节点状态。</param>
        private static void ConnectToCenter(int port, System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession> activeGatewaySessions)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string loginHost = ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1";
            string nodeId = $"Login-{loginHost}:{port}";
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Shared.Log.Info($"Login -> Center 已连接 (Host:{centerHost} Port:{centerPort}) SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");

                // 内部连接认证：先发送认证握手，再注册节点
                centerClient.SendInternalAuthHandshake(ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret", nodeId);
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
                Shared.Log.Warning($"Login 与 Center 服务器断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Reason:{reason}");
            };
            centerClient.OnDataReceived += (session, data) =>
            {
                if (data.Length < 4)
                {
                    Shared.Log.Warning($"Login <- Center 收到无效数据，长度不足4 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                Shared.Log.Debug("Login <- Center 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, data.Length - 4);
            };
            _ = centerClient.ConnectAsync();
        }

        /// <summary>
        /// 构造包含节点信息与签名的注册请求，序列化为 JSON 并发送到中心服务。
        /// </summary>
        /// <remarks>计算基于节点信息与时间戳的签名，使用 UTF-8 将请求序列化为 JSON，构建 MessageIds.CenterRegisterNodeReq
        /// 协议包并发送；发送后将临时缓冲区返回共享数组池。时间戳以 Unix 秒为单位。</remarks>
        /// <param name="centerClient">与中心服务通信的 TCP 客户端包装器，用于发送注册请求。</param>
        /// <param name="nodeId">节点唯一标识。</param>
        /// <param name="nodeType">节点类型或角色。</param>
        /// <param name="host">节点主机名或 IP 地址。</param>
        /// <param name="port">节点监听端口号。</param>
        /// <param name="currentLoad">节点当前负载值，上报给中心用于负载衡量。</param>
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

        /// <summary>
        /// 序列化并发送节点状态（节点 ID、当前负载与 UTC Unix 时间戳）到中心服务器。
        /// </summary>
        /// <remarks>生成 UTC Unix 时间戳，并基于 nodeId|currentLoad|timestamp 计算签名；将 CenterNodeStatusRequest 序列化为
        /// UTF-8，使用 MessageIds.CenterNodeStatusReq 构建并发送消息包，仅发送实际字节长度，并将缓冲区归还至 ArrayPool。</remarks>
        /// <param name="centerClient">用于与中心服务器通信的 TCP 客户端包装器。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="currentLoad">节点当前的负载值。</param>
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

        /// <summary>
        /// 使用配置密钥对输入字符串计算 HMAC-SHA256 并返回 Base64 编码的签名。
        /// </summary>
        /// <remarks>使用配置键 "CenterNodeSharedSecret" 获取共享密钥；若未配置则回退为
        /// "change-this-secret"。密钥应妥善保护并定期更换。</remarks>
        /// <param name="source">要计算签名的输入字符串（使用 UTF-8 编码）。</param>
        /// <returns>输入字符串的 HMAC-SHA256 签名，经过 UTF-8 编码并以 Base64 字符串形式返回。</returns>
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
