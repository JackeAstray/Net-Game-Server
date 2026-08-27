using Framework.Protocol;
using Framework.Protocol.Generated;
using ISession = Network.ISession;

namespace Battle.Handlers;

/// <summary>
/// Battle 服务器的会话上下文适配（ISessionContext 实现）：
/// 将 MessageDispatcher 的抽象发送接口适配到 Battle 的网关会话 + 路由元数据。
/// </summary>
public sealed class BattleSessionContext : ISessionContext
{
    private readonly ISession gatewaySession;
    private readonly long clientSessionId;

    public BattleSessionContext(ISession gatewaySession, long clientSessionId)
    {
        this.gatewaySession = gatewaySession;
        this.clientSessionId = clientSessionId;
    }

    public long ClientSessionId => clientSessionId;

    /// <summary>底层网关会话（业务处理器需要它做 AOI/帧同步定向发送）。</summary>
    public ISession GatewaySession => gatewaySession;

    /// <summary>向当前客户端发送 [MsgId][Payload]（附加 __targetSessionId 路由元数据）。</summary>
    public void Send(int msgId, ReadOnlyMemory<byte> payload)
    {
        SendTo(clientSessionId, msgId, payload);
    }

    /// <summary>向当前客户端发送消息对象（自动 MemoryPack 序列化）。</summary>
    public void Send(IGameMessage message)
    {
        byte[] payload = message.Serialize();
        Send(message.MessageId, payload);
    }

    /// <summary>向指定客户端会话发送（广播/帧同步用）。</summary>
    public void SendTo(long targetSessionId, int msgId, ReadOnlyMemory<byte> payload)
    {
        byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
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
