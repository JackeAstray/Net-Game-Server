using System;
using System.Threading.Tasks;
using Shared;
using Network;
using Network.Tcp;

namespace Game
{
    public static class GameServerApp
    {
        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("GamePort") == 0 ? 30004 : ConfigHelper.GetConfig<int>("GamePort");

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            var router = new global::Network.Routing.MessageRouter();
            var chatHandler = new Handlers.ChatHandler(networkManager);
            chatHandler.Register(router);

            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) => 
            {
                Log.Info($"接收到数据，长度: {data.Length}");
                if (data.Length >= 12) 
                {
                    long originalSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    var innerData = data.Slice(8); 

                    if (innerData.Length >= 4) 
                    {
                        var msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(innerData.Span.Slice(0,4));
                        var payload = innerData.Slice(4);

                        var clientSession = new Game.Network.ClientSessionWrapper(session, originalSessionId);
                        router.RouteMessage(clientSession, msgId, payload);
                    }
                }
            };
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("GameTcp", tcpServer);

            await networkManager.StartServerAsync("GameTcp", port);
            Log.Info($"游戏服务器已启动，监听端口: {port}");
        }

        public static void ConnectToDatabase()
        {
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");

            _ = dbClient.ConnectAsync();
        }
    }
}
