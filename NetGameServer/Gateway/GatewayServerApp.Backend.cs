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
    /// <summary>
    /// 网关 —— 后端连接模块（Login/Game/Center/Battle 客户端建立、内部认证、Battle 节点回包转发）。
    /// 与 GatewayServerApp.cs 同属一个 partial class，按关注点分文件组织。
    /// </summary>
    public static partial class GatewayServerApp
    {
        /// <summary>
        /// 建立并返回到后端 Login, Game, Center, Battle 服务器的 TCP 客户端包装器。
        /// - 读取配置的 Host/Port（支持默认值）。
        /// - 为每个后端客户端注册连接、断开、接收数据事件，负责将后端返回的数据解析并转发给相应的客户端会话。
        /// - 连接建立后发送内部认证握手（InternalAuthFilter），未认证的连接将被后端拒绝业务消息。
        /// </summary>
        private static (TcpClientWrapper, TcpClientWrapper, TcpClientWrapper) ConnectToBackendServers()
        {
            // 安全修复：拒绝占位符密钥。
            string sharedSecret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            string gatewayHost = ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1";
            int gatewayPort = ConfigHelper.GetConfig<int>("GatewayPort") == 0 ? 31300 : ConfigHelper.GetConfig<int>("GatewayPort");
            string gatewayNodeId = $"Gateway-{gatewayHost}:{gatewayPort}";

            void SendAuthHandshake(TcpClientWrapper client)
            {
                var authFilter = new Framework.Core.Security.InternalAuthFilter(sharedSecret, gatewayNodeId);
                byte[] authPacket = authFilter.BuildAuthPacket();
                Shared.Log.Info($"Gateway 向后端发送内部认证握手 Length:{authPacket.Length}");
                // 帧长度修复（P1）：auth 包为裸 [MsgId][payload]，显式加长度头再发送，避免长度启发式误判
                byte[] payload = authPacket.AsSpan(4).ToArray();
                byte[] framed = Network.Routing.PacketBuilder.BuildPacket(
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(authPacket.AsSpan(0, 4)),
                    payload, out int totalLength);
                client.SendFromPool(framed, totalLength);
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
                        Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet, totalLength);
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
                        Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet, broadcastLength);
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
                    Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet, totalLength);
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

    }
}
