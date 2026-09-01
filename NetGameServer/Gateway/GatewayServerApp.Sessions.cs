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

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 显式监听指定端口，避免被 IISExpress 或其他默认配置干扰
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(httpPort);
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
            app.MapReverseProxy();

            Shared.Log.Info($"网关 HTTP API 反向代理已启动，监听端口: {httpPort} 并路由 /api 至 {loginHttpUrl}");
            _ = app.RunAsync();
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
            Shared.Log.Info("Gateway 优雅关闭开始：断开全部客户端会话...");
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
                    catch
                    {
                        // 单会话关闭失败不阻塞整体
                    }
                }
            }
            catch (Exception ex)
            {
                Shared.Log.Error(ex, "Gateway 关闭客户端会话异常");
            }
            Shared.Log.Info($"Gateway 优雅关闭完成（已断开 {closed} 个客户端会话）。");
            await Task.CompletedTask;
        }
    }
}
