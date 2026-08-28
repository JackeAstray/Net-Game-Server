using System.Security.Cryptography;
using System.Text;

namespace Framework.Core.Security;

/// <summary>
/// 无状态 Token 服务（对标 KBE 的登录票据体系）。
/// Token = base64url(payload).base64url(hmac-sha256(payload))
/// payload 内含 userId、uid、SessionSeq（单调序号，D6 防重放）、签发时间、过期时间，
/// 服务端不存 token 状态即可验证；seq 单调性由 <see cref="SessionGuard.AntiReplayState"/> 维护。
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

    /// <summary>签发 Token。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="uid">全局唯一标识（业务侧账户串）。</param>
    /// <param name="seq">单调序号（D6 防重放；通常由 <see cref="SessionGuard.AntiReplayState.IssueNextSeq"/> 产生，登录为 1，续签递增）。</param>
    /// <param name="ttl">可选有效期。</param>
    public string Issue(int userId, string uid, long seq, TimeSpan? ttl = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long expires = now + (long)(ttl ?? defaultTtl).TotalSeconds;
        string payload = $"{userId}|{uid}|{seq}|{now}|{expires}";
        string signature = Sign(payload);
        return $"{ToBase64Url(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)))}.{signature}";
    }

    /// <summary>
    /// 验证 Token。成功返回 (userId, uid, seq, expires)；失败返回 null。
    /// D6 防重放：传入 <paramref name="antiReplay"/> 时，token 中的 seq 必须严格大于该用户已接受的最大值；
    /// 重放（seq &lt;= 上次）返回 null。Token TTL 仍由签发/过期字段保障。
    /// </summary>
    /// <param name="token">客户端提交的 token。</param>
    /// <param name="antiReplay">可选的单调序号状态；为 null 时不做防重放校验（仅 TTL 校验）。</param>
    public (int UserId, string Uid, long Seq, long Expires)? Verify(string? token, SessionGuard.AntiReplayState? antiReplay = null)
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
        if (fields.Length != 5)
        {
            return null;
        }

        if (!int.TryParse(fields[0], out int userId) ||
            !long.TryParse(fields[2], out long seq) ||
            !long.TryParse(fields[3], out long issuedAt) ||
            !long.TryParse(fields[4], out long expires))
        {
            return null;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < issuedAt - maxClockSkewSeconds || now > expires)
        {
            return null; // 未到生效时间（时钟偏移）或已过期
        }

        // D6 单调序号防重放：seq 必须严格递增（首次任意正数）
        if (antiReplay != null && !antiReplay.TryAcceptSeq(userId, seq))
        {
            return null; // 重放旧 token
        }

        return (userId, fields[1], seq, expires);
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
