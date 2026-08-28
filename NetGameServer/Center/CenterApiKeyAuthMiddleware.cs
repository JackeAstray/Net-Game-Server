using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Center;

/// <summary>
/// Center 管理 HTTP 接口的 API Key 鉴权中间件（强鉴权，节点/房间/集群视图都是敏感信息）。
/// 与 <see cref="Login.ApiKeyAuthMiddleware"/> 行为一致，独立定义以避免跨项目共享 WebApi 程序集。
/// 配置项 <c>CenterHttpApiKeys</c>（或 fallback <c>HttpApiKeys</c>）列出允许的 API Key。
/// </summary>
public sealed class CenterApiKeyAuthMiddleware
{
    private readonly RequestDelegate next;
    private readonly IReadOnlyList<string> allowedKeys;
    private readonly IReadOnlyList<string> allowAnonymousPaths;

    public CenterApiKeyAuthMiddleware(RequestDelegate next,
        IReadOnlyList<string> allowedKeys,
        IReadOnlyList<string>? allowAnonymousPaths = null)
    {
        this.next = next;
        this.allowedKeys = allowedKeys;
        this.allowAnonymousPaths = allowAnonymousPaths ?? Array.Empty<string>();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        foreach (var allowed in allowAnonymousPaths)
        {
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }
        }

        var providedKey = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(
                "{\"success\":false,\"error\":\"缺少 X-Api-Key 请求头\"}");
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        bool matched = false;
        foreach (var configured in allowedKeys)
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
