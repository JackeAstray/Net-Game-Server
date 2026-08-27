using System.Security.Cryptography;
using System.Text;

namespace Framework.Core.Security;

/// <summary>
/// 无状态 Token 服务（对标 KBE 的登录票据体系）。
/// Token = base64url(payload).base64url(hmac-sha256(payload))
/// payload 内含 userId、uid、签发时间、过期时间，服务端不存状态即可验证。
/// </summary>
public sealed class TokenService
{
    private readonly byte[] key;
    private readonly TimeSpan defaultTtl;
    private readonly int maxClockSkewSeconds;

    /// <param name="secret">HMAC 密钥（生产环境从配置读取，禁止默认值上线）</param>
    /// <param name="defaultTtl">Token 默认有效期</param>
    /// <param name="maxClockSkewSeconds">允许的时钟偏移（秒）</param>
    public TokenService(string secret, TimeSpan? defaultTtl = null, int maxClockSkewSeconds = 60)
    {
        key = Encoding.UTF8.GetBytes(secret);
        this.defaultTtl = defaultTtl ?? TimeSpan.FromHours(4);
        this.maxClockSkewSeconds = maxClockSkewSeconds;
    }

    /// <summary>签发 Token</summary>
    public string Issue(int userId, string uid, TimeSpan? ttl = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long expires = now + (long)(ttl ?? defaultTtl).TotalSeconds;
        string payload = $"{userId}|{uid}|{now}|{expires}";
        string signature = Sign(payload);
        return $"{ToBase64Url(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)))}.{signature}";
    }

    /// <summary>
    /// 验证 Token。成功返回 (userId, uid, expires)；失败返回 null。
    /// 防重放说明：无状态 token 本身无法防重放，业务层应对高频敏感操作附加一次性 nonce（见 NonceService）。
    /// </summary>
    public (int UserId, string Uid, long Expires)? Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        string payload;
        try
        {
            payload = FromBase64Url(parts[0]);
        }
        catch (FormatException)
        {
            return null;
        }

        // 恒定时间比较签名
        string expected = Sign(payload);
        byte[] a = Encoding.UTF8.GetBytes(parts[1]);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return null;
        }

        var fields = payload.Split('|');
        if (fields.Length != 4)
        {
            return null;
        }

        if (!int.TryParse(fields[0], out int userId) ||
            !long.TryParse(fields[2], out long issuedAt) ||
            !long.TryParse(fields[3], out long expires))
        {
            return null;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < issuedAt - maxClockSkewSeconds || now > expires)
        {
            return null; // 未到生效时间（时钟偏移）或已过期
        }

        return (userId, fields[1], expires);
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(key);
        return ToBase64Url(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))));
    }

    private static string ToBase64Url(string base64) =>
        base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        int padding = b64.Length % 4;
        if (padding == 2) b64 += "==";
        else if (padding == 3) b64 += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }
}
