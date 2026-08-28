using System.Security.Cryptography;
using System.Text;

namespace Framework.Core.Security;

/// <summary>
/// 共享密钥读取工具（防御未配置或使用默认 fallback 密钥）：
/// 用于 InternalAuthFilter / TokenService / CenterSignature 等场景。
/// 缺失或仍使用占位符密钥时强制 panic，提示运维配置。
/// </summary>
public static class SecretConfig
{
    /// <summary>已知的占位符/默认密钥集合（黑名单）。</summary>
    private static readonly string[] PlaceholderSecrets = new[]
    {
        "change-this-secret",
        "change-me",
        "default",
        "secret",
        ""
    };

    /// <summary>
    /// 读取必需的共享密钥：缺失、为空或命中占位符时抛异常。
    /// </summary>
    /// <param name="configKey">配置键名（如 "CenterNodeSharedSecret"）</param>
    /// <param name="minLength">最小长度（建议 ≥ 16 字节）</param>
    public static string Require(string configKey, int minLength = 16)
    {
        var value = Config.Get(configKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"未配置 {configKey}：服务间 HMAC 共享密钥必须显式提供，禁止硬编码默认值。" +
                "请在 appsettings.json 或环境变量中设置。");
        }
        if (value.Length < minLength)
        {
            throw new InvalidOperationException(
                $"{configKey} 长度过短（{value.Length} < {minLength}）：共享密钥至少 {minLength} 字节。");
        }
        // 测试模式：允许占位符密钥（仅由集成测试启动时显式开启）
        if (AllowPlaceholderSecrets)
        {
            return value;
        }
        foreach (var placeholder in PlaceholderSecrets)
        {
            if (string.Equals(value, placeholder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{configKey} 不能使用占位符密钥 '{placeholder}'。请配置为强随机密钥。" +
                    "（测试场景可调用 SecretConfig.AllowPlaceholderSecretsInTests() 临时绕过。）");
            }
        }
        return value;
    }

    /// <summary>
    /// 允许占位符密钥的开关。仅供集成测试/开发模式使用；
    /// 生产代码必须保持 false。
    /// </summary>
    public static bool AllowPlaceholderSecrets { get; private set; }

    /// <summary>测试启动入口：允许占位符密钥。仅应在测试 setup/teardown 调用。</summary>
    public static void AllowPlaceholderSecretsInTests() => AllowPlaceholderSecrets = true;

    /// <summary>恢复为禁止占位符（测试清理用）。</summary>
    public static void ResetForTests() => AllowPlaceholderSecrets = false;

    /// <summary>
    /// 读取共享密钥但允许 fallback（仅测试场景使用）。
    /// 生产代码必须使用 <see cref="Require"/>。
    /// </summary>
    public static string GetOrRandom(string configKey, int randomBytes = 32)
    {
        var value = Config.Get(configKey);
        if (!string.IsNullOrWhiteSpace(value) &&
            !Array.Exists(PlaceholderSecrets, p => p == value) &&
            value.Length >= 16)
        {
            return value;
        }

        // 未配置/使用占位符：随机生成一个新密钥，进程重启后旧 token 失效（已有注释保证安全性）
        var rng = new byte[randomBytes];
        RandomNumberGenerator.Fill(rng);
        var generated = Convert.ToBase64String(rng);
        Log.Warn($"未配置或使用了不安全占位符的 {configKey}：自动生成临时密钥（重启失效）。生产环境请显式配置。");
        return generated;
    }
}

/// <summary>
/// 内部连接认证：服务间 TCP 连接建立后，客户端（如 Gateway）必须先发送
/// 带 HMAC 签名的认证握手，服务端验证通过后才处理业务消息。
/// 解决"内部端口无认证，可绕过网关伪造身份"的安全漏洞。
/// </summary>
public sealed class InternalAuthFilter
{
    /// <summary>认证握手消息 ID（与 Protocol/defs/Center.def 的 InternalAuth 一致）</summary>
    public const int AuthMsgId = 90999;

    /// <summary>时钟偏移容忍（秒）</summary>
    private const int MaxClockSkewSeconds = 120;

    private readonly byte[] key;
    private readonly string nodeId;

    /// <summary>当前连接是否已通过认证</summary>
    public bool IsAuthenticated { get; private set; }

    public InternalAuthFilter(string sharedSecret, string nodeId)
    {
        key = Encoding.UTF8.GetBytes(sharedSecret);
        this.nodeId = nodeId;
    }

    /// <summary>生成认证握手包（[MsgId(4)][payload]），连接建立后立即发送。</summary>
    public byte[] BuildAuthPacket()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature($"{nodeId}|{timestamp}");
        string payload = $"{nodeId}|{timestamp}|{signature}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] packet = new byte[4 + payloadBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), AuthMsgId);
        payloadBytes.CopyTo(packet.AsSpan(4));
        return packet;
    }

    /// <summary>
    /// 验证收到的认证握手负载。成功则标记连接已认证。
    /// 负载格式：nodeId|timestamp|signature
    /// </summary>
    public bool TryAuthenticate(ReadOnlySpan<byte> payload)
    {
        if (IsAuthenticated) return true;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 3)
        {
            return false;
        }

        string remoteNodeId = parts[0];
        if (!long.TryParse(parts[1], out long timestamp))
        {
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > MaxClockSkewSeconds)
        {
            return false; // 时间戳过期或提前
        }

        string expected = ComputeSignature($"{remoteNodeId}|{timestamp}");
        byte[] a = Encoding.UTF8.GetBytes(parts[2]);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return false;
        }

        IsAuthenticated = true;
        return true;
    }

    /// <summary>负载是否是认证握手。</summary>
    public static bool IsAuthMessage(int msgId) => msgId == AuthMsgId;

    private string ComputeSignature(string source)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(source)));
    }
}
