using Network;
using Shared;
using System.Collections.Concurrent;

namespace Gateway.Managers
{
    /// <summary>
    /// 网关会话管理器（单例）
    ///
    /// 负责维护客户端连接会话的映射关系，使后端服务能够通过 SessionId 找到对应的客户端会话并向其发送数据。
    /// 该类为线程安全，使用 ConcurrentDictionary 存储会话信息。
    /// </summary>
    public class GatewaySessionManager
    {
        /// <summary>
        /// 单例实例（惰性静态初始化）
        /// </summary>
        private static readonly GatewaySessionManager instance = new();

        /// <summary>
        /// 获取网关会话管理器的全局单例
        /// </summary>
        public static GatewaySessionManager Instance => instance;

        /// <summary>
        /// 存储 sessionId -> ISession 的映射。
        /// 使用 ConcurrentDictionary 保证在并发环境下的线程安全读写。
        /// 该映射用于：
        /// - 当客户端连接建立时，保存客户端会话；
        /// - 当后端需要向某个客户端发送数据时，根据 sessionId 查找对应的会话并发送；
        /// - 当客户端断开时，从映射中移除对应的会话。
        /// </summary>
        private readonly ConcurrentDictionary<long, Network.ISession> clientSessions = new();
        private readonly ConcurrentDictionary<long, int> sessionUsers = new();
        private readonly ConcurrentDictionary<long, string> sessionUids = new();
        private readonly ConcurrentDictionary<string, long> uidSessions = new();
        private readonly ConcurrentDictionary<long, string> sessionNicknames = new();
        // D6 客户端会话防重放：会话建立时间，用于 SessionGuard 时间窗判定（防止过期 SessionId 重放）
        private readonly ConcurrentDictionary<long, DateTime> sessionCreatedAt = new();

        /// <summary>
        /// 私有构造函数，防止外部实例化（实现单例模式）
        /// </summary>
        private GatewaySessionManager() { }

        /// <summary>
        /// 添加或更新一个客户端会话到管理器中。
        /// 如果相同的 SessionId 已存在，则会被新的 session 覆盖。
        /// </summary>
        /// <param name="session">要添加的客户端会话</param>
        public void AddSession(Network.ISession session)
        {
            clientSessions[session.SessionId] = session;
            // D6：记录会话建立时间，供 SessionGuard 判定生命周期窗口
            sessionCreatedAt[session.SessionId] = DateTime.UtcNow;
            Shared.Log.Info($"Gateway 会话已加入 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
        }

        /// <summary>
        /// 从管理器中移除指定的会话（通常在客户端断开连接时调用）。
        /// </summary>
        /// <param name="sessionId">要移除的会话 Id</param>
        public void RemoveSession(long sessionId)
        {
            clientSessions.TryRemove(sessionId, out _);
            sessionUsers.TryRemove(sessionId, out _);

            if (sessionUids.TryRemove(sessionId, out string? uid))
            {
                if (uidSessions.TryGetValue(uid, out long mappedSessionId) && mappedSessionId == sessionId)
                {
                    uidSessions.TryRemove(uid, out _);
                }
            }

            sessionNicknames.TryRemove(sessionId, out _);
            sessionCreatedAt.TryRemove(sessionId, out _);
            Shared.Log.Info($"Gateway 会话已移除 SessionId:{sessionId}");
        }

        /// <summary>D6：获取客户端会话的建立时间（UTC）。无记录返回 null。</summary>
        public DateTime? GetCreatedAt(long sessionId)
            => sessionCreatedAt.TryGetValue(sessionId, out var t) ? t : null;

        /// <summary>
        /// 根据 sessionId 获取对应的客户端会话。
        /// 找不到时返回 null。
        /// </summary>
        /// <param name="sessionId">要查找的会话 Id</param>
        /// <returns>对应的 ISession 实例，或 null</returns>
        public Network.ISession? GetSession(long sessionId)
        {
            clientSessions.TryGetValue(sessionId, out var session);
            return session;
        }

        /// <summary>
        /// 广播数据给所有已保存的客户端会话。
        /// 注意：此方法会遍历所有会话并调用 Send，可能会产生较多并发发送操作。
        /// </summary>
        /// <param name="data">要发送的字节数据</param>
        public void Broadcast(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                Shared.Log.Warning("Gateway 广播数据为空，已丢弃。");
                return;
            }

            foreach (var session in clientSessions.Values)
            {
                try
                {
                    session.Send(data);
                }
                catch (System.Exception ex)
                {
                    Shared.Log.Error($"Gateway 广播失败 SessionId:{session.SessionId} Exception:{ex}");
                }
            }
        }

        /// <summary>
        /// 将指定会话绑定到指定用户。
        /// </summary>
        /// <remarks>如果 sessionId 或 userId 非正，则不执行任何操作。若会话已存在绑定，则用新的 userId 覆盖。</remarks>
        /// <param name="sessionId">要绑定的会话标识，必须大于 0。</param>
        /// <param name="userId">要绑定的用户标识，必须大于 0。</param>
        public void BindUser(long sessionId, int userId)
        {
            if (sessionId <= 0 || userId <= 0)
            {
                return;
            }

            sessionUsers[sessionId] = userId;
        }

        /// <summary>
        /// 从内部会话-用户映射中移除与指定会话标识符关联的用户绑定。
        /// </summary>
        /// <remarks>在并发环境中安全；若未找到对应条目则静默返回。</remarks>
        /// <param name="sessionId">要从映射中移除其关联用户的会话标识符。</param>
        public void UnbindUser(long sessionId)
        {
            sessionUsers.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// 将指定会话绑定到指定 UID。
        /// </summary>
        /// <param name="sessionId">要绑定的会话标识，必须大于 0。</param>
        /// <param name="uid">要绑定的 UID，不能为空。</param>
        public void BindUid(long sessionId, string uid)
        {
            if (sessionId <= 0 || string.IsNullOrWhiteSpace(uid))
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
        /// 解除指定会话上的 UID 绑定。
        /// </summary>
        /// <param name="sessionId">会话标识。</param>
        public void UnbindUid(long sessionId)
        {
            if (sessionUids.TryRemove(sessionId, out string? uid))
            {
                if (uidSessions.TryGetValue(uid, out long mappedSessionId) && mappedSessionId == sessionId)
                {
                    uidSessions.TryRemove(uid, out _);
                }
            }
        }

        public void BindNickname(long sessionId, string nickname)
        {
            if (sessionId <= 0 || string.IsNullOrWhiteSpace(nickname))
            {
                return;
            }

            sessionNicknames[sessionId] = nickname;
        }

        public void UnbindNickname(long sessionId)
        {
            sessionNicknames.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// 检索与指定会话标识关联的用户标识。
        /// </summary>
        /// <remarks>返回 0 表示未找到关联的用户。</remarks>
        /// <param name="sessionId">要查找的会话标识符。</param>
        /// <returns>匹配的用户标识；若未找到则返回 0。</returns>
        public int GetUserIdBySessionId(long sessionId)
        {
            return sessionUsers.TryGetValue(sessionId, out var userId) ? userId : 0;
        }

        /// <summary>
        /// 检索与指定会话标识关联的 UID。
        /// </summary>
        /// <param name="sessionId">会话标识。</param>
        /// <returns>匹配的 UID；若未找到则返回空字符串。</returns>
        public string GetUidBySessionId(long sessionId)
        {
            return sessionUids.TryGetValue(sessionId, out string? uid) ? uid : string.Empty;
        }

        public string GetNicknameBySessionId(long sessionId)
        {
            return sessionNicknames.TryGetValue(sessionId, out string? nickname) ? nickname : string.Empty;
        }

        /// <summary>
        /// 检索与指定 UID 关联的会话标识。
        /// </summary>
        /// <param name="uid">UID。</param>
        /// <returns>匹配的会话标识；若未找到则返回 0。</returns>
        public long GetSessionIdByUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid))
            {
                return 0;
            }

            return uidSessions.TryGetValue(uid, out long sessionId) ? sessionId : 0;
        }

        /// <summary>
        /// 获取当前在线客户端会话数。
        /// </summary>
        /// <returns>当前在线客户端会话的数量。</returns>
        public int GetOnlineCount()
        {
            return clientSessions.Count;
        }

        /// <summary>所有客户端会话（空闲超时扫描等用）。</summary>
        public IEnumerable<Network.ISession> GetAllSessions()
        {
            return clientSessions.Values;
        }

        /// <summary>
        /// 断线重连：把新会话的身份绑定（userId/uid/nickname）迁移到旧会话 ID 上，旧 ID 保留
        /// （后端业务服按旧 ID 续接挂起的实体），新会话 ID 注销。返回 true 表示迁移成功。
        /// </summary>
        public bool ResumeSession(long newSessionId, long oldSessionId)
        {
            if (newSessionId <= 0 || oldSessionId <= 0 || newSessionId == oldSessionId)
            {
                return false;
            }
            if (!clientSessions.TryGetValue(newSessionId, out var session))
            {
                return false;
            }

            if (sessionUsers.TryRemove(newSessionId, out int userId))
            {
                sessionUsers[oldSessionId] = userId;
            }
            if (sessionUids.TryRemove(newSessionId, out string? uid))
            {
                sessionUids[oldSessionId] = uid;
                uidSessions[uid] = oldSessionId;
            }
            if (sessionNicknames.TryRemove(newSessionId, out string? nickname))
            {
                sessionNicknames[oldSessionId] = nickname;
            }

            clientSessions.TryRemove(newSessionId, out _);
            clientSessions[oldSessionId] = session;
            Shared.Log.Info($"Gateway 断线重连：会话 {newSessionId} 恢复为 {oldSessionId} Remote:{session.RemoteEndPoint}");
            return true;
        }
    }
}
