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
/// </summary>
public static class EntityCallHub
{
    private static long callIdSeed = DateTime.UtcNow.Ticks;

    /// <summary>待回执调用表：CallId -> PendingCall。</summary>
    private static readonly ConcurrentDictionary<long, PendingCall> pending = new();

    /// <summary>一次待回执的远程调用。</summary>
    public sealed class PendingCall
    {
        public long CallId { get; init; }
        public string? TargetNodeId { get; init; }
        public string? MethodName { get; init; }
        public DateTime DeadlineUtc { get; set; }
        /// <summary>回执回调：(Success, ResultValue)。超时或失败时 Success=false。</summary>
        public Action<bool, object?> Callback { get; init; } = static (_, _) => { };
    }

    /// <summary>分配下一个调用 ID（线程安全）。</summary>
    public static long NextCallId()
    {
        return System.Threading.Interlocked.Increment(ref callIdSeed);
    }

    /// <summary>注册待回执调用。</summary>
    public static void Register(long callId, PendingCall item)
    {
        pending[callId] = item;
    }

    /// <summary>
    /// 处理远程调用回执：按 CallId 匹配待回执项并完成回调。
    /// 返回 true 表示匹配并消费了该回执；无匹配（重复/过期/未知）返回 false。
    /// </summary>
    public static bool HandleResult(Framework.Protocol.Generated.EntityRemoteCallResult result)
    {
        if (!pending.TryRemove(result.CallId, out var pc))
        {
            return false;
        }

        object? value = null;
        if (result.Success && result.Result.Length > 0)
        {
            object?[] args = ArgCodec.Deserialize(result.Result);
            value = args.Length > 0 ? args[0] : null;
        }

        try
        {
            pc.Callback(result.Success, value);
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
    public static int SweepExpired(DateTime now)
    {
        int expired = 0;
        foreach (var pair in pending)
        {
            if (pair.Value.DeadlineUtc > now)
            {
                continue;
            }

            if (!pending.TryRemove(pair.Key, out var pc))
            {
                continue;
            }

            expired++;
            try
            {
                pc.Callback(false, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"EntityCall 超时回调异常 CallId:{pc.CallId} Method:{pc.MethodName}");
            }
        }
        return expired;
    }

    /// <summary>当前待回执调用数（含已超时未清理项）。</summary>
    public static int PendingCount => pending.Count;
}
