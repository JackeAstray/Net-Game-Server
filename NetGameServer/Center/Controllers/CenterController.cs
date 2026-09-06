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
        Shared.ConfigHelper.SetRuntimeOverride(key, item.Value);
        var overrides = RuntimeConfigStore.Load();
        if (item.Value == null)
        {
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = item.Value;
        }
        RuntimeConfigStore.Save(overrides);
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
        Shared.ConfigHelper.SetRuntimeOverride(trimmed, null);
        var overrides = RuntimeConfigStore.Load();
        overrides.Remove(trimmed);
        RuntimeConfigStore.Save(overrides);
        return Ok(new { success = true, key = trimmed });
    }

    public sealed class ConfigItem
    {
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}