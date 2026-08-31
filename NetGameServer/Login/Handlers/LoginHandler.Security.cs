using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Network.Tcp;
using Shared;
using Shared.Messages.Login;
using MailKit.Net.Smtp;
using MimeKit;
using Shared.Data;
using Shared.Messages;
namespace Login.Handlers
{
    /// <summary>
    /// 登录 Handler —— 安全与基础设施模块（DB 调用封装/失败节流/动作键/邮件发送）。
    /// 与 LoginHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// </summary>
    public partial class LoginHandler
    {
        /// <summary>
        /// 向 DB 服务发送请求并等待响应的通用方法。
        /// 方法将请求序列化为 JSON，并在包头写入消息 ID；通过 dbClient 发送后，监听回包并在
        /// 收到匹配 msgId 的响应时反序列化为目标类型 T 并返回。
        /// </summary>
        /// <typeparam name="T">期望从 DB 返回的响应类型。</typeparam>
        /// <param name="msgId">用于标识请求/响应类型的消息 ID。</param>
        /// <param name="requestData">要发送到 DB 的请求对象（将被序列化）。</param>
        /// <returns>反序列化后的响应对象，或在超时/异常时返回 null。</returns>
        private async Task<T?> CallDbAsync<T>(int msgId, object requestData) where T : class
        {
            var tcs = new TaskCompletionSource<byte[]>();
            byte[] data = Shared.Json.SerializeToUtf8Bytes(requestData);

            // Generate sequence/request Id
            long requestId = System.Threading.Interlocked.Increment(ref sequenceId);
            LoginServerApp.PendingRequests[requestId] = tcs;

            byte[] payloadWithRequestId = Shared.RouteMetadata.AttachRequestId(data, requestId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadWithRequestId, out int totalLength);
            try
            {
                dbClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                // 发送失败必须移除已注册的待回执项，否则 PendingRequests 无界增长
                LoginServerApp.PendingRequests.TryRemove(requestId, out _);
                Shared.Log.Error($"向 DB 发送请求失败 MsgId:{msgId}, RequestId:{requestId}, Exception:{ex}");
                return null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            int timeoutMs = ConfigHelper.GetConfig<int>("DbRequestTimeoutMs");
            if (timeoutMs <= 0)
            {
                timeoutMs = 5000;
            }

            using var cts = new System.Threading.CancellationTokenSource();
            var timeoutTask = Task.Delay(timeoutMs, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                LoginServerApp.PendingRequests.TryRemove(requestId, out _);
                Shared.Log.Warning($"向 DB 请求 MsgId:{msgId} 超时，TimeoutMs:{timeoutMs}, RequestId:{requestId}");
                return null;
            }

            cts.Cancel(); // 取消 Task.Delay 防止资源泄露
            var responseData = await tcs.Task;
            if (responseData == null)
            {
                Shared.Log.Error($"DB 回包为空，MsgId:{msgId}, RequestId:{requestId}");
                return null;
            }

            try
            {
                return Shared.Json.DeserializeFromUtf8Bytes<T>(responseData);
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"反序列化响应异常，MsgId:{msgId}, RequestId:{requestId}, Exception:{ex}");
                return null;
            }
        }

        /// <summary>
        /// 尝试获取指定操作与标识的剩余节流（锁定）时间。
        /// </summary>
        /// <remarks>若跟踪器存在但未锁定且 FailedCount <= 0，则会尝试从缓存中移除该跟踪器。时间基于 UTC 计算。</remarks>
        /// <param name="action">要检查节流状态的操作名称。</param>
        /// <param name="identity">与操作关联的标识（例如用户 ID 或 IP）。</param>
        /// <param name="remaining">当返回 true 时输出锁定剩余时间；否则为 TimeSpan.Zero。</param>
        /// <returns>若存在跟踪器且当前处于锁定期，返回 true 并通过 remaining 返回剩余时间；否则返回 false。</returns>
        private static bool TryGetThrottleRemaining(string action, string identity, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            string key = BuildActionKey(action, identity);
            // V13 修复：偶发清理失效的失败尝试跟踪（防按账号/IP 永久增长）
            if (actionAttemptTrackers.Count >= 1024 && (actionAttemptTrackers.Count & 255) == 0)
            {
                SweepExpiredAttemptTrackers();
            }
            if (!actionAttemptTrackers.TryGetValue(key, out var tracker))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (tracker.LockedUntilUtc > now)
            {
                remaining = tracker.LockedUntilUtc - now;
                return true;
            }

            if (tracker.FailedCount <= 0)
            {
                actionAttemptTrackers.TryRemove(key, out _);
            }

            return false;
        }

        /// <summary>移除已失效的失败尝试跟踪项（V13 兜底）：既不在锁定中、且最近一次失败已超过 ThrottleLockDuration 的条目。</summary>
        private static void SweepExpiredAttemptTrackers()
        {
            DateTime cutoff = DateTime.UtcNow.Add(-ThrottleLockDuration);
            foreach (var kv in actionAttemptTrackers)
            {
                if (kv.Value.LockedUntilUtc <= cutoff && kv.Value.LastFailedAtUtc <= cutoff)
                {
                    actionAttemptTrackers.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>
        /// 记录指定操作与身份的失败尝试，递增失败计数并在达到阈值时按 UTC 将该项锁定一段时间。
        /// </summary>
        /// <remarks>使用并发字典的 AddOrUpdate 原子操作；若条目处于锁定期（LockedUntilUtc > 当前 UTC 时间）则不修改；当失败次数达到
        /// MaxFailedAttempts 时记录警告、将 LockedUntilUtc 设置为当前 UTC 时间加上 ThrottleLockDuration 并将 FailedCount 重置为 0；时间基于
        /// DateTime.UtcNow。</remarks>
        /// <param name="action">要跟踪的操作名称或标识符。</param>
        /// <param name="identity">与失败尝试相关的身份标识（例如用户名、用户 ID 或 IP 地址）。</param>
        private static void RegisterFailedAttempt(string action, string identity)
        {
            DateTime now = DateTime.UtcNow;
            string key = BuildActionKey(action, identity);
            actionAttemptTrackers.AddOrUpdate(
                key,
                _ => new ActionAttemptTracker { FailedCount = 1, LockedUntilUtc = DateTime.MinValue, LastFailedAtUtc = now },
                (_, existing) =>
                {
                    if (existing.LockedUntilUtc > now)
                    {
                        return existing;
                    }

                    int failedCount = existing.FailedCount + 1;
                    if (failedCount >= MaxFailedAttempts)
                    {
                        Log.Warning($"{action}:{identity} 连续失败达到阈值，已锁定 {ThrottleLockDuration.TotalMinutes} 分钟");
                        return new ActionAttemptTracker
                        {
                            FailedCount = 0,
                            LockedUntilUtc = now.Add(ThrottleLockDuration),
                            LastFailedAtUtc = now
                        };
                    }

                    return new ActionAttemptTracker
                    {
                        FailedCount = failedCount,
                        LockedUntilUtc = DateTime.MinValue,
                        LastFailedAtUtc = now
                    };
                });
        }

        /// <summary>
        /// 移除与指定操作和标识关联的失败尝试跟踪项。
        /// </summary>
        /// <param name="action">要清除其失败尝试记录的操作名称。</param>
        /// <param name="identity">与操作关联的标识（例如用户或实体）。</param>
        private static void ClearFailedAttempts(string action, string identity)
        {
            string key = BuildActionKey(action, identity);
            actionAttemptTrackers.TryRemove(key, out _);
        }

        /// <summary>
        /// 构建用于标识操作的键，格式为 "{action}:{identity}"；当 identity 为 null、空或只包含空白字符时使用 "unknown"。
        /// </summary>
        /// <remarks>对 identity 调用 Trim，并将 null 视为空字符串；若结果为空或仅空白，则使用 "unknown" 作为默认值。</remarks>
        /// <param name="action">操作名称，作为键的前缀。</param>
        /// <param name="identity">主体标识，经过 Trim 规范化；若为空或仅有空白，则替换为 "unknown"，作为键的后缀。</param>
        /// <returns>由 action 和规范化后的 identity 以冒号连接组成的字符串键（例如 "save:alice" 或 "delete:unknown"）。</returns>
        private static string BuildActionKey(string action, string identity)
        {
            string normalizedIdentity = (identity ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedIdentity))
            {
                normalizedIdentity = "unknown";
            }

            return $"{action}:{normalizedIdentity}";
        }

        private sealed class ActionAttemptTracker
        {
            public int FailedCount { get; set; }
            public DateTime LockedUntilUtc { get; set; }
            /// <summary>最近一次失败时刻（V13：失效条目清理依据）。</summary>
            public DateTime LastFailedAtUtc { get; set; }
        }
    }
}
