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

    /// <summary>网关会话（房间广播等场景需要原始会话）。</summary>
    public ISession GatewaySession => gatewaySession;

    /// <summary>路由元数据中的玩家身份（由收包入口注入）。</summary>
    public int RoutedUserId { get; set; }
    public string RoutedUid { get; set; } = string.Empty;
    public string RoutedNickname { get; set; } = string.Empty;

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

    /// <summary>
    /// 通知广播（对标旧 SendToGateway 的多网关路由）：优先按客户端会话路由到其网关会话，
    /// 找不到时回退当前网关会话。通知对象以 JSON 序列化（与旧协议一致，兼容旧客户端）。
    /// </summary>
    public void Notify(long targetSessionId, int msgId, object notification)
    {
        byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(notification);
        byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(responsePayload, targetSessionId);
        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
        try
        {
            if (targetSessionId > 0
                && Center.Handlers.NodeManager.Instance.TryGetGatewaySessionByClientSessionId(targetSessionId, out var routed)
                && routed.IsConnected)
            {
                routed.Send(packet.AsSpan(0, totalLength).ToArray());
                return;
            }
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

        // 房间准备（带通知广播回调：状态变化通知房间成员）
        dispatcher.RegisterSync<RoomReady>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomReadyRequest { RoomId = msg.RoomId, IsReady = msg.IsReady };
            var res = matchHandler.HandleRoomReadyRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname,
                req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            var resMsg = new RoomReadyResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 房主转移
        dispatcher.RegisterSync<RoomTransferOwner>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomTransferOwnerRequest { RoomId = msg.RoomId, TargetUserId = msg.TargetUserId };
            var res = matchHandler.HandleRoomTransferOwnerRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            var resMsg = new RoomTransferOwnerResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 踢出房间成员
        dispatcher.RegisterSync<RoomKickMember>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomKickMemberRequest { RoomId = msg.RoomId, TargetUserId = msg.TargetUserId };
            var res = matchHandler.HandleRoomKickMemberRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            var resMsg = new RoomKickMemberResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 匹配（排队中返回 false + 提示消息；匹配成功广播给所有匹配玩家）
        dispatcher.RegisterSync<CenterMatch>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterMatchRequest { CategoryId = msg.CategoryId };
            var res = matchHandler.HandleMatchRequestAsync(
                ctx.ClientSessionId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            if (res != null)
            {
                ctx.Send(new CenterMatchResult
                {
                    Success = res.Success,
                    Message = res.Message ?? string.Empty,
                    RoomId = res.RoomId ?? string.Empty,
                    BattleNodeId = res.BattleNodeId ?? string.Empty,
                    SceneId = res.SceneId ?? string.Empty,
                    SceneType = res.SceneType ?? string.Empty
                });
            }
        }, jsonFallback: true);

        // 创建房间
        dispatcher.RegisterSync<CenterCreateRoom>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterCreateRoomRequest
            {
                SceneType = msg.SceneType,
                IsPrivate = msg.IsPrivate,
                RoomName = msg.RoomName,
                Password = msg.Password,
                MaxPlayers = msg.MaxPlayers
            };
            var res = matchHandler.HandleCreateRoomRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname, req)
                .GetAwaiter().GetResult();
            ctx.Send(new CenterCreateRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty,
                RoomName = res.RoomName ?? string.Empty,
                BattleNodeId = res.BattleNodeId ?? string.Empty,
                SceneId = res.SceneId ?? string.Empty,
                HasPassword = res.HasPassword,
                MaxPlayers = res.MaxPlayers,
                CurrentPlayers = res.CurrentPlayers
            });
        }, jsonFallback: true);

        // 加入房间
        dispatcher.RegisterSync<CenterJoinRoom>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterJoinRoomRequest { RoomId = msg.RoomId, Password = msg.Password };
            var res = matchHandler.HandleJoinRoomRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname, req)
                .GetAwaiter().GetResult();
            ctx.Send(new CenterJoinRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty,
                RoomName = res.RoomName ?? string.Empty,
                BattleNodeId = res.BattleNodeId ?? string.Empty,
                SceneId = res.SceneId ?? string.Empty,
                SceneType = res.SceneType ?? string.Empty,
                HasPassword = res.HasPassword,
                MaxPlayers = res.MaxPlayers,
                CurrentPlayers = res.CurrentPlayers
            });
        }, jsonFallback: true);

        // 关闭房间
        dispatcher.RegisterSync<CenterCloseRoom>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterCloseRoomRequest { RoomId = msg.RoomId };
            var res = matchHandler.HandleCloseRoomRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            ctx.Send(new CenterCloseRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty
            });
        }, jsonFallback: true);

        // 离开房间（房主自动转移/空房关闭通知）
        dispatcher.RegisterSync<CenterLeaveRoom>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterLeaveRoomRequest { RoomId = msg.RoomId };
            var res = matchHandler.HandleLeaveRoomRequestAsync(
                ctx.ClientSessionId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            ctx.Send(new CenterLeaveRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty
            });
        }, jsonFallback: true);

        // 更新房间设置
        dispatcher.RegisterSync<CenterUpdateRoomSettings>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterUpdateRoomSettingsRequest
            {
                RoomId = msg.RoomId,
                SceneType = msg.SceneType,
                RoomName = msg.RoomName,
                Password = msg.Password,
                MaxPlayers = msg.MaxPlayers,
                IsPrivate = msg.IsPrivate,
                CustomRules = msg.CustomRules
            };
            var res = matchHandler.HandleUpdateRoomSettingsRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            ctx.Send(new CenterUpdateRoomSettingsResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty
            });
        }, jsonFallback: true);

        // 开始游戏
        dispatcher.RegisterSync<CenterStartRoomGame>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterStartRoomGameRequest { RoomId = msg.RoomId };
            var res = matchHandler.HandleStartRoomGameRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            ctx.Send(new CenterStartRoomGameResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty,
                BattleNodeId = res.BattleNodeId ?? string.Empty,
                SceneId = res.SceneId ?? string.Empty,
                SceneType = res.SceneType ?? string.Empty
            });
        }, jsonFallback: true);

        // 房间聊天
        dispatcher.RegisterSync<CenterRoomChat>((ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterRoomChatRequest { RoomId = msg.RoomId, Content = msg.Content };
            var res = matchHandler.HandleRoomChatRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname,
                req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                .GetAwaiter().GetResult();
            ctx.Send(new CenterRoomChatResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty
            });
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
