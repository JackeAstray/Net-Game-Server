using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace DB
{
    internal class Program
    {
        public static ServiceProvider ServiceProvider => DbServerApp.ServiceProvider;


        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/DB.log", ConfigHelper.GetConfig<string>("Logging:MinimumLevel") ?? "Information");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            Shared.RemoteLog.Initialize($"DB-{ConfigHelper.GetConfig<string>("DBHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("DBPort")}");
            Log.Info("DB服务器正在启动...");

            DbServerApp.InitializeDatabase();
            await DbServerApp.StartNetworkAsync();

            await Task.Delay(-1);
        }
    }
}