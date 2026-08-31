using Framework.Core;
using Framework.Tick;
using Framework.Protocol.Generated;

namespace Battle.Handlers;

/// <summary>
/// 客户端-服务端时间同步（KBE-Gap-Review D7，对标 KBE time sync）：
/// - 客户端周期性发 ClientTimeSync(clientSendMs, lastServerSendMs)；
/// - 服务端回 ServerTimeSync(clientSendMs, serverRecvMs, serverSendMs, authFrame)；
/// - 客户端按经典 NTP 公式估算 RTT/offset：
///     RTT = (now - clientSendMs) - (serverSendMs - lastServerSendMs)
///     offset = ((serverRecvMs + serverSendMs) / 2) - ((clientSendMs + now) / 2)
///   多点采样取中位数更稳。
/// - 服务端只做无状态回包（不强绑定 session，便于水平扩展）。
///   鉴权帧号 authFrame 让客户端用真实权威帧号校时（与帧同步锚定）。
/// </summary>
public sealed class TimeSyncManager
{
    private readonly TickEngine tickEngine;
    private long lastSyncRequests;
    private long lastSyncResponses;
    private long lastSyncAtTicks;

    public TimeSyncManager(TickEngine tickEngine)
    {
        this.tickEngine = tickEngine;
    }

    /// <summary>累计收到的时间同步请求数（统计/Profile）。</summary>
    public long SyncRequestCount => Interlocked.Read(ref lastSyncRequests);

    /// <summary>累计回包数（用于一致性自检）。</summary>
    public long SyncResponseCount => Interlocked.Read(ref lastSyncResponses);

    /// <summary>最近一次时间同步 tick 号（Profile）。</summary>
    public long LastSyncAtTick => Interlocked.Read(ref lastSyncAtTicks);

    /// <summary>
    /// 处理 ClientTimeSync：填充服务端时间戳并回包。
    /// </summary>
    public ServerTimeSync HandleSync(ClientTimeSync req)
    {
        Interlocked.Increment(ref lastSyncRequests);
        long recv = NowMs();
        long send = NowMs();
        Interlocked.Increment(ref lastSyncResponses);
        Interlocked.Exchange(ref lastSyncAtTicks, tickEngine.CurrentFrame);
        return new ServerTimeSync
        {
            ClientSendMs = req.ClientSendMs,
            ServerRecvMs = recv,
            ServerSendMs = send,
            AuthFrame = tickEngine.CurrentFrame
        };
    }

    /// <summary>
    /// 估算时间漂移（基准为收到时戳）。客户端用此值校正显示/输入预测。
    /// 经典 NTP 公式：rtt = (t3 - t0) - (t2 - t1)，offset = ((t1 + t2) / 2) - ((t0 + t3) / 2)
    ///   t0 = clientSendMs（客户端发包）
    ///   t1 = serverRecvMs（服务端收包）
    ///   t2 = serverSendMs（服务端发包）
    ///   t3 = clientNowMs（客户端收到回包）
    /// </summary>
    public static (long RttMs, long OffsetMs) Estimate(long clientSendMs, long clientNowMs,
        long lastServerSendMs, long serverRecvMs, long serverSendMs)
    {
        long rtt = (clientNowMs - clientSendMs) - (serverSendMs - serverRecvMs);
        if (rtt < 0) rtt = 0; // 防御性：rtt 不能为负（时钟回拨/调试器暂停）
        long offset = ((serverRecvMs + serverSendMs) / 2) - ((clientSendMs + clientNowMs) / 2);
        return (rtt, offset);
    }

    private static long NowMs() => Environment.TickCount64;
}
