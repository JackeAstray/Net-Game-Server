using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Routing;
using Network.Tcp;
using Serilog;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;
// Serilog 与 Shared.Log 存在 Log 类型冲突：显式别名指向仓库统一日志门面（Game/Login 均用 Shared.Log）。
using Log = Shared.Log;

namespace App
{
    /// <summary>
    /// 应用节点（AppServer）程序入口类 —— "游戏服务器 + 应用服务器"并存的承载节点。
    /// 关注点：
    ///   1. 内部 TCP 服务（AppPort，默认 31308）：接收网关转发的客户端消息，HMAC 内部认证 fail-closed，
    ///      按客户端会话串行分发（AppDispatcher，新协议 MemoryPack）；
    ///   2. Center 注册 + 心跳（NodeType="App"，签名注册，与各游戏节点一致）；
    ///   3. ASP.NET Core REST API（AppHttpPort，默认 31309）：应用服务器 HTTP 面（控制器 + API Key 鉴权）。
    /// </summary>
    public static class AppServerApp
    {
        /// <summary>会话串行队列（对齐 Login/Game/Center）：同客户端会话业务消息按键串行，防 await 边界乱序。</summary>
        private static readonly Framework.Core.OrderedTaskQueue sessionSerialQueue = new("App-SessionSerial");
        private static TcpServer? tcpServer;
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;
        private static WebApplication? webApiApp;
        private static Task? webApiRunTask;
        /// <summary>当前节点标识（注册 + 回包 NodeId 用）。</summary>
        private static string nodeId = string.Empty;

        /// <summary>
        /// 启动接收网关连接的 TCP 服务并处理来自网关的数据包。
        /// 数据包结构：[MsgId(4)][Payload]，路由信息通过 payload 中的 RouteMetadata（__clientSessionId 等）传递。
        /// 安全要点：内部连接认证（InternalAuthFilter）fail-closed、会话串行、新协议分发器优先。
        /// </summary>
        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("AppPort") == 0 ? 31308 : ConfigHelper.GetConfig<int>("AppPort");
            string appHost = ConfigHelper.GetConfig<string>("AppHost") ?? "127.0.0.1";
            nodeId = ConfigHelper.GetConfig<string>("NodeId") ?? $"App-{appHost}:{port}";

            // 内部连接认证：网关连接必须先通过认证握手（InternalAuth），密钥与 Center 节点注册共用。
            // 安全修复：拒绝占位符密钥。
            string authSecret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            // 重启窗口修复：周期持久化防重放状态，重启不重置握手重放窗口
            Framework.Core.Security.InternalAuthFilter.ConfigureReplayPersistence(
                System.IO.Path.Combine(AppContext.BaseDirectory, "data", "replay_state.bin"));
            var gatewayAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

            var server = new TcpServer();
            tcpServer = server;

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 MsgId 分支
            var appDispatcher = App.Handlers.AppDispatcher.BuildDispatcher(nodeId);

            server.OnSessionConnected += session =>
            {
                Log.Info($"客户端已连接: {session.RemoteEndPoint}");
                gatewayAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, nodeId);
            };
            server.OnSessionDisconnected += (session, reason) =>
            {
                gatewayAuthFilters.TryRemove(session.SessionId, out _);
                Log.Info($"客户端断开连接，原因: {reason}");
            };

            server.OnDataReceived += global::Network.AsyncEventGuard.Wrap(async (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"App 收到无效客户端数据包，长度不足 4，Session:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;

                    // 内部连接认证：未认证连接只接受认证握手消息，其余业务消息一律拒绝（fail-closed）。
                    if (gatewayAuthFilters.TryGetValue(session.SessionId, out var authFilter))
                    {
                        if (!authFilter.IsAuthenticated)
                        {
                            if (Framework.Core.Security.InternalAuthFilter.IsAuthMessage(msgId))
                            {
                                byte[] authPayload = data.Slice(4).ToArray();
                                if (authFilter.TryAuthenticate(authPayload))
                                {
                                    Log.Info($"App <- Gateway 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                }
                                else
                                {
                                    Log.Warning($"App <- Gateway 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                    session.Close();
                                    return;
                                }
                                return; // 认证握手不进入业务分发
                            }

                            Log.Warning($"App 拒绝未认证连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            return;
                        }
                    }
                    else
                    {
                        // 未注册认证过滤器 = 未认证连接：默认 fail-closed 拒绝
                        if (!Shared.ConfigHelper.GetConfig<bool>("AllowUnauthenticatedInternal"))
                        {
                            Log.Warning($"App 拒绝无认证过滤器连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}（fail-closed）");
                            session.Close();
                            return;
                        }
                    }

                    byte[] payload = data.Slice(4).ToArray();
                    if (!Shared.RouteMetadata.TryExtractClientSessionId(payload, out long originalSessionId, out var cleanPayload))
                    {
                        Log.Warning($"App 收到缺少路由元数据的消息 MsgId:{msgId}");
                        return;
                    }

                    // 业务段按键（客户端会话）串行执行（对齐 Center/Login/Game OrderedTaskQueue）。
                    // 内部消息（originalSessionId=0）按网关连接会话串行，跨连接并发。
                    long serialKey = originalSessionId > 0 ? originalSessionId : -session.SessionId;
                    await sessionSerialQueue.EnqueueAsync(serialKey, async () =>
                    {
                        try
                        {
                            // 新协议分发优先（强类型 + MemoryPack）
                            bool dispatched = await appDispatcher.TryDispatch(
                                new App.Handlers.AppSessionContext(session, originalSessionId), msgId, cleanPayload);
                            if (!dispatched)
                            {
                                Log.Warning($"App 未注册的消息类型: {msgId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"App 处理客户端数据异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error($"App 处理客户端数据异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            });

            await server.StartAsync(port);
            Log.Info($"应用服务器已启动，监听端口: {port}");

            ConnectToCenter(port);
        }

        /// <summary>
        /// 启动 ASP.NET Core Web API 服务（应用服务器 HTTP 面）。
        /// 安全默认：仅监听回环地址（AppHttpListenAddress 可放开为 0.0.0.0）；
        /// 生产环境应配置 HTTPS（见 Login API 的 ForceApiHttps 模式）或在反代层终止 TLS。
        /// </summary>
        public static async Task StartHttpAsync(string[] args)
        {
            int httpPort = ConfigHelper.GetConfig<int>("AppHttpPort") == 0 ? 31309 : ConfigHelper.GetConfig<int>("AppHttpPort");
            string bindAddress = ConfigHelper.GetConfig<string>("AppHttpListenAddress") ?? "127.0.0.1";

            var builder = WebApplication.CreateBuilder(args);
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
            builder.Services.AddControllers();

            // 应用面鉴权：非公开接口需要 X-Api-Key 请求头（fail-closed：未配置 Key 时全部拒绝）。
            var apiKeys = (ConfigHelper.GetConfig<string>("HttpApiKeys") ?? string.Empty)
                .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            if (apiKeys.Length == 0)
            {
                Log.Warning("未配置 HttpApiKeys——App HTTP 非公开接口将被全部拒绝。生产环境务必配置。");
            }

            var app = builder.Build();
            webApiApp = app;

            app.UseMiddleware<App.Auth.AppApiKeyMiddleware>(new App.Auth.AppApiKeyOptions
            {
                Keys = apiKeys,
                AllowAnonymousPaths = new[] { "/api/app/info", "/api/app/ping", "/health" }
            });

            app.MapControllers();

            Log.Info($"App HTTP API 已启动，监听 {bindAddress}:{httpPort}");
            webApiRunTask = app.RunAsync();
        }

        /// <summary>
        /// 连接 Center 并注册应用节点（NodeType="App"）+ 周期心跳。
        /// 与各游戏节点完全一致：认证握手 → 签名注册 → 心跳；断开自动重连。
        /// </summary>
        private static void ConnectToCenter(int port)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string appHost = ConfigHelper.GetConfig<string>("AppHost") ?? "127.0.0.1";
            string instanceId = ConfigHelper.GetConfig<string>("InstanceId") ?? string.Empty;
            string machineId = ConfigHelper.GetConfig<string>("MachineId") ?? string.Empty;
            string supervisedBy = ConfigHelper.GetConfig<string>("SupervisedBy") ?? string.Empty;
            var centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                centerClient.SendInternalAuthHandshake(Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret"), nodeId);
                SendRegisterNode(centerClient, nodeId, "App", appHost, port, GetCurrentLoad(), instanceId, machineId, supervisedBy);

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
                centerHeartbeatCts = new System.Threading.CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Shared.NodeHeartbeatDefaults.HeartbeatIntervalSeconds), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, GetCurrentLoad());
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"App 心跳循环异常（本轮跳过，下轮继续重试）: {ex}");
                        }
                    }
                }, cancellationToken);
            };

            centerClient.OnDisconnected += (session, reason) =>
            {
                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
                Log.Warning($"与 Center 服务器断开连接: {reason}");
            };
            centerClient.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"App 收到 Center 异常数据，长度不足 4，实际: {data.Length}");
                        return;
                    }
                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    Log.Debug("App <- Center 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength}", session.SessionId, session.RemoteEndPoint!, msgId, data.Length);
                }
                catch (Exception ex)
                {
                    Log.Error($"App 处理 Center 回包异常 Exception:{ex}");
                }
            };
            _ = centerClient.ConnectAsync();
        }

        private static void SendRegisterNode(TcpClientWrapper centerClient, string nodeId, string nodeType, string host, int port, int currentLoad,
            string instanceId = "", string machineId = "", string supervisedBy = "")
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signatureSource = $"{nodeId}|{nodeType}|{host}|{port}|{currentLoad}|{instanceId}|{machineId}|{supervisedBy}|{timestamp}";
            var registerRequest = new CenterRegisterNodeRequest
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                InstanceId = instanceId,
                MachineId = machineId,
                SupervisedBy = supervisedBy,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(registerRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRegisterNodeReq, payload, out int totalLength);
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"向 Center 注册节点失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        private static void SendNodeStatus(TcpClientWrapper centerClient, string nodeId, int currentLoad)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signatureSource = $"{nodeId}|{currentLoad}|{timestamp}";
            var statusRequest = new CenterNodeStatusRequest
            {
                NodeId = nodeId,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(statusRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterNodeStatusReq, payload, out int totalLength);
            try
            {
                centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"向 Center 上报节点状态失败 NodeId:{nodeId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        private static string ComputeCenterSignature(string source)
        {
            string secret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>当前负载（最小骨架：固定返回 1；后续可接队列深度/在线会话数）。</summary>
        private static int GetCurrentLoad() => 1;

        /// <summary>
        /// 优雅关闭（NodeLifecycle 关闭钩子）：
        /// 取消 Center 心跳 → 停止 TCP 监听 → 停止 HTTP API。
        /// </summary>
        public static async Task ShutdownAsync()
        {
            Log.Info("App 节点开始优雅关闭...");

            centerHeartbeatCts?.Cancel();
            centerHeartbeatCts?.Dispose();
            centerHeartbeatCts = null;

            try
            {
                if (tcpServer != null)
                {
                    await tcpServer.StopAsync();
                    tcpServer = null;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"App 停止 TCP 服务异常: {ex.Message}");
            }

            try
            {
                if (webApiApp != null)
                {
                    await webApiApp.StopAsync();
                    webApiApp = null;
                }
                if (webApiRunTask != null)
                {
                    await Task.WhenAny(webApiRunTask, Task.Delay(3000));
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"App 停止 HTTP API 异常: {ex.Message}");
            }

            Log.Info("App 节点关闭完成。");
        }
    }
}
