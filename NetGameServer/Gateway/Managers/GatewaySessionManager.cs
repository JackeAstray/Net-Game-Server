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
            Shared.Log.Info($"Gateway 会话已移除 SessionId:{sessionId}");
        }

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

        public void BindUser(long sessionId, int userId)
        {
            if (sessionId <= 0 || userId <= 0)
            {
                return;
            }

            sessionUsers[sessionId] = userId;
        }

        public void UnbindUser(long sessionId)
        {
            sessionUsers.TryRemove(sessionId, out _);
        }

        public int GetUserIdBySessionId(long sessionId)
        {
            return sessionUsers.TryGetValue(sessionId, out var userId) ? userId : 0;
        }

        public int GetOnlineCount()
        {
            return clientSessions.Count;
        }
    }
}
