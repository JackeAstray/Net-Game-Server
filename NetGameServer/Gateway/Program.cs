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

            tcpServer.OnSessionConnected += session => 
            {
                Log.Info($"客户端已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            
            // Connect Backend Servers Before Accepting Data from Clients to ensure they exist
            // 连接 Login
            int loginPort = ConfigHelper.GetConfig<int>("LoginPort");
            if (loginPort == 0) loginPort = 8182;
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


            // Implement Gateway Routing Logic
            tcpServer.OnDataReceived += (session, data) => 
            {
                // In a robust implementation, we read the header here and decide who gets the message.
                // Assuming MsgId is an int (4 bytes)
                if (data.Length >= 4)
                {
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Log.Info($"Gateway 接收到数据 长度:{data.Length} MsgId:{msgId}");

                    // We wrap the data sending to backend: [SessionId (8 bytes long)] + [Original Packet (MsgId + Payload)]
                    byte[] wrapperMsg = new byte[8 + data.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(wrapperMsg.AsSpan(0, 8), session.SessionId);
                    data.Span.CopyTo(wrapperMsg.AsSpan(8));

                    // Simple route definition:
                    // ID 1000-1999: Login server
                    // ID 2000-2999: Game server
                    if (msgId >= 1000 && msgId < 2000)
                    {
                        loginClient.Send(wrapperMsg);
                    }
                    else if (msgId >= 2000 && msgId < 3000)
                    {
                        gameClient.Send(wrapperMsg);
                    }
                    else
                    {
                        Log.Warning($"Gateway: 未知的消息路由 MsgId=>{msgId}");
                    }
                }
                else
                {
                    Log.Warning("收到无效的数据包长度。");
                }
            };

            tcpServer.OnSessionDisconnected += (session, reason) => 
            {
                Log.Info($"客户端断开连接，原因: {reason}");
                Gateway.Managers.GatewaySessionManager.Instance.RemoveSession(session.SessionId);
            };

            networkManager.RegisterServer("GatewayTcp", tcpServer);

            await networkManager.StartServerAsync("GatewayTcp", port);
            Log.Info($"网关服务器已启动，监听端口: {port}");

            await Task.Delay(-1);
        }
    }
}