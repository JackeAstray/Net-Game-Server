namespace Center;

/// <summary>
/// 配置变更历史（版本化 + 回滚）：记录每次 SetConfig/DeleteConfig 的前后值，
/// 支持回滚到指定版本（撤销其后全部变更）。内存环形缓冲，仅服务运行期有效。
/// </summary>
public static class ConfigHistory
{
    private sealed class Entry
    {
        public required int Version;
        public required string Key;
        public string? ValueBefore;
        public string? ValueAfter;
        public required string Op; // set | delete | rollback
        public required DateTime TimeUtc;
    }

    private const int MaxEntries = 100;
    private static readonly List<Entry> entries = new();
    private static readonly object gate = new();
    private static int nextVersion = 1;

    /// <summary>记录一次已生效的配置变更，返回版本号。</summary>
    public static int Record(string key, string? valueBefore, string? valueAfter, string op)
    {
        lock (gate)
        {
            entries.Add(new Entry
            {
                Version = nextVersion,
                Key = key,
                ValueBefore = valueBefore,
                ValueAfter = valueAfter,
                Op = op,
                TimeUtc = DateTime.UtcNow
            });
            if (entries.Count > MaxEntries) entries.RemoveAt(0);
            return nextVersion++;
        }
    }

    /// <summary>历史快照（新→旧）。</summary>
    public static object Snapshot()
    {
        lock (gate)
        {
            return entries.AsEnumerable().Reverse().Select(e => new
            {
                version = e.Version,
                key = e.Key,
                valueBefore = e.ValueBefore,
                valueAfter = e.ValueAfter,
                op = e.Op,
                timeUtc = e.TimeUtc.ToString("O")
            }).ToArray();
        }
    }

    /// <summary>
    /// 回滚到目标版本：撤销所有 version &gt; target 的变更（倒序应用各自旧值），并记录一条 rollback。
    /// <paramref name="apply"/> 负责把 (key, valueBefore) 应用到运行时与持久化。
    /// 返回撤销的变更条数。
    /// </summary>
    public static int RollbackTo(int targetVersion, Action<string, string?> apply)
    {
        lock (gate)
        {
            var toUndo = entries
                .Where(e => e.Version > targetVersion && e.Op != "rollback")
                .OrderByDescending(e => e.Version)
                .ToList();
            foreach (var e in toUndo)
            {
                apply(e.Key, e.ValueBefore);
            }
            if (toUndo.Count > 0)
            {
                entries.Add(new Entry
                {
                    Version = nextVersion,
                    Key = "<rollback>",
                    Op = "rollback",
                    TimeUtc = DateTime.UtcNow
                });
                if (entries.Count > MaxEntries) entries.RemoveAt(0);
                nextVersion++;
            }
            return toUndo.Count;
        }
    }
}
