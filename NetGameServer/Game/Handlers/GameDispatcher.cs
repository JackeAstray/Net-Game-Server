using Framework.Protocol;
using Framework.Protocol.Generated;
using Shared.Messages.Chat;
using Shared.Data.Chat;
using ISession = Network.ISession;

namespace Game.Handlers;

/// <summary>
/// Game 服务器的会话上下文适配（ISessionContext 实现）：
/// 将 MessageDispatcher 的抽象发送接口适配到 Game 的网关会话 + __targetSessionId 路由元数据。
/// </summary>
public sealed class GameSessionContext : ISessionContext
{
    private readonly ISession gatewaySession;
    private readonly long clientSessionId;

    public GameSessionContext(ISession gatewaySession, long clientSessionId)
    {
        this.gatewaySession = gatewaySession;
        this.clientSessionId = clientSessionId;
    }

    public long ClientSessionId => clientSessionId;

    /// <summary>底层网关会话（业务处理器需要它做定向发送）。</summary>
    public ISession GatewaySession => gatewaySession;

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
        byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
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
/// 基于 MessageDispatcher 的强类型处理器（Game 服务器）。
/// 使用生成的消息类 + MemoryPack 二进制序列化（JSON 兼容回退），消灭手写 MsgId 分支。
/// </summary>
public static class GameDispatcher
{
    /// <summary>
    /// 构建 Game 服务器的配置化分发器（当前迁移聊天消息）。
    /// 未注册的 MsgId 由调用方回退旧路由器。
    /// </summary>
    public static Framework.Protocol.MessageDispatcher BuildDispatcher(ChatHandler chatHandler)
    {
        var dispatcher = new Framework.Protocol.MessageDispatcher();

        // 聊天发送（旧客户端 JSON / 新客户端 MemoryPack 双格式）
        dispatcher.RegisterSync<ChatSend>((ctx, msg) =>
        {
            var req = new SendChatRequest
            {
                SenderId = msg.SenderId,
                SenderUniqueId = msg.SenderUniqueId,
                SenderName = msg.SenderName,
                ReceiverId = msg.ReceiverId,
                ReceiverUniqueId = msg.ReceiverUniqueId,
                Channel = (ChatChannel)msg.Channel,
                RoomId = msg.RoomId,
                Content = msg.Content
            };
            // 复用现有业务逻辑（内部按会话 ID 映射玩家身份）
            var session = new Game.Network.ClientSessionWrapper(
                ((GameSessionContext)ctx).GatewaySession, ctx.ClientSessionId);
            chatHandler.HandleSendChatRequest(session, req);
        }, jsonFallback: true);

        return dispatcher;
    }
}
