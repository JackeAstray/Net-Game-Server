using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Network;
using Network.Tcp;

namespace Center
{
    public static class CenterServerApp
    {
        private static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>? handlers;

        /// <summary>
        /// 启动中心调度服务器的网络，注册并配置内部 TCP 服务器、消息路由和事件处理器，并监听指定端口。
        /// </summary>
        /// <remarks>从配置读取端口（默认 31306）。接收并分发网关转发的内部消息，维护会话绑定，并在后台周期性清理超时节点。</remarks>
        /// <returns>表示启动操作完成的异步任务。</returns>
        public static async Task StartNetworkAsync()
        {
            // 例如配置中 CenterPort 默认 31306
            int port = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");

            var matchHandler = new Center.Handlers.MatchHandler();
            handlers = Center.Handlers.MessageRouter.BuildHandlers(matchHandler);

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点已连接到中心服: {session.RemoteEndPoint}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Log.Info($"节点从中心服断开，原因: {reason}");
                Center.Handlers.NodeManager.Instance.RemoveNodeBySession(session);
            };

            tcpServer.OnDataReceived += async (session, data) =>
            {
                if (data.Length < 4)
                {
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                byte[] payload = data.Slice(4).ToArray();

                long originalSessionId = 0;
                if (Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                {
                    originalSessionId = clientSessionId;
                    payload = cleanPayload;
                }

                if (originalSessionId > 0)
                {
                    Center.Handlers.NodeManager.Instance.BindClientGatewayRoute(originalSessionId, session);
                }

                if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                {
                    try
                    {
                        await handlerAction(payload, session, originalSessionId);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Center 处理消息 ({msgId}) 发生异常: " + ex);
                    }
                }
                else
                {
                    Log.Warning($"Center 收到未知 MsgId {msgId}");
                }
            };

            networkManager.RegisterServer("CenterTcp", tcpServer);

            await networkManager.StartServerAsync("CenterTcp", port);
            Log.Info($"Center 调度服务器网络已启动，监听内部端口: {port}");

            _ = Task.Run(async () =>
            {
                TimeSpan timeout = TimeSpan.FromSeconds(30);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    int removedCount = Center.Handlers.NodeManager.Instance.RemoveInactiveNodes(timeout);
                    if (removedCount > 0)
                    {
                        Log.Warning($"Center 已清理超时节点数: {removedCount}，当前剩余节点数: {Center.Handlers.NodeManager.Instance.GetNodeCount()}");
                    }
                }
            });
        }
    }
}