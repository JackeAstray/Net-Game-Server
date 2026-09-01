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

    /// <summary>每 key 请求窗口时长（1 分钟）。</summary>
    private static readonly long RateWindowTicks = TimeSpan.FromMinutes(1).Ticks;
    /// <summary>每 key 每分钟请求上限（P6 加固：防有效 key 被洪泛/滥用打爆管理面）。</summary>
    private const int MaxRequestsPerMinute = 120;

    private sealed class RateBucket
    {
        public long WindowStartTicks;
        public int Count;
    }

    /// <summary>按已匹配 key 计数（键集合有界 = 允许的 key 数量，无泄漏）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RateBucket> rateBuckets = new();

    public CenterApiKeyAuthMiddleware(RequestDelegate next,
        IReadOnlyList<string> allowedKeys,
        IReadOnlyList<string>? allowAnonymousPaths = null)
    {
        this.next = next;
        this.allowedKeys = allowedKeys;
        this.allowAnonymousPaths = allowAnonymousPaths ?? Array.Empty<string>();
    }

    private bool TryConsumeRate(string key)
    {
        var bucket = rateBuckets.GetOrAdd(key, _ => new RateBucket { WindowStartTicks = DateTime.UtcNow.Ticks });
        long nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - bucket.WindowStartTicks >= RateWindowTicks)
        {
            bucket.WindowStartTicks = nowTicks;
            bucket.Count = 0;
        }
        return Interlocked.Increment(ref bucket.Count) <= MaxRequestsPerMinute;
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
        string? matchedKey = null;
        foreach (var configured in allowedKeys)
        {
            var configuredBytes = Encoding.UTF8.GetBytes(configured);
            if (configuredBytes.Length == providedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
            {
                matchedKey = configured;
                break;
            }
        }

        if (matchedKey == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(
                "{\"success\":false,\"error\":\"X-Api-Key 无效\"}");
        }

        // P6 加固：按 key 限流（超出则 429）。管理接口为低频轮询，120/分钟充足；防止泄漏/被攻破的 key 被洪泛。
        if (!TryConsumeRate(matchedKey))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(
                "{\"success\":false,\"error\":\"请求过于频繁，请稍后重试\"}");
        }

        return next(context);
    }
}
