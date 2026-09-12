using Center.Handlers;

namespace Center;

/// <summary>
/// Center 节点趋势采样器：维护循环每心跳周期采样一次全局状态（节点数/在线数/负载/房间数），
/// 供管理台 /api/center/metrics-trend 画趋势图。内存环形缓冲，无外部依赖。
/// </summary>
public static class MetricsSampler
{
    private sealed class TrendPoint
    {
        public required DateTime TimeUtc;
        public int NodeCount;
        public int ConnectedCount;
        public int TotalLoad;
        public int RoomCount;
    }

    /// <summary>保留样本数（默认心跳 10s × 180 = 30 分钟窗口）。</summary>
    private const int MaxSamples = 180;

    private static readonly List<TrendPoint> samples = new();
    private static readonly object gate = new();

    /// <summary>由 Center 维护循环周期调用。</summary>
    public static void Sample(DateTime nowUtc)
    {
        var nodes = NodeManager.Instance.GetNodeSnapshots();
        var rooms = CenterServerApp.Match?.GetRoomsSnapshot();

        lock (gate)
        {
            samples.Add(new TrendPoint
            {
                TimeUtc = nowUtc,
                NodeCount = nodes.Count,
                ConnectedCount = nodes.Count(n => n.IsConnected),
                TotalLoad = nodes.Sum(n => n.CurrentLoad),
                RoomCount = rooms?.Count ?? 0
            });
            if (samples.Count > MaxSamples)
            {
                samples.RemoveAt(0);
            }
        }
    }

    /// <summary>返回最近样本（时间升序，最新在后），供管理台渲染。</summary>
    public static object GetTrend()
    {
        lock (gate)
        {
            return samples.Select(s => new
            {
                timeUtc = s.TimeUtc.ToString("O"),
                nodeCount = s.NodeCount,
                connectedCount = s.ConnectedCount,
                totalLoad = s.TotalLoad,
                roomCount = s.RoomCount
            }).ToArray();
        }
    }
}
