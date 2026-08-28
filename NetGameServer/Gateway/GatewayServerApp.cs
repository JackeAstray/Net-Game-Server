using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Routing;
using Network.Tcp;
using Serilog;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Gateway
{
    // 网关服务器主应用类
    // 负责启动监听客户端连接的网络服务（TCP/UDP/KCP/WebSocket），
    // 将来自客户端的数据根据消息 ID 路由到后端的 Login 或 Game 服务器，
    // 并处理后端返回的数据转发给相应的客户端会话。
    public static class GatewayServerApp
    {
        private static CancellationTokenSource? centerHeartbeatCts;

        /// <summary>断线重连挂起记录（客户端断线后宽限期内可恢复会话）。</summary>
        private sealed class PendingReconnect
        {
            public int UserId;
            public DateTime ExpiresAtUtc;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, PendingReconnect> pendingReconnects = new();

        // ===== 静态分片（对标 KBE cellappmgr 调度）：多 Battle 节点 + 按玩家绑定路由 =====

        // 后端发送器（Login/Game/Center）：在连接发起前创建并订阅 OnConnected，
        // 避免 localhost 快速连上时 OnConnected 先于订阅触发导致 isConnected 永远为 false（消息被静默缓冲）。
        private static BufferedBackendSender? loginSender;
        private static BufferedBackendSender? gameSender;
        private static BufferedBackendSender? centerSender;
        /// <summary>Battle 节点连接：nodeId("Battle-{host}:{port}") -> 客户端。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TcpClientWrapper> battleNodes = new(StringComparer.Ordinal);

        /// <summary>Battle 节点发送器（缓冲队列）：nodeId -> sender。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BufferedBackendSender> battleNodeSenders = new(StringComparer.Ordinal);

        /// <summary>玩家 -> Battle 节点绑定：clientSessionId -> nodeId（从匹配结果学习）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> clientBattleNodeBindings = new();

        /// <summary>默认 Battle 节点（无绑定时的回退目标）。</summary>
        private static volatile string defaultBattleNodeId = string.Empty;

        /// <summary>按玩家绑定（或默认节点）发送到对应 Battle 节点。</summary>
        private static void SendToBattle(byte[] outbound, long clientSessionId)
        {
            string nodeId = clientBattleNodeBindings.TryGetValue(clientSessionId, out var bound)
                ? bound
                : defaultBattleNodeId;
            if (battleNodeSenders.TryGetValue(nodeId, out var sender))
            {
                sender.SendOrBuffer(outbound);
            }
            else if (battleNodeSenders.Count > 0)
            {
                // 绑定节点不存在（节点已下线）：回退默认节点并清除绑定
                clientBattleNodeBindings.TryRemove(clientSessionId, out _);
                battleNodeSenders.TryGetValue(defaultBattleNodeId, out sender);
                sender?.SendOrBuffer(outbound);
            }
            else
            {
                Shared.Log.Warning($"Gateway 无可用 Battle 节点，丢弃消息 ClientSessionId:{clientSessionId}");
            }
        }

        private sealed class BufferedBackendSender
        {
            private readonly string backendName;
            private readonly Action<ReadOnlyMemory<byte>> sendAction;
            private readonly ConcurrentQueue<byte[]> pendingPackets = new ConcurrentQueue<byte[]>();
            private readonly int maxPending;
            private volatile bool isConnected;

            public BufferedBackendSender(string backendName, Action<ReadOnlyMemory<byte>> sendAction, int maxPending = 512)
            {
                this.backendName = backendName;
                this.sendAction = sendAction;
                this.maxPending = maxPending;
            }

            public void OnConnected()
            {
                isConnected = true;
                Shared.Log.Info($"Gateway->{backendName} 通道已连接，开始冲刷缓冲队列，当前待发:{pendingPackets.Count}");
                FlushPending();
            }

            public void OnDisconnected()
            {
                isConnected = false;
                Shared.Log.Warning($"Gateway->{backendName} 通道断开，后续消息将进入缓冲队列。当前待发:{pendingPackets.Count}");
            }

            public void SendOrBuffer(byte[] packet)
            {
                if (isConnected)
                {
                    Shared.Log.Debug($"Gateway->{backendName} 实时发送 Length:{packet.Length}");
                    sendAction(packet);
                    return;
                }

                pendingPackets.Enqueue(packet);
                Shared.Log.Warning($"Gateway->{backendName} 未连接，消息入缓冲 Length:{packet.Length} 当前待发:{pendingPackets.Count}");
                int droppedCount = 0;
                while (pendingPackets.Count > maxPending && pendingPackets.TryDequeue(out _))
                {
                    droppedCount++;
                }

                if (droppedCount > 0)
                {
                    Shared.Log.Warning($"Gateway->{backendName} 发送缓冲已满，丢弃旧消息 {droppedCount} 条，当前队列上限:{maxPending}");
                }
            }

            private void FlushPending()
            {
                int flushedCount = 0;
                while (isConnected && pendingPackets.TryDequeue(out var packet))
                {
                    flushedCount++;
                    sendAction(packet);
                }

                if (flushedCount > 0)
                {
                    Shared.Log.Info($"Gateway->{backendName} 缓冲冲刷完成，已发送:{flushedCount} 剩余待发:{pendingPackets.Count}");
                }
            }
        }

        /// <summary>
        /// 启动网关的网络服务并注册事件处理器。
        /// - 根据配置获取监听端口（默认 TCP/UDP:31300，WebSocket:31301）。
        /// - 创建并注册 TCP/UDP/WebSocket 三种服务器实例，统一将连接加入会话管理器。
        /// - 将接收到的客户端数据解析出 MsgId，并通过 payload 中的 RouteMetadata（__clientSessionId）附加路由信息后转发给后端服务器。
        /// - 处理客户端断开事件并从会话管理器移除对应会话。
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 读取配置端口，若配置为 0 或未配置则使用默认端口
            int port = ConfigHelper.GetConfig<int>("GatewayPort") == 0 ? 31300 : ConfigHelper.GetConfig<int>("GatewayPort");

            // 创建各类型的监听服务器（TCP/KCP/UDP/WebSocket）
            var tcpServer = new TcpServer();
            var kcpServer = new Network.Kcp.KcpServer();
            var udpServer = new Network.Udp.UdpServer();
            var webSocketServer = new Network.WebSockets.WebSocketServer();

            // 当有客户端新建会话（连接）时，记录日志并将会话加入网关会话管理器
            tcpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(TCP)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            kcpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(KCP)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            udpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(UDP)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            webSocketServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(WebSocket)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };

            // 建立到后端 Login, Game, Center 与 Battle 节点池的连接（异步连接启动）
            // 注：login/game/center 的发送器在 ConnectToBackendServers 内于连接发起前创建并订阅
            var (loginClient, gameClient, centerClient) = ConnectToBackendServers();

            void NotifyPlayerDisconnected(long clientSessionId)
            {
                var disconnectPayload = Shared.RouteMetadata.AttachClientSessionId(Array.Empty<byte>(), clientSessionId);
                var disconnectPacket = PacketBuilder.BuildPacket(MessageIds.PlayerDisconnectNotif, disconnectPayload, out int totalLength);
                var outbound = disconnectPacket.AsSpan(0, totalLength).ToArray();
                System.Buffers.ArrayPool<byte>.Shared.Return(disconnectPacket);

                Shared.Log.Info($"Gateway 广播玩家断线通知 MsgId:{MessageIds.PlayerDisconnectNotif} ClientSessionId:{clientSessionId} PacketLength:{totalLength}");
                loginSender.SendOrBuffer(outbound);
                gameSender.SendOrBuffer(outbound);
                // 断线通知广播到全部 Battle 节点（玩家可能挂在任一节点）
                foreach (var sender in battleNodeSenders.Values)
                {
                    sender.SendOrBuffer(outbound);
                }
            }

            // 数据接收处理器：统一协议 [MsgId(4)][Payload]
            // 会话路由信息放入 JSON payload 元数据 __clientSessionId
            DataReceivedHandler onDataReceived = (session, data) =>
            {
                try
                {
                    if (data.Length >= 4)
                    {
                        int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                        int payloadLength = data.Length - 4;
                        Shared.Log.Debug("Gateway 接收到客户端数据 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);

                        byte[] payload = data.Slice(4).ToArray();
                        byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, session.SessionId);
                        int boundUserId = Gateway.Managers.GatewaySessionManager.Instance.GetUserIdBySessionId(session.SessionId);
                        if (boundUserId > 0)
                        {
                            routedPayload = Shared.RouteMetadata.AttachUserId(routedPayload, boundUserId);
                        }

                        string boundUid = Gateway.Managers.GatewaySessionManager.Instance.GetUidBySessionId(session.SessionId);
                        if (!string.IsNullOrWhiteSpace(boundUid))
                        {
                            routedPayload = Shared.RouteMetadata.AttachUid(routedPayload, boundUid);
                        }

                        string boundNickname = Gateway.Managers.GatewaySessionManager.Instance.GetNicknameBySessionId(session.SessionId);
                        if (!string.IsNullOrWhiteSpace(boundNickname))
                        {
                            routedPayload = Shared.RouteMetadata.AttachNickname(routedPayload, boundNickname);
                        }

                        byte[] wrapperMsg = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int routedLength);
                        byte[] outbound = wrapperMsg.AsSpan(0, routedLength).ToArray();
                        System.Buffers.ArrayPool<byte>.Shared.Return(wrapperMsg);

                        // 配置化路由：优先查 Protogen 生成的路由表（Protocol/defs/*.def 为唯一事实来源），
                        // 未定义的消息回退到旧区间路由（过渡期兼容）。
                        string? targetServer = Framework.Protocol.Generated.RouterTable.GetTargetServer(msgId);
                        if (targetServer != null)
                        {
                            var route = Framework.Protocol.Generated.RouterTable.Routes[msgId];
                            if (route.IsInternal)
                            {
                                Shared.Log.Warning($"Gateway 拒绝客户端发送的内部消息 MsgId:{msgId}");
                                return;
                            }

                            Shared.Log.Debug("Gateway 配置化路由客户端消息 MsgId:{MsgId} ClientSessionId:{ClientSessionId} Target:{Target} OutboundLength:{OutboundLength}", msgId, session.SessionId, targetServer, outbound.Length);
                            switch (targetServer)
                            {
                                case "Login":
                                    loginSender.SendOrBuffer(outbound);
                                    break;
                                case "Game":
                                    gameSender.SendOrBuffer(outbound);
                                    break;
                                case "Center":
                                    centerSender.SendOrBuffer(outbound);
                                    break;
                                case "Battle":
                                    SendToBattle(outbound, session.SessionId);
                                    break;
                                default:
                                    Shared.Log.Warning($"Gateway: 未知的路由目标 TargetServer=>{targetServer} MsgId=>{msgId}");
                                    break;
                            }
                            return;
                        }

                        if (msgId >= 10000 && msgId < 20000)
                        {
                            Shared.Log.Debug("Gateway 路由客户端消息 -> Login MsgId:{MsgId} ClientSessionId:{ClientSessionId} BoundUserId:{BoundUserId} OutboundLength:{OutboundLength}", msgId, session.SessionId, boundUserId, outbound.Length);
                            loginSender.SendOrBuffer(outbound);
                        }
                        else if ((msgId >= 20000 && msgId < 30000) || (msgId >= 50000 && msgId < 70000))
                        {
                            Shared.Log.Debug("Gateway 路由客户端消息 -> Game MsgId:{MsgId} ClientSessionId:{ClientSessionId} BoundUserId:{BoundUserId} OutboundLength:{OutboundLength}", msgId, session.SessionId, boundUserId, outbound.Length);
                            gameSender.SendOrBuffer(outbound);
                        }
                        else if (msgId >= 30000 && msgId < 40000)
                        {
                            Shared.Log.Debug("Gateway 路由客户端消息 -> Center MsgId:{MsgId} ClientSessionId:{ClientSessionId} BoundUserId:{BoundUserId} OutboundLength:{OutboundLength}", msgId, session.SessionId, boundUserId, outbound.Length);
                            centerSender.SendOrBuffer(outbound);
                        }
                        else if (msgId >= 40000 && msgId < 50000)
                        {
                            Shared.Log.Debug("Gateway 路由客户端消息 -> Battle MsgId:{MsgId} ClientSessionId:{ClientSessionId} BoundUserId:{BoundUserId} OutboundLength:{OutboundLength}", msgId, session.SessionId, boundUserId, outbound.Length);
                            SendToBattle(outbound, session.SessionId);
                        }
                        else
                        {
                            Shared.Log.Warning($"Gateway: 未知的消息路由 MsgId=>{msgId}");
                        }
                    }
                    else
                    {
                        Shared.Log.Warning($"收到无效的数据包长度。SessionId:{session.SessionId} Length:{data.Length}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"Gateway 处理客户端数据异常 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };

            // 将数据处理器注册到四种服务器上
            tcpServer.OnDataReceived += onDataReceived;
            kcpServer.OnDataReceived += onDataReceived;
            udpServer.OnDataReceived += onDataReceived;
            webSocketServer.OnDataReceived += onDataReceived;

            // 客户端断开连接处理：记录日志并从会话管理器移除会话
            SessionDisconnectedHandler onSessionDisconnected = (session, reason) =>
            {
                Shared.Log.Info($"客户端断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Reason:{reason}");
                // 断线重连（对标 KBE 断线恢复）：有用户绑定的会话记录挂起，宽限期内重新登录可恢复
                int boundUserId = Gateway.Managers.GatewaySessionManager.Instance.GetUserIdBySessionId(session.SessionId);
                if (boundUserId > 0)
                {
                    int grace = ConfigHelper.GetConfig<int>("GatewayReconnectGraceSeconds") == 0 ? 30 : ConfigHelper.GetConfig<int>("GatewayReconnectGraceSeconds");
                    if (grace > 0)
                    {
                        pendingReconnects[session.SessionId] = new PendingReconnect
                        {
                            UserId = boundUserId,
                            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(grace)
                        };
                        Shared.Log.Info($"Gateway 记录断线重连会话 SessionId:{session.SessionId} UserId:{boundUserId} 宽限:{grace}s");
                    }
                }
                Gateway.Managers.GatewaySessionManager.Instance.RemoveSession(session.SessionId);
                clientBattleNodeBindings.TryRemove(session.SessionId, out _); // 清除 Battle 节点绑定
                NotifyPlayerDisconnected(session.SessionId);
            };

            tcpServer.OnSessionDisconnected += onSessionDisconnected;
            kcpServer.OnSessionDisconnected += onSessionDisconnected;
            udpServer.OnSessionDisconnected += onSessionDisconnected;
            webSocketServer.OnSessionDisconnected += onSessionDisconnected;

            await tcpServer.StartAsync(port);
            await kcpServer.StartAsync(port + 1);
            await udpServer.StartAsync(port + 2);
            await webSocketServer.StartAsync(port + 3);

            Shared.Log.Info($"网关服务器已启动，监听 TCP 端口: {port}, KCP 端口: {port + 1}, UDP 端口: {port + 2}, WebSocket 端口: {port + 3}");

            // TCP 空闲会话超时踢线 + 重连挂起清理（对标 KBE 心跳超时；UDP/KCP 已有各自 5 分钟超时）
            int tcpTimeoutSeconds = ConfigHelper.GetConfig<int>("GatewayTcpTimeoutSeconds") == 0 ? 300 : ConfigHelper.GetConfig<int>("GatewayTcpTimeoutSeconds");
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    var now = DateTime.UtcNow;

                    // 重连挂起过期清理
                    foreach (var pair in pendingReconnects)
                    {
                        if (pair.Value.ExpiresAtUtc < now)
                        {
                            pendingReconnects.TryRemove(pair.Key, out _);
                        }
                    }

                    // TCP 空闲超时踢线（无任何收发超过阈值的连接）
                    foreach (var session in Gateway.Managers.GatewaySessionManager.Instance.GetAllSessions())
                    {
                        if (session is Network.Tcp.TcpSession && now - session.LastActivityTime > TimeSpan.FromSeconds(tcpTimeoutSeconds))
                        {
                            Shared.Log.Warning($"Gateway TCP 会话空闲超时，断开 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} 超时:{tcpTimeoutSeconds}s");
                            session.Close();
                        }
                    }
                }
            });

            AttachCenterNodeLifecycle(centerClient, port);
        }

        /// <summary>
        /// 建立并返回到后端 Login, Game, Center, Battle 服务器的 TCP 客户端包装器。
        /// - 读取配置的 Host/Port（支持默认值）。
        /// - 为每个后端客户端注册连接、断开、接收数据事件，负责将后端返回的数据解析并转发给相应的客户端会话。
        /// - 连接建立后发送内部认证握手（InternalAuthFilter），未认证的连接将被后端拒绝业务消息。
        /// </summary>
        private static (TcpClientWrapper, TcpClientWrapper, TcpClientWrapper) ConnectToBackendServers()
        {
            string sharedSecret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            string gatewayHost = ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1";
            int gatewayPort = ConfigHelper.GetConfig<int>("GatewayPort") == 0 ? 31300 : ConfigHelper.GetConfig<int>("GatewayPort");
            string gatewayNodeId = $"Gateway-{gatewayHost}:{gatewayPort}";

            void SendAuthHandshake(TcpClientWrapper client)
            {
                var authFilter = new Framework.Core.Security.InternalAuthFilter(sharedSecret, gatewayNodeId);
                byte[] authPacket = authFilter.BuildAuthPacket();
                Shared.Log.Info($"Gateway 向后端发送内部认证握手 Length:{authPacket.Length}");
                client.Send(authPacket);
            }

            // 读取 Login 后端配置（支持默认端口）
            int loginPort = ConfigHelper.GetConfig<int>("LoginPort") == 0 ? 31302 : ConfigHelper.GetConfig<int>("LoginPort");
            string loginHost = ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1";
            var loginClient = new TcpClientWrapper(loginHost, loginPort);
            // 发送器必须在 ConnectAsync 之前创建并订阅 OnConnected（避免快速连接竞态导致缓冲永不冲刷）
            loginSender = new BufferedBackendSender("Login", data => loginClient.Send(data));
            loginClient.OnConnected += _ => loginSender?.OnConnected();
            loginClient.OnDisconnected += (_, __) => loginSender?.OnDisconnected();
            loginClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 Login 服务器 (Host:{loginHost} Port:{loginPort})");
                SendAuthHandshake(loginClient);
            };
            loginClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Login 服务器断开连接: {reason}");
            loginClient.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Shared.Log.Warning("Login 回包长度不足，已丢弃。");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;
                    Shared.Log.Debug("Gateway <- Login 收到回包 MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} Remote:{Remote}", msgId, data.Length, payloadLength, session.RemoteEndPoint);
                    byte[] payload = data.Slice(4).ToArray();

                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                    {
                        Shared.Log.Warning($"Login 回包缺少目标会话元数据 MsgId:{msgId}");
                        return;
                    }

                    int resumedLoginUserId = 0;
                    if (msgId == MessageIds.LoginRes)
                    {
                        var loginRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Login.LoginResponse>(cleanPayload);
                        if (loginRes?.Success == true && loginRes.UserId > 0)
                        {
                            resumedLoginUserId = loginRes.UserId;
                            Gateway.Managers.GatewaySessionManager.Instance.BindUser(clientSessionId, loginRes.UserId);
                            if (!string.IsNullOrWhiteSpace(loginRes.UniqueId))
                            {
                                Gateway.Managers.GatewaySessionManager.Instance.BindUid(clientSessionId, loginRes.UniqueId);
                            }
                            if (!string.IsNullOrWhiteSpace(loginRes.Nickname))
                            {
                                Gateway.Managers.GatewaySessionManager.Instance.BindNickname(clientSessionId, loginRes.Nickname);
                            }
                        }
                        else if (loginRes != null && !loginRes.Success)
                        {
                            Shared.Log.Warning($"Login 登录失败回包 MsgId:{msgId} ClientSessionId:{clientSessionId} Message:{loginRes.Message}");
                        }
                    }
                    else if (msgId == MessageIds.LogoutRes)
                    {
                        var logoutRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Login.LogoutResponse>(cleanPayload);
                        if (logoutRes?.Success == true)
                        {
                            Gateway.Managers.GatewaySessionManager.Instance.UnbindUser(clientSessionId);
                            Gateway.Managers.GatewaySessionManager.Instance.UnbindUid(clientSessionId);
                            Gateway.Managers.GatewaySessionManager.Instance.UnbindNickname(clientSessionId);
                        }
                        else if (logoutRes != null && !logoutRes.Success)
                        {
                            Shared.Log.Warning($"Logout 回包失败 MsgId:{msgId} ClientSessionId:{clientSessionId} Message:{logoutRes.Message}");
                        }
                    }

                    var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(clientSessionId);
                    if (clientSession == null)
                    {
                        Shared.Log.Warning($"Login 回包目标会话不存在，已丢弃 MsgId:{msgId} ClientSessionId:{clientSessionId}");
                        return;
                    }

                    byte[] clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, cleanPayload, out int totalLength);
                    Shared.Log.Debug("Gateway -> Client 转发 Login 回包 MsgId:{MsgId} ClientSessionId:{ClientSessionId} TargetRemote:{TargetRemote} PacketLength:{PacketLength}", msgId, clientSessionId, clientSession.RemoteEndPoint, totalLength);
                    Network.PacketSender.Send(clientSession, clientPacket, totalLength);

                    // 断线重连（对标 KBE 断线恢复）：登录成功且该用户存在挂起重连记录 →
                    // 把新会话迁移到旧会话 ID（后端按旧 ID 续接挂起实体），并通知 Battle 实体恢复在线
                    if (resumedLoginUserId > 0)
                    {
                        TryResumePendingSession(clientSessionId, resumedLoginUserId);
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"Gateway 处理 Login 回包异常 Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };
            _ = loginClient.ConnectAsync();

            int gamePort = ConfigHelper.GetConfig<int>("GamePort") == 0 ? 31304 : ConfigHelper.GetConfig<int>("GamePort");
            string gameHost = ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1";
            var gameClient = new TcpClientWrapper(gameHost, gamePort);
            // 发送器必须在 ConnectAsync 之前创建并订阅 OnConnected（避免快速连接竞态）
            gameSender = new BufferedBackendSender("Game", data => gameClient.Send(data));
            gameClient.OnConnected += _ => gameSender?.OnConnected();
            gameClient.OnDisconnected += (_, __) => gameSender?.OnDisconnected();
            gameClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 Game 服务器 (Host:{gameHost} Port:{gamePort})");
                SendAuthHandshake(gameClient);
            };
            gameClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Game 服务器断开连接: {reason}");
            gameClient.OnDataReceived += (session, data) =>
            {
                if (data.Length < 4)
                {
                    Shared.Log.Warning("Game 回包长度不足，已丢弃。");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                int payloadLength = data.Length - 4;
                Shared.Log.Debug("Gateway <- Game 收到回包 MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} Remote:{Remote}", msgId, data.Length, payloadLength, session.RemoteEndPoint);
                byte[] payload = data.Slice(4).ToArray();

                bool broadcast = Shared.RouteMetadata.TryExtractBroadcast(payload, out bool broadcastFlag, out var payloadAfterBroadcast) && broadcastFlag;
                if (broadcast)
                {
                    var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadAfterBroadcast, out int totalLength);
                    try
                    {
                        Shared.Log.Debug("Gateway 广播 Game 回包 MsgId:{MsgId} PacketLength:{PacketLength}", msgId, totalLength);
                        Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, totalLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                    }
                    return;
                }

                long targetSessionId;
                byte[] cleanPayload;
                if (!Shared.RouteMetadata.TryExtractTargetSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
                {
                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
                    {
                        Shared.Log.Warning($"Game 回包缺少目标会话元数据 MsgId:{msgId}");
                        return;
                    }
                }

                var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(targetSessionId);
                if (clientSession == null)
                {
                    Shared.Log.Warning($"Game 回包目标会话不存在，已丢弃 MsgId:{msgId} TargetSessionId:{targetSessionId}");
                    return;
                }

                var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, cleanPayload, out int responseLength);
                Shared.Log.Debug("Gateway -> Client 转发 Game 回包 MsgId:{MsgId} TargetSessionId:{TargetSessionId} TargetRemote:{TargetRemote} PacketLength:{PacketLength}", msgId, targetSessionId, clientSession.RemoteEndPoint, responseLength);
                Network.PacketSender.Send(clientSession, clientPacket, responseLength);
            };
            _ = gameClient.ConnectAsync();

            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            var centerClient = new TcpClientWrapper(centerHost, centerPort);
            // 发送器必须在 ConnectAsync 之前创建并订阅 OnConnected（避免快速连接竞态）
            centerSender = new BufferedBackendSender("Center", data => centerClient.Send(data));
            centerClient.OnConnected += _ => centerSender?.OnConnected();
            centerClient.OnDisconnected += (_, __) => centerSender?.OnDisconnected();
            centerClient.OnConnected += session =>
            {
                Shared.Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                SendAuthHandshake(centerClient);
            };
            centerClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Center 服务器断开连接: {reason}");
            centerClient.OnDataReceived += delegate (Network.ISession session, ReadOnlyMemory<byte> data)
            {
                if (data.Length < 4)
                {
                    Shared.Log.Warning("Center 回包长度不足，已丢弃。");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                int payloadLength = data.Length - 4;
                Shared.Log.Debug("Gateway <- Center 收到回包 MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} Remote:{Remote}", msgId, data.Length, payloadLength, session.RemoteEndPoint);
                byte[] payload = data.Slice(4).ToArray();

                // 实体迁移（91005）：Center 通知切换玩家 Battle 节点绑定（对标 KBE cellappmgr 实体搬迁后的路由更新）。
                // 内部控制消息无客户端路由元数据，须在元数据提取之前处理。
                if (msgId == Framework.Protocol.Generated.MessageIds.EntityMigrateRouted)
                {
                    try
                    {
                        var routed = MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.EntityMigrateRouted>(payload.AsSpan());
                        if (routed != null && routed.ClientSessionId > 0 && !string.IsNullOrWhiteSpace(routed.NewNodeId))
                        {
                            clientBattleNodeBindings[routed.ClientSessionId] = routed.NewNodeId;
                            Shared.Log.Info($"Gateway 实体迁移更新玩家 Battle 绑定 ClientSessionId:{routed.ClientSessionId} -> {routed.NewNodeId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Warning($"Gateway 解析 EntityMigrateRouted 失败: {ex.Message}");
                    }
                    return;
                }

                bool broadcast = Shared.RouteMetadata.TryExtractBroadcast(payload, out bool broadcastFlag, out var payloadAfterBroadcast) && broadcastFlag;
                if (broadcast)
                {
                    var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadAfterBroadcast, out int broadcastLength);
                    try
                    {
                        Shared.Log.Debug("Gateway 广播 Center 回包 MsgId:{MsgId} PacketLength:{PacketLength}", msgId, broadcastLength);
                        Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, broadcastLength).ToArray());
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(packet); }
                    return;
                }

                long targetSessionId;
                byte[] cleanPayload;
                if (!Shared.RouteMetadata.TryExtractTargetSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
                {
                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
                    {
                        Shared.Log.Warning($"Center 回包缺少目标会话元数据 MsgId:{msgId}");
                        return;
                    }
                }

                var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(targetSessionId);
                if (clientSession == null)
                {
                    Shared.Log.Warning($"Center 回包目标会话不存在，已丢弃 MsgId:{msgId} TargetSessionId:{targetSessionId}");
                    return;
                }

                // 静态分片：匹配成功回包携带 BattleNodeId → 绑定玩家到对应 Battle 节点（对标 KBE cellappmgr 调度）
                if (msgId == Framework.Protocol.Generated.MessageIds.CenterMatchResult)
                {
                    try
                    {
                        var matchRes = MemoryPackSerializer.Deserialize<Framework.Protocol.Generated.CenterMatchResult>(cleanPayload.AsSpan());
                        if (matchRes != null && matchRes.Success && !string.IsNullOrWhiteSpace(matchRes.BattleNodeId))
                        {
                            clientBattleNodeBindings[targetSessionId] = matchRes.BattleNodeId;
                            Shared.Log.Info($"Gateway 绑定玩家到 Battle 节点 ClientSessionId:{targetSessionId} Node:{matchRes.BattleNodeId}");
                        }
                    }
                    catch
                    {
                        // 非 MemoryPack（旧 JSON 协议）或解析失败：忽略，走默认节点
                    }
                }

                var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, cleanPayload, out int responseLength);
                Shared.Log.Debug("Gateway -> Client 转发 Center 回包 MsgId:{MsgId} TargetSessionId:{TargetSessionId} TargetRemote:{TargetRemote} PacketLength:{PacketLength}", msgId, targetSessionId, clientSession.RemoteEndPoint, responseLength);
                Network.PacketSender.Send(clientSession, clientPacket, responseLength);
            };
            _ = centerClient.ConnectAsync();

            // Battle 节点（静态分片，对标 KBE cellappmgr 调度）：
            // 支持多节点配置 BattleNodes=["host:port",...]；缺省回退单节点 BattleHost:BattlePort
            var battleNodeEndpoints = new List<string>();
            string? battleNodesCfg = ConfigHelper.GetConfig<string>("BattleNodes");
            if (!string.IsNullOrWhiteSpace(battleNodesCfg))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(battleNodesCfg);
                    if (parsed != null && parsed.Count > 0)
                    {
                        battleNodeEndpoints.AddRange(parsed);
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"Gateway BattleNodes 配置解析失败，回退单节点: {ex.Message}");
                }
            }
            if (battleNodeEndpoints.Count == 0)
            {
                int battlePort = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 31307 : ConfigHelper.GetConfig<int>("BattlePort");
                string battleHost = ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1";
                battleNodeEndpoints.Add($"{battleHost}:{battlePort}");
            }

            foreach (var endpoint in battleNodeEndpoints)
            {
                var parts = endpoint.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out int nodePort))
                {
                    Shared.Log.Warning($"Gateway Battle 节点地址无效: {endpoint}");
                    continue;
                }
                string nodeHost = parts[0];
                string nodeId = $"Battle-{nodeHost}:{nodePort}";
                var battleClient = new TcpClientWrapper(nodeHost, nodePort);
                battleNodes[nodeId] = battleClient;

                var sender = new BufferedBackendSender(nodeId, data => battleClient.Send(data));
                battleNodeSenders[nodeId] = sender;

                battleClient.OnConnected += _ =>
                {
                    Shared.Log.Info($"已连接到 Battle 节点 {nodeId}");
                    sender.OnConnected();
                    SendAuthHandshake(battleClient);
                };
                battleClient.OnDisconnected += (_, reason) => sender.OnDisconnected();
                battleClient.OnDataReceived += HandleBattleNodeData;
                _ = battleClient.ConnectAsync();
            }
            if (battleNodeSenders.Count > 0 && string.IsNullOrEmpty(defaultBattleNodeId))
            {
                defaultBattleNodeId = battleNodeSenders.Keys.OrderBy(k => k).First();
            }
            Shared.Log.Info($"Gateway Battle 节点就绪: {battleNodeSenders.Count} 个（默认 {defaultBattleNodeId}）");

            return (loginClient, gameClient, centerClient);
        }

        /// <summary>处理 Battle 节点回包（转发给客户端；多节点共用同一处理）。</summary>
        private static void HandleBattleNodeData(Network.ISession session, ReadOnlyMemory<byte> data)
        {
            if (data.Length < 4)
            {
                Shared.Log.Warning("Battle 回包长度不足，已丢弃。");
                return;
            }

            int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
            int payloadLength = data.Length - 4;
            Shared.Log.Debug("Gateway <- Battle 收到回包 MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength} Remote:{Remote}", msgId, data.Length, payloadLength, session.RemoteEndPoint);
            byte[] payload = data.Slice(4).ToArray();

            bool broadcast = Shared.RouteMetadata.TryExtractBroadcast(payload, out bool broadcastFlag, out var payloadAfterBroadcast) && broadcastFlag;
            if (broadcast)
            {
                var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadAfterBroadcast, out int totalLength);
                try
                {
                    Shared.Log.Debug("Gateway 广播 Battle 回包 MsgId:{MsgId} PacketLength:{PacketLength}", msgId, totalLength);
                    Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, totalLength).ToArray());
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(packet); }
                return;
            }

            long targetSessionId;
            byte[] cleanPayload;
            if (!Shared.RouteMetadata.TryExtractTargetSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
            {
                if (!Shared.RouteMetadata.TryExtractClientSessionId(payloadAfterBroadcast, out targetSessionId, out cleanPayload))
                {
                    Shared.Log.Warning($"Battle 回包缺少目标会话元数据 MsgId:{msgId}");
                    return;
                }
            }

            var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(targetSessionId);
            if (clientSession == null)
            {
                Shared.Log.Warning($"Battle 回包目标会话不存在，已丢弃 MsgId:{msgId} TargetSessionId:{targetSessionId}");
                return;
            }

            var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, cleanPayload, out int responseLength);
            Shared.Log.Debug("Gateway -> Client 转发 Battle 回包 MsgId:{MsgId} TargetSessionId:{TargetSessionId} TargetRemote:{TargetRemote} PacketLength:{PacketLength}", msgId, targetSessionId, clientSession.RemoteEndPoint, responseLength);
            Network.PacketSender.Send(clientSession, clientPacket, responseLength);
        }

        /// <summary>
        /// 将网关节点的生命周期挂载到指定的 Center 连接：连接时注册节点并启动定期心跳上报，断开时停止心跳。
        /// </summary>
        /// <remarks>从 ConfigHelper 获取 Center/Gateway 配置；连接后发送注册信息并以 10 秒间隔上报在线数，使用
        /// CancellationTokenSource 管理心跳任务的取消。</remarks>
        /// <param name="centerClient">用于与 Center 建立连接并处理连接与断开事件的 TcpClientWrapper。</param>
        /// <param name="port">网关的本地监听端口，用于构建节点标识和注册信息。</param>
        private static void AttachCenterNodeLifecycle(TcpClientWrapper centerClient, int port)
        {
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string gatewayHost = ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1";
            string nodeId = $"Gateway-{gatewayHost}:{port}";

            centerClient.OnConnected += session =>
            {
                Shared.Log.Info($"Gateway 节点生命周期已挂载到 Center 连接 (Host:{centerHost} Port:{centerPort})");
                SendRegisterNode(centerClient, nodeId, "Gateway", gatewayHost, port, Gateway.Managers.GatewaySessionManager.Instance.GetOnlineCount());

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts = new CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, Gateway.Managers.GatewaySessionManager.Instance.GetOnlineCount());
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
            };
        }

        /// <summary>
        /// 构建节点注册请求（包含时间戳与签名）、序列化为 UTF-8 并通过指定的 TcpClientWrapper 发送到中心服务器。
        /// </summary>
        /// <remarks>计算基于节点信息和当前 UTC 时间的时间戳与签名；将 CenterRegisterNodeRequest 序列化为 UTF-8 字节数组；使用
        /// MessageIds.CenterRegisterNodeReq 构建数据包并发送实际长度的字节；发送后将用于构建包的字节数组返回到共享数组池。</remarks>
        /// <param name="centerClient">用于向中心服务器发送数据的 TcpClientWrapper 实例。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="nodeType">节点类型标识符。</param>
        /// <param name="host">节点的主机名或 IP 地址。</param>
        /// <param name="port">节点监听的端口号。</param>
        /// <param name="currentLoad">节点当前的负载值。</param>
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
        /// 发送包含节点标识、当前负载、时间戳与签名的状态消息到中心服务器。
        /// </summary>
        /// <remarks>使用 UTC Unix 时间戳（秒），并基于 "{nodeId}|{currentLoad}|{timestamp}" 计算签名；请求以 UTF-8 JSON
        /// 序列化并通过 PacketBuilder 构建后发送，临时缓冲区会返回到 ArrayPool。</remarks>
        /// <param name="centerClient">用于与中心服务器通信的 TCP 客户端包装器。</param>
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
            centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        /// <summary>
        /// 使用配置的共享密钥对输入字符串计算基于 HMAC-SHA256 的签名，并以 Base64 编码返回。
        /// </summary>
        /// <remarks>从配置键 CenterNodeSharedSecret 获取密钥（不存在时使用默认值 'change-this-secret'），使用 UTF-8 编码对输入进行
        /// HMAC-SHA256 计算，使用完毕后释放 HMAC 实例。</remarks>
        /// <param name="source">要签名的原始字符串。</param>
        /// <returns>返回使用配置键 CenterNodeSharedSecret（若未配置则使用默认值 'change-this-secret'）作为密钥生成的 HMAC-SHA256 哈希的 Base64 编码字符串。</returns>
        private static string ComputeCenterSignature(string source)
        {
            string secret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 配置并启动基于 YARP 的反向代理，在指定或默认端口上使用 Kestrel 监听，并将 /api 和 /swagger 路由到登录后端。
        /// </summary>
        /// <remarks>从配置读取 GatewayHttpPort 和 LoginHttpUrl（默认分别为 31301 和 http://127.0.0.1:31303），显式配置
        /// Kestrel 监听端口，使用 Serilog，并通过内存加载 YARP 路由与集群配置；调用 app.RunAsync() 以非阻塞方式运行主机。</remarks>
        /// <param name="args">传递给 WebApplication.CreateBuilder 的命令行参数。</param>
        /// <returns>可等待的 Task，表示异步启动操作的完成。</returns>
        public static async Task StartReverseProxyAsync(string[] args)
        {
            // HTTP 监听端口和后端 Login HTTP 地址（支持默认值）
            int httpPort = ConfigHelper.GetConfig<int>("GatewayHttpPort") == 0 ? 31301 : ConfigHelper.GetConfig<int>("GatewayHttpPort");
            string loginHttpUrl = ConfigHelper.GetConfig<string>("LoginHttpUrl") ?? "http://127.0.0.1:31303";

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(httpPort);
            });

            builder.Host.UseSerilog();
            builder.Services.AddReverseProxy()
                .LoadFromMemory(
                    new[] {
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_api_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/api/{**catch-all}"
                            }
                        },
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_swagger_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/swagger/{**catch-all}"
                            }
                        }
                    },
                    new[] {
                        new Yarp.ReverseProxy.Configuration.ClusterConfig()
                        {
                            ClusterId = "login_api_cluster",
                            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "default", new Yarp.ReverseProxy.Configuration.DestinationConfig() { Address = loginHttpUrl } }
                            }
                        }
                    }
                );

            var app = builder.Build();
            app.MapReverseProxy();

            Shared.Log.Info($"网关 HTTP API 反向代理已启动，监听端口: {httpPort} 并路由 /api 至 {loginHttpUrl}");
            _ = app.RunAsync();
        }

        /// <summary>
        /// 断线重连恢复：把新登录会话迁移到挂起的旧会话 ID（后端按旧 ID 续接挂起实体），
        /// 并通知 Battle 实体从挂起转在线。在登录成功回包处理中调用。
        /// </summary>
        private static void TryResumePendingSession(long newSessionId, int userId)
        {
            foreach (var pair in pendingReconnects)
            {
                if (pair.Value.UserId != userId) continue;
                pendingReconnects.TryRemove(pair.Key, out _);
                if (pair.Value.ExpiresAtUtc < DateTime.UtcNow)
                {
                    Shared.Log.Info($"Gateway 重连挂起已过期，忽略 SessionId:{pair.Key}");
                    return;
                }
                if (Gateway.Managers.GatewaySessionManager.Instance.ResumeSession(newSessionId, pair.Key))
                {
                    // 通知 Battle 节点：玩家会话恢复（实体从挂起转在线），按玩家绑定路由到所在节点
                    var resume = new Framework.Protocol.Generated.PlayerSessionResume { ClientSessionId = pair.Key };
                    byte[] payload = resume.Serialize();
                    byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, pair.Key);
                    byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.PlayerSessionResume, routedPayload, out int totalLength);
                    string nodeId = clientBattleNodeBindings.TryGetValue(pair.Key, out var bound) ? bound : defaultBattleNodeId;
                    if (battleNodeSenders.TryGetValue(nodeId, out var sender))
                    {
                        sender.SendOrBuffer(packet.AsSpan(0, totalLength).ToArray());
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                    }
                    else
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                        Shared.Log.Warning($"Gateway Battle 节点不可用（{nodeId}），重连恢复通知丢弃 ClientSessionId:{pair.Key}");
                    }
                }
                return;
            }
        }
    }
}