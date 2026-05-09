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

            // 在接受来自客户端的数据之前连接后端服务器，以确保它们存在
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
                // 在健壮的实现中，我们在这里读取标头并决定谁会收到消息。
                // 假设MsgId是一个整数（4个字节）
                if (data.Length >= 4)
                {
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Log.Info($"Gateway 接收到数据 长度:{data.Length} MsgId:{msgId}");

                    // 我们包装发送到后端的数据：[SessionId（8字节长）]+[原始数据包（MsgId+Payload）]
                    byte[] wrapperMsg = new byte[8 + data.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(wrapperMsg.AsSpan(0, 8), session.SessionId);
                    data.Span.CopyTo(wrapperMsg.AsSpan(8));

                    // 简单路线定义：
                    // ID 10000-19999：登录服务器（10000个可用ID）
                    // ID 20000-99999：游戏服务器（可提供80000个ID）
                    if (msgId >= 10000 && msgId < 20000)
                    {
                        loginClient.Send(wrapperMsg);
                    }
                    else if (msgId >= 20000 && msgId < 100000)
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