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
        /// 将指定的只读字节序列发送到网关会话。
        /// </summary>
        /// <param name="data">要发送的只读字节序列。</param>
        public void Send(ReadOnlyMemory<byte> data)
        {
            gatewaySession.Send(data);
        }

        /// <summary>
        /// 向网关会话发送指定的数据。
        /// </summary>
        /// <remarks>同步委托给 gatewaySession.Send；方法名不表示异步行为。</remarks>
        /// <param name="data">要发送到网关的只读字节内存。</param>
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