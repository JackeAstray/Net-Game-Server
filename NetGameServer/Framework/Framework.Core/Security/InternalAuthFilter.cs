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
        var value = Resolve(configKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"未配置 {configKey}：服务间 HMAC 共享密钥必须显式提供，禁止硬编码默认值。" +
                "请在 appsettings.json（Security:{configKey}）或环境变量 {configKey} 中设置（≥16 字符）。" +
                "单机快速启动：运行 Publish/StartServers.bat 会自动生成并注入（详见 README 快速启动）；" +
                "手动启动示例：set {configKey}=<32位以上强随机串>");
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

    /// <summary>
    /// 按优先级从多个文档化来源解析共享密钥：
    /// 1) 配置树顶层键（appsettings.json 顶层 "CenterNodeSharedSecret" 或 NG_ 前缀环境变量）；
    /// 2) appsettings.json 的 Security:{configKey} 节（README 手写配置方式）；
    /// 3) 无前缀环境变量 {configKey}（README 手动启动 / StartServers.bat / Machine 注入约定）。
    /// </summary>
    private static string? Resolve(string configKey)
    {
        var value = Config.Get(configKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Config.Get("Security:" + configKey);
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable(configKey);
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

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
        var value = Resolve(configKey);
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

    /// <summary>
    /// 防重放缓存：nodeId|nonce -> 接受时刻（Ticks）。
    /// 握手携带每连接随机 nonce，防重放以 nonce 为粒度判定：
    /// 同一 nonce 只能被接受一次（跨连接亦然），
    /// 同时允许同一节点在相同秒内建立多条合法连接（快速重连/并行链路）。
    /// 条目按 TTL 定期清理，避免无界增长。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> AcceptedHandshakes = new();
    private static readonly TimeSpan ReplayCacheTtl = TimeSpan.FromSeconds(MaxClockSkewSeconds * 2);
    private static long lastReplaySweepTicks;

    // ---- 防重放状态持久化（重启窗口修复）：----
    // 进程重启会清空 AcceptedHandshakes，攻击者可重放此前抓取的握手（时间戳仍在 120s 窗口内）。
    // 将已接受握手集合周期落盘、启动时恢复，使重启不重置重放窗口。集合受 ReplayCacheTtl 约束，文件有界。
    private static string? replayStatePath;
    private static readonly object replayPersistGate = new();
    private static long lastReplayPersistTicks;
    private static TimeSpan replayPersistInterval = TimeSpan.FromSeconds(30);

    public InternalAuthFilter(string sharedSecret, string nodeId)
    {
        key = Encoding.UTF8.GetBytes(sharedSecret);
        this.nodeId = nodeId;
    }

    /// <summary>生成认证握手包（[MsgId(4)][payload]），连接建立后立即发送。</summary>
    public byte[] BuildAuthPacket()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 每连接随机 nonce：使同一节点同秒内的多次连接不再互为"重放"
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string signature = ComputeSignature($"{nodeId}|{timestamp}|{nonce}");
        string payload = $"{nodeId}|{timestamp}|{nonce}|{signature}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] packet = new byte[4 + payloadBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), AuthMsgId);
        payloadBytes.CopyTo(packet.AsSpan(4));
        return packet;
    }

    /// <summary>
    /// 验证收到的认证握手负载。成功则标记连接已认证。
    /// 负载格式：nodeId|timestamp|nonce|signature
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
        if (parts.Length != 4)
        {
            return false;
        }

        string remoteNodeId = parts[0];
        if (!long.TryParse(parts[1], out long timestamp))
        {
            return false;
        }
        string nonce = parts[2];
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > MaxClockSkewSeconds)
        {
            return false; // 时间戳过期或提前
        }

        string expected = ComputeSignature($"{remoteNodeId}|{timestamp}|{nonce}");
        byte[] a = Encoding.UTF8.GetBytes(parts[3]);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return false;
        }

        // 防重放：同一握手（nodeId + nonce）只能被接受一次（跨连接亦然）。
        if (!AcceptedHandshakes.TryAdd($"{remoteNodeId}|{nonce}", DateTime.UtcNow.Ticks))
        {
            Framework.Core.Log.Warning($"内部认证握手重放被拒绝 NodeId:{remoteNodeId}");
            return false;
        }
        MaybeSweepReplayCache();
        MaybePersistReplayState();

        IsAuthenticated = true;
        return true;
    }

    /// <summary>
    /// 启用防重放状态持久化（各节点启动时调用一次）。
    /// 重启后恢复上一进程已接受的握手集合（仅恢复 TTL 内条目），
    /// 使"重启即重置重放窗口"不再成立。
    /// </summary>
    /// <param name="filePath">持久化文件路径（应位于本节点数据目录，进程私有）。</param>
    /// <param name="persistInterval">落盘间隔（默认 30s）。</param>
    public static void ConfigureReplayPersistence(string filePath, TimeSpan? persistInterval = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }
        replayStatePath = filePath;
        if (persistInterval.HasValue && persistInterval.Value > TimeSpan.Zero)
        {
            replayPersistInterval = persistInterval.Value;
        }

        try
        {
            string? dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            if (!System.IO.File.Exists(filePath))
            {
                return;
            }

            long cutoff = DateTime.UtcNow.Ticks - ReplayCacheTtl.Ticks;
            int restored = 0;
            foreach (var line in System.IO.File.ReadAllLines(filePath))
            {
                // 行格式：base64(nodeId|nonce)|ticks（用 base64 消除 key 内分隔符歧义）
                int sep = line.LastIndexOf('|');
                if (sep <= 0 || !long.TryParse(line.AsSpan(sep + 1), out long ticks) || ticks < cutoff)
                {
                    continue;
                }
                string key;
                try
                {
                    key = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(0, sep)));
                }
                catch
                {
                    continue;
                }
                if (AcceptedHandshakes.TryAdd(key, ticks))
                {
                    restored++;
                }
            }
            Log.Info($"已从 {filePath} 恢复防重放状态 {restored} 条（TTL 内）");
        }
        catch (Exception ex)
        {
            Log.Warn($"防重放状态恢复失败（不影响运行，仅重启后重放窗口略大）: {ex.Message}");
        }
    }

    /// <summary>按间隔把已接受握手集合原子落盘（成功认证路径上调用，节流 30s）。</summary>
    private static void MaybePersistReplayState()
    {
        string? path = replayStatePath;
        if (path == null)
        {
            return;
        }
        long now = DateTime.UtcNow.Ticks;
        if (now - System.Threading.Volatile.Read(ref lastReplayPersistTicks) < replayPersistInterval.Ticks)
        {
            return;
        }
        System.Threading.Interlocked.Exchange(ref lastReplayPersistTicks, now);

        try
        {
            lock (replayPersistGate)
            {
                var sb = new StringBuilder();
                foreach (var kv in AcceptedHandshakes)
                {
                    sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Key))).Append('|').Append(kv.Value).Append('\n');
                }
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, sb.ToString());
                System.IO.File.Move(tmp, path, true); // 原子替换，避免读到半写状态
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"防重放状态持久化失败（不影响运行）: {ex.Message}");
        }
    }

    private static void MaybeSweepReplayCache()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = System.Threading.Volatile.Read(ref lastReplaySweepTicks);
        if (now - last < ReplayCacheTtl.Ticks)
        {
            return;
        }
        if (System.Threading.Interlocked.CompareExchange(ref lastReplaySweepTicks, now, last) != last)
        {
            return;
        }

        long cutoff = now - ReplayCacheTtl.Ticks;
        foreach (var kv in AcceptedHandshakes)
        {
            if (kv.Value < cutoff)
            {
                AcceptedHandshakes.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>负载是否是认证握手。</summary>
    public static bool IsAuthMessage(int msgId) => msgId == AuthMsgId;

    private string ComputeSignature(string source)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(source)));
    }
}
