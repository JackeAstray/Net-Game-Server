using Network;
using System.Net;

namespace Game.Network
{
    public class ClientSessionWrapper : ISession
    {
        private readonly ISession _gatewaySession;
        public long SessionId { get; }

        public ClientSessionWrapper(ISession gatewaySession, long originalSessionId)
        {
            _gatewaySession = gatewaySession;
            SessionId = originalSessionId;
        }

        public EndPoint? RemoteEndPoint => _gatewaySession.RemoteEndPoint;
        public bool IsConnected => _gatewaySession.IsConnected;
        public DateTime LastActivityTime => _gatewaySession.LastActivityTime;
        public object? UserData { get => null; set { } }

        public void Send(ReadOnlyMemory<byte> data)
        {
            _gatewaySession.Send(data);
        }

        public void SendAsync(ReadOnlyMemory<byte> data)
        {
            _gatewaySession.Send(data);
        }

        public void Close()
        {
            // Do not close gateway session
        }
    }
}