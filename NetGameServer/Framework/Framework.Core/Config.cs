using Microsoft.Extensions.Configuration;

namespace Framework.Core;

/// <summary>
/// 统一配置读取（appsettings.json + 环境变量覆盖），底层框架的一部分。
///
/// 前缀语义（P3 修复，与 Shared.ConfigHelper 对齐）：
/// 同时接受**无前缀**与 <c>NG_</c> 前缀环境变量，例如 <c>HealthPort=31302</c> 与 <c>NG_HealthPort=31302</c>
/// 均生效；<c>NG_</c> 前缀更具体，优先级更高。此前本类只认 <c>NG_</c> 前缀，而部署（docker-compose /
/// systemd / install-service-nssm.ps1）实际使用无前缀命名 → 同一份配置在两套读取路径下一处生效一处失效。
/// 读取优先级（低 → 高）：appsettings.json &lt; 无前缀环境变量 &lt; NG_ 环境变量。
/// 注意：本类是扁平键读取（<c>Get("A:B")</c> 用冒号分层）；需要的节缓存/运行时覆盖/校验器请用
/// 服务器侧的 <c>Shared.ConfigHelper</c>，两者对上述前缀约定保持一致。
/// </summary>
public static class Config
{
    private static IConfigurationRoot? root;

    private static IConfigurationRoot Root =>
        root ??= new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()        // 部署约定：无前缀（如 HealthPort / CenterNodeSharedSecret）
            .AddEnvironmentVariables("NG_")   // NG_HealthPort 覆盖 HealthPort（前缀更具体，优先级更高）
            .Build();

    /// <summary>获取配置节并绑定为 T；不存在返回 default(T)。</summary>
    public static T? Get<T>(string key) => Root.GetSection(key).Get<T>();

    /// <summary>获取字符串配置；不存在返回 null。</summary>
    public static string? Get(string key) => Root[key];

    /// <summary>
    /// 获取配置，不存在时返回默认值。
    /// </summary>
    public static T GetOrDefault<T>(string key, T defaultValue) =>
        Root.GetSection(key).Exists() ? Root.GetSection(key).Get<T>() ?? defaultValue : defaultValue;

    /// <summary>
    /// 重新加载配置（测试用）。
    /// </summary>
    public static void Reload() => root?.Reload();
}
