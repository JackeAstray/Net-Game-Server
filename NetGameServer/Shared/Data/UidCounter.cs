namespace Shared.Data;

/// <summary>
/// UID 发号计数器（单行表，Id 恒为 1）：
/// 供 Login 通过 DB 原子领取发号段（SELECT ... FOR UPDATE），
/// 根治多 Login/DB 实例"读 MAX + 本地自增"的跨实例碰撞问题。
/// </summary>
public class UidCounter
{
    public int Id { get; set; }

    /// <summary>当前区服（冗余记录，供运维排查）。</summary>
    public int RegionId { get; set; }

    /// <summary>已发放的最大序列号（不含区服前缀）。</summary>
    public long CurrentValue { get; set; }
}
