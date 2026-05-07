using Network;
using Network.Tcp;
using Shared;

namespace Login
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Shared.log");
            Log.Info("登录服务器正在启动...");

            int port = ConfigHelper.GetConfig<int>("LoginPort");
            if (port == 0) port = 8182;

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) => Log.Info($"接收到数据，长度: {data.Length}");
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("LoginTcp", tcpServer);

            await networkManager.StartServerAsync("LoginTcp", port);
            Log.Info($"登录服务器已启动，监听端口: {port}");

            // 连接 DB
            int dbPort = ConfigHelper.GetConfig<int>("DBPort");
            if (dbPort == 0) dbPort = 8083;
            var dbClient = new TcpClientWrapper("127.0.0.1", dbPort);
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Port:{dbPort})");
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");
            _ = dbClient.ConnectAsync();

            await Task.Delay(-1);
        }
    }
}