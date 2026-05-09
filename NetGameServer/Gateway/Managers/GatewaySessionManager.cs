using Network;
using System.Collections.Concurrent;

namespace Gateway.Managers
{
    public class GatewaySessionManager
    {
        private static readonly GatewaySessionManager instance = new();
        public static GatewaySessionManager Instance => instance;

        // Stores sessionId -> ISession mapping (so backend can tell Gateway which client to reply to)
        private readonly ConcurrentDictionary<long, ISession> clientSessions = new();

        private GatewaySessionManager() { }

        public void AddSession(ISession session)
        {
            clientSessions[session.SessionId] = session;
        }

        public void RemoveSession(long sessionId)
        {
            clientSessions.TryRemove(sessionId, out _);
        }

        public ISession? GetSession(long sessionId)
        {
            clientSessions.TryGetValue(sessionId, out var session);
            return session;
        }
    }
}