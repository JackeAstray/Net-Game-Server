using Network;
using Network.Tcp;
using Shared;

namespace Gateway
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Gateway.log");
            Log.Info("网关服务器正在启动...");

            int port = ConfigHelper.GetConfig<int>("GatewayPort");
            if (port == 0) port = 8180;

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) => Log.Info($"接收到数据，长度: {data.Length}");
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("GatewayTcp", tcpServer);

            await networkManager.StartServerAsync("GatewayTcp", port);
            Log.Info($"网关服务器已启动，监听端口: {port}");

            // 连接 Login
            int loginPort = ConfigHelper.GetConfig<int>("LoginPort");
            if (loginPort == 0) loginPort = 8082;
            string loginHost = ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1";
            var loginClient = new TcpClientWrapper(loginHost, loginPort);
            loginClient.OnConnected += session => Log.Info($"已连接到 Login 服务器 (Host:{loginHost} Port:{loginPort})");
            loginClient.OnDisconnected += (session, reason) => Log.Warning($"与 Login 服务器断开连接: {reason}");
            _ = loginClient.ConnectAsync();

            // 连接 Game
            int gamePort = ConfigHelper.GetConfig<int>("GamePort");
            if (gamePort == 0) gamePort = 8081;
            string gameHost = ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1";
            var gameClient = new TcpClientWrapper(gameHost, gamePort);
            gameClient.OnConnected += session => Log.Info($"已连接到 Game 服务器 (Host:{gameHost} Port:{gamePort})");
            gameClient.OnDisconnected += (session, reason) => Log.Warning($"与 Game 服务器断开连接: {reason}");
            _ = gameClient.ConnectAsync();

            await Task.Delay(-1);
        }
    }
}