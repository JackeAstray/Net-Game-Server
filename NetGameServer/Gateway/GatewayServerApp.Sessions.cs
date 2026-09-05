using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Routing;
using Network.Tcp;
using Serilog;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Gateway
{
    /// <summary>
    /// 网关 —— 会话与 HTTP 反代模块（断线重连恢复、YARP 反向代理）。
    /// 与 GatewayServerApp.cs 同属一个 partial class，按关注点分文件组织。
    /// </summary>
    public static partial class GatewayServerApp
    {
        public static async Task StartReverseProxyAsync(string[] args)
        {
            // HTTP 监听端口和后端 Login HTTP 地址（支持默认值）
            int httpPort = ConfigHelper.GetConfig<int>("GatewayHttpPort") == 0 ? 31301 : ConfigHelper.GetConfig<int>("GatewayHttpPort");
            string loginHttpUrl = ConfigHelper.GetConfig<string>("LoginHttpUrl") ?? "http://127.0.0.1:31303";
            // 安全默认：仅监听回环地址，避免反代管理口默认暴露到公网。
            string bindAddress = ConfigHelper.GetConfig<string>("GatewayHttpListenAddress") ?? "127.0.0.1";

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (string.Equals(bindAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(bindAddress, "*", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(bindAddress, "::", StringComparison.OrdinalIgnoreCase))
                {
                    options.ListenAnyIP(httpPort);
                }
                else
                {
                    options.Listen(System.Net.IPAddress.Parse(bindAddress), httpPort);
                }
            });

            builder.Host.UseSerilog();
            builder.Services.AddReverseProxy()
                .LoadFromMemory(
                    new[] {
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_api_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/api/{**catch-all}"
                            }
                        },
                        new Yarp.ReverseProxy.Configuration.RouteConfig()
                        {
                            RouteId = "login_swagger_route",
                            ClusterId = "login_api_cluster",
                            Match = new Yarp.ReverseProxy.Configuration.RouteMatch
                            {
                                Path = "/swagger/{**catch-all}"
                            }
                        }
                    },
                    new[] {
                        new Yarp.ReverseProxy.Configuration.ClusterConfig()
                        {
                            ClusterId = "login_api_cluster",
                            Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "default", new Yarp.ReverseProxy.Configuration.DestinationConfig() { Address = loginHttpUrl } }
                            }
                        }
                    }
                );

            var app = builder.Build();
            reverseProxyApp = app;
            app.MapReverseProxy();

            bool nonLoopback = !string.Equals(bindAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(bindAddress, "::1", StringComparison.OrdinalIgnoreCase);
            if (nonLoopback && loginHttpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Shared.Log.Warning($"Gateway 反代以明文 HTTP 暴露在 {bindAddress}:{httpPort}，且上游目标为 {loginHttpUrl}。生产环境建议启用 HTTPS/TLS 或仅绑定回环地址。 ");
            }

            Shared.Log.Info($"网关 HTTP API 反向代理已启动，监听地址: {bindAddress}:{httpPort} 并路由 /api 至 {loginHttpUrl}");
            reverseProxyRunTask = app.RunAsync();
        }

        /// <summary>
        /// 断线重连恢复：把新登录会话迁移到挂起的旧会话 ID（后端按旧 ID 续接挂起实体），
        /// 并通知 Battle 实体从挂起转在线。在登录成功回包处理中调用。
        /// </summary>
        private static void TryResumePendingSession(long newSessionId, int userId)
        {
            // 安全修复：ConcurrentDictionary 的枚举器在迭代过程中被 TryRemove 修改时会抛
            // InvalidOperationException。先快照 key 集合。
            var keys = pendingReconnects.Keys.ToArray();
            foreach (var key in keys)
            {
                if (!pendingReconnects.TryGetValue(key, out var pr))
                {
                    continue;
                }
                if (pr.UserId != userId)
                {
                    continue;
                }
                pendingReconnects.TryRemove(key, out _);
                if (pr.ExpiresAtUtc < DateTime.UtcNow)
                {
                    Shared.Log.Info($"Gateway 重连挂起已过期，忽略 SessionId:{key}");
                    return;
                }
                if (Gateway.Managers.GatewaySessionManager.Instance.ResumeSession(newSessionId, key))
                {
                    // 通知 Battle 节点：玩家会话恢复（实体从挂起转在线），按玩家绑定路由到所在节点
                    var resume = new Framework.Protocol.Generated.PlayerSessionResume { ClientSessionId = key };
                    byte[] payload = resume.Serialize();
                    byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, key);
                    byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.PlayerSessionResume, routedPayload, out int totalLength);
                    string nodeId = clientBattleNodeBindings.TryGetValue(key, out var bound) ? bound : defaultBattleNodeId;
                    if (battleNodeSenders.TryGetValue(nodeId, out var sender))
                    {
                        sender.SendOrBuffer(packet.AsSpan(0, totalLength).ToArray());
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                    }
                    else
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                        Shared.Log.Warning($"Gateway Battle 节点不可用（{nodeId}），重连恢复通知丢弃 ClientSessionId:{key}");
                    }
                }
                return;
            }
        }

        /// <summary>
        /// 优雅关闭（迭代 21，NodeLifecycle 关闭钩子）：
        /// 断开全部客户端会话（触发后端实体离场/持久化），随后由心跳超时自动摘除 Center 注册。
        /// </summary>
        public static async Task ShutdownAsync()
        {
            Shared.Log.Info("Gateway 优雅关闭开始：停止后台循环与HTTP反代，并断开全部客户端会话...");

            maintenanceLoopCts?.Cancel();
            maintenanceLoopCts?.Dispose();
            maintenanceLoopCts = null;

            centerHeartbeatCts?.Cancel();
            centerHeartbeatCts?.Dispose();
            centerHeartbeatCts = null;

            var app = reverseProxyApp;
            if (app != null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Shared.Log.Error(ex, "Gateway 停止 HTTP 反代异常");
                }
                reverseProxyApp = null;
                reverseProxyRunTask = null;
            }

            int closed = 0;
            try
            {
                foreach (var session in Gateway.Managers.GatewaySessionManager.Instance.GetAllSessions())
                {
                    try
                    {
                        session.Close();
                        closed++;
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Warning($"Gateway 关闭会话失败 SessionId:{session.SessionId} Exception:{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Shared.Log.Error(ex, "Gateway 关闭客户端会话异常");
            }
            Shared.Log.Info($"Gateway 优雅关闭完成（已断开 {closed} 个客户端会话）。");
        }
    }
}
