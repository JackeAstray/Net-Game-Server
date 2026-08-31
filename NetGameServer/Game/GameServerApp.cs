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
        public static TcpClientWrapper DbClient { get; private set; } = null!;
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;

        // P2 修复（跨网关投递）：客户端会话 -> 网关会话 反向索引。
        // 多网关部署下，向"目标客户端会话"发消息时必须选中该客户端真正所在的网关连接；
        // 原实现一律沿用请求来源的网关会话，目标在另一网关时消息被发错网关而丢失。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, global::Network.ISession> clientSessionGateways =
            new();

        /// <summary>登记/刷新客户端会话与网关会话的归属关系（收到该客户端任何数据包时调用）。</summary>
        public static void RegisterClientGateway(long clientSessionId, global::Network.ISession gatewaySession)
        {
            if (clientSessionId > 0 && gatewaySession != null)
            {
                clientSessionGateways[clientSessionId] = gatewaySession;
            }
        }

        /// <summary>移除客户端会话的网关归属（玩家断线/网关断开时调用）。</summary>
        public static void UnregisterClientGateway(long clientSessionId)
        {
            if (clientSessionId > 0)
            {
                clientSessionGateways.TryRemove(clientSessionId, out _);
            }
        }

        /// <summary>解析目标客户端会话所在的网关会话；未知或已断开返回 null。</summary>
        public static global::Network.ISession? ResolveGatewayForClient(long clientSessionId)
        {
            if (clientSessionId <= 0)
            {
                return null;
            }
            if (clientSessionGateways.TryGetValue(clientSessionId, out var gw) && gw.IsConnected)
            {
                return gw;
            }
            return null;
        }

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

            // 内部连接认证：网关连接必须先通过认证握手（InternalAuth），密钥与 Center 节点注册共用。
            // 安全修复：拒绝占位符密钥。
            string authSecret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            // 重启窗口修复：周期持久化防重放状态，重启不重置握手重放窗口
            Framework.Core.Security.InternalAuthFilter.ConfigureReplayPersistence(
                System.IO.Path.Combine(AppContext.BaseDirectory, "data", "replay_state.bin"));
            var gatewayAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

            // V5 修复：网关连接 -> 经它路由的客户端会话集合。
            // 网关连接断开时必须级联清理这些客户端在 Game 侧的全部状态（会话绑定/聊天限频/好友在线），防泄漏。
            var gatewayClients = new System.Collections.Concurrent.ConcurrentDictionary<long, System.Collections.Concurrent.ConcurrentDictionary<long, byte>>();

            // 创建网络管理器，用于管理多个服务器实例
            var networkManager = new NetworkManager();
            // 创建 TCP 服务器实例以接收客户端连接
            var tcpServer = new TcpServer();

            // 创建消息路由器，将收到的消息分发到对应的处理器
            // 注：PlayerDisconnectNotif 不在此注册（下方 OnDataReceived 内联按 originalSessionId 处理；
            // 此前的重复注册会误用网关会话 ID 解绑错误目标，并造成该消息被处理两次）。
            var router = new global::Network.Routing.MessageRouter();
            // 注册聊天处理器（示例），处理聊天相关消息并将其挂载到路由器
            var chatHandler = new Handlers.ChatHandler(networkManager);
            chatHandler.Register(router);

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            var gameDispatcher = Handlers.GameDispatcher.BuildDispatcher(chatHandler);

            // 注册好友处理器
            Handlers.FriendHandler.Register(router);

            // 当客户端建立连接时记录信息（可在此处加入鉴权或会话初始化逻辑）
            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"客户端已连接: {session.RemoteEndPoint}");
                gatewayAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, $"Game-{ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1"}:{port}");
            };
            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                gatewayAuthFilters.TryRemove(session.SessionId, out _);
                Log.Info($"客户端断开连接，原因: {reason}");

                // V5 修复：网关连接断开时级联清理经该网关路由的全部客户端会话状态
                // （此前只删 authFilter，PlayerSessionManager 绑定/聊天限频/好友在线全部泄漏）。
                if (gatewayClients.TryRemove(session.SessionId, out var clients))
                {
                    foreach (var clientId in clients.Keys)
                    {
                        int offlineUserId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(clientId);
                        Game.Handlers.FriendHandler.NotifyFriendOnlineStatus(session, clientId, offlineUserId, false);
                        Game.Managers.PlayerSessionManager.Instance.UnbindSession(clientId);
                        Handlers.ChatHandler.RemoveSession(clientId);
                        // P2 修复：网关断开时级联移除跨网关反向索引。
                        UnregisterClientGateway(clientId);
                        // P2 修复：清理离线玩家的好友/黑名单缓存，防字典无界增长（仅当无其他在线会话时清理）。
                        if (offlineUserId > 0 && Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(offlineUserId) <= 0)
                        {
                            Game.Handlers.FriendHandler.ClearUserCaches(offlineUserId);
                        }
                    }
                    Log.Warning($"网关断开，级联清理客户端会话 {clients.Count} 个");
                }
            };

            // 数据接收事件：统一协议 [MsgId][Payload]，路由元数据在 payload 内
            // 安全修复：使用 AsyncEventGuard 包装 async lambda，避免 async void 异常冒泡
            tcpServer.OnDataReceived += global::Network.AsyncEventGuard.Wrap(async (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Game 收到无效客户端数据包，长度不足 4，Session:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
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
                                    Log.Info($"Game <- Gateway 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                }
                                else
                                {
                                    Log.Warning($"Game <- Gateway 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                    session.Close();
                                    return;
                                }
                                return; // 认证握手不进入业务分发
                            }

                            Log.Warning($"Game 拒绝未认证连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            return;
                        }
                    }
                    else
                    {
                        // 未注册认证过滤器 = 未认证连接：默认 fail-closed 拒绝（安全修复，杜绝"过滤器缺失即放行"）。
                        if (!Shared.ConfigHelper.GetConfig<bool>("AllowUnauthenticatedInternal"))
                        {
                            Log.Warning($"Game 拒绝无认证过滤器连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}（fail-closed）");
                            session.Close();
                            return;
                        }
                    }

                    var payloadPreview = data.Slice(4);
                    // 每包日志降级为 Debug 并带级别守卫：仅在调试模式启用时构造十六进制/UTF8 预览（该预览构造开销大，不能进热路径）
                    if (Log.IsDebugEnabled)
                    {
                        Log.Debug("Game 接收到客户端数据 Session:{Session} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} RawHexPreview:{RawHexPreview} Utf8Preview:{Utf8Preview}",
                            session.SessionId, session.RemoteEndPoint!, msgId, data.Length, payloadLength,
                            BuildHexPreview(payloadPreview), BuildUtf8Preview(payloadPreview));
                    }

                    byte[] payload = payloadPreview.ToArray();

                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long originalSessionId, out var cleanPayload))
                    {
                        Log.Warning($"Game 收到缺少路由元数据的消息 MsgId:{msgId}");
                        return;
                    }

                    // V5 修复：登记该客户端会话与网关连接的关系（网关断开时用于级联清理）。
                    // P2 修复：同时维护"客户端会话 -> 网关会话"反向索引，供跨网关向目标会话投递时选中正确网关。
                    if (originalSessionId > 0)
                    {
                        gatewayClients.GetOrAdd(session.SessionId, _ => new System.Collections.Concurrent.ConcurrentDictionary<long, byte>())[originalSessionId] = 0;
                        RegisterClientGateway(originalSessionId, session);
                    }

                    if (msgId == MessageIds.PlayerDisconnectNotif)
                    {
                        int disconnectedUserId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(originalSessionId);
                        Game.Handlers.FriendHandler.NotifyFriendOnlineStatus(session, originalSessionId, disconnectedUserId, false);
                        Game.Managers.PlayerSessionManager.Instance.UnbindSession(originalSessionId);
                        Handlers.ChatHandler.RemoveSession(originalSessionId);
                        // P2 修复：移除跨网关反向索引（该客户端会话已离线）。
                        UnregisterClientGateway(originalSessionId);
                        // P2 修复：清理离线玩家的好友/黑名单缓存，防字典无界增长。
                        // 仅当该用户已无其他在线会话时才清理，避免顶号场景下旧会话断开清掉新会话刚暖好的缓存。
                        if (disconnectedUserId > 0 && Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(disconnectedUserId) <= 0)
                        {
                            Game.Handlers.FriendHandler.ClearUserCaches(disconnectedUserId);
                        }
                        if (gatewayClients.TryGetValue(session.SessionId, out var clientsForGateway))
                        {
                            clientsForGateway.TryRemove(originalSessionId, out _);
                        }
                    }
                    else
                    {
                        if (Shared.RouteMetadata.TryExtractUserId(cleanPayload, out int routedUserId, out var payloadWithoutUserId) && routedUserId > 0)
                        {
                            bool firstBind = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(originalSessionId) <= 0;
                            long displacedSessionId = Game.Managers.PlayerSessionManager.Instance.BindSession(originalSessionId, routedUserId);
                            if (displacedSessionId > 0)
                            {
                                // R4 修复：同账号异地登录——顶号，向旧会话发送踢下线通知
                                Log.Warning($"同账号异地登录，顶号踢出旧会话 UserId:{routedUserId} OldSessionId:{displacedSessionId} NewSessionId:{originalSessionId}");
                                SendKickedOff(session, displacedSessionId);
                            }
                            if (firstBind)
                            {
                                Game.Handlers.FriendHandler.WarmupSocialCache(session, originalSessionId, routedUserId);
                            }
                            cleanPayload = payloadWithoutUserId;
                        }

                        if (Shared.RouteMetadata.TryExtractUid(cleanPayload, out string routedUid, out var payloadWithoutUid) && !string.IsNullOrWhiteSpace(routedUid))
                        {
                            bool hadUid = !string.IsNullOrWhiteSpace(Game.Managers.PlayerSessionManager.Instance.GetUidBySessionId(originalSessionId));
                            Game.Managers.PlayerSessionManager.Instance.BindUid(originalSessionId, routedUid);
                            if (!hadUid)
                            {
                                int onlineUserId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(originalSessionId);
                                Game.Handlers.FriendHandler.NotifyFriendOnlineStatus(session, originalSessionId, onlineUserId, true);
                            }
                            cleanPayload = payloadWithoutUid;
                        }
                    }

                    var clientSession = new Game.Network.ClientSessionWrapper(session, originalSessionId);
                    // 新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容）
                    bool dispatched = await gameDispatcher.TryDispatch(
                        new Game.Handlers.GameSessionContext(session, originalSessionId), msgId, cleanPayload);
                    if (dispatched)
                    {
                        return;
                    }

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
                            MessageIds.FriendApplyReq => MessageIds.FriendApplyRes,
                            MessageIds.FriendApplyListReq => MessageIds.FriendApplyListRes,
                            MessageIds.FriendApplyHandleReq => MessageIds.FriendApplyHandleRes,
                            MessageIds.InviteGameAckReq => MessageIds.InviteGameAckRes,
                            _ => 0
                        };

                        if (responseMsgId > 0)
                        {
                            // D2 修复：错误响应的逐类型构造已抽取为 BuildUnhandledMessageErrorPayload，避免热点内联 14 路重复 switch
                            byte[] unknownPayload = BuildUnhandledMessageErrorPayload(responseMsgId, $"未支持的游戏消息类型: {msgId}");
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
            });

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
            dbClient.OnConnected += session =>
            {
                Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
                // 内部连接认证：先发送认证握手，再发业务请求
                dbClient.SendInternalAuthHandshake(Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret"), $"Game-{ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1"}:{dbPort}");
            };
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

                    Log.Debug("Game <- DB 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint!, msgId, data.Length, payload.Length);

                    bool handled = Handlers.FriendHandler.TryHandleDbResponse(session, msgId, payload);
                    if (!handled)
                    {
                        Log.Warning($"Game 未处理的 DB 响应消息 MsgId:{msgId} SessionId:{session.SessionId}");
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
            // nodeId 优先级：配置（machine 注入） > 按 host:port 派生（保持后向兼容）
            string nodeId = ConfigHelper.GetConfig<string>("NodeId") ?? $"Game-{gameHost}:{port}";
            string instanceId = ConfigHelper.GetConfig<string>("InstanceId") ?? string.Empty;
            string machineId = ConfigHelper.GetConfig<string>("MachineId") ?? string.Empty;
            string supervisedBy = ConfigHelper.GetConfig<string>("SupervisedBy") ?? string.Empty;
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                // 内部连接认证：先发送认证握手，再注册节点
                centerClient.SendInternalAuthHandshake(Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret"), nodeId);
                SendRegisterNode(centerClient, nodeId, "Game", gameHost, port, GetCurrentLoad(), instanceId, machineId, supervisedBy);

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
                centerHeartbeatCts = new System.Threading.CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Shared.NodeHeartbeatDefaults.HeartbeatIntervalSeconds), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, GetCurrentLoad());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Game 心跳循环异常（下轮继续重试）: {ex}");
                    }
                }, cancellationToken);
            };

            centerClient.OnDisconnected += (session, reason) =>
            {
                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
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

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Log.Debug("Game <- Center 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint!, msgId, data.Length, data.Length - 4);
                }
                catch (Exception ex)
                {
                    Log.Error($"Game 处理 Center 回包异常 Exception:{ex}");
                }
            };
            _ = centerClient.ConnectAsync();
        }

        /// <summary>
        /// 向中心服务发送节点注册请求，包含节点标识、类型、主机、端口、当前负载、时间戳和签名。
        /// </summary>
        /// <remarks>请求对象序列化为 JSON 并封装为二进制数据包发送；发送异常会记录错误；发送完成后归还 ArrayPool 缓冲区。</remarks>
        /// <param name="centerClient">与中心服务器的 TCP 连接封装，用于发送注册数据。</param>
        /// <param name="nodeId">节点唯一标识。</param>
        /// <param name="nodeType">节点类型。</param>
        /// <param name="host">节点主机或 IP。</param>
        /// <param name="port">节点监听端口。</param>
        /// <param name="currentLoad">节点当前负载值（例如并发连接数或任务数）。</param>
        /// <param name="instanceId">实例 ID（machine 注入；可空）。</param>
        /// <param name="machineId">托管本节点的 Machine 进程 ID（可空）。</param>
        /// <param name="supervisedBy">托管方类型（可空）。</param>
        private static void SendRegisterNode(TcpClientWrapper centerClient, string nodeId, string nodeType, string host, int port, int currentLoad,
            string instanceId = "", string machineId = "", string supervisedBy = "")
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 协议扩展（迭代 20）：Machine 注入字段参与签名源
            string signatureSource = $"{nodeId}|{nodeType}|{host}|{port}|{currentLoad}|{instanceId}|{machineId}|{supervisedBy}|{timestamp}";
            var registerRequest = new CenterRegisterNodeRequest
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                InstanceId = instanceId,
                MachineId = machineId,
                SupervisedBy = supervisedBy,
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

        /// <summary>
        /// 向中心服务器上报节点状态，包括节点标识、当前负载、时间戳与签名。
        /// </summary>
        /// <remarks>发送失败时记录错误日志；使用 ArrayPool 返回临时字节缓冲区以减少分配。</remarks>
        /// <param name="centerClient">用于向中心服务器发送数据的 TCP 客户端包装器。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="currentLoad">节点的当前负载值。</param>
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

        /// <summary>
        /// 使用配置中的共享密钥对输入字符串计算 HMAC-SHA256 并返回 Base64 编码的签名。
        /// </summary>
        /// <remarks>共享密钥从配置键 CenterNodeSharedSecret 获取，若未设置则回退到默认值 "change-this-secret"。使用 UTF-8
        /// 编码；请妥善保护并更改默认密钥以保证安全。</remarks>
        /// <param name="source">要签名的输入字符串。</param>
        /// <returns>Base64 编码的 HMAC-SHA256 签名。</returns>
        private static string ComputeCenterSignature(string source)
        {
            string secret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 获取当前在线玩家数。
        /// </summary>
        /// <remarks>从 Game.Managers.PlayerSessionManager.Instance 查询在线玩家数。</remarks>
        /// <returns>当前在线玩家的数量。</returns>
        private static int GetCurrentLoad()
        {
            return Game.Managers.PlayerSessionManager.Instance.GetOnlinePlayerCount();
        }

        /// <summary>
        /// 向指定客户端会话发送"被顶号踢下线"通知（R4 修复；经目标会话所在网关路由到目标会话）。
        /// P2 修复：跨网关部署下优先解析目标会话所在网关，而不是沿用请求来源网关。
        /// </summary>
        private static void SendKickedOff(global::Network.ISession gatewaySession, long targetSessionId)
        {
            global::Network.ISession sendSession = ResolveGatewayForClient(targetSessionId) ?? gatewaySession;
            var msg = new Shared.Messages.Login.KickedOffMessage
            {
                Reason = "您的账号在其他设备登录",
                Time = DateTime.UtcNow
            };
            byte[] payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(msg), targetSessionId);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.KickedOffNotif, payload, out int packetLength);
            try
            {
                sendSession.Send(packet.AsSpan(0, packetLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Warning($"顶号踢出旧会话发送失败 TargetSessionId:{targetSessionId} Exception:{ex.Message}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 构造"未处理消息"的失败响应负载（D2 修复：将 14 路逐类型错误响应构造收敛为单一入口；
        /// 列表型响应附带空数组，其余仅 Success/Message）。
        /// </summary>
        private static byte[] BuildUnhandledMessageErrorPayload(int responseMsgId, string errorMessage)
        {
            return responseMsgId switch
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
                MessageIds.FriendApplyRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.FriendApplyResponse
                {
                    Success = false,
                    Message = errorMessage
                }),
                MessageIds.FriendApplyListRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.FriendApplyListResponse
                {
                    Success = false,
                    Message = errorMessage,
                    Applies = Array.Empty<Shared.Messages.Social.FriendApplyInfo>()
                }),
                MessageIds.FriendApplyHandleRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.FriendApplyHandleResponse
                {
                    Success = false,
                    Message = errorMessage
                }),
                MessageIds.InviteGameAckRes => Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Social.InviteGameAckResponse
                {
                    Success = false,
                    Message = errorMessage
                }),
                _ => Array.Empty<byte>()
            };
        }

        /// <summary>
        /// 生成字节序列的十六进制预览字符串；若输入为空返回"<empty>"。
        /// </summary>
        /// <param name="data">要生成预览的只读字节序列（ReadOnlyMemory<byte>）。</param>
        /// <returns>包含输入前最多64字节的连贯大写十六进制表示的字符串；若输入长度超过64字节，则在末尾追加类似"...(truncated,total:12345bytes)"的文本以指示已截断并显示总字节数。</returns>
        private static string BuildHexPreview(ReadOnlyMemory<byte> data)
        {
            if (data.Length == 0)
            {
                return "<empty>";
            }

            const int maxBytes = 64;
            var span = data.Span;
            int take = Math.Min(span.Length, maxBytes);
            string hex = Convert.ToHexString(span.Slice(0, take));
            return span.Length > maxBytes ? $"{hex}...(truncated,total:{span.Length}bytes)" : hex;
        }

        /// <summary>
        /// 为给定的 UTF-8 字节序列生成简洁可读的预览文本，处理不可打印字符并在必要时截断。
        /// </summary>
        /// <remarks>最多显示前 128 字节；对控制字符用 '.' 替换以保证可读性；截断时使用格式 "...(truncated,total:{n}bytes)"
        /// 指示原始总字节数。</remarks>
        /// <param name="data">要生成预览的 UTF-8 字节序列。</param>
        /// <returns>预览字符串；空序列返回 "<empty>"；回车换行以 "\\r"/"\\n" 表示，控制字符替换为 '.'，若长度超过 128 字节则截断并在末尾注明总字节数。</returns>
        private static string BuildUtf8Preview(ReadOnlyMemory<byte> data)
        {
            if (data.Length == 0)
            {
                return "<empty>";
            }

            const int maxBytes = 128;
            var span = data.Span;
            int take = Math.Min(span.Length, maxBytes);
            string text = Encoding.UTF8.GetString(span.Slice(0, take));
            text = text.Replace("\r", "\\r").Replace("\n", "\\n");

            var sanitized = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                sanitized.Append(char.IsControl(c) ? '.' : c);
            }

            return span.Length > maxBytes
                ? $"{sanitized}...(truncated,total:{span.Length}bytes)"
                : sanitized.ToString();
        }
    }
}
