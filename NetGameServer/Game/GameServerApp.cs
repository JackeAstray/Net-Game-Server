using System;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Network;
using Network.Routing;
using Network.Tcp;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Game
{
    /// <summary>
    /// 游戏服务器应用入口静态类。
    /// 负责初始化并启动游戏服务器所需的网络监听、路由和对外部数据库的连接。
    /// 所有方法均为静态，适合作为应用启动阶段的初始化调用点。
    /// </summary>
    public static class GameServerApp
    {
        public static TcpClientWrapper DbClient { get; private set; }
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;

        /// <summary>
        /// 异步启动网络监听。
        /// 关键步骤：
        /// 1. 从配置读取 GamePort（默认 31304）。
        /// 2. 创建 NetworkManager 和 TcpServer，注册消息路由和处理器。
        /// 3. 处理客户端的连接、断开以及接收数据事件，并将数据解析后交由路由器分发。
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 从配置中读取端口，若未配置或为 0 则使用默认端口 31304
            int port = ConfigHelper.GetConfig<int>("GamePort") == 0 ? 31304 : ConfigHelper.GetConfig<int>("GamePort");

            // 创建网络管理器，用于管理多个服务器实例
            var networkManager = new NetworkManager();
            // 创建 TCP 服务器实例以接收客户端连接
            var tcpServer = new TcpServer();

            // 创建消息路由器，将收到的消息分发到对应的处理器
            var router = new global::Network.Routing.MessageRouter();
            router.RegisterHandler(MessageIds.PlayerDisconnectNotif, (clientSession, payload) =>
            {
                Game.Managers.PlayerSessionManager.Instance.UnbindSession(clientSession.SessionId);
            });
            // 注册聊天处理器（示例），处理聊天相关消息并将其挂载到路由器
            var chatHandler = new Handlers.ChatHandler(networkManager);
            chatHandler.Register(router);

            // 注册好友处理器
            Handlers.FriendHandler.Register(router);

            // 当客户端建立连接时记录信息（可在此处加入鉴权或会话初始化逻辑）
            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");

            // 数据接收事件：统一协议 [MsgId][Payload]，路由元数据在 payload 内
            tcpServer.OnDataReceived += (session, data) =>
            {
                try
                {
                    Log.Info($"接收到数据，长度: {data.Length}");
                    if (data.Length < 4)
                    {
                        Log.Warning($"Game 收到无效客户端数据包，长度不足 4，Session:{session.SessionId} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    byte[] payload = data.Slice(4).ToArray();

                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long originalSessionId, out var cleanPayload))
                    {
                        Log.Warning($"Game 收到缺少路由元数据的消息 MsgId:{msgId}");
                        return;
                    }

                    if (msgId == MessageIds.PlayerDisconnectNotif)
                    {
                        Game.Managers.PlayerSessionManager.Instance.UnbindSession(originalSessionId);
                    }
                    else
                    {
                        if (Shared.RouteMetadata.TryExtractUserId(cleanPayload, out int routedUserId, out var payloadWithoutUserId) && routedUserId > 0)
                        {
                            Game.Managers.PlayerSessionManager.Instance.BindSession(originalSessionId, routedUserId);
                            cleanPayload = payloadWithoutUserId;
                        }
                    }

                    var clientSession = new Game.Network.ClientSessionWrapper(session, originalSessionId);
                    bool handled = router.TryRouteMessage(clientSession, msgId, cleanPayload);
                    if (!handled)
                    {
                        int responseMsgId = msgId switch
                        {
                            MessageIds.ChatMessageReq => MessageIds.ChatMessageRes,
                            MessageIds.AddFriendReq => MessageIds.AddFriendRes,
                            MessageIds.RemoveFriendReq => MessageIds.RemoveFriendRes,
                            MessageIds.SetFriendRemarkReq => MessageIds.SetFriendRemarkRes,
                            MessageIds.GetFriendsReq => MessageIds.GetFriendsRes,
                            MessageIds.InviteGameReq => MessageIds.InviteGameRes,
                            MessageIds.AddBlacklistReq => MessageIds.AddBlacklistRes,
                            MessageIds.RemoveBlacklistReq => MessageIds.RemoveBlacklistRes,
                            MessageIds.GetBlacklistReq => MessageIds.GetBlacklistRes,
                            _ => 0
                        };

                        if (responseMsgId > 0)
                        {
                            string errorMessage = $"未支持的游戏消息类型: {msgId}";
                            byte[] unknownPayload = responseMsgId switch
                            {
                                MessageIds.ChatMessageRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Chat.SendChatResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.AddFriendRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.AddFriendResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.RemoveFriendRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.RemoveFriendResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.SetFriendRemarkRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.SetFriendRemarkResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.GetFriendsRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.GetFriendsResponse
                                {
                                    Success = false,
                                    Message = errorMessage,
                                    Friends = Array.Empty<Shared.Messages.Social.FriendInfo>()
                                }),
                                MessageIds.InviteGameRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.InviteGameResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.AddBlacklistRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.AddBlacklistResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.RemoveBlacklistRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.RemoveBlacklistResponse
                                {
                                    Success = false,
                                    Message = errorMessage
                                }),
                                MessageIds.GetBlacklistRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.GetBlacklistResponse
                                {
                                    Success = false,
                                    Message = errorMessage,
                                    Blacklists = Array.Empty<Shared.Messages.Social.BlacklistInfo>()
                                }),
                                _ => Array.Empty<byte>()
                            };

                            if (unknownPayload.Length > 0)
                            {
                                byte[] routedUnknownPayload = Shared.RouteMetadata.AttachTargetSessionId(unknownPayload, originalSessionId);
                                byte[] unknownPacket = PacketBuilder.BuildPacket(responseMsgId, routedUnknownPayload, out int unknownLength);
                                try
                                {
                                    session.Send(unknownPacket.AsSpan(0, unknownLength).ToArray());
                                }
                                finally
                                {
                                    System.Buffers.ArrayPool<byte>.Shared.Return(unknownPacket);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Game 处理客户端数据异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };

            // 客户端断开连接事件（记录原因）。这里可以添加清理会话状态或通知其他子系统的逻辑。
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            await tcpServer.StartAsync(port);
            Log.Info($"游戏服务器已启动，监听端口: {port}");

            ConnectToCenter(port);
        }

        /// <summary>
        /// 连接到外部数据库服务器（通过 TcpClientWrapper 封装的 TCP 客户端）。
        /// 关键点：
        /// 1. 从配置读取 DBHost 和 DBPort（默认 127.0.0.1:31305）。
        /// 2. 订阅连接/断开事件以记录和处理重连/告警逻辑。
        /// 3. 启动异步连接（不阻塞调用者）。
        /// </summary>
        public static void ConnectToDatabase()
        {
            // 从配置中读取 DB 端口与主机，使用默认值作为回退
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 31305 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            DbClient = dbClient;

            // 成功连接到 DB 时记录日志（可在此处进行首次握手或认证）
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
            // 与 DB 断开时记录警告（可触发重试或告警机制）
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");
            dbClient.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Game 收到 DB 异常数据，长度不足 4，实际: {data.Length}");
                        return;
                    }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                var payload = data.Slice(4);

                bool handled = Handlers.FriendHandler.TryHandleDbResponse(session, msgId, payload);
                if (!handled)
                {
                    Log.Warning($"Game 未处理的 DB 响应消息: {msgId}");
                }
                }
                catch (Exception ex)
                {
                    Log.Error($"Game 处理 DB 回包异常 Exception:{ex}");
                }
            };

            // 开始异步连接（不等待结果），如需重试策略请在 TcpClientWrapper 外层实现
            _ = dbClient.ConnectAsync();
        }

        private static void ConnectToCenter(int port)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string gameHost = ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1";
            string nodeId = $"Game-{gameHost}:{port}";
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                SendRegisterNode(centerClient, nodeId, "Game", gameHost, port, GetCurrentLoad());

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
                            SendNodeStatus(centerClient, nodeId, GetCurrentLoad());
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
                Log.Warning($"与 Center 服务器断开连接: {reason}");
            };
            centerClient.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Game 收到 Center 异常数据，长度不足 4，实际: {data.Length}");
                        return;
                    }

                    Log.Info($"Game 收到 Center 消息，长度: {data.Length}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Game 处理 Center 回包异常 Exception:{ex}");
                }
            };
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
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"向 Center 注册节点失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
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
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"向 Center 上报节点状态失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        private static string ComputeCenterSignature(string source)
        {
            string secret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        private static int GetCurrentLoad()
        {
            return Game.Managers.PlayerSessionManager.Instance.GetOnlinePlayerCount();
        }
    }
}
