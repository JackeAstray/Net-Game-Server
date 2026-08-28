using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Login;

/// <summary>
/// 简单 API Key 鉴权中间件（对标 KBE logger/center 管理端口保护）：
/// - 配置项 <c>HttpApiKeys</c> 列出允许的 API Key（每行一个/逗号分隔）
/// - 客户端必须在请求头 <c>X-Api-Key</c> 中提供有效 Key
/// - 无 Key 或 Key 不匹配返回 401
/// - 排除路径：<see cref="ApiKeyAuthOptions.AllowAnonymousPaths"/> 配置的路径匿名访问
/// 使用：
///   app.UseMiddleware&lt;ApiKeyAuthMiddleware&gt;(new ApiKeyAuthOptions { ... });
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate next;
    private readonly ApiKeyAuthOptions options;

    public ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyAuthOptions? options = null)
    {
        this.next = next;
        this.options = options ?? new ApiKeyAuthOptions();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 排除路径
        foreach (var allowed in options.AllowAnonymousPaths)
        {
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }
        }

        // Swagger
        if ((path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/swagger", StringComparison.OrdinalIgnoreCase)) &&
            options.AllowAnonymousSwagger)
        {
            return next(context);
        }

        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(
                "{\"success\":false,\"error\":\"缺少 X-Api-Key 请求头\"}");
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
            return context.Response.WriteAsync(
                "{\"success\":false,\"error\":\"X-Api-Key 无效\"}");
        }

        return next(context);
    }
}

/// <summary>API Key 中间件配置。</summary>
public sealed class ApiKeyAuthOptions
{
    /// <summary>允许的 API Key 列表（任一匹配即通过）。</summary>
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();

    /// <summary>无需 Key 即可访问的路径前缀列表（精确或前缀匹配）。</summary>
    public IReadOnlyList<string> AllowAnonymousPaths { get; set; } = Array.Empty<string>();

    /// <summary>是否允许匿名访问 Swagger UI。生产环境建议 false。</summary>
    public bool AllowAnonymousSwagger { get; set; } = false;
}
