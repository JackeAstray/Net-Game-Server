using System.Collections.Concurrent;

namespace Game.Managers
{
    public class PlayerSessionManager
    {
        private static readonly PlayerSessionManager _instance = new();
        public static PlayerSessionManager Instance => _instance;

        // SessionId -> UserId
        private readonly ConcurrentDictionary<long, int> _sessionUsers = new();
        // UserId -> SessionId
        private readonly ConcurrentDictionary<int, long> _userSessions = new();

        public void BindSession(long sessionId, int userId)
        {
            _sessionUsers[sessionId] = userId;
            _userSessions[userId] = sessionId;
        }

        public void UnbindSession(long sessionId)
        {
            if (_sessionUsers.TryRemove(sessionId, out int userId))
            {
                _userSessions.TryRemove(userId, out _);
            }
        }

        public int GetUserIdBySessionId(long sessionId)
        {
            return _sessionUsers.TryGetValue(sessionId, out int userId) ? userId : 0;
        }

        public long GetSessionIdByUserId(int userId)
        {
            return _userSessions.TryGetValue(userId, out long sessionId) ? sessionId : 0;
        }
    }
}
