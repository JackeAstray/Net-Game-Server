using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Network.Http;

/// <summary>
/// 纯粹的 ASP.NET Core HTTP 接口服务。
/// 用于提供弱联网环境下的基础短连接 HTTP API (如：登录、注册、支付回调、GM指令等)。
/// </summary>
public class AspNetServer : INetworkServer
{
    private IHost? host;
    private readonly Action<IServiceCollection>? configureServices;
    private readonly Action<IApplicationBuilder>? configureApp;

    // 因为是短连接的 HTTP 请求，不支持长连接的 Session 机制和回调委托
    public event SessionConnectedHandler? OnSessionConnected
    {
        add { /* 不支持 */ }
        remove { /* 不支持 */ }
    }

    public event DataReceivedHandler? OnDataReceived
    {
        add { /* 不支持 */ }
        remove { /* 不支持 */ }
    }

    public event SessionDisconnectedHandler? OnSessionDisconnected
    {
        add { /* 不支持 */ }
        remove { /* 不支持 */ }
    }

    /// <summary>
    /// 支持传入自定义外部配置用于加载控制器、中间件、跨域等。
    /// </summary>
    public AspNetServer(Action<IServiceCollection>? configureServices = null, Action<IApplicationBuilder>? configureApp = null)
    {
        this.configureServices = configureServices;
        this.configureApp = configureApp;
    }

    /// <summary>
    /// 启动 HTTP 服务，监听指定端口。
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    public async Task StartAsync(int port)
    {
        host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://*:{port}");

                webBuilder.ConfigureServices(services =>
                {
                    // 默认开启控制器的拦截
                    services.AddControllers();
                    configureServices?.Invoke(services);
                });

                webBuilder.Configure(app =>
                {
                    app.UseRouting();

                    // 让外部能够自由决定是否加入鉴权、跨域等中间件
                    configureApp?.Invoke(app);

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();

                        endpoints.MapGet("/", async context =>
                        {
                            await context.Response.WriteAsync("游戏服务器 HTTP API 正在运行。");
                        });
                    });
                });
            })
            .Build();

        await host.StartAsync();
    }

    /// <summary>
    /// 停止 HTTP 服务。
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync()
    {
        if (host != null)
        {
            await host.StopAsync();
            host.Dispose();
            host = null;
        }
    }
}