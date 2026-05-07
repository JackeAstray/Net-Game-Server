using Microsoft.Extensions.Configuration;
using System.IO;

namespace Shared
{
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

        public static T GetConfig<T>(string key)
        {
            return Configuration.GetSection(key).Get<T>();
        }

        public static string GetConfig(string key)
        {
            return Configuration[key];
        }
    }
}