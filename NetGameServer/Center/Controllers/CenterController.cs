using Microsoft.AspNetCore.Mvc;
using Center.Handlers;

namespace Center.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CenterController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "ok",
            isLeader = CenterServerApp.IsLeader,
            nodeCount = NodeManager.Instance.GetNodeCount(),
            timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("nodes")]
    public IActionResult Nodes()
    {
        return Ok(NodeManager.Instance.GetNodeSnapshots());
    }

    [HttpGet("summary")]
    public IActionResult Summary()
    {
        var nodes = NodeManager.Instance.GetNodeSnapshots();
        return Ok(new
        {
            total = nodes.Count,
            battle = nodes.Count(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase)),
            game = nodes.Count(n => n.NodeType.Equals("Game", StringComparison.OrdinalIgnoreCase)),
            gateway = nodes.Count(n => n.NodeType.Equals("Gateway", StringComparison.OrdinalIgnoreCase)),
            login = nodes.Count(n => n.NodeType.Equals("Login", StringComparison.OrdinalIgnoreCase)),
            timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("rooms")]
    public IActionResult Rooms()
    {
        var rooms = CenterServerApp.Match?.GetRoomsSnapshot() ?? Array.Empty<Shared.Messages.Center.RoomInfo>();
        return Ok(rooms);
    }

    /// <summary>节点趋势采样（维护循环每心跳周期写入，管理台画趋势图）。</summary>
    [HttpGet("metrics-trend")]
    public IActionResult MetricsTrend()
    {
        return Ok(MetricsSampler.GetTrend());
    }

    /// <summary>节点运行指标聚合（尽力而为拉取各节点 /metrics，10s 缓存；health 端口默认回环时不可达）。</summary>
    [HttpGet("node-metrics")]
    public async Task<IActionResult> NodeMetrics()
    {
        return Ok(await NodeMetricsService.GetAsync());
    }

    /// <summary>
    /// 按机器聚合的节点列表（KBE machine 化，迭代 20）：
    /// 从 NodeManager 节点注册表读，按 MachineId 分组；空 MachineId 归到 "unassigned" 组。
    /// 用于管理台"机器/进程总览"页 + 运维侧脚本拉取。
    /// </summary>
    [HttpGet("cluster")]
    public IActionResult Cluster()
    {
        var snapshots = NodeManager.Instance.GetNodeSnapshots();
        var grouped = snapshots
            .GroupBy(n => string.IsNullOrEmpty(n.MachineId) ? "unassigned" : n.MachineId)
            .Select(g => new
            {
                machineId = g.Key,
                supervisedBy = g.First().SupervisedBy,
                totalNodes = g.Count(),
                battle = g.Count(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase)),
                game = g.Count(n => n.NodeType.Equals("Game", StringComparison.OrdinalIgnoreCase)),
                gateway = g.Count(n => n.NodeType.Equals("Gateway", StringComparison.OrdinalIgnoreCase)),
                login = g.Count(n => n.NodeType.Equals("Login", StringComparison.OrdinalIgnoreCase)),
                db = g.Count(n => n.NodeType.Equals("DB", StringComparison.OrdinalIgnoreCase)),
                center = g.Count(n => n.NodeType.Equals("Center", StringComparison.OrdinalIgnoreCase)),
                online = g.Count(n => n.IsConnected),
                nodes = g.Select(n => new
                {
                    nodeId = n.NodeId,
                    instanceId = n.InstanceId,
                    nodeType = n.NodeType,
                    host = n.Host,
                    port = n.Port,
                    currentLoad = n.CurrentLoad,
                    isConnected = n.IsConnected,
                    lastHeartbeat = n.LastHeartbeat
                }).ToArray()
            })
            .OrderBy(g => g.machineId)
            .ToArray();

        return Ok(new
        {
            timestamp = DateTime.UtcNow,
            total = snapshots.Count,
            machines = grouped
        });
    }

    // ===== 配置中心（B4）：运行时覆盖的读写与持久化（经 ConfigHelper.SetRuntimeOverride 立即热更） =====

    /// <summary>禁止运行时热更的敏感配置键特征（共享密钥/连接串/密码等，大小写不敏感）。</summary>
    private static readonly string[] SensitiveConfigPatterns = new[]
    {
        "secret", "password", "connectionstrings", "apikey", "token", "privatekey"
    };

    /// <summary>是否敏感配置键：禁止经管理接口热更（防持 key 方覆盖认证/存储凭据）。</summary>
    private static bool IsSensitiveConfigKey(string key)
    {
        string k = key.ToLowerInvariant();
        foreach (var p in SensitiveConfigPatterns)
        {
            if (k.Contains(p, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [HttpGet("config")]
    public IActionResult Config()
    {
        var overrides = RuntimeConfigStore.Load();
        return Ok(new { count = overrides.Count, overrides });
    }

    [HttpPost("config")]
    public IActionResult SetConfig([FromBody] ConfigItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Key))
        {
            return BadRequest(new { success = false, message = "key 不能为空" });
        }
        string key = item.Key.Trim();
        // P2 修复：敏感键（认证/连接凭据）禁止运行时热更，防管理面被利用篡改
        if (IsSensitiveConfigKey(key))
        {
            return BadRequest(new { success = false, message = "敏感配置键不允许运行时修改" });
        }
        Shared.ConfigHelper.SetRuntimeOverride(key, item.Value);
        var overrides = RuntimeConfigStore.Load();
        string? before = overrides.TryGetValue(key, out var old) ? old : null;
        if (item.Value == null)
        {
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = item.Value;
        }
        RuntimeConfigStore.Save(overrides);
        ConfigHistory.Record(key, before, item.Value, "set");
        return Ok(new { success = true, key, value = item.Value });
    }

    [HttpDelete("config/{key}")]
    public IActionResult DeleteConfig(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new { success = false, message = "key 不能为空" });
        }
        string trimmed = key.Trim();
        // P2 修复：敏感键禁止删除（防恢复被覆盖前的恶意还原）
        if (IsSensitiveConfigKey(trimmed))
        {
            return BadRequest(new { success = false, message = "敏感配置键不允许运行时删除" });
        }
        var overrides = RuntimeConfigStore.Load();
        string? before = overrides.TryGetValue(trimmed, out var old) ? old : null;
        Shared.ConfigHelper.SetRuntimeOverride(trimmed, null);
        overrides.Remove(trimmed);
        RuntimeConfigStore.Save(overrides);
        ConfigHistory.Record(trimmed, before, null, "delete");
        return Ok(new { success = true, key = trimmed });
    }

    /// <summary>配置变更历史（新→旧，含前后值与版本号）。</summary>
    [HttpGet("config-history")]
    public IActionResult GetConfigHistory()
    {
        return Ok(ConfigHistory.Snapshot());
    }

    /// <summary>回滚到指定版本：撤销其后全部配置变更（敏感键在写入时已被拒绝，历史中不会出现）。</summary>
    [HttpPost("config-rollback")]
    public IActionResult ConfigRollback([FromBody] RollbackRequest req)
    {
        if (req == null || req.Version < 1)
        {
            return BadRequest(new { success = false, message = "version 必须 ≥ 1" });
        }
        int undone = ConfigHistory.RollbackTo(req.Version, (key, value) =>
        {
            Shared.ConfigHelper.SetRuntimeOverride(key, value);
            var overrides = RuntimeConfigStore.Load();
            if (value == null) overrides.Remove(key);
            else overrides[key] = value;
            RuntimeConfigStore.Save(overrides);
        });
        return Ok(new { success = true, undone });
    }

    public sealed class ConfigItem
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }

    public sealed class RollbackRequest
    {
        public int Version { get; set; }
    }
}