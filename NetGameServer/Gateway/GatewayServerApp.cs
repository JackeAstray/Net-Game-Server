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
    public static class GatewayServerApp
    {
        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("GatewayPort") == 0 ? 30000 : ConfigHelper.GetConfig<int>("GatewayPort");

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();
            var udpServer = new Network.Udp.UdpServer();
            var webSocketServer = new Network.WebSockets.WebSocketServer();

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

            var (loginClient, gameClient) = ConnectToBackendServers();

            // 实现网关路由逻辑
            DataReceivedHandler onDataReceived = (session, data) =>
            {
                if (data.Length >= 4)
                {
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Shared.Log.Info($"Gateway 接收到数据 长度:{data.Length} MsgId:{msgId} 来自:{session.RemoteEndPoint}");

                    byte[] wrapperMsg = new byte[8 + data.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(wrapperMsg.AsSpan(0, 8), session.SessionId);
                    data.Span.CopyTo(wrapperMsg.AsSpan(8));

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
                        Shared.Log.Warning($"Gateway: 未知的消息路由 MsgId=>{msgId}");
                    }
                }
                else
                {
                    Shared.Log.Warning("收到无效的数据包长度。");
                }
            };

            tcpServer.OnDataReceived += onDataReceived;
            udpServer.OnDataReceived += onDataReceived;
            webSocketServer.OnDataReceived += onDataReceived;

            SessionDisconnectedHandler onSessionDisconnected = (session, reason) =>
            {
                Shared.Log.Info($"客户端断开连接，原因: {reason} ID:{session.SessionId}");
                Gateway.Managers.GatewaySessionManager.Instance.RemoveSession(session.SessionId);
            };

            tcpServer.OnSessionDisconnected += onSessionDisconnected;
            udpServer.OnSessionDisconnected += onSessionDisconnected;
            webSocketServer.OnSessionDisconnected += onSessionDisconnected;

            networkManager.RegisterServer("GatewayTcp", tcpServer);
            networkManager.RegisterServer("GatewayUdp", udpServer);
            networkManager.RegisterServer("GatewayWebSocket", webSocketServer);

            await networkManager.StartServerAsync("GatewayTcp", port);
            await networkManager.StartServerAsync("GatewayUdp", port); 
            await networkManager.StartServerAsync("GatewayWebSocket", port + 1);

            Shared.Log.Info($"网关服务器已启动，监听 TCP 端口: {port}, UDP/KCP 端口: {port}, WebSocket 端口: {port + 1}");
        }

        private static (TcpClientWrapper, TcpClientWrapper) ConnectToBackendServers()
        {
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

            return (loginClient, gameClient);
        }

        public static async Task StartReverseProxyAsync(string[] args)
        {
            int httpPort = ConfigHelper.GetConfig<int>("GatewayHttpPort") == 0 ? 30001 : ConfigHelper.GetConfig<int>("GatewayHttpPort");
            string loginHttpUrl = ConfigHelper.GetConfig<string>("LoginHttpUrl") ?? "http://127.0.0.1:30003";

            var builder = WebApplication.CreateBuilder(args);
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
            _ = app.RunAsync($"http://*:{httpPort}");
        }
    }
}
