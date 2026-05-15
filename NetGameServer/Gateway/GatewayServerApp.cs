using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Network;
using Network.Tcp;
using Serilog;

namespace Gateway
{
    // 网关服务器主应用类
    // 负责启动监听客户端连接的网络服务（TCP/UDP/KCP/WebSocket），
    // 将来自客户端的数据根据消息 ID 路由到后端的 Login 或 Game 服务器，
    // 并处理后端返回的数据转发给相应的客户端会话。
    public static class GatewayServerApp
    {
        /// <summary>
        /// 启动网关的网络服务并注册事件处理器。
        /// - 根据配置获取监听端口（默认 TCP/UDP:30000，WebSocket:30001）。
        /// - 创建并注册 TCP/UDP/WebSocket 三种服务器实例，统一将连接加入会话管理器。
        /// - 将接收到的客户端数据解析出 MsgId，并在客户端 SessionId 前附加 8 字节后转发给后端服务器（Login/Game）。
        /// - 处理客户端断开事件并从会话管理器移除对应会话。
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            // 读取配置端口，若配置为 0 或未配置则使用默认端口
            int port = ConfigHelper.GetConfig<int>("GatewayPort") == 0 ? 30000 : ConfigHelper.GetConfig<int>("GatewayPort");

            // 创建网络管理器和各类型的监听服务器（TCP/UDP/KCP/WebSocket）
            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();
            var udpServer = new Network.Udp.UdpServer();
            var webSocketServer = new Network.WebSockets.WebSocketServer();

            // 当有客户端新建会话（连接）时，记录日志并将会话加入网关会话管理器
            tcpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(TCP)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            udpServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(UDP/KCP)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };
            webSocketServer.OnSessionConnected += session =>
            {
                Shared.Log.Info($"客户端(WebSocket)已连接: {session.RemoteEndPoint} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.AddSession(session);
            };

            // 建立到后端 Login, Game, Center, Battle 服务器的连接（异步连接启动）
            var (loginClient, gameClient, centerClient, battleClient) = ConnectToBackendServers();

            // 数据接收处理器：将客户端发送的原始数据打包成网关到后端的格式
            // 格式为: [ClientSessionId(8)][原始数据...]
            DataReceivedHandler onDataReceived = (session, data) =>
            {
                if (data.Length >= 4)
                {
                    // 客户端协议假定前 4 字节为 MsgId（小端）
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Shared.Log.Info($"Gateway 接收到数据 长度:{data.Length} MsgId:{msgId} 来自:{session.RemoteEndPoint}");

                    // 在数据前面写入 8 字节的 SessionId，后端根据该 SessionId 知道要回发给哪个客户端
                    byte[] wrapperMsg = new byte[8 + data.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(wrapperMsg.AsSpan(0, 8), session.SessionId);
                    data.Span.CopyTo(wrapperMsg.AsSpan(8));

                    // 根据 MsgId 范围选择路由到 Login 或 Game 或 Center 或 Battle 后端
                    if (msgId >= 10000 && msgId < 20000)
                    {
                        // 登录相关消息路由到 Login 服务器
                        loginClient.Send(wrapperMsg);
                    }
                    else if (msgId >= 20000 && msgId < 30000)
                    {
                        // 游戏大世界相关消息路由到 Game 服务器
                        gameClient.Send(wrapperMsg);
                    }
                    else if (msgId >= 30000 && msgId < 40000)
                    {
                        // 调度、匹配相关消息路由到 Center 服务器
                        centerClient.Send(wrapperMsg);
                    }
                    else if (msgId >= 40000 && msgId < 50000)
                    {
                        // 战斗、房间相关消息路由到 Battle 服务器
                        battleClient.Send(wrapperMsg);
                    }
                    else
                    {
                        Shared.Log.Warning($"Gateway: 未知的消息路由 MsgId=>{msgId}");
                    }
                }
                else
                {
                    Shared.Log.Warning("收到无效的数据包长度。");
                }
            };

            // 将数据处理器注册到三种服务器上
            tcpServer.OnDataReceived += onDataReceived;
            udpServer.OnDataReceived += onDataReceived;
            webSocketServer.OnDataReceived += onDataReceived;

            // 客户端断开连接处理：记录日志并从会话管理器移除会话
            SessionDisconnectedHandler onSessionDisconnected = (session, reason) =>
            {
                Shared.Log.Info($"客户端断开连接，原因: {reason} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.RemoveSession(session.SessionId);
            };

            tcpServer.OnSessionDisconnected += onSessionDisconnected;
            udpServer.OnSessionDisconnected += onSessionDisconnected;
            webSocketServer.OnSessionDisconnected += onSessionDisconnected;

            // 在网络管理器中注册服务器并启动监听
            networkManager.RegisterServer("GatewayTcp", tcpServer);
            networkManager.RegisterServer("GatewayUdp", udpServer);
            networkManager.RegisterServer("GatewayWebSocket", webSocketServer);

            await networkManager.StartServerAsync("GatewayTcp", port);
            await networkManager.StartServerAsync("GatewayUdp", port); 
            await networkManager.StartServerAsync("GatewayWebSocket", port + 1);

            Shared.Log.Info($"网关服务器已启动，监听 TCP 端口: {port}, UDP/KCP 端口: {port}, WebSocket 端口: {port + 1}");
        }

        /// <summary>
        /// 建立并返回到后端 Login, Game, Center, Battle 服务器的 TCP 客户端包装器。
        /// - 读取配置的 Host/Port（支持默认值）。
        /// - 为每个后端客户端注册连接、断开、接收数据事件，负责将后端返回的数据解析并转发给相应的客户端会话。
        /// </summary>
        private static (TcpClientWrapper, TcpClientWrapper, TcpClientWrapper, TcpClientWrapper) ConnectToBackendServers()
        {
            // 读取 Login 后端配置（支持默认端口）
            int loginPort = ConfigHelper.GetConfig<int>("LoginPort") == 0 ? 30002 : ConfigHelper.GetConfig<int>("LoginPort");
            string loginHost = ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1";
            var loginClient = new TcpClientWrapper(loginHost, loginPort);
            loginClient.OnConnected += session => Shared.Log.Info($"已连接到 Login 服务器 (Host:{loginHost} Port:{loginPort})");
            loginClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Login 服务器断开连接: {reason}");
            loginClient.OnDataReceived += (session, data) => 
            {
                if (data.Length >= 12)
                {
                    long sessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(sessionId);
                    if (clientSession != null)
                    {
                        int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                        var payload = data.Span.Slice(12);

                        byte[] clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                        try 
                        {
                            clientSession.Send(clientPacket.AsSpan(0, totalLength).ToArray());
                        } 
                        finally 
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(clientPacket);
                        }
                    }
                }
            };
            _ = loginClient.ConnectAsync();

            int gamePort = ConfigHelper.GetConfig<int>("GamePort") == 0 ? 30004 : ConfigHelper.GetConfig<int>("GamePort");
            string gameHost = ConfigHelper.GetConfig<string>("GameHost") ?? "127.0.0.1";
            var gameClient = new TcpClientWrapper(gameHost, gamePort);
            gameClient.OnConnected += session => Shared.Log.Info($"已连接到 Game 服务器 (Host:{gameHost} Port:{gamePort})");
            gameClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Game 服务器断开连接: {reason}");
            gameClient.OnDataReceived += (session, data) => 
            {
                if (data.Length >= 12) 
                {
                    long sessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                    var payload = data.Span.Slice(12);

                    if (sessionId == 0)
                    {
                        var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                        try
                        {
                            Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, totalLength).ToArray());
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                        }
                    }
                    else
                    {
                        var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(sessionId);
                        if (clientSession != null)
                        {
                            var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                            try 
                            {
                                clientSession.Send(clientPacket.AsSpan(0, totalLength).ToArray());
                            } 
                            finally 
                            {
                                System.Buffers.ArrayPool<byte>.Shared.Return(clientPacket);
                            }
                        }
                    }
                }
            };
            _ = gameClient.ConnectAsync();

            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 30006 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            var centerClient = new TcpClientWrapper(centerHost, centerPort);
            centerClient.OnConnected += session => Shared.Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
            centerClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Center 服务器断开连接: {reason}");
            centerClient.OnDataReceived += delegate (Network.ISession session, ReadOnlyMemory<byte> data)
            {
                if (data.Length >= 12) 
                {
                    long sessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                    var payload = data.Span.Slice(12);

                    if (sessionId == 0)
                    {
                        var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                        try { Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, totalLength).ToArray()); }
                        finally { System.Buffers.ArrayPool<byte>.Shared.Return(packet); }
                    }
                    else
                    {
                        var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(sessionId);
                        if (clientSession != null)
                        {
                            var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                            try { clientSession.Send(clientPacket.AsSpan(0, totalLength).ToArray()); } 
                            finally { System.Buffers.ArrayPool<byte>.Shared.Return(clientPacket); }
                        }
                    }
                }
            };
            _ = centerClient.ConnectAsync();

            int battlePort = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 30007 : ConfigHelper.GetConfig<int>("BattlePort");
            string battleHost = ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1";
            var battleClient = new TcpClientWrapper(battleHost, battlePort);
            battleClient.OnConnected += session => Shared.Log.Info($"已连接到 Battle 服务器 (Host:{battleHost} Port:{battlePort})");
            battleClient.OnDisconnected += (session, reason) => Shared.Log.Warning($"与 Battle 服务器断开连接: {reason}");
            battleClient.OnDataReceived += delegate (Network.ISession session, ReadOnlyMemory<byte> data)
            {
                if (data.Length >= 12) 
                {
                    long sessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(8, 4));
                    var payload = data.Span.Slice(12);

                    if (sessionId == 0)
                    {
                        var packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                        try { Gateway.Managers.GatewaySessionManager.Instance.Broadcast(packet.AsSpan(0, totalLength).ToArray()); }
                        finally { System.Buffers.ArrayPool<byte>.Shared.Return(packet); }
                    }
                    else
                    {
                        var clientSession = Gateway.Managers.GatewaySessionManager.Instance.GetSession(sessionId);
                        if (clientSession != null)
                        {
                            var clientPacket = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
                            try { clientSession.Send(clientPacket.AsSpan(0, totalLength).ToArray()); } 
                            finally { System.Buffers.ArrayPool<byte>.Shared.Return(clientPacket); }
                        }
                    }
                }
            };
            _ = battleClient.ConnectAsync();

            return (loginClient, gameClient, centerClient, battleClient);
        }

        /// <summary>
        /// 启动一个 HTTP 反向代理，将网关的 /api 和 /swagger 路由转发到 Login 服务的 HTTP 地址。
        /// - 使用 YARP 以内存配置方式注册路由与集群。
        /// - 读取配置 GatewayHttpPort 和 LoginHttpUrl（支持默认值）。
        /// </summary>
        public static async Task StartReverseProxyAsync(string[] args)
        {
            // HTTP 监听端口和后端 Login HTTP 地址（支持默认值）
            int httpPort = ConfigHelper.GetConfig<int>("GatewayHttpPort") == 0 ? 30001 : ConfigHelper.GetConfig<int>("GatewayHttpPort");
            string loginHttpUrl = ConfigHelper.GetConfig<string>("LoginHttpUrl") ?? "http://127.0.0.1:30003";

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(httpPort);
            });

            builder.Host.UseSerilog();
            builder.Services.AddReverseProxy()
                .LoadFromMemory(
                    new[] {
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_api_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/api/{**catch-all}"
                            }
                        },
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_swagger_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/swagger/{**catch-all}"
                            }
                        }
                    },
                    new[] {
                        new Yarp.ReverseProxy.Configuration.ClusterConfig()
                        {
                            ClusterId = "login_api_cluster",
                            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "default", new Yarp.ReverseProxy.Configuration.DestinationConfig() { Address = loginHttpUrl } }
                            }
                        }
                    }
                );

            var app = builder.Build();
            app.MapReverseProxy();

            Shared.Log.Info($"网关 HTTP API 反向代理已启动，监听端口: {httpPort} 并路由 /api 至 {loginHttpUrl}");
            _ = app.RunAsync();
        }
    }
}
