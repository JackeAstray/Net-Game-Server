using Shared.Data;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Network;
using Network.Tcp;

namespace Login.Managers
{
    /// <summary>
    /// 会话管理器（单例）。
    /// 负责维护在线用户的会话映射关系，处理登录时的顶号逻辑以及会话断开后的延迟离线处理。
    /// </summary>
    public class SessionManager
    {
        // 单例实例
        private static readonly SessionManager instance = new SessionManager();
        /// <summary>
        /// 获取 SessionManager 单例对象。
        /// </summary>
        public static SessionManager Instance => instance;

        /// <summary>
        /// 保存已登录用户的会话信息：UserId -> ISession。
        /// 用于根据用户 Id 查找其当前在线会话并进行消息发送或断开处理。
        /// </summary>
        private readonly ConcurrentDictionary<int, Network.ISession> userSessions = new ConcurrentDictionary<int, Network.ISession>();

        /// <summary>
        /// 保存会话对应的用户 Id 映射：ISession -> UserId。
        /// 用于在会话断开时查找对应的用户并执行离线流程。
        /// </summary>
        private readonly ConcurrentDictionary<Network.ISession, int> sessionUsers = new ConcurrentDictionary<Network.ISession, int>();

        // 私有构造函数，确保单例
        private SessionManager() { }

        /// <summary>
        /// 处理用户登录事件的方法。
        /// </summary>
        /// <param name="user">登录的用户对象</param>
        /// <param name="session">用户的会话对象</param>
        /// <returns>返回一个表示操作是否成功的任务</returns>
        public async Task<bool> OnUserLoginAsync(User user, Network.ISession session)
        {
            // 顶号处理
            if (userSessions.TryGetValue(user.Id, out var existingSession))
            {
                if (existingSession != session)
                {
                    Shared.Log.Info($"用户{user.Id}从其他位置登录。正在断开旧会话的连接。");
                    
                    var kickMessage = new Shared.Messages.Login.KickedOffMessage 
                    { 
                        Reason = "您的账号在其他设备登录",
                        Time = System.DateTime.UtcNow 
                    };
                    byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(kickMessage);
                    byte[] packet = new byte[data.Length + 4];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1007); // 假设1007为KickedOffMessage的MsgId
                    data.CopyTo(packet.AsSpan(4));
                    existingSession.Send(packet);

                    existingSession.Close();

                    userSessions.TryRemove(user.Id, out _);
                    sessionUsers.TryRemove(existingSession, out _);
                }
            }

            userSessions[user.Id] = session;
            sessionUsers[session] = user.Id;
            return true;
        }

        /// <summary>
        /// 表示用户断开连接的事件处理。
        /// 这里我们不直接将用户标记为离线，
        /// 而是启动一个延迟任务来处理实际的离线逻辑，
        /// 以便在用户短暂断线后重新连接时能够恢复状态。
        /// </summary>
        /// <param name="session">断开连接的会话对象</param>
        public void OnSessionDisconnected(Network.ISession session)
        {
            if (sessionUsers.TryGetValue(session, out var userId))
            {
                Shared.Log.Info($"用户{userId}断开连接。正在处理离线状态。");
                // 断线/离线处理
                sessionUsers.TryRemove(session, out _);

                if (userSessions.TryGetValue(userId, out var currentSession) && currentSession == session)
                {
                    userSessions.TryRemove(userId, out _);
                    // TODO: 更新用户状态为离线 (如果需要向DB或者Gateway报告)

                    // 如果未重新连接，则启动延迟任务以处理实际的脱机处理
                    Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t =>
                    {
                        if (!userSessions.ContainsKey(userId))
                        {
                            Shared.Log.Info($"用户{userId}已离线5分钟。正在处理最终离线步骤。");
                            // 离线处理真实数据，保存数据等
                        }
                    });
                }
            }
        }

        /// <summary>
        /// 获取用户的Session信息
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public Network.ISession GetUserSession(int userId)
        {
            userSessions.TryGetValue(userId, out var session);
            return session;
        }
    }
}