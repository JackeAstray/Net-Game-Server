using System.Collections.Concurrent;

namespace Framework.Entity;

/// <summary>
/// 实体位置路由缓存（迭代 21，对标 ET Location 代理的调用方侧缓存）：
/// 缓存 entityId → 所在节点 nodeId，供跨节点实体调用（EntityCall）在发送时覆盖
/// "调用方硬编码的、可能已因实体迁移而过期"的目标节点（stale routing 修正）。
/// - Update：迁移结果 / 位置查询响应 / 本节点生成实体时写入
/// - Invalidate：实体迁出/销毁时清除
/// - Resolve：读取新鲜缓存；无缓存/过期则回退调用方提示（hint）
/// 线程安全（ConcurrentDictionary），供 tick 线程与收包线程并发读写。
/// </summary>
public sealed class EntityCallRouter
{
    private sealed class CacheEntry
    {
        public required string NodeId;
        public long ExpiresAtTicks;
    }

    private readonly ConcurrentDictionary<long, CacheEntry> cache = new();

    /// <summary>缓存 TTL（默认 30s；短于实体驻留时间，迁移后最多 30s 内收敛到新位置）。</summary>
    public long CacheTtlTicks { get; set; } = TimeSpan.FromSeconds(30).Ticks;

    /// <summary>缓存条目数（统计用）。</summary>
    public int CacheCount => cache.Count;

    /// <summary>
    /// 解析实体的目标节点：优先返回未过期的缓存位置；否则返回调用方提示（可能旧）。
    /// </summary>
    public string? Resolve(long entityId, string? hintNodeId)
    {
        if (cache.TryGetValue(entityId, out var entry) && DateTime.UtcNow.Ticks < entry.ExpiresAtTicks)
        {
            return entry.NodeId;
        }
        return string.IsNullOrEmpty(hintNodeId) ? null : hintNodeId;
    }

    /// <summary>写入/刷新位置缓存。</summary>
    public void Update(long entityId, string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return;
        }
        cache[entityId] = new CacheEntry
        {
            NodeId = nodeId,
            ExpiresAtTicks = DateTime.UtcNow.Ticks + CacheTtlTicks
        };
    }

    /// <summary>失效单条缓存（实体迁出/销毁时）。</summary>
    public bool Invalidate(long entityId) => cache.TryRemove(entityId, out _);

    /// <summary>清空全部缓存（节点重连/重配时）。</summary>
    public void Clear() => cache.Clear();
}
