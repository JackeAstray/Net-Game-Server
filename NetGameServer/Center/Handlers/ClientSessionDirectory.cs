using System;
using System.Collections.Concurrent;

namespace Center.Handlers
{
    /// <summary>
    /// Gateway 集群挂起会话目录（B1）：跨实例断线重连接管。
    /// Gateway 断线挂起时登记（clientSessionId → userId + 过期时间），客户端换 Gateway 重连时
    /// 按 UserId 查询到旧 clientSessionId，由新实例恢复别名并续接后端实体。
    /// 周期清扫过期条目（Center 维护循环），防无界增长。
    /// </summary>
    public sealed class ClientSessionDirectory
    {
        /// <summary>clientSessionId → 挂起条目。</summary>
        private readonly ConcurrentDictionary<long, SuspendedEntry> bySessionId = new();

        /// <summary>userId → clientSessionId（一个用户同时至多一条挂起记录）。</summary>
        private readonly ConcurrentDictionary<int, long> byUserId = new();

        public sealed class SuspendedEntry
        {
            public long ClientSessionId { get; set; }
            public int UserId { get; set; }
            public string GatewayNodeId { get; set; } = string.Empty;
            public DateTime ExpiresAtUtc { get; set; }
        }

        /// <summary>登记挂起会话（覆盖同 userId 的旧记录）。</summary>
        public void Suspend(long clientSessionId, int userId, string gatewayNodeId, TimeSpan grace)
        {
            if (clientSessionId <= 0 || userId <= 0)
            {
                return;
            }
            var entry = new SuspendedEntry
            {
                ClientSessionId = clientSessionId,
                UserId = userId,
                GatewayNodeId = gatewayNodeId ?? string.Empty,
                ExpiresAtUtc = DateTime.UtcNow.Add(grace)
            };
            bySessionId[clientSessionId] = entry;
            byUserId[userId] = clientSessionId;
        }

        /// <summary>注销挂起会话（恢复成功/彻底离场）。</summary>
        public void Unsuspend(long clientSessionId)
        {
            if (clientSessionId <= 0)
            {
                return;
            }
            if (bySessionId.TryRemove(clientSessionId, out var entry))
            {
                byUserId.TryRemove(new System.Collections.Generic.KeyValuePair<int, long>(entry.UserId, clientSessionId));
            }
        }

        /// <summary>按 UserId 查询挂起的 clientSessionId（未过期才返回）。</summary>
        public bool TryLocate(int userId, out SuspendedEntry? entry)
        {
            entry = null;
            if (userId <= 0 || !byUserId.TryGetValue(userId, out long clientSessionId))
            {
                return false;
            }
            if (!bySessionId.TryGetValue(clientSessionId, out var found))
            {
                byUserId.TryRemove(userId, out _);
                return false;
            }
            if (found.ExpiresAtUtc < DateTime.UtcNow)
            {
                bySessionId.TryRemove(clientSessionId, out _);
                byUserId.TryRemove(new System.Collections.Generic.KeyValuePair<int, long>(userId, clientSessionId));
                return false;
            }
            entry = found;
            return true;
        }

        /// <summary>清理所有过期条目（Center 维护循环周期调用）。</summary>
        public int SweepExpired(DateTime now)
        {
            int removed = 0;
            foreach (var kv in bySessionId.ToArray())
            {
                if (kv.Value.ExpiresAtUtc < now)
                {
                    bySessionId.TryRemove(kv.Key, out _);
                    byUserId.TryRemove(new System.Collections.Generic.KeyValuePair<int, long>(kv.Value.UserId, kv.Key));
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>当前挂起记录数（监控）。</summary>
        public int Count => bySessionId.Count;
    }
}
