using System.Collections.Concurrent;

namespace Game.Managers
{
    /// <summary>
    /// 玩家会话管理器（单例）。
    /// 负责维护会话ID（SessionId）与用户ID（UserId）之间的双向映射关系，
    /// 以便通过会话查找对应用户，或通过用户查找对应会话。
    /// 使用线程安全的 ConcurrentDictionary 来支持并发访问。
    /// </summary>
    public class PlayerSessionManager
    {
        private static readonly PlayerSessionManager instance = new();
        public static PlayerSessionManager Instance => instance;

        // SessionId -> UserId 会话ID到用户ID的映射
        private readonly ConcurrentDictionary<long, int> sessionUsers = new();
        // UserId -> SessionId 用户ID到会话ID的映射
        private readonly ConcurrentDictionary<int, long> userSessions = new();

        /// <summary>
        /// 绑定会话与用户，将两个映射同时写入（线程安全）。
        /// 如果已有相同的 sessionId 或 userId，会被覆盖为新的值。
        /// </summary>
        public void BindSession(long sessionId, int userId)
        {
            sessionUsers[sessionId] = userId;
            userSessions[userId] = sessionId;
        }

        /// <summary>
        /// 解除会话绑定：根据会话ID移除对应的用户映射，并同时移除用户到会话的映射。
        /// </summary>
        public void UnbindSession(long sessionId)
        {
            if (sessionUsers.TryRemove(sessionId, out int userId))
            {
                userSessions.TryRemove(userId, out _);
            }
        }

        /// <summary>
        /// 通过会话ID获取用户ID，找不到则返回 0（表示未绑定或无效）。
        /// </summary>
        public int GetUserIdBySessionId(long sessionId)
        {
            return sessionUsers.TryGetValue(sessionId, out int userId) ? userId : 0;
        }

        /// <summary>
        /// 通过用户ID获取会话ID，找不到则返回 0（表示未绑定或无效）。
        /// </summary>
        public long GetSessionIdByUserId(int userId)
        {
            return userSessions.TryGetValue(userId, out long sessionId) ? sessionId : 0;
        }
    }
}
