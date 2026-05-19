using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Shared;

namespace Center;

internal static class CenterHttpServer
{
    /// <summary>
    /// 启动并运行中心服务器的 ASP.NET Core Web 应用；配置 Kestrel 在配置的 HTTP 端口（默认 31316）监听，启用 Serilog，注册并映射控制器。
    /// </summary>
    /// <remarks>若配置项 CenterHttpPort 为 0 或未配置，则使用默认端口 31316。启动完成后记录运行信息并异步监听连接。</remarks>
    /// <param name="args">传递给 WebApplication 创建器的命令行参数。</param>
    /// <returns>表示应用启动并异步运行直到停止的可等待任务。</returns>
    public static async Task StartAsync(string[] args)
    {
        int httpPort = ConfigHelper.GetConfig<int>("CenterHttpPort") == 0 ? 31316 : ConfigHelper.GetConfig<int>("CenterHttpPort");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(httpPort);
        });

        builder.Host.UseSerilog();
        builder.Services.AddControllers();

        var app = builder.Build();

        app.MapControllers();

        Shared.Log.Info($"中心服务器启动完成，等待其他服务节点接入。监控 HTTP 端口: {httpPort}");
        await app.RunAsync();
    }
}
