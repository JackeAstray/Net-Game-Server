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
        private static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>? _handlers;

        public static async Task StartNetworkAsync()
        {
            // 例如配置中 CenterPort 默认 30006
            int port = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 30006 : ConfigHelper.GetConfig<int>("CenterPort");

            var matchHandler = new Center.Handlers.MatchHandler();
            _handlers = Center.Handlers.MessageRouter.BuildHandlers(matchHandler);

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点已连接到中心服: {session.RemoteEndPoint}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Log.Info($"节点从中心服断开，原因: {reason}");
                // TODO: 节点断开时进行清理操作，离线玩家，或者标记节点不可用
            };

            tcpServer.OnDataReceived += async (session, data) =>
            {
                // 解析 SessionId 和内部消息结构 [SessionId(8)][MsgId(4)][Payload]
                if (data.Length >= 12)
                {
                    long originalSessionId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(0, 8));
                    var innerData = data.Slice(8);

                    if (innerData.Length >= 4)
                    {
                        var msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(innerData.Span.Slice(0, 4));
                        var payload = innerData.Slice(4);

                        // 使用 Router 或 Handler 分发匹配/调度逻辑
                        // 这里预留出处理和响应的回调，网关期望的返回格式依然是 [OriginalSessionId(8)][MsgId(4)][Payload]
                        if (_handlers != null && _handlers.TryGetValue(msgId, out var handlerAction))
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
                    }
                }
            };

            networkManager.RegisterServer("CenterTcp", tcpServer);
            networkManager.Router.UnbindServer(tcpServer);

            await networkManager.StartServerAsync("CenterTcp", port);
            Log.Info($"Center 调度服务器网络已启动，监听内部端口: {port}");
        }
    }
}
