using Microsoft.Extensions.Configuration;

namespace Framework.Core;

/// <summary>
/// 统一配置读取（appsettings.json + 环境变量覆盖），底层框架的一部分。
/// </summary>
public static class Config
{
    private static IConfigurationRoot? root;

    private static IConfigurationRoot Root =>
        root ??= new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("NG_") // NG_GatewayPort=31300 覆盖 GatewayPort
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
