using System;
using Microsoft.AspNetCore.Mvc;

namespace App.Controllers;

/// <summary>
/// 应用节点 HTTP 面最小示例控制器（REST 应用服务器能力演示）。
/// 公开端点：GET /api/app/info、GET /api/app/ping（无 Key 可访问）；
/// 其余端点需要 X-Api-Key（见 AppApiKeyMiddleware，配置键 HttpApiKeys）。
/// </summary>
[ApiController]
[Route("api/app")]
public sealed class AppInfoController : ControllerBase
{
    private static readonly string StartedAtUtc = DateTime.UtcNow.ToString("O");

    /// <summary>节点信息（启动时间/节点标识/运行时）。</summary>
    [HttpGet("info")]
    public IActionResult GetInfo() => Ok(new
    {
        success = true,
        node = "app",
        startedAtUtc = StartedAtUtc,
        runtime = Environment.Version.ToString(),
        message = "应用服务器（AppServer）HTTP 面工作正常——与游戏节点共享 Center 注册与内部认证基建。"
    });

    /// <summary>存活探测（应用面 /health 之外的轻量探测）。</summary>
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { success = true, pong = true, utc = DateTime.UtcNow.ToString("O") });

    /// <summary>回显（受 Key 保护；演示应用面可调用方鉴权）。</summary>
    [HttpGet("echo")]
    public IActionResult Echo([FromQuery] string? text) => Ok(new { success = true, echo = string.IsNullOrEmpty(text) ? "(empty)" : text });
}
