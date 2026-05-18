using Microsoft.Extensions.Configuration;
using System.IO;

namespace Shared
{
    /// <summary>
    /// 配置帮助类，用于加载和访问应用程序的配置设置。
    /// </summary>
    public static class ConfigHelper
    {
        public static IConfigurationRoot Configuration { get; }

        static ConfigHelper()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            Configuration = builder.Build();
        }

        /// <summary>
        /// 从全局 Configuration 中获取指定配置节并将其绑定为类型 T 的实例。
        /// </summary>
        /// <remarks>依赖静态 Configuration 实例，调用前应已初始化。</remarks>
        /// <typeparam name="T">要绑定配置节的目标类型。</typeparam>
        /// <param name="key">配置节的键或路径（支持使用冒号分隔子路径）。</param>
        /// <returns>绑定后的类型 T 实例；如果配置节不存在则返回引用类型的 null 或值类型的默认值。</returns>
        public static T GetConfig<T>(string key)
        {
            return Configuration.GetSection(key).Get<T>();
        }

        /// <summary>
        /// 获取指定配置键的值。
        /// </summary>
        /// <remarks>从静态 Configuration 对象读取对应键的值。</remarks>
        /// <param name="key">要检索的配置键名称。</param>
        /// <returns>对应的配置值；若键不存在或无值则返回 null。</returns>
        public static string GetConfig(string key)
        {
            return Configuration[key];
        }
    }
}