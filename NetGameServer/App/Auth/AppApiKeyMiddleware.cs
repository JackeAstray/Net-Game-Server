using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace App.Auth;

/// <summary>
/// 应用节点 HTTP 面 API Key 鉴权中间件（对齐 Login/Center 管理面防护）：
/// - 配置项 <c>HttpApiKeys</c> 列出允许的 API Key（每行一个/逗号分隔）
/// - 客户端必须在请求头 <c>X-Api-Key</c> 中提供有效 Key，否则 401
/// - <see cref="AppApiKeyOptions.AllowAnonymousPaths"/> 配置的路径匿名访问（精确匹配）
/// - fail-closed：未配置任何 Key 时除匿名路径外全部拒绝
/// - 有效 Key 按分钟限流，防止泄漏或被攻破的 Key 洪泛管理面
/// </summary>
public sealed class AppApiKeyMiddleware
{
    private readonly RequestDelegate next;
    private readonly AppApiKeyOptions options;
    private static readonly long RateWindowTicks = TimeSpan.FromMinutes(1).Ticks;
    private const int MaxRequestsPerMinute = 120;

    private sealed class RateBucket
    {
        public long WindowStartTicks;
        public int Count;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RateBucket> rateBuckets = new();

    public AppApiKeyMiddleware(RequestDelegate next, AppApiKeyOptions? options = null)
    {
        this.next = next;
        this.options = options ?? new AppApiKeyOptions();
    }

    private bool TryConsumeRate(string key)
    {
        var bucket = rateBuckets.GetOrAdd(key, _ => new RateBucket { WindowStartTicks = DateTime.UtcNow.Ticks });
        long nowTicks = DateTime.UtcNow.Ticks;
        long window = Volatile.Read(ref bucket.WindowStartTicks);
        if (nowTicks - window >= RateWindowTicks &&
            Interlocked.CompareExchange(ref bucket.WindowStartTicks, nowTicks, window) == window)
        {
            Volatile.Write(ref bucket.Count, 0);
        }
        return Interlocked.Increment(ref bucket.Count) <= MaxRequestsPerMinute;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 精确匹配，避免匿名父路径意外放行未来新增的受保护子路由
        foreach (var allowed in options.AllowAnonymousPaths)
        {
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase))
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
        string? matchedKey = null;
        foreach (var configured in options.Keys)
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
            return context.Response.WriteAsync("{\"success\":false,\"error\":\"X-Api-Key 无效\"}");
        }

        if (!TryConsumeRate(matchedKey))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync("{\"success\":false,\"error\":\"请求过于频繁，请稍后重试\"}");
        }

        return next(context);
    }
}

/// <summary>应用节点 API Key 中间件配置。</summary>
public sealed class AppApiKeyOptions
{
    /// <summary>允许的 API Key 列表（任一匹配即通过）。</summary>
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();

    /// <summary>无需 Key 即可访问的路径列表（精确匹配）。</summary>
    public IReadOnlyList<string> AllowAnonymousPaths { get; set; } = Array.Empty<string>();
}
