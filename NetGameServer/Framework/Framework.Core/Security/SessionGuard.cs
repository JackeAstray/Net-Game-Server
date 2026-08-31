using System.Collections.Concurrent;

namespace Framework.Core.Security;

/// <summary>
/// 客户端会话防护（防重放 + 时间窗）：
/// - <see cref="IsSessionValid"/>：会话生命周期 + 空闲窗口判定（纯函数，调用方提供时间戳）。
/// - <see cref="AntiReplayState"/>：按 userId 维护单调递增 SessionSeq，TokenService.Verify 拒绝旧 seq 重放。
/// D6 客户端会话侧防重放：补齐 Token/SessionId 路径的"时间窗 + 单调序号"。
/// </summary>
public static class SessionGuard
{
    /// <summary>会话最大生命周期（超出视为过期，强制重登）。</summary>
    public static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromHours(2);

    /// <summary>会话最大空闲时间（无活动超出后失效，缩小重放窗口）。</summary>
    public static readonly TimeSpan MaxIdleSeconds = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 判定会话是否仍有效（生命周期窗 + 空闲窗）。纯函数，便于单测。
    /// </summary>
    /// <param name="establishedAt">会话建立时间（UTC）。</param>
    /// <param name="lastActivity">最近活动时间（UTC）。</param>
    /// <param name="now">当前时间（UTC，测试可注入）。</param>
    public static bool IsSessionValid(DateTime establishedAt, DateTime lastActivity, DateTime now)
    {
        if (now - establishedAt > MaxSessionLifetime)
        {
            return false; // 超过最大生命周期
        }
        if (now - lastActivity > MaxIdleSeconds)
        {
            return false; // 超过最大空闲
        }
        return true;
    }

    /// <summary>按 userId 维护已接受的 SessionSeq 单调递增，阻止旧 token 重放。</summary>
    public sealed class AntiReplayState
    {
        private readonly ConcurrentDictionary<int, long> lastSeqByUser = new();
        // 签发侧序号（与"已接受序号"分离）：签发新 Token 时递增，但不改变已接受值，
        // 避免同一用户重新登录的新 Token（更高的 seq）被当作旧 token 重放拒绝。
        private readonly ConcurrentDictionary<int, long> issuedSeqByUser = new();
        // P2 修复：最近一次活动时间（单调时钟 TickCount64），供 Sweep 清理长期不活跃用户条目，防无界增长。
        private readonly ConcurrentDictionary<int, long> lastActivityByUser = new();

        /// <summary>查询某用户当前已接受的最大 seq（0 表示未登记过）。</summary>
        public long GetLastSeq(int userId) => lastSeqByUser.TryGetValue(userId, out var v) ? v : 0L;

        /// <summary>
        /// 为某用户签发下一个单调递增序号（供签发新 Token 使用）。
        /// 并发安全：原子递增，多线程下不重复。
        /// </summary>
        public long IssueNextSeq(int userId)
        {
            lastActivityByUser[userId] = Environment.TickCount64;
            return issuedSeqByUser.AddOrUpdate(userId, 1L, (_, last) => last + 1);
        }

        /// <summary>
        /// 接受一次 token 使用：seq 必须不小于上次接受值（首次为任意正数）。
        /// 返回 true 表示接受；false 表示回退（旧 token 重放）或参数无效 → TokenService.Verify 应据此拒绝。
        /// 修复（token 幂等复用）：原实现要求 seq 必须严格大于 lastSeq，导致同一合法 token 只能被验证一次，
        /// HTTP 管理接口（如 query-account）第二次携带同一 token 时被判重放返回 401。
        /// 改为"不允许回退"：seq == lastSeq（同一 token 复用）视为合法并幂等接受；
        /// seq &lt; lastSeq（旧登录会话的 token）仍被拒绝，防重放语义保留。
        /// 无锁 CAS 循环：并发竞争下保持单调性。
        /// </summary>
        public bool TryAcceptSeq(int userId, long seq)
        {
            if (seq <= 0)
            {
                return false;
            }
            while (true)
            {
                if (lastSeqByUser.TryGetValue(userId, out var last))
                {
                    if (seq < last)
                    {
                        return false; // 回退：旧 token 重放
                    }
                    if (seq == last)
                    {
                        lastActivityByUser[userId] = Environment.TickCount64;
                        return true; // 幂等：同一 token 复用
                    }
                    if (lastSeqByUser.TryUpdate(userId, seq, last))
                    {
                        lastActivityByUser[userId] = Environment.TickCount64;
                        return true;
                    }
                    // CAS 失败（并发更新），重试
                }
                else
                {
                    if (lastSeqByUser.TryAdd(userId, seq))
                    {
                        lastActivityByUser[userId] = Environment.TickCount64;
                        return true;
                    }
                    // 别的线程先注册了，重试读取并走更新分支
                }
            }
        }

        /// <summary>重置某用户的 seq 记录（账号注销/封禁时使用）。</summary>
        public void Reset(int userId)
        {
            lastSeqByUser.TryRemove(userId, out _);
            issuedSeqByUser.TryRemove(userId, out _);
            lastActivityByUser.TryRemove(userId, out _);
        }

        /// <summary>
        /// 清理超过 idle 阈值未活动的用户条目（P2 修复：防字典随累计登录用户数无界增长）。
        /// 周期调用（如 Login 心跳循环）。
        /// </summary>
        public void Sweep(TimeSpan idle)
        {
            long threshold = Environment.TickCount64 - (long)idle.TotalMilliseconds;
            foreach (var key in lastActivityByUser.Keys.ToArray())
            {
                if (lastActivityByUser.TryGetValue(key, out long last) && last < threshold)
                {
                    lastSeqByUser.TryRemove(key, out _);
                    issuedSeqByUser.TryRemove(key, out _);
                    lastActivityByUser.TryRemove(key, out _);
                }
            }
        }
    }
}
