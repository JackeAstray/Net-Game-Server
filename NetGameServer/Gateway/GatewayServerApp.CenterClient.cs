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
    /// 网关 —— Center 节点协同模块（生命周期挂载、节点注册、状态上报、签名）。
    /// 与 GatewayServerApp.cs 同属一个 partial class，按关注点分文件组织。
    /// </summary>
    public static partial class GatewayServerApp
    {
        /// <summary>
        /// 将网关节点的生命周期挂载到指定的 Center 连接：连接时注册节点并启动定期心跳上报，断开时停止心跳。
        /// </summary>
        /// <remarks>从 ConfigHelper 获取 Center/Gateway 配置；连接后发送注册信息并以 10 秒间隔上报在线数，使用
        /// CancellationTokenSource 管理心跳任务的取消。</remarks>
        /// <param name="centerClient">用于与 Center 建立连接并处理连接与断开事件的 TcpClientWrapper。</param>
        /// <param name="port">网关的本地监听端口，用于构建节点标识和注册信息。</param>
        private static void AttachCenterNodeLifecycle(TcpClientWrapper centerClient, int port)
        {
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string gatewayHost = ConfigHelper.GetConfig<string>("GatewayHost") ?? "127.0.0.1";
            // nodeId 优先级：配置（machine 注入） > 按 host:port 派生（保持后向兼容）
            string nodeId = ConfigHelper.GetConfig<string>("NodeId") ?? $"Gateway-{gatewayHost}:{port}";
            string instanceId = ConfigHelper.GetConfig<string>("InstanceId") ?? string.Empty;
            string machineId = ConfigHelper.GetConfig<string>("MachineId") ?? string.Empty;
            string supervisedBy = ConfigHelper.GetConfig<string>("SupervisedBy") ?? string.Empty;

            centerClient.OnConnected += session =>
            {
                Shared.Log.Info($"Gateway 节点生命周期已挂载到 Center 连接 (Host:{centerHost} Port:{centerPort})");
                SendRegisterNode(centerClient, nodeId, "Gateway", gatewayHost, port, Gateway.Managers.GatewaySessionManager.Instance.GetOnlineCount(), instanceId, machineId, supervisedBy);

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
                centerHeartbeatCts = new CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Shared.NodeHeartbeatDefaults.HeartbeatIntervalSeconds), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, Gateway.Managers.GatewaySessionManager.Instance.GetOnlineCount());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Error($"Gateway 心跳循环异常（下轮继续重试）: {ex}");
                    }
                }, cancellationToken);
            };

            centerClient.OnDisconnected += (session, reason) =>
            {
                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts?.Dispose();
            };
        }

        /// <summary>
        /// 构建节点注册请求（包含时间戳与签名）、序列化为 UTF-8 并通过指定的 TcpClientWrapper 发送到中心服务器。
        /// </summary>
        /// <remarks>计算基于节点信息和当前 UTC 时间的时间戳与签名；将 CenterRegisterNodeRequest 序列化为 UTF-8 字节数组；使用
        /// MessageIds.CenterRegisterNodeReq 构建数据包并发送实际长度的字节；发送后将用于构建包的字节数组返回到共享数组池。</remarks>
        /// <param name="centerClient">用于向中心服务器发送数据的 TcpClientWrapper 实例。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="nodeType">节点类型标识符。</param>
        /// <param name="host">节点的主机名或 IP 地址。</param>
        /// <param name="port">节点监听的端口号。</param>
        /// <param name="currentLoad">节点当前的负载值。</param>
        /// <param name="instanceId">实例 ID（machine 注入；可空）。</param>
        /// <param name="machineId">托管本节点的 Machine 进程 ID（可空）。</param>
        /// <param name="supervisedBy">托管方类型（可空）。</param>
        private static void SendRegisterNode(TcpClientWrapper centerClient, string nodeId, string nodeType, string host, int port, int currentLoad,
            string instanceId = "", string machineId = "", string supervisedBy = "")
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 协议扩展（迭代 20）：Machine 注入字段参与签名源
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
            centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        /// <summary>
        /// 发送包含节点标识、当前负载、时间戳与签名的状态消息到中心服务器。
        /// </summary>
        /// <remarks>使用 UTC Unix 时间戳（秒），并基于 "{nodeId}|{currentLoad}|{timestamp}" 计算签名；请求以 UTF-8 JSON
        /// 序列化并通过 PacketBuilder 构建后发送，临时缓冲区会返回到 ArrayPool。</remarks>
        /// <param name="centerClient">用于与中心服务器通信的 TCP 客户端包装器。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="currentLoad">节点的当前负载值。</param>
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
            centerClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }

        /// <summary>
        /// 使用配置的共享密钥对输入字符串计算基于 HMAC-SHA256 的签名，并以 Base64 编码返回。
        /// </summary>
        /// <remarks>从配置键 CenterNodeSharedSecret 获取密钥（不存在时使用默认值 'change-this-secret'），使用 UTF-8 编码对输入进行
        /// HMAC-SHA256 计算，使用完毕后释放 HMAC 实例。</remarks>
        /// <param name="source">要签名的原始字符串。</param>
        /// <returns>返回使用配置键 CenterNodeSharedSecret（若未配置则使用默认值 'change-this-secret'）作为密钥生成的 HMAC-SHA256 哈希的 Base64 编码字符串。</returns>
          private static string ComputeCenterSignature(string source)
        {
            // 安全修复：拒绝占位符密钥（生产环境必须显式配置）。
            string secret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 配置并启动基于 YARP 的反向代理，在指定或默认端口上使用 Kestrel 监听，并将 /api 和 /swagger 路由到登录后端。
        /// </summary>
        /// <remarks>从配置读取 GatewayHttpPort 和 LoginHttpUrl（默认分别为 31301 和 http://127.0.0.1:31303），显式配置
        /// Kestrel 监听端口，使用 Serilog，并通过内存加载 YARP 路由与集群配置；调用 app.RunAsync() 以非阻塞方式运行主机。</remarks>
        /// <param name="args">传递给 WebApplication.CreateBuilder 的命令行参数。</param>
        /// <returns>可等待的 Task，表示异步启动操作的完成。</returns>
    }
}
