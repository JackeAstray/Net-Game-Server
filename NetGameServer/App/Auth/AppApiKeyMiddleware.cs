using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace App.Auth;

/// <summary>
/// 应用节点 HTTP 面 API Key 鉴权中间件（对齐 Login/Center 管理面防护）：
/// - 配置项 <c>HttpApiKeys</c> 列出允许的 API Key（每行一个/逗号分隔）
/// - 客户端必须在请求头 <c>X-Api-Key</c> 中提供有效 Key，否则 401
/// - <see cref="AppApiKeyOptions.AllowAnonymousPaths"/> 配置的路径匿名访问（精确或子路径前缀匹配）
/// - fail-closed：未配置任何 Key 时除匿名路径外全部拒绝
/// </summary>
public sealed class AppApiKeyMiddleware
{
    private readonly RequestDelegate next;
    private readonly AppApiKeyOptions options;

    public AppApiKeyMiddleware(RequestDelegate next, AppApiKeyOptions? options = null)
    {
        this.next = next;
        this.options = options ?? new AppApiKeyOptions();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 排除路径：精确匹配（或该路径的子路径），避免前缀匹配误放行 /api/app/infoX 这类路径
        foreach (var allowed in options.AllowAnonymousPaths)
        {
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                (path.Length > allowed.Length &&
                 path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) &&
                 path[allowed.Length] == '/'))
            {
                return next(context);
            }
        }

        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync("{\"success\":false,\"error\":\"缺少 X-Api-Key 请求头\"}");
        }

        // 恒定时间比较（防御计时攻击）
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        bool matched = false;
        foreach (var configured in options.Keys)
        {
            var configuredBytes = Encoding.UTF8.GetBytes(configured);
            if (configuredBytes.Length == providedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync("{\"success\":false,\"error\":\"X-Api-Key 无效\"}");
        }

        return next(context);
    }
}

/// <summary>应用节点 API Key 中间件配置。</summary>
public sealed class AppApiKeyOptions
{
    /// <summary>允许的 API Key 列表（任一匹配即通过）。</summary>
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();

    /// <summary>无需 Key 即可访问的路径列表（精确或子路径前缀匹配）。</summary>
    public IReadOnlyList<string> AllowAnonymousPaths { get; set; } = Array.Empty<string>();
}
