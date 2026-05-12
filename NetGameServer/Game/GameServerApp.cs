using System;
using System.Threading.Tasks;
using Shared;
using Network;
using Network.Tcp;

namespace Game
{
    /// <summary>
    /// 游戏服务器应用入口静态类。
    /// 负责初始化并启动游戏服务器所需的网络监听、路由和对外部数据库的连接。
    /// 所有方法均为静态，适合作为应用启动阶段的初始化调用点。
    /// </summary>
    public static class GameServerApp
    {
        /// <summary>
        /// 异步启动网络监听。
        /// 关键步骤：
        /// 1. 从配置读取 GamePort（默认 30004）。
        /// 2. 创建 NetworkManager 和 TcpServer，注册消息路由和处理器。
        /// 3. 处理客户端的连接、断开以及接收数据事件，并将数据解析后交由路由器分发。
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 从配置中读取端口，若未配置或为 0 则使用默认端口 30004
            int port = ConfigHelper.GetConfig<int>("GamePort") == 0 ? 30004 : ConfigHelper.GetConfig<int>("GamePort");

            // 创建网络管理器，用于管理多个服务器实例
            var networkManager = new NetworkManager();
            // 创建 TCP 服务器实例以接收客户端连接
            var tcpServer = new TcpServer();

            // 创建消息路由器，将收到的消息分发到对应的处理器
            var router = new global::Network.Routing.MessageRouter();
            // 注册聊天处理器（示例），处理聊天相关消息并将其挂载到路由器
            var chatHandler = new Handlers.ChatHandler(networkManager);
            chatHandler.Register(router);

            // 当客户端建立连接时记录信息（可在此处加入鉴权或会话初始化逻辑）
            tcpServer.OnSessionConnected += session => Log.Info($"客户端已连接: {session.RemoteEndPoint}");

            // 数据接收事件：期望的数据格式为 [SessionId(8)][MsgId(4)][Payload]
            // - 先读取原始的客户端 SessionId（8 字节 little-endian）
            // - 然后读取内部数据的 MsgId（4 字节 little-endian）与剩余的负载
            // - 使用 ClientSessionWrapper 将原始 SessionId 传递给内部路由
            tcpServer.OnDataReceived += (session, data) =>
            {
                Log.Info($"接收到数据，长度: {data.Length}");
                // 确保至少包含 8 字节 SessionId + 4 字节 MsgId
                if (data.Length >= 12)
                {
                    // 读取原始客户端会话 ID（8 字节，小端序）
                    long originalSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    var innerData = data.Slice(8);

                    // 检查内部数据至少包含 MsgId
                    if (innerData.Length >= 4)
                    {
                        // 读取消息 ID（4 字节，小端序）
                        var msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(innerData.Span.Slice(0, 4));
                        // 剩余部分作为消息负载
                        var payload = innerData.Slice(4);

                        // 使用自定义的客户端会话包装器携带原始 SessionId，便于内部处理链路识别真实客户端
                        var clientSession = new Game.Network.ClientSessionWrapper(session, originalSessionId);
                        // 将消息交由路由器分发到对应的处理器
                        router.RouteMessage(clientSession, msgId, payload);
                    }
                }
            };

            // 客户端断开连接事件（记录原因）。这里可以添加清理会话状态或通知其他子系统的逻辑。
            tcpServer.OnSessionDisconnected += (session, reason) => Log.Info($"客户端断开连接，原因: {reason}");

            // 在网络管理器中注册名为 "GameTcp" 的服务器实例，便于统一管理和启动
            networkManager.RegisterServer("GameTcp", tcpServer);

            // 启动指定名称的服务器并监听端口
            await networkManager.StartServerAsync("GameTcp", port);
            Log.Info($"游戏服务器已启动，监听端口: {port}");
        }

        /// <summary>
        /// 连接到外部数据库服务器（通过 TcpClientWrapper 封装的 TCP 客户端）。
        /// 关键点：
        /// 1. 从配置读取 DBHost 和 DBPort（默认 127.0.0.1:30005）。
        /// 2. 订阅连接/断开事件以记录和处理重连/告警逻辑。
        /// 3. 启动异步连接（不阻塞调用者）。
        /// </summary>
        public static void ConnectToDatabase()
        {
            // 从配置中读取 DB 端口与主机，使用默认值作为回退
            int dbPort = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");
            var dbHost = ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1";
            var dbClient = new TcpClientWrapper(dbHost, dbPort);

            // 成功连接到 DB 时记录日志（可在此处进行首次握手或认证）
            dbClient.OnConnected += session => Log.Info($"已连接到 DB 服务器 (Host:{dbHost} Port:{dbPort})");
            // 与 DB 断开时记录警告（可触发重试或告警机制）
            dbClient.OnDisconnected += (session, reason) => Log.Warning($"与 DB 服务器断开连接: {reason}");

            // 开始异步连接（不等待结果），如需重试策略请在 TcpClientWrapper 外层实现
            _ = dbClient.ConnectAsync();
        }
    }
}