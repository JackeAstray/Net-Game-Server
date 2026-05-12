using Network;
using System.Collections.Concurrent;

namespace Gateway.Managers
{
    public class GatewaySessionManager
    {
        private static readonly GatewaySessionManager instance = new();
        public static GatewaySessionManager Instance => instance;

        // 存储sessionId->ISession映射（以便后端可以告诉网关要回复哪个客户端）
        private readonly ConcurrentDictionary<long, Network.ISession> clientSessions = new();

        private GatewaySessionManager() { }

        public void AddSession(Network.ISession session)
        {
            clientSessions[session.SessionId] = session;
        }

        public void RemoveSession(long sessionId)
        {
            clientSessions.TryRemove(sessionId, out _);
        }

        public Network.ISession? GetSession(long sessionId)
        {
            clientSessions.TryGetValue(sessionId, out var session);
            return session;
        }

        public void Broadcast(byte[] data)
        {
            foreach (var session in clientSessions.Values)
            {
                session.Send(data);
            }
        }
    }
}