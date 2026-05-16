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
