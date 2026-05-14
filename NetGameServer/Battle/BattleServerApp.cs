using System;
using System.Threading.Tasks;
using Shared;
using Network;
using Network.Tcp;

namespace Battle
{
    public static class BattleServerApp
    {
        private static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>? _handlers;

        public static async Task StartNetworkAsync()
        {
            Configs.ConfigManager.LoadAll(); // 读取策划配置文件

            int port = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 30007 : ConfigHelper.GetConfig<int>("BattlePort");

            var sceneManager = new Battle.Handlers.SceneManager();
            var entitySyncHandler = new Battle.Handlers.EntitySyncHandler(sceneManager);
            var roomHandler = new Battle.Handlers.RoomHandler(sceneManager, entitySyncHandler);

            _handlers = Battle.Handlers.MessageRouter.BuildHandlers(roomHandler, entitySyncHandler);

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
                        if (_handlers != null && _handlers.TryGetValue(msgId, out var handlerAction))
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
        }
    }
}
