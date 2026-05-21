using System.Collections.Concurrent;

namespace Game.Managers
{
    /// <summary>
    /// 玩家会话管理器（单例）。
    /// 负责维护会话ID（SessionId）与用户ID（UserId）以及 UID（UniqueId）之间的映射关系，
    /// 以便通过会话查找对应用户/UID，或通过用户/UID查找对应会话。
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
        // SessionId -> UID 会话ID到UID的映射
        private readonly ConcurrentDictionary<long, string> sessionUids = new();
        // UID -> SessionId UID到会话ID的映射
        private readonly ConcurrentDictionary<string, long> uidSessions = new();

        /// <summary>
        /// 建立会话标识与用户标识的双向映射；在冲突时移除先前的对应关系。
        /// </summary>
        /// <remarks>若 sessionId 已绑定到不同的用户，则从 userSessions 中移除该先前用户的映射；若 userId 已绑定到不同的会话，则从 sessionUsers
        /// 中移除该先前会话的映射。随后在两个字典中设置新的映射。线程安全取决于所用集合的并发特性（例如 ConcurrentDictionary）。</remarks>
        /// <param name="sessionId">要绑定的会话标识。</param>
        /// <param name="userId">要绑定的用户标识。</param>
        public void BindSession(long sessionId, int userId)
        {
            if (sessionUsers.TryGetValue(sessionId, out int previousUserId) && previousUserId != userId)
            {
                userSessions.TryRemove(previousUserId, out _);
            }

            if (userSessions.TryGetValue(userId, out long previousSessionId) && previousSessionId != sessionId)
            {
                sessionUsers.TryRemove(previousSessionId, out _);
            }

            sessionUsers[sessionId] = userId;
            userSessions[userId] = sessionId;
        }

        /// <summary>
        /// 建立会话标识与 UID 的双向映射；在冲突时移除先前的对应关系。
        /// </summary>
        /// <param name="sessionId">要绑定的会话标识。</param>
        /// <param name="uid">要绑定的 UID。</param>
        public void BindUid(long sessionId, string uid)
        {
            if (string.IsNullOrWhiteSpace(uid))
            {
                return;
            }

            if (sessionUids.TryGetValue(sessionId, out string? previousUid) && previousUid != uid)
            {
                uidSessions.TryRemove(previousUid, out _);
            }

            if (uidSessions.TryGetValue(uid, out long previousSessionId) && previousSessionId != sessionId)
            {
                sessionUids.TryRemove(previousSessionId, out _);
            }

            sessionUids[sessionId] = uid;
            uidSessions[uid] = sessionId;
        }

        /// <summary>
        /// 从内部并发字典中移除与指定会话 ID 关联的用户映射，并在反向映射仍指向该会话时移除对应的用户到会话条目。
        /// </summary>
        /// <remarks>对不存在的会话为无操作；使用 TryRemove/TryGetValue 进行并发安全的移除以避免抛出异常。</remarks>
        /// <param name="sessionId">要解绑的会话的唯一标识符。</param>
        public void UnbindSession(long sessionId)
        {
            if (sessionUsers.TryRemove(sessionId, out int userId))
            {
                if (userSessions.TryGetValue(userId, out long mappedSessionId) && mappedSessionId == sessionId)
                {
                    userSessions.TryRemove(userId, out _);
                }
            }

            if (sessionUids.TryRemove(sessionId, out string? uid))
            {
                if (uidSessions.TryGetValue(uid, out long mappedSessionId) && mappedSessionId == sessionId)
                {
                    uidSessions.TryRemove(uid, out _);
                }
            }
        }

        /// <summary>
        /// 根据会话 ID 返回关联的用户 ID；找不到时返回 0。
        /// </summary>
        /// <param name="sessionId">会话的唯一标识符。</param>
        /// <returns>关联的用户 ID；找不到时返回 0。</returns>
        public int GetUserIdBySessionId(long sessionId)
        {
            return sessionUsers.TryGetValue(sessionId, out int userId) ? userId : 0;
        }

        /// <summary>
        /// 根据会话 ID 返回关联的 UID；找不到时返回空字符串。
        /// </summary>
        /// <param name="sessionId">会话的唯一标识符。</param>
        /// <returns>关联的 UID；找不到时返回空字符串。</returns>
        public string GetUidBySessionId(long sessionId)
        {
            return sessionUids.TryGetValue(sessionId, out string? uid) ? uid : string.Empty;
        }

        /// <summary>
        /// 返回与指定用户 ID 关联的会话 ID。
        /// </summary>
        /// <remarks>0 表示未找到会话。</remarks>
        /// <param name="userId">要查找其会话 ID 的用户 ID。</param>
        /// <returns>找到时返回会话 ID；未找到时返回 0。</returns>
        public long GetSessionIdByUserId(int userId)
        {
            return userSessions.TryGetValue(userId, out long sessionId) ? sessionId : 0;
        }

        /// <summary>
        /// 返回与指定 UID 关联的会话 ID。
        /// </summary>
        /// <remarks>0 表示未找到会话。</remarks>
        /// <param name="uid">要查找其会话 ID 的 UID。</param>
        /// <returns>找到时返回会话 ID；未找到时返回 0。</returns>
        public long GetSessionIdByUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid))
            {
                return 0;
            }

            return uidSessions.TryGetValue(uid, out long sessionId) ? sessionId : 0;
        }

        /// <summary>
        /// 获取当前会话中的在线玩家数。
        /// </summary>
        /// <returns>当前在线玩家的数量。</returns>
        public int GetOnlinePlayerCount()
        {
            return sessionUsers.Count;
        }
    }
}
