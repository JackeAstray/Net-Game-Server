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
        /// <summary>离线处理执行中标志（P0 修复）：顶号/强退/5 分钟离线任务并发时仅一个执行者，防 OnUserOfflineAction 重复触发。</summary>
        private readonly ConcurrentDictionary<int, byte> offlineProcessing = new();
        /// <summary>顶号/断开/强退共享锁：串行化 userSessions+sessionUsers 双字典的"读-删-写"临界区，防并发顶号产生幽灵会话。</summary>
        private readonly object sessionGate = new();

        private SessionManager() { }

        /// <summary>
        /// 处理用户登录事件的方法。
        /// </summary>
        /// <param name="user">登录的用户对象</param>
        /// <param name="clientSessionId">用户的网关会话ID</param>
        /// <returns>返回一个表示操作是否成功的任务</returns>
        public async Task<bool> OnUserLoginAsync(User user, long clientSessionId)
        {
            lock (sessionGate)
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
            }
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
            lock (sessionGate)
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
                    // 覆盖前先取消旧实例，防 CancellationTokenSource 泄漏（重连再断线场景）
                    if (offlineTasks.TryRemove(userId, out var previousCts))
                    {
                        previousCts.Cancel();
                        previousCts.Dispose();
                    }
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
                                RunOfflineOnce(userId);
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

            long sId;
            lock (sessionGate)
            {
            userSessions.TryRemove(userId, out sId);
            if (sId != 0)
            {
                sessionUsers.TryRemove(sId, out _);
            }
            }

            if (sId != 0)
            {
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

            // 通知 DB 从内存/库里抹除（裁决唯一执行者，防与 5 分钟离线任务并发重复触发）
            RunOfflineOnce(userId);
        }

        /// <summary>裁决离线处理唯一执行者：offlineProcessing.TryAdd 成功者负责调用 OnUserOfflineAction，结束后释放。</summary>
        private void RunOfflineOnce(int userId)
        {
            if (!offlineProcessing.TryAdd(userId, 0))
            {
                return; // 已有执行者在处理
            }
            try
            {
                Shared.Log.Info($"用户{userId}正在处理最终离线步骤。");
                OnUserOfflineAction?.Invoke(userId);
            }
            finally
            {
                offlineProcessing.TryRemove(userId, out _);
            }
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
