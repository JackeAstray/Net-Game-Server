using Center.Handlers;

namespace Center;

/// <summary>
/// 节点运行指标聚合（尽力而为）：从注册节点快照取 host:业务端口，抓取健康端口
/// （业务端口+10000）的 /metrics，解析进程级指标供管理台展示。
/// 注意：依赖节点 HealthListenAddress 可达（默认回环=仅同机；Docker/K8s 设 0.0.0.0）。
/// 拉取失败标记 reachable=false，不致命。
/// </summary>
public static class NodeMetricsService
{
    private sealed class NodeMetric
    {
        public string NodeId = string.Empty;
        public string NodeType = string.Empty;
        public double UptimeSeconds;
        public double MemoryBytes;
        public int Threads;
        public bool Reachable;
    }

    private static readonly object gate = new();
    private static List<NodeMetric>? cache;
    private static long cacheTimeTicks;
    /// <summary>进行中的抓取任务（P2 修复：缓存过期瞬间只允许一个请求刷新，其余等待同一任务，防 cache stampede）。</summary>
    private static Task<object>? inflight;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static Task<object> GetAsync()
    {
        lock (gate)
        {
            if (cache != null && Environment.TickCount64 - cacheTimeTicks < (long)CacheTtl.TotalMilliseconds)
            {
                return Task.FromResult(Build(cache));
            }
            if (inflight != null)
            {
                return inflight;
            }
            inflight = RefreshAsync();
            return inflight;
        }
    }

    private static async Task<object> RefreshAsync()
    {
        try
        {
            var list = await ScrapeAsync();
            lock (gate)
            {
                cache = list;
                cacheTimeTicks = Environment.TickCount64;
            }
            return Build(list);
        }
        finally
        {
            lock (gate)
            {
                inflight = null;
            }
        }
    }

    private static async Task<List<NodeMetric>> ScrapeAsync()
    {
        var nodes = NodeManager.Instance.GetNodeSnapshots();
        var tasks = nodes.Select(async n =>
        {
            var m = new NodeMetric { NodeId = n.NodeId, NodeType = n.NodeType };
            try
            {
                int healthPort = n.Port + 10000;
                using var resp = await http.GetAsync($"http://{n.Host}:{healthPort}/metrics");
                if (resp.IsSuccessStatusCode)
                {
                    ParseMetrics(await resp.Content.ReadAsStringAsync(), m);
                    m.Reachable = true;
                }
            }
            catch
            {
                // 不可达（health 监听回环/节点未就绪）：保持 reachable=false
            }
            return m;
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>解析 Prometheus 文本格式的关键进程指标（行首匹配，无正则依赖）。</summary>
    private static void ParseMetrics(string text, NodeMetric m)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("netgame_process_uptime_seconds ", StringComparison.Ordinal))
            {
                double.TryParse(t.AsSpan("netgame_process_uptime_seconds ".Length), out m.UptimeSeconds);
            }
            else if (t.StartsWith("netgame_process_managed_memory_bytes ", StringComparison.Ordinal))
            {
                double.TryParse(t.AsSpan("netgame_process_managed_memory_bytes ".Length), out m.MemoryBytes);
            }
            else if (t.StartsWith("netgame_process_threads ", StringComparison.Ordinal))
            {
                int.TryParse(t.AsSpan("netgame_process_threads ".Length), out m.Threads);
            }
        }
    }

    private static object Build(List<NodeMetric> list) => list.Select(m => new
    {
        nodeId = m.NodeId,
        nodeType = m.NodeType,
        reachable = m.Reachable,
        uptimeSeconds = m.UptimeSeconds,
        memoryBytes = m.MemoryBytes,
        threads = m.Threads
    }).ToArray();
}
