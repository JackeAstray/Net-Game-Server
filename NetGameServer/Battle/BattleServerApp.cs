using System;
using System.Threading.Tasks;
using Shared;
using Network;
using Network.Tcp;

namespace Battle
{
    public static class BattleServerApp
    {
        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 30007 : ConfigHelper.GetConfig<int>("BattlePort");

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点/网关已连接到战斗服: {session.RemoteEndPoint}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Log.Info($"节点/网关从战斗服断开，原因: {reason}");
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

                        // 战斗服高频包处理分发（如位移、技能同步）
                    }
                }
                await Task.CompletedTask;
            };

            networkManager.RegisterServer("BattleTcp", tcpServer);

            await networkManager.StartServerAsync("BattleTcp", port);
            Log.Info($"Battle 战斗服务器网络已启动，监听端口: {port}");
        }
    }
}
