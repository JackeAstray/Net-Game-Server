using System.Buffers.Binary;
using System.Collections.Concurrent;
using Network;
using Network.Routing;
using Network.Tcp;
using Shared;

namespace Battle;

/// <summary>
/// 跨 Battle 直达路由（迭代 21，对标 ET Location 的节点直达）：绕过 Center 中继直接投递 EntityRemoteCall，
/// 降低 Center 热点的中继压力与一跳延迟。要点：
/// - 会话复用 InternalAuth 握手（共享密钥），复用 Battle 主 TCP 服务端的认证与分发；
/// - 任何连接/发送失败都静默回退 Center 中继（TrySendDirect 返回 false），绝不阻塞业务；
/// - 配置 EntityCallDirectRouting=true 开启（默认 false，保持既有 Center 中继语义）。
/// 注意：本类只负责"发送侧"的直达会话；接收侧由目标 Battle 的主 TcpServer 按内部消息处理，
/// 回执（91002）经 <see cref="BattleServerApp.SendEntityRemoteCallResult"/> 回发到来源会话。
/// </summary>
public static class EntityCallDirectRouter
{
    /// <summary>nodeId -> 直达客户端（含自动重连）。</summary>
    private static readonly ConcurrentDictionary<string, TcpClientWrapper> peers = new(StringComparer.Ordinal);

    /// <summary>已登记的目标节点数（统计用）。</summary>
    public static int PeerCount => peers.Count;

    /// <summary>
    /// 尝试经直达会话发送 EntityRemoteCall。无可用直达会话（未建立/断开/发送异常）返回 false，
    /// 调用方回退 Center 中继。包格式与 Center 中继一致（[TotalLength][MsgId][Payload]）。
    /// </summary>
    public static bool TrySendDirect(Framework.Protocol.Generated.EntityRemoteCall call)
    {
        if (string.IsNullOrEmpty(call.TargetNodeId))
        {
            return false;
        }
        if (!peers.TryGetValue(call.TargetNodeId, out var wrapper) || wrapper == null || !wrapper.IsConnected)
        {
            return false;
        }
        try
        {
            byte[] payload = call.Serialize();
            byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.EntityRemoteCall, payload, out int totalLength);
            wrapper.SendFromPool(packet, totalLength);
            return true;
        }
        catch (Exception ex)
        {
            Framework.Core.Log.Warning($"实体远程调用直达发送失败（回退 Center）Node:{call.TargetNodeId} Err:{ex.Message}");
            peers.TryRemove(call.TargetNodeId, out _);
            try { wrapper.Stop(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 建立（或复用）到目标 Battle 的直达会话。连接失败/断开由 TcpClientWrapper 自动重连；
    /// 成功后通过 OnConnected 发送认证握手。非阻塞（ConnectAsync 后台自动重连）。
    /// </summary>
    public static void EnsurePeer(string nodeId, string host, int port)
    {
        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(host) || port <= 0)
        {
            return;
        }
        if (peers.TryGetValue(nodeId, out var existing) && existing != null && existing.IsConnected)
        {
            return;
        }
        var wrapper = new TcpClientWrapper(host, port);
        wrapper.OnConnected += _ =>
        {
            try
            {
                wrapper.SendInternalAuthHandshake(
                    Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret"),
                    BattleServerApp.CurrentNodeId);
            }
            catch (Exception ex)
            {
                Framework.Core.Log.Error(ex, $"直达会话认证握手发送失败 Node:{nodeId}");
            }
        };
        wrapper.OnDataReceived += (_, data) => HandleDirectInbound(nodeId, data);
        wrapper.OnDisconnected += (_, _) =>
        {
            Framework.Core.Log.Warning($"Battle 直达会话断开（自动重连）Node:{nodeId}");
        };
        // 先占位再连接，避免并发重复创建
        if (peers.TryAdd(nodeId, wrapper))
        {
            Framework.Core.Log.Info($"Battle 直达会话建立中 -> {nodeId} ({host}:{port})");
            _ = wrapper.ConnectAsync();
        }
        else if (peers.TryGetValue(nodeId, out var w) && w != null && w.IsConnected)
        {
            try { wrapper.Stop(); } catch { }
        }
    }

    /// <summary>直达会话收到的业务消息：解析 [MsgId(4)][Payload]，处理实体远程调用回执（91002）。</summary>
    private static void HandleDirectInbound(string nodeId, ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }
        int msgId = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
        if (msgId == Framework.Protocol.Generated.MessageIds.EntityRemoteCallResult)
        {
            try
            {
                var result = Framework.Protocol.ProtocolCodec.Decode<Framework.Protocol.Generated.EntityRemoteCallResult>(data.Slice(4).Span);
                if (result != null)
                {
                    Framework.Entity.EntityCallHubRegistry.Default.HandleResult(result);
                }
            }
            catch (Exception ex)
            {
                Framework.Core.Log.Error(ex, $"直达会话回执反序列化失败 Node:{nodeId}");
            }
        }
        else
        {
            Framework.Core.Log.Warning($"直达会话收到未处理消息 MsgId:{msgId} Node:{nodeId}");
        }
    }
}
