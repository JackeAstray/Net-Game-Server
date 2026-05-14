using Network;
using System.Net;

namespace Game.Network
{
    public class ClientSessionWrapper : ISession
    {
        private readonly ISession gatewaySession;
        public long SessionId { get; }

        public ClientSessionWrapper(ISession gatewaySession, long originalSessionId)
        {
            this.gatewaySession = gatewaySession;
            SessionId = originalSessionId;
        }

        public EndPoint? RemoteEndPoint => gatewaySession.RemoteEndPoint;
        public bool IsConnected => gatewaySession.IsConnected;
        public DateTime LastActivityTime => gatewaySession.LastActivityTime;
        public object? UserData { get => null; set { } }

        /// <summary>
        /// 将数据发送到客户端。数据格式应符合协议要求，通常包含消息 ID 和消息体。
        /// </summary>
        /// <param name="data"></param>
        public void Send(ReadOnlyMemory<byte> data)
        {
            gatewaySession.Send(data);
        }

        /// <summary>
        /// 将数据异步发送到客户端。数据格式应符合协议要求，通常包含消息 ID 和消息体。
        /// </summary>
        /// <param name="data"></param>
        public void SendAsync(ReadOnlyMemory<byte> data)
        {
            gatewaySession.Send(data);
        }

        public void Close()
        {
            // Do not close gateway session
        }
    }
}