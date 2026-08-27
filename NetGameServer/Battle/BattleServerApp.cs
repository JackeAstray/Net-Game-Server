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

namespace Battle
{
    public static class BattleServerApp
    {
        private static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>? handlers;
        private static Framework.Protocol.MessageDispatcher? dispatcher;
        private static Framework.Scripting.ScriptHost? scriptHost;
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;
        private static Battle.Handlers.SceneManager? sceneManager;
        private static TcpClientWrapper? centerClient;
        public static string CurrentNodeId { get; private set; } = string.Empty;

        /// <summary>通知游戏逻辑脚本：实体创建（加入场景）。</summary>
        public static void NotifyEntityCreated(Framework.Entity.Entity entity)
        {
            scriptHost?.NotifyCreate(entity);
        }

        /// <summary>通知游戏逻辑脚本：实体销毁（离开场景）。</summary>
        public static void NotifyEntityDestroyed(Framework.Entity.Entity entity)
        {
            scriptHost?.NotifyDestroy(entity);
        }

        /// <summary>客户端会话 -> 网关会话 映射（帧同步广播用；收包时登记，断开时清除）</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession> clientGatewaySessions = new();

        /// <summary>根据客户端会话 ID 查找其网关会话（用于定向回包）。</summary>
        public static Network.ISession? GetGatewaySessionByClient(long clientSessionId)
        {
            clientGatewaySessions.TryGetValue(clientSessionId, out var session);
            return session;
        }

        /// <summary>登记客户端会话 -> 网关会话 绑定。</summary>
        public static void BindClientGateway(long clientSessionId, Network.ISession gatewaySession)
        {
            if (clientSessionId > 0 && gatewaySession != null)
            {
                clientGatewaySessions[clientSessionId] = gatewaySession;
            }
        }

        /// <summary>解除客户端会话绑定。</summary>
        public static void UnbindClientGateway(long clientSessionId)
        {
            clientGatewaySessions.TryRemove(clientSessionId, out _);
        }

        /// <summary>
        /// 加载配置，构建场景与消息处理器，注册并启动战斗服务器的 TCP 网络，处理会话连接/断开与数据接收并分发内部消息，随后连接到中心服。
        /// </summary>
        /// <remarks>使用 ConfigManager 加载配置；若未配置端口则使用默认端口 31307。初始化
        /// SceneManager、EntitySyncHandler、RoomHandler 和 BattleMainHandler，并通过 MessageRouter 构建处理器集合。创建 NetworkManager 与
        /// TcpServer，订阅连接/断开与数据接收事件；按二进制协议解析 [SessionId(8)][MsgId(4)][Payload] 并根据 MsgId 分发到相应处理器，处理器异常会被记录。注册并启动名为
        /// BattleTcp 的服务器，启动完成后记录监听端口并调用 ConnectToCenter(port)。</remarks>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task StartNetworkAsync()
        {
            Configs.ConfigManager.LoadAll(); // 读取策划配置文件

            int port = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 31307 : ConfigHelper.GetConfig<int>("BattlePort");

            // 单线程 tick 引擎（对标 KBE gameUpdateHertz，默认 20Hz）：驱动帧同步与定时逻辑
            int tickHertz = ConfigHelper.GetConfig<int>("BattleTickHertz") == 0 ? 20 : ConfigHelper.GetConfig<int>("BattleTickHertz");
            var tickEngine = new Framework.Tick.TickEngine(tickHertz);
            tickEngine.Start();

            // 实体备份服务（对标 KBE backuper 平滑分摊 + archiver 落盘）
            string backupFile = ConfigHelper.GetConfig<string>("BackupFilePath")
                ?? Path.Combine(AppContext.BaseDirectory, "backups", "entities.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            var backupService = new Framework.Entity.EntityBackupService(
                backupFile,
                periodInTicks: ConfigHelper.GetConfig<int>("BackupPeriodTicks") == 0 ? 100 : ConfigHelper.GetConfig<int>("BackupPeriodTicks"));

            // 游戏逻辑脚本宿主（对标 KBE Python 脚本层）：玩法逻辑与底层框架物理分离，可热更新
            string scriptsDir = ConfigHelper.GetConfig<string>("ScriptsDir") ?? Path.Combine(AppContext.BaseDirectory, "scripts");
            var scriptHost = new Framework.Scripting.ScriptHost(scriptsDir);
            scriptHost.Start();
            BattleServerApp.scriptHost = scriptHost;
            // tick 引擎驱动脚本 OnTick（游戏逻辑获得确定性帧驱动）
            tickEngine.OnTick += frame =>
            {
                if (sceneManager != null)
                {
                    foreach (var scene in sceneManager.GetAllScenes())
                    {
                        scriptHost.TickAll(scene.EntityManager, frame);
                        backupService.AddManager(scene.EntityManager);
                    }
                    // 按实体量平滑分摊备份（对标 KBE backuper：每 tick 只备份部分实体）
                    backupService.Tick();
                }
            };

            sceneManager = new Battle.Handlers.SceneManager();
            var entitySyncHandler = new Battle.Handlers.EntitySyncHandler(sceneManager);
            var roomHandler = new Battle.Handlers.RoomHandler(sceneManager, entitySyncHandler);
            var battleMainHandler = new Battle.Handlers.BattleMainHandler(sceneManager);

            // 帧同步管理器：客户端输入入队，tick 引擎聚合广播权威帧
            var frameSyncManager = new Battle.Handlers.FrameSyncManager(sceneManager, tickEngine);
            frameSyncManager.SetSendAction((targetSessionId, msgId, payload) =>
            {
                var gatewaySession = GetGatewaySessionByClient(targetSessionId);
                if (gatewaySession != null)
                {
                    byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
                    byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
                    try
                    {
                        gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                    }
                }
            });

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            dispatcher = Battle.Handlers.MessageRouter.BuildDispatcher(roomHandler, entitySyncHandler, frameSyncManager);
            handlers = Battle.Handlers.MessageRouter.BuildHandlers(roomHandler, entitySyncHandler, battleMainHandler, frameSyncManager);

            var tcpServer = new TcpServer();

            // 内部连接认证：网关/节点连接必须先通过认证握手（InternalAuth），密钥与 Center 节点注册共用。
            string authSecret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            var gatewayAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点/网关已连接到战斗服: {session.RemoteEndPoint}");
                gatewayAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, $"Battle-{ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1"}:{port}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                gatewayAuthFilters.TryRemove(session.SessionId, out _);
                // 清除该网关会话下绑定的所有客户端会话（玩家断开/网关断开）
                foreach (var pair in clientGatewaySessions)
                {
                    if (ReferenceEquals(pair.Value, session))
                    {
                        clientGatewaySessions.TryRemove(pair.Key, out _);
                    }
                }
                Log.Info($"节点/网关从战斗服断开，原因: {reason}");
            };

            tcpServer.OnDataReceived += async (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Battle 收到无效数据包，长度不足 4，Session:{session.SessionId} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;

                    // 内部连接认证：未认证连接只接受认证握手消息。
                    if (gatewayAuthFilters.TryGetValue(session.SessionId, out var authFilter))
                    {
                        if (!authFilter.IsAuthenticated)
                        {
                            if (Framework.Core.Security.InternalAuthFilter.IsAuthMessage(msgId))
                            {
                                byte[] authPayload = data.Slice(4).ToArray();
                                if (authFilter.TryAuthenticate(authPayload))
                                {
                                    Log.Info($"Battle <- Gateway/Node 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                }
                                else
                                {
                                    Log.Warning($"Battle <- Gateway/Node 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                    session.Close();
                                    return;
                                }
                                return;
                            }

                            Log.Warning($"Battle 拒绝未认证连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            return;
                        }
                    }

                    Log.Info($"Battle <- Gateway/Node 收到消息 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} MsgId:{msgId} PacketLength:{data.Length} PayloadLength:{payloadLength}");
                    byte[] payload = data.Slice(4).ToArray();

                    long originalSessionId = 0;
                    if (Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                    {
                        originalSessionId = clientSessionId;
                        payload = cleanPayload;
                        // 登记客户端会话 -> 网关会话 绑定（帧同步广播用）
                        BindClientGateway(originalSessionId, session);
                        Log.Debug($"Battle 路由元数据解析成功 ClientSessionId:{originalSessionId} MsgId:{msgId}");
                    }

                    // 新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容）
                    if (dispatcher != null && await dispatcher.TryDispatch(new Battle.Handlers.BattleSessionContext(session, originalSessionId), msgId, payload))
                    {
                        Log.Debug($"Battle 新协议分发完成 MsgId:{msgId} ClientSessionId:{originalSessionId}");
                    }
                    else if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                    {
                        try
                        {
                            Log.Info($"Battle 开始处理消息 MsgId:{msgId} SessionId:{session.SessionId} OriginalSessionId:{originalSessionId} PayloadLength:{payload.Length}");
                            await handlerAction(payload, session, originalSessionId);
                            Log.Info($"Battle 完成处理消息 MsgId:{msgId} SessionId:{session.SessionId} OriginalSessionId:{originalSessionId}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Battle 处理消息 ({msgId}) 发生异常: {ex}");
                        }
                    }
                    else
                    {
                        Log.Warning($"Battle 收到未知 MsgId: {msgId}");

                        if (originalSessionId > 0 && msgId >= 40000 && msgId < 50000)
                        {
                            int responseMsgId = msgId switch
                            {
                                MessageIds.BattleJoinReq => MessageIds.BattleJoinRes,
                                _ => 0
                            };

                            if (responseMsgId > 0)
                            {
                                byte[] unknownPayload = Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Battle.BattleJoinResponse
                                {
                                    Success = false,
                                    Message = $"未支持的战斗消息类型: {msgId}"
                                });
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
                    Log.Error($"Battle 处理客户端数据异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };

            await tcpServer.StartAsync(port);
            Log.Info($"Battle 战斗服务器网络已启动，监听端口: {port}");

            ConnectToCenter(port);
        }

        /// <summary>
        /// 连接到 Center 服务器，向其注册本节点并维持心跳与事件处理。
        /// </summary>
        /// <remarks>从配置读取 CenterPort、CenterHost 和 BattleHost（分别默认为 31306、127.0.0.1、127.0.0.1）。建立
        /// TcpClientWrapper，连接成功后发送注册信息、启动每 10 秒一次的心跳上报任务；断开时取消心跳并记录日志，同时处理接收的数据事件。</remarks>
        /// <param name="port">用于对外的端口号，用于在注册和状态上报中标识节点。</param>
        private static void ConnectToCenter(int port)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string battleHost = ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1";
            string nodeId = $"Battle-{battleHost}:{port}";
            CurrentNodeId = nodeId;
            centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                // 内部连接认证：先发送认证握手，再注册节点
                centerClient.SendInternalAuthHandshake(ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret", nodeId);
                SendRegisterNode(centerClient, nodeId, "Battle", battleHost, port, GetCurrentLoad());

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
            centerClient.OnDataReceived += async (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Battle 收到 Center 无效数据包，长度不足 4，Session:{session.SessionId} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;
                    Log.Info($"Battle <- Center 收到消息 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} MsgId:{msgId} PacketLength:{data.Length} PayloadLength:{payloadLength}");
                    byte[] payload = data.Slice(4).ToArray();

                    if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                    {
                        try
                        {
                            Log.Info($"Battle 开始处理 Center 消息 MsgId:{msgId} SessionId:{session.SessionId} PayloadLength:{payload.Length}");
                            await handlerAction(payload, session, 0);
                            Log.Info($"Battle 完成处理 Center 消息 MsgId:{msgId} SessionId:{session.SessionId}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Battle 处理 Center 下发消息 ({msgId}) 发生异常: {ex}");
                        }
                    }
                    else
                    {
                        Log.Warning($"Battle 收到未处理的 Center MsgId: {msgId}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Battle 处理 Center 回包异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };
            _ = centerClient.ConnectAsync();
        }

        public static void SyncRoomPlayerCount(string roomId)
        {
            if (centerClient == null || sceneManager == null || string.IsNullOrWhiteSpace(roomId))
            {
                return;
            }

            var request = new CenterRoomPlayerCountSyncRequest
            {
                RoomId = roomId,
                CurrentPlayers = sceneManager.GetPlayerCount(roomId)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRoomPlayerCountSyncReq, payload, out int totalLength);
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 同步房间人数失败 RoomId:{roomId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        public static void SyncRoomMemberLeave(string roomId, long clientSessionId)
        {
            if (centerClient == null || string.IsNullOrWhiteSpace(roomId) || clientSessionId <= 0)
            {
                return;
            }

            var request = new CenterRoomMemberLeaveSyncRequest
            {
                RoomId = roomId,
                ClientSessionId = clientSessionId
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRoomMemberLeaveSyncReq, payload, out int totalLength);
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 同步房间成员退出失败 RoomId:{roomId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 将本节点的注册请求发送到中心服务器。
        /// </summary>
        /// <remarks>将 CenterRegisterNodeRequest 序列化为 UTF-8 JSON，构建包含 MessageIds.CenterRegisterNodeReq
        /// 的会话包装数据包并通过 centerClient 发送。</remarks>
        /// <param name="centerClient">用于与中心服务器通信并发送数据的客户端包装器。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="nodeType">节点类型标识（例如角色或服务）。</param>
        /// <param name="host">节点的主机名或 IP 地址。</param>
        /// <param name="port">节点的监听端口。</param>
        /// <param name="currentLoad">节点当前的负载值，用于负载均衡或监控。</param>
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
                Log.Error($"Battle 向 Center 注册节点失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 将节点的当前负载序列化为 CenterNodeStatusRequest 并通过中心客户端发送。
        /// </summary>
        /// <remarks>将 CenterNodeStatusRequest 序列化为 UTF-8 字节，使用 MessageIds.CenterNodeStatusReq 构建会话封装报文并通过
        /// centerClient 发送。</remarks>
        /// <param name="centerClient">用于向中心服务器发送封装报文的 TcpClientWrapper 实例。</param>
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
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 向 Center 上报节点状态失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 使用共享密钥和 HMAC-SHA256 计算输入字符串的签名，并以 Base64 编码返回。
        /// </summary>
        /// <remarks>从配置键 'CenterNodeSharedSecret' 读取共享密钥；如果未配置，则回退到默认值 'change-this-secret'。</remarks>
        /// <param name="source">要计算签名的输入字符串。</param>
        /// <returns>签名的 Base64 编码字符串，使用 UTF-8 编码的输入和 HMAC-SHA256 生成。</returns>
        private static string ComputeCenterSignature(string source)
        {
            string secret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 返回当前负载计数，等于已绑定玩家数与场景数中的较大值。
        /// </summary>
        /// <remarks>通过比较 sceneManager.GetBoundPlayerCount() 与 sceneManager.GetSceneCount()
        /// 的值确定负载。</remarks>
        /// <returns>当前负载计数；sceneManager 为 null 时返回 0。</returns>
        private static int GetCurrentLoad()
        {
            if (sceneManager == null)
            {
                return 0;
            }

            return Math.Max(sceneManager.GetBoundPlayerCount(), sceneManager.GetSceneCount());
        }
    }
}