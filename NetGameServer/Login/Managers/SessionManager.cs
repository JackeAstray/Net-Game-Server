using Shared.Data;
using Shared.Messages;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Network;

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

        public static SessionManager Instance => instance;

        public Action<int>? OnUserOfflineAction { get; set; }
        public Action<long, byte[]>? SendToGatewayAction { get; set; }

        private readonly ConcurrentDictionary<int, long> userSessions = new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<long, int> sessionUsers = new ConcurrentDictionary<long, int>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> offlineTasks = new();

        private SessionManager() { }

        /// <summary>
        /// 处理用户登录事件的方法。
        /// </summary>
        /// <param name="user">登录的用户对象</param>
        /// <param name="clientSessionId">用户的网关会话ID</param>
        /// <returns>返回一个表示操作是否成功的任务</returns>
        public async Task<bool> OnUserLoginAsync(User user, long clientSessionId)
        {
            // 取消可能存在的该用户离线倒计时任务
            if (offlineTasks.TryRemove(user.Id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // 顶号处理
            if (userSessions.TryGetValue(user.Id, out var existingSessionId))
            {
                if (existingSessionId != clientSessionId)
                {
                    Shared.Log.Info($"用户{user.Id}从其他位置登录。正在断开旧会话的连接。");

                    var kickMessage = new Shared.Messages.Login.KickedOffMessage
                    {
                        Reason = "您的账号在其他设备登录",
                        Time = System.DateTime.UtcNow
                    };
                    byte[] data = Shared.Json.SerializeToUtf8Bytes(kickMessage);
                    byte[] packet = new byte[data.Length + 4];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), MessageIds.KickedOffNotif);
                    data.CopyTo(packet.AsSpan(4));

                    SendToGatewayAction?.Invoke(existingSessionId, packet);

                    userSessions.TryRemove(user.Id, out _);
                    sessionUsers.TryRemove(existingSessionId, out _);
                }
            }

            userSessions[user.Id] = clientSessionId;
            sessionUsers[clientSessionId] = user.Id;
            return true;
        }

        /// <summary>
        /// 表示用户断开连接的事件处理。
        /// 这里我们不直接将用户标记为离线，
        /// 而是启动一个延迟任务来处理实际的离线逻辑，
        /// 以便在用户短暂断线后重新连接时能够恢复状态。
        /// </summary>
        /// <param name="clientSessionId">断开连接的客户端会话ID</param>
        public void OnSessionDisconnected(long clientSessionId)
        {
            if (sessionUsers.TryGetValue(clientSessionId, out var userId))
            {
                Shared.Log.Info($"用户{userId}断开连接。正在处理离线状态。");
                // 断线/离线处理
                sessionUsers.TryRemove(clientSessionId, out _);

                if (userSessions.TryGetValue(userId, out var currentSessionId) && currentSessionId == clientSessionId)
                {
                    userSessions.TryRemove(userId, out _);

                    var cts = new CancellationTokenSource();
                    offlineTasks[userId] = cts;

                    // 消除 Task.Delay 滥用，使用 CancellationTokenSource 来管理。一旦重连立即取消注销任务
                    Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                            if (!cts.Token.IsCancellationRequested && !userSessions.ContainsKey(userId))
                            {
                                offlineTasks.TryRemove(userId, out _);
                                Shared.Log.Info($"用户{userId}已离线5分钟。正在处理最终离线步骤。");
                                // 离线处理真实数据，通知 DB 下线
                                OnUserOfflineAction?.Invoke(userId);
                            }
                        }
                        catch (TaskCanceledException)
                        {
                            Shared.Log.Info($"用户{userId}取消离线任务，可能已重连。");
                        }
                    });
                }
            }
        }

        /// <summary>
        /// 强制用户下线的方法。
        /// </summary>
        /// <param name="userId">用户ID</param>
        public void ForceLogout(int userId)
        {
            if (offlineTasks.TryRemove(userId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            userSessions.TryRemove(userId, out var sId);
            if (sId != 0)
            {
                sessionUsers.TryRemove(sId, out _);
                // 主动踢下线通知
                var kickMessage = new Shared.Messages.Login.KickedOffMessage
                {
                    Reason = "已主动登出",
                    Time = System.DateTime.UtcNow
                };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(kickMessage);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), MessageIds.KickedOffNotif);
                data.CopyTo(packet.AsSpan(4));

                SendToGatewayAction?.Invoke(sId, packet);
            }

            // 通知 DB 从内存/库里抹除
            OnUserOfflineAction?.Invoke(userId);
        }

        /// <summary>
        /// 获取用户的 Session ID
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public long GetUserSessionId(int userId)
        {
            userSessions.TryGetValue(userId, out var sessionId);
            return sessionId;
        }

        /// <summary>
        /// 检索与指定客户端会话标识关联的用户标识。
        /// </summary>
        /// <param name="clientSessionId">客户端会话标识，用于查找关联的用户标识。</param>
        /// <returns>与指定会话关联的用户标识；若未找到则返回默认的 int 值（0）。</returns>
        public int GetUserIdBySessionId(long clientSessionId)
        {
            sessionUsers.TryGetValue(clientSessionId, out var userId);
            return userId;
        }
    }
}
