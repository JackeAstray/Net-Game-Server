using Network;
using Network.Tcp;
using Shared;

namespace Game
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Game.log");
            Log.Info("游戏服务器正在启动...");

            int port = ConfigHelper.GetConfig<int>("GamePort");
            if (port == 0) port = 8181;

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) => Log.Info($"接收到数据，长度: {data.Length}");
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("GameTcp", tcpServer);

            await networkManager.StartServerAsync("GameTcp", port);
            Log.Info($"游戏服务器已启动，监听端口: {port}");

            // 连接 DB
            int dbPort = ConfigHelper.GetConfig<int>("DBPort");
            if (dbPort == 0) dbPort = 8083;
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");

            // 异步连接，不阻塞主线程
            _ = dbClient.ConnectAsync();

            Log.Info("服务器启动流程完成。按 Ctrl+C 退出。");
            await Task.Delay(Timeout.Infinite);
        }
    }
}