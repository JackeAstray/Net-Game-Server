using System;
using System.Collections.Concurrent;
using Framework.Core;

namespace Framework.Entity;

/// <summary>
/// 实体远程调用中枢（对标 KBE EntityCall 的回执/超时机制）：
/// - 每次异步远程调用分配唯一 CallId，注册待回执项（回调 + 超时截止时间）
/// - 收到 EntityRemoteCallResult 时按 CallId 关联并完成回调（回执关联）
/// - 超时未回执的调用在 SweepExpired 中被判定失败并回调（超时表）
/// 挂载方（宿主服务器）需在 tick 中周期调用 <see cref="SweepExpired"/> 驱动超时判定。
///
/// 安全修复：原实现为静态类，所有节点共享同一张 pending 表 + 全局 CallId 计数。
/// 改为实例类，按节点隔离；CallId 自增基数由实例字段持有，避免跨节点冲突。
/// 旧静态 API 仍然以兼容方式委托到全局 EntityCallHubRegistry.Default 实例，
/// 但建议新代码显式持有实例（尤其在 Battle/Game/Center 多节点进程场景）。
/// </summary>
public sealed class EntityCallHub
{
    private long callIdSeed;

    /// <summary>待回执调用表：CallId -> PendingCall。</summary>
    private readonly ConcurrentDictionary<long, PendingCall> pending = new();

    /// <summary>CallId 随机掩码（P3 加固：混淆 CallId 序列，防止攻击者根据已观察的 CallId 预测后续值并伪造回执）。</summary>
    private readonly long callIdMask = ((long)Random.Shared.NextInt64() << 32) ^ (long)Random.Shared.NextInt64();

    /// <summary>实例唯一性后缀（用于 CallId 空间隔离）。</summary>
    public string HubId { get; }

    public EntityCallHub(string? hubId = null)
    {
        // 用进程内单调时钟（Stopwatch.GetTimestamp，QPC/CLOCK_MONOTONIC）+ 实例哈希做 CallId 起点，
        // 确保多节点不冲突且不受系统时钟回拨影响（原 UtcNow.Ticks 受校时影响，C 组加固）。
        long baseTs = System.Diagnostics.Stopwatch.GetTimestamp();
        int instanceHash = hubId != null ? hubId.GetHashCode() & 0xFFFF : Random.Shared.Next(0, 0xFFFF);
        // 起点：低 16 位为实例哈希，高位为单调时间戳；保证全局单调
        callIdSeed = (baseTs & 0x7FFFFFFFFFFF0000L) | (uint)instanceHash;
        if (callIdSeed < 0)
        {
            callIdSeed = -callIdSeed;
        }
        HubId = hubId ?? $"EntityCallHub-{instanceHash:X4}";
    }

    /// <summary>一次待回执的远程调用。</summary>
    public sealed class PendingCall
    {
        public long CallId { get; init; }
        public string? TargetNodeId { get; init; }
        public long EntityId { get; init; }
        public string? MethodName { get; init; }
        public DateTime DeadlineUtc { get; set; }
        /// <summary>回执回调：(Success, ResultValue)。超时或失败时 Success=false。</summary>
        public Action<bool, object?>? Callback { get; init; }
    }

    /// <summary>分配下一个调用 ID（线程安全）。用随机掩码混淆原始单调序列（XOR 为双射，不产生碰撞）。</summary>
    public long NextCallId()
    {
        return System.Threading.Interlocked.Increment(ref callIdSeed) ^ callIdMask;
    }

    /// <summary>注册待回执调用。</summary>
    public void Register(long callId, PendingCall item)
    {
        pending[callId] = item;
    }

    /// <summary>
    /// 处理远程调用回执：按 CallId 匹配待回执项并完成回调。
    /// 返回 true 表示匹配并消费了该回执；无匹配（重复/过期/未知）返回 false。
    /// </summary>
    public bool HandleResult(Framework.Protocol.Generated.EntityRemoteCallResult result)
    {
        // P3 加固：回执必须与发起调用的目标（实体 + 方法）一致，否则视为伪造/串扰回执。
        // 仅凭可预测 CallId 无法再完成他人待回执调用（攻击者需同时猜中实体与方法名）。
        if (!pending.TryGetValue(result.CallId, out var pc))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(pc.MethodName) && !string.Equals(pc.MethodName, result.MethodName, StringComparison.Ordinal))
        {
            Log.Warn($"EntityCall 回执方法不匹配 CallId:{result.CallId} 期望:{pc.MethodName} 实际:{result.MethodName}，已拒绝");
            return false;
        }
        if (pc.EntityId != 0 && pc.EntityId != result.EntityId)
        {
            Log.Warn($"EntityCall 回执实体不匹配 CallId:{result.CallId} 期望:{pc.EntityId} 实际:{result.EntityId}，已拒绝");
            return false;
        }
        if (!pending.TryRemove(result.CallId, out pc))
        {
            return false;
        }

        object? value = null;
        if (result.Success && result.Result.Length > 0)
        {
            // E8 修复：畸形/损坏的回执 payload 不能抛出接收循环，按失败回调处理
            try
            {
                object?[] args = ArgCodec.Deserialize(result.Result);
                value = args.Length > 0 ? args[0] : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"EntityCall 回执反序列化失败，按失败回调 CallId:{result.CallId} Method:{pc.MethodName} Err:{ex.Message}");
                result.Success = false;
            }
        }

        try
        {
            pc.Callback?.Invoke(result.Success, value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"EntityCall 回执回调异常 CallId:{result.CallId} Method:{pc.MethodName}");
        }
        return true;
    }

    /// <summary>
    /// 超时判定：移除所有已过截止时间的待回执调用，并以失败（Success=false）回调。
    /// 建议由宿主 tick 周期调用（如每 100~500ms）。
    /// </summary>
    /// <returns>本次清理的超时调用数。</returns>
    public int SweepExpired(DateTime now)
    {
        int expired = 0;
        // 安全修复：先快照 key 集合，避免 foreach + TryRemove 抛异常
        foreach (var key in pending.Keys.ToArray())
        {
            if (!pending.TryGetValue(key, out var pc))
            {
                continue;
            }
            if (pc.DeadlineUtc > now)
            {
                continue;
            }
            if (!pending.TryRemove(key, out _))
            {
                continue;
            }

            expired++;
            try
            {
                pc.Callback?.Invoke(false, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"EntityCall 超时回调异常 CallId:{pc.CallId} Method:{pc.MethodName}");
            }
        }
        return expired;
    }

    /// <summary>当前待回执调用数（含已超时未清理项）。</summary>
    public int PendingCount => pending.Count;
}

/// <summary>
/// EntityCallHub 全局注册表（向后兼容层）。
/// 各节点启动时应通过 <see cref="RegisterDefault"/> 注册自己的实例，
/// 之后 EntityCall/EntityMailbox 的静态 API 会自动委派到该实例。
/// 未注册时退回进程内默认实例（仅用于单元测试）。
/// </summary>
public static class EntityCallHubRegistry
{
    private static EntityCallHub? _default;

    /// <summary>当前节点注册的默认 hub。设置后所有静态 API 都走它。</summary>
    public static EntityCallHub Default => _default ??= new EntityCallHub("Default");

    /// <summary>注册当前节点的 hub（启动时调用一次）。</summary>
    public static void RegisterDefault(EntityCallHub hub)
    {
        if (hub == null) throw new ArgumentNullException(nameof(hub));
        _default = hub;
    }
}

/// <summary>
/// 兼容层：将原静态 API 委派到 <see cref="EntityCallHubRegistry.Default"/>。
/// 推荐新代码直接使用实例 API（创建 EntityCallHub 实例并通过 RegisterDefault 注册）。
/// </summary>
[Obsolete("EntityCallHub 静态 API 已废弃，请使用实例化 EntityCallHub 并通过 EntityCallHubRegistry.RegisterDefault 注册", false)]
public static class EntityCallHubCompat
{
    /// <summary>分配下一个调用 ID（线程安全）。</summary>
    public static long NextCallId() => EntityCallHubRegistry.Default.NextCallId();

    /// <summary>注册待回执调用。</summary>
    public static void Register(long callId, EntityCallHub.PendingCall item)
        => EntityCallHubRegistry.Default.Register(callId, item);

    /// <summary>处理远程调用回执：按 CallId 匹配待回执项并完成回调。</summary>
    public static bool HandleResult(Framework.Protocol.Generated.EntityRemoteCallResult result)
        => EntityCallHubRegistry.Default.HandleResult(result);

    /// <summary>超时判定。</summary>
    public static int SweepExpired(DateTime now) => EntityCallHubRegistry.Default.SweepExpired(now);

    /// <summary>当前待回执调用数。</summary>
    public static int PendingCount => EntityCallHubRegistry.Default.PendingCount;
}
