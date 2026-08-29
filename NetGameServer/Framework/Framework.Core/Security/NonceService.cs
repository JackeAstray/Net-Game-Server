using System.Collections.Concurrent;

namespace Framework.Core.Security;

/// <summary>
/// 一次性 nonce 缓存（防重放）：按 nonce 字符串登记，成功返回 true，重复或已过期返回 false。
/// 内存按 nonce 维度 + TTL 上界自然增长，<see cref="Cleanup"/> 周期性回收过期项，防止无界膨胀。
/// 对标 KBE 业务层一次性 challenge 模式（TokenService 文档承诺但此前未落地）。
/// </summary>
public sealed class NonceService
{
    private readonly ConcurrentDictionary<string, long> nonces = new(StringComparer.Ordinal);
    private long lastCleanupTicks;

    /// <summary>清理间隔（默认 60s）：仅在 <see cref="RegisterOnce"/> 命中时按需触发，避免独立 timer。</summary>
    private static readonly long CleanupIntervalTicks = TimeSpan.FromSeconds(60).Ticks;

    /// <summary>
    /// 登记一次性 nonce。返回 true 表示接受（首次且未过期）；false 表示重放（已登记）或参数无效。
    /// </summary>
    /// <param name="nonce">调用方生成的不可预测字符串（建议 128bit+ 随机）。</param>
    /// <param name="ttl">nonce 有效期（建议略大于业务最大处理延迟）。</param>
    /// <param name="now">当前 UTC 时间（测试可注入）。默认 UtcNow。</param>
    public bool RegisterOnce(string nonce, TimeSpan ttl, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(nonce) || ttl <= TimeSpan.Zero)
        {
            return false;
        }
        // C5 修复：统一规范化为 UTC 再取 ToBinary/Ticks。
        // DateTime.ToBinary() 编码了 Kind，Local/Unspecified 与 UTC 的二进制值不可直接比较，
        // 之前若调用方注入非 UTC 的 now 会导致过期判断失效。
        DateTime n = (now ?? DateTime.UtcNow).ToUniversalTime();
        long expiresAt = n.Add(ttl).ToBinary();
        // TryAdd 失败说明 nonce 已被登记过 → 重放攻击
        if (!nonces.TryAdd(nonce, expiresAt))
        {
            return false;
        }
        MaybeCleanup(n);
        return true;
    }

    /// <summary>
    /// 主动清理过期 nonce（按当前时间）。通常由 <see cref="RegisterOnce"/> 内部按需触发；
    /// 也可在空闲期主动调用一次以释放内存。
    /// </summary>
    public int Cleanup(DateTime? now = null)
    {
        DateTime n = (now ?? DateTime.UtcNow).ToUniversalTime();
        long cutoff = n.ToBinary();
        int removed = 0;
        foreach (var kv in nonces)
        {
            if (kv.Value < cutoff && nonces.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }
        lastCleanupTicks = n.Ticks;
        return removed;
    }

    private void MaybeCleanup(DateTime now)
    {
        if (now.Ticks - lastCleanupTicks < CleanupIntervalTicks)
        {
            return;
        }
        Cleanup(now);
    }

    /// <summary>当前缓存的 nonce 数量（监控/测试用）。</summary>
    public int Count => nonces.Count;
}
