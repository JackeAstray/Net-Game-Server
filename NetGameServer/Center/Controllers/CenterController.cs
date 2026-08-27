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
}