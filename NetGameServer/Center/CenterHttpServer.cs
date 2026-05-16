using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Shared;

namespace Center;

internal static class CenterHttpServer
{
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
