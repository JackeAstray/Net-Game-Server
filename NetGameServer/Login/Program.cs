using Shared;
using Log = Shared.Log;

namespace Login
{
    /// <summary>
    /// 登录服务器入口：初始化日志与远程日志上报，启动网络服务与 HTTP API。
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Log.Configure(true, "Logs/Login.log");

            // 远程日志上报（配置 LoggerHost/LoggerPort 后生效，对标 KBE logger 聚合）
            RemoteLog.Initialize($"Login-{ConfigHelper.GetConfig<string>("LoginHost") ?? "127.0.0.1"}:{ConfigHelper.GetConfig<int>("LoginPort")}");
            Log.Info("登录服务器正在启动...");

            await LoginServerApp.StartNetworkAsync();
            await LoginServerApp.StartWebApiAsync(args);

            await Task.Delay(-1);
        }
    }
}
