using System.Security.Cryptography;
using System.Text;

namespace Framework.Core.Security;

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
