using Framework.Protocol;
using Framework.Protocol.Generated;
using Shared.Messages.Center;
using ISession = Network.ISession;
using GenRoomInfo = Framework.Protocol.Generated.RoomInfo;
using GenRoomMemberInfo = Framework.Protocol.Generated.RoomMemberInfo;

namespace Center.Handlers;

/// <summary>
/// Center 服务器的会话上下文适配（ISessionContext 实现）：
/// 将 MessageDispatcher 的抽象发送接口适配到 Center 的网关会话 + __clientSessionId 路由元数据。
/// </summary>
public sealed class CenterSessionContext : ISessionContext
{
    private readonly ISession gatewaySession;
    private readonly long clientSessionId;

    public CenterSessionContext(ISession gatewaySession, long clientSessionId)
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

/// <summary>
/// 基于 MessageDispatcher 的强类型处理器（Center 服务器）。
/// 使用生成的消息类 + MemoryPack 二进制序列化（JSON 兼容回退），消灭手写 MsgId 分支。
/// 当前迁移无身份依赖的查询类消息；带 sendToGatewayFunc 回调的复杂消息逐步迁移。
/// </summary>
public static class CenterDispatcher
{
    /// <summary>构建 Center 服务器的配置化分发器。未注册的 MsgId 由调用方回退旧字典。</summary>
    public static Framework.Protocol.MessageDispatcher BuildDispatcher(MatchHandler matchHandler)
    {
        var dispatcher = new Framework.Protocol.MessageDispatcher();

        // 房间列表查询（旧客户端 JSON / 新客户端 MemoryPack 双格式）
        dispatcher.RegisterSync<CenterListRooms>((ctx, msg) =>
        {
            var req = new CenterListRoomsRequest
            {
                SceneType = msg.SceneType,
                IncludePrivate = msg.IncludePrivate
            };
            var res = matchHandler.HandleListRoomsRequestAsync(req).GetAwaiter().GetResult();
            var resMsg = new CenterListRoomsResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Rooms = MapRooms(res.Rooms)
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 房间成员列表查询
        dispatcher.RegisterSync<RoomMemberList>((ctx, msg) =>
        {
            var req = new RoomMemberListRequest { RoomId = msg.RoomId };
            var res = matchHandler.HandleRoomMemberListRequestAsync(req).GetAwaiter().GetResult();
            var resMsg = new RoomMemberListResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        return dispatcher;
    }

    private static GenRoomInfo MapRoom(Shared.Messages.Center.RoomInfo r)
    {
        return new GenRoomInfo
        {
            RoomId = r.RoomId ?? string.Empty,
            RoomName = r.RoomName ?? string.Empty,
            SceneId = r.SceneId ?? string.Empty,
            SceneType = r.SceneType ?? string.Empty,
            BattleNodeId = r.BattleNodeId ?? string.Empty,
            OwnerUserId = r.OwnerUserId,
            IsPrivate = r.IsPrivate,
            HasPassword = r.HasPassword,
            MaxPlayers = r.MaxPlayers,
            CurrentPlayers = r.CurrentPlayers,
            RoomStatus = r.RoomStatus ?? string.Empty,
            CreatedAtUtc = new DateTimeOffset(r.CreatedAtUtc, TimeSpan.Zero).ToUnixTimeSeconds()
        };
    }

    private static List<GenRoomInfo> MapRooms(Shared.Messages.Center.RoomInfo[]? rooms)
    {
        if (rooms == null) return new List<GenRoomInfo>();
        return rooms.Select(MapRoom).ToList();
    }
}
