namespace Game.Handlers;

/// <summary>
/// DB 请求 ID 全局共享序列（P2 修复：原 Friend/Guild 各自以 DateTime.UtcNow.Ticks 起始、
/// Interlocked.Increment 独立递增，值域完全重叠，一旦响应 MsgId 区间复用即误匹配他人请求）。
/// </summary>
internal static class DbRequestIds
{
    private static long seed = DateTime.UtcNow.Ticks;

    public static long Next() => Interlocked.Increment(ref seed);
}
