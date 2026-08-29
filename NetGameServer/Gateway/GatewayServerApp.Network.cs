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
using System.Linq;

namespace Gateway
{
    /// <summary>
    /// 网关 —— 网络引导模块（客户端监听四协议 + 数据接收路由 + 断线重连挂起 + TCP 超时踢线）。
    /// 与 GatewayServerApp.cs 同属一个 partial class，按关注点分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class GatewayServerApp
    {
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
                // 安全修复（P2）：断线通知属尽力而为，任一后端未连接/发送失败不得中断断开流程或产生未观察异常。
                try
                {
                    var disconnectPayload = Shared.RouteMetadata.AttachClientSessionId(Array.Empty<byte>(), clientSessionId);
                    var disconnectPacket = PacketBuilder.BuildPacket(MessageIds.PlayerDisconnectNotif, disconnectPayload, out int totalLength);
                    var outbound = disconnectPacket.AsSpan(0, totalLength).ToArray();
                    System.Buffers.ArrayPool<byte>.Shared.Return(disconnectPacket);

                    Shared.Log.Info($"Gateway 广播玩家断线通知 MsgId:{MessageIds.PlayerDisconnectNotif} ClientSessionId:{clientSessionId} PacketLength:{totalLength}");
                    loginSender?.SendOrBuffer(outbound);
                    gameSender?.SendOrBuffer(outbound);
                    // 断线通知广播到全部 Battle 节点（玩家可能挂在任一节点）
                    foreach (var sender in battleNodeSenders.Values)
                    {
                        sender?.SendOrBuffer(outbound);
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"Gateway 广播玩家断线通知异常 ClientSessionId:{clientSessionId} Exception:{ex.Message}");
                }
            }

            // 数据接收处理器：统一协议 [MsgId(4)][Payload]
            // 会话路由信息放入 JSON payload 元数据 __clientSessionId
            DataReceivedHandler onDataReceived = (session, data) =>
            {
                try
                {
                    // D6 客户端会话防重放：生命周期窗（MaxSessionLifetime）+ 空闲窗（MaxIdleSeconds）判定。
                    // 超出窗口的 SessionId 直接关连接并丢弃，防止捕获重放。
                    DateTime? createdAt = Gateway.Managers.GatewaySessionManager.Instance.GetCreatedAt(session.SessionId);
                    DateTime? lastActivity = Gateway.Managers.GatewaySessionManager.Instance.GetLastActivity(session.SessionId);
                    if (createdAt.HasValue && lastActivity.HasValue &&
                        !Framework.Core.Security.SessionGuard.IsSessionValid(createdAt.Value, lastActivity.Value, DateTime.UtcNow))
                    {
                        Shared.Log.Warning($"Gateway 会话超过最大生命周期或空闲超时，关连接 SessionId:{session.SessionId} EstablishedAt:{createdAt:O} LastActivity:{lastActivity:O}");
                        try { session.Close(); } catch { /* 关闭异常吞掉 */ }
                        return;
                    }
                    // 收到数据包：刷新最近活动时间
                    Gateway.Managers.GatewaySessionManager.Instance.TouchSession(session.SessionId);

                    if (data.Length >= 4)
                    {
                        int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                        int payloadLength = data.Length - 4;
                        Shared.Log.Debug("Gateway 接收到客户端数据 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);

                        // 安全修复（P0）：先剥离客户端可能注入的 __* 路由元数据（JSON 内嵌 / 伪造二进制尾部块），
                        // 再由网关附加受信任的元数据，防止未登录客户端伪造 __userId/__uid/__nickname 冒充他人。
                        byte[] payload = Shared.RouteMetadata.StripClientFields(data.Slice(4));
                        int boundUserId = Gateway.Managers.GatewaySessionManager.Instance.GetUserIdBySessionId(session.SessionId);
                        // 安全修复（P0）：Game 节点业务消息（好友/账户/背包，20000-29999 + 50000-69999）
                        // 必须已登录绑定，未绑定会话拒绝转发。
                        // 注意：Center(30000-39999)/Battle(40000-49999) 属会话路由消息（匹配/加入对局），
                        // 系统支持访客匹配——身份以 clientSessionId 为准、由服务端回包建立节点绑定；
                        // 身份伪造已由 StripClientFields + 受信元数据附加闭环（见上），此处为 Game 的纵深防御。
                        if (boundUserId <= 0 && ((msgId >= 20000 && msgId < 30000) || (msgId >= 50000 && msgId < 70000)))
                        {
                            Shared.Log.Warning($"Gateway 拒绝未登录会话的 Game 业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            return;
                        }

                        string boundUid = Gateway.Managers.GatewaySessionManager.Instance.GetUidBySessionId(session.SessionId);
                        string boundNickname = Gateway.Managers.GatewaySessionManager.Instance.GetNicknameBySessionId(session.SessionId);
                        // 性能优化（P-H1）：批量附加全部路由元数据（一次解析 + 一次构建），
                        // 取代逐字段 Attach 的 4 次 body 拷贝 + 4 次 JSON 序列化。
                        byte[] routedPayload = Shared.RouteMetadata.AttachClientRouteMetadata(
                            payload, session.SessionId, boundUserId > 0 ? boundUserId : (int?)null, boundUid, boundNickname);

                        byte[] wrapperMsg = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int routedLength);
                        byte[] outbound = wrapperMsg.AsSpan(0, routedLength).ToArray();
                        System.Buffers.ArrayPool<byte>.Shared.Return(wrapperMsg);

                        // 配置化路由：查源生成器产出的路由表（[GameMessage] 声明为唯一事实来源），
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

            int webSocketPort = ConfigHelper.GetConfig<int>("WebSocketPort");
            if (webSocketPort <= 0)
            {
                // 默认 GatewayPort + 10，避免与 Login HTTP API 的 31303（ApiPort）在单机部署时发生 TCP 端口冲突
                webSocketPort = port + 10;
            }

            await tcpServer.StartAsync(port);
            await kcpServer.StartAsync(port + 1);
            await udpServer.StartAsync(port + 2);
            await webSocketServer.StartAsync(webSocketPort);

            Shared.Log.Info($"网关服务器已启动，监听 TCP 端口: {port}, KCP 端口: {port + 1}, UDP 端口: {port + 2}, WebSocket 端口: {webSocketPort}");

            // TCP 空闲会话超时踢线 + 重连挂起清理（对标 KBE 心跳超时；UDP/KCP 已有各自 5 分钟超时）
            int tcpTimeoutSeconds = ConfigHelper.GetConfig<int>("GatewayTcpTimeoutSeconds") == 0 ? 300 : ConfigHelper.GetConfig<int>("GatewayTcpTimeoutSeconds");
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    var now = DateTime.UtcNow;

                    // 重连挂起过期清理
                    // 安全修复：ConcurrentDictionary 枚举 + TryRemove 抛异常；先快照 key 集合
                    foreach (var key in pendingReconnects.Keys.ToArray())
                    {
                        if (pendingReconnects.TryGetValue(key, out var pr) && pr.ExpiresAtUtc < now)
                        {
                            pendingReconnects.TryRemove(key, out _);
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
    }
}
