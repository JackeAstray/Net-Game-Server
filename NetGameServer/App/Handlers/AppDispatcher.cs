using Framework.Protocol;
using Framework.Protocol.Generated;
using ISession = Network.ISession;

namespace App.Handlers;

/// <summary>
/// 应用节点的会话上下文适配（ISessionContext 实现）：
/// 将 MessageDispatcher 的抽象发送接口适配到 App 的网关会话 + __clientSessionId 路由元数据。
/// </summary>
public sealed class AppSessionContext : ISessionContext
{
    private readonly ISession gatewaySession;
    private readonly long clientSessionId;

    public AppSessionContext(ISession gatewaySession, long clientSessionId)
    {
        this.gatewaySession = gatewaySession;
        this.clientSessionId = clientSessionId;
    }

    public long ClientSessionId => clientSessionId;

    public void Send(int msgId, ReadOnlyMemory<byte> payload)
    {
        SendTo(clientSessionId, msgId, payload);
    }

    public void Send(IGameMessage message)
    {
        Send(message.MessageId, message.Serialize());
    }

    public void SendTo(long targetSessionId, int msgId, ReadOnlyMemory<byte> payload)
    {
        byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, targetSessionId);
        byte[] packet = global::Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
        try
        {
            gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }
    }
}

/// <summary>
/// 基于 MessageDispatcher 的强类型处理器（应用节点）。
/// 使用生成的消息类 + MemoryPack 二进制序列化（JSON 兼容回退），消灭手写 MsgId 分支。
/// </summary>
public static partial class AppDispatcher
{
    /// <summary>构建应用节点的配置化分发器。</summary>
    public static Framework.Protocol.MessageDispatcher BuildDispatcher(string nodeId)
    {
        var dispatcher = new Framework.Protocol.MessageDispatcher();

        // 最小可用示例：客户端 AppEchoRequest -> AppEchoResult（回显 + 节点标识 + 会话 ID）。
        // 演示"游戏消息通道"在应用节点上同样可用：客户端经 Gateway 发送，App 处理并回包。
        dispatcher.Register<AppEchoRequest>((ctx, msg) =>
        {
            var res = new AppEchoResult
            {
                Success = true,
                Echo = string.IsNullOrEmpty(msg.Text) ? "(empty)" : msg.Text,
                ClientSessionId = ctx.ClientSessionId,
                NodeId = nodeId
            };
            ctx.Send(res);
            return Task.CompletedTask;
        });

        // 玩家断线通知（网关内部消息）：最小骨架仅记录；后续业务可在断开时清理会话态。
        dispatcher.RegisterSync<PlayerDisconnect>((ctx, msg) =>
        {
            Framework.Core.Log.Debug($"App 客户端会话断开 ClientSessionId:{ctx.ClientSessionId}");
        }, jsonFallback: true);

        return dispatcher;
    }
}
