using System;
using System.Threading.Tasks;
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
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;
        private static Battle.Handlers.SceneManager? sceneManager;

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

            sceneManager = new Battle.Handlers.SceneManager();
            var entitySyncHandler = new Battle.Handlers.EntitySyncHandler(sceneManager);
            var roomHandler = new Battle.Handlers.RoomHandler(sceneManager, entitySyncHandler);
            var battleMainHandler = new Battle.Handlers.BattleMainHandler(sceneManager);

            handlers = Battle.Handlers.MessageRouter.BuildHandlers(roomHandler, entitySyncHandler, battleMainHandler);

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点/网关已连接到战斗服: {session.RemoteEndPoint}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Log.Info($"节点/网关从战斗服断开，原因: {reason}");
            };

            tcpServer.OnDataReceived += async (session, data) =>
            {
                // 解析 SessionId 和内部消息结构 [SessionId(8)][MsgId(4)][Payload]
                if (data.Length >= 12)
                {
                    long originalSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    var innerData = data.Slice(8);

                    if (innerData.Length >= 4)
                    {
                        var msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(innerData.Span.Slice(0, 4));
                        var payload = innerData.Slice(4);

                        // 战斗服高频包处理分发（如位移、技能同步）
                        if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                        {
                            try
                            {
                                await handlerAction(payload, session, originalSessionId);
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Battle 处理消息 ({msgId}) 发生异常: " + ex);
                            }
                        }
                        else
                        {
                            // 打印或其他处理
                        }
                    }
                }
            };

            networkManager.RegisterServer("BattleTcp", tcpServer);

            await networkManager.StartServerAsync("BattleTcp", port);
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
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
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
            centerClient.OnDataReceived += (session, data) => Log.Info($"Battle 收到 Center 消息，长度: {data.Length}");
            _ = centerClient.ConnectAsync();
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
            var registerRequest = new CenterRegisterNodeRequest
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                CurrentLoad = currentLoad
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(registerRequest);
            byte[] packet = PacketBuilder.BuildSessionWrapperPacket(0, MessageIds.CenterRegisterNodeReq, payload);
            centerClient.Send(packet);
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
            var statusRequest = new CenterNodeStatusRequest
            {
                NodeId = nodeId,
                CurrentLoad = currentLoad
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(statusRequest);
            byte[] packet = PacketBuilder.BuildSessionWrapperPacket(0, MessageIds.CenterNodeStatusReq, payload);
            centerClient.Send(packet);
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