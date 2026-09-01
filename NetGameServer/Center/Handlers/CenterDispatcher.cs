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
        dispatcher.Register<CenterListRooms>(async (ctx, msg) =>
        {
            var req = new CenterListRoomsRequest
            {
                SceneType = msg.SceneType,
                IncludePrivate = msg.IncludePrivate
            };
            var res = await matchHandler.HandleListRoomsRequestAsync(req);
            var resMsg = new CenterListRoomsResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Rooms = MapRooms(res.Rooms)
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 房间成员列表查询
        dispatcher.Register<RoomMemberList>(async (ctx, msg) =>
        {
            var req = new RoomMemberListRequest { RoomId = msg.RoomId };
            var res = await matchHandler.HandleRoomMemberListRequestAsync(req);
            var resMsg = new RoomMemberListResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 房间准备（带通知广播回调：状态变化通知房间成员）
        dispatcher.Register<RoomReady>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomReadyRequest { RoomId = msg.RoomId, IsReady = msg.IsReady };
            var res = await matchHandler.HandleRoomReadyRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname,
                req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            var resMsg = new RoomReadyResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 房主转移
        dispatcher.Register<RoomTransferOwner>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomTransferOwnerRequest { RoomId = msg.RoomId, TargetUserId = msg.TargetUserId };
            var res = await matchHandler.HandleRoomTransferOwnerRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            var resMsg = new RoomTransferOwnerResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 踢出房间成员
        dispatcher.Register<RoomKickMember>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new RoomKickMemberRequest { RoomId = msg.RoomId, TargetUserId = msg.TargetUserId };
            var res = await matchHandler.HandleRoomKickMemberRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            var resMsg = new RoomKickMemberResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                Room = res.Room != null ? MapRoom(res.Room) : null
            };
            ctx.Send(resMsg);
        }, jsonFallback: true);

        // 匹配（排队中返回 false + 提示消息；匹配成功广播给所有匹配玩家）
        dispatcher.Register<CenterMatch>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterMatchRequest { CategoryId = msg.CategoryId };
            var res = await matchHandler.HandleMatchRequestAsync(
                ctx.ClientSessionId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
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
        dispatcher.Register<CenterCreateRoom>(async (ctx, msg) =>
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
            var res = await matchHandler.HandleCreateRoomRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname, req)
                ;
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
        dispatcher.Register<CenterJoinRoom>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterJoinRoomRequest { RoomId = msg.RoomId, Password = msg.Password };
            var res = await matchHandler.HandleJoinRoomRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname, req)
                ;
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
        dispatcher.Register<CenterCloseRoom>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterCloseRoomRequest { RoomId = msg.RoomId };
            var res = await matchHandler.HandleCloseRoomRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            ctx.Send(new CenterCloseRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty
            });
        }, jsonFallback: true);

        // 离开房间（房主自动转移/空房关闭通知）
        dispatcher.Register<CenterLeaveRoom>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterLeaveRoomRequest { RoomId = msg.RoomId };
            var res = await matchHandler.HandleLeaveRoomRequestAsync(
                ctx.ClientSessionId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif),
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            ctx.Send(new CenterLeaveRoomResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                RoomId = res.RoomId ?? string.Empty
            });
        }, jsonFallback: true);

        // 更新房间设置
        dispatcher.Register<CenterUpdateRoomSettings>(async (ctx, msg) =>
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
            var res = await matchHandler.HandleUpdateRoomSettingsRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            ctx.Send(new CenterUpdateRoomSettingsResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty
            });
        }, jsonFallback: true);

        // 开始游戏
        dispatcher.Register<CenterStartRoomGame>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterStartRoomGameRequest { RoomId = msg.RoomId };
            var res = await matchHandler.HandleStartRoomGameRequestAsync(
                cctx.RoutedUserId, req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
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
        dispatcher.Register<CenterRoomChat>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var req = new CenterRoomChatRequest { RoomId = msg.RoomId, Content = msg.Content };
            var res = await matchHandler.HandleRoomChatRequestAsync(
                ctx.ClientSessionId, cctx.RoutedUserId, cctx.RoutedUid, cctx.RoutedNickname,
                req, cctx.GatewaySession,
                (gs, targetId, notifMsgId, notif) => cctx.Notify(targetId, notifMsgId, notif))
                ;
            ctx.Send(new CenterRoomChatResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty
            });
        }, jsonFallback: true);

        // ==== 实体在线迁移（C2 第二阶段：Center 协调中继，对标 KBE cellappmgr 实体搬迁） ====

        // 中继 91003：源 Battle -> 目标 Battle（目标恢复实体后回 91004）
        dispatcher.Register<EntityMigrateRequest>(async (ctx, msg) =>
        {
            // 发送方绑定（B7 收敛）：SourceNodeId 一律从认证会话推导，不信任消息体字段，
            // 防止任意持密钥节点伪造 SourceNodeId 制造"在途迁移"状态。
            var originNodeId = NodeManager.Instance.GetNodeIdBySession(((CenterSessionContext)ctx).GatewaySession);
            if (string.IsNullOrEmpty(originNodeId))
            {
                Shared.Log.Warning($"实体迁移中继拒绝：发送方不是已注册节点（无法推导来源）ClientSessionId:{msg.ClientSessionId} EntityId:{msg.EntityId}");
                return;
            }
            var target = NodeManager.Instance.GetNode(msg.TargetNodeId ?? string.Empty);
            if (target?.Session == null || !target.Session.IsConnected)
            {
                Shared.Log.Warning($"实体迁移中继失败：目标 Battle 节点不可用 TargetNodeId:{msg.TargetNodeId} ClientSessionId:{msg.ClientSessionId}");
                SendPacketToNode(((CenterSessionContext)ctx).GatewaySession, MessageIds.EntityMigrateResult, new EntityMigrateResult
                {
                    Success = false,
                    ClientSessionId = msg.ClientSessionId,
                    EntityId = msg.EntityId,
                    NewNodeId = msg.TargetNodeId ?? string.Empty,
                    Message = "目标 Battle 节点不可用"
                });
                return;
            }

            if (msg.ClientSessionId > 0)
            {
                pendingMigrationSource[msg.ClientSessionId] = new PendingRoute
                {
                    SourceNodeId = originNodeId,
                    TargetNodeId = msg.TargetNodeId ?? string.Empty,
                    CreatedTicks = DateTime.UtcNow.Ticks
                };
            }
            SendPacketToNode(target.Session, MessageIds.EntityMigrateRequest, msg);
            Shared.Log.Info($"Center 中继实体迁移 ClientSessionId:{msg.ClientSessionId} -> {msg.TargetNodeId} EntityType:{msg.EntityType} PropsBytes:{msg.Props?.Length ?? 0}");
        });

        // 回源 91004：目标 Battle -> 源 Battle（成功则源节点移除本地实体）；成功时同步通知 Gateway 切换绑定
        dispatcher.Register<EntityMigrateResult>(async (ctx, msg) =>
        {
            var cctx = (CenterSessionContext)ctx;
            var senderNodeId = NodeManager.Instance.GetNodeIdBySession(cctx.GatewaySession);

            // 回源校验（B7 收敛）：91004 必须来自"本次迁移的目标 Battle 节点"（pending.TargetNodeId），
            // 且成功回执必须存在在途迁移记录，否则视为伪造迁移结果（可强制删除他人实体/重绑会话）拒绝。
            if (msg.Success)
            {
                if (!pendingMigrationSource.TryGetValue(msg.ClientSessionId, out var pendingSuccess)
                    || string.IsNullOrEmpty(pendingSuccess.TargetNodeId))
                {
                    Shared.Log.Warning($"实体迁移成功回执无在途迁移记录，拒绝 ClientSessionId:{msg.ClientSessionId} 发送方:{senderNodeId}");
                    return;
                }
                if (!string.Equals(pendingSuccess.TargetNodeId, senderNodeId, StringComparison.Ordinal))
                {
                    Shared.Log.Warning($"实体迁移回执来源不匹配，拒绝 ClientSessionId:{msg.ClientSessionId} 期望目标:{pendingSuccess.TargetNodeId} 实际发送方:{senderNodeId}");
                    return;
                }
            }
            else if (pendingMigrationSource.TryGetValue(msg.ClientSessionId, out var pendingFail)
                && !string.IsNullOrEmpty(pendingFail.TargetNodeId)
                && !string.Equals(pendingFail.TargetNodeId, senderNodeId, StringComparison.Ordinal))
            {
                // 失败回执也要求来自目标节点（防伪造失败误导源节点/重绑状态）。
                Shared.Log.Warning($"实体迁移失败回执来源不匹配，拒绝 ClientSessionId:{msg.ClientSessionId} 期望目标:{pendingFail.TargetNodeId} 实际发送方:{senderNodeId}");
                return;
            }

            if (msg.Success)
            {
                // 多网关场景：优先通知玩家所属网关（按客户端会话路由）；路由缺失时广播全部网关
                // （各网关自行忽略不属于自己会话的绑定更新，避免只通知第一个网关导致错误路由）。
                var routed = new EntityMigrateRouted
                {
                    ClientSessionId = msg.ClientSessionId,
                    NewNodeId = msg.NewNodeId ?? string.Empty
                };
                if (msg.ClientSessionId > 0
                    && Center.Handlers.NodeManager.Instance.TryGetGatewaySessionByClientSessionId(msg.ClientSessionId, out var ownerGw)
                    && ownerGw.IsConnected)
                {
                    SendPacketToNode(ownerGw, MessageIds.EntityMigrateRouted, routed);
                    Shared.Log.Info($"Center 通知玩家所属 Gateway 切换 Battle 绑定 ClientSessionId:{msg.ClientSessionId} -> {msg.NewNodeId}");
                }
                else
                {
                    int notified = 0;
                    foreach (var gw in Center.Handlers.NodeManager.Instance.GetAllNodesByType("Gateway"))
                    {
                        if (gw.Session?.IsConnected == true)
                        {
                            SendPacketToNode(gw.Session, MessageIds.EntityMigrateRouted, routed);
                            notified++;
                        }
                    }
                    Shared.Log.Info($"Center 广播 Gateway 切换玩家 Battle 绑定 ClientSessionId:{msg.ClientSessionId} -> {msg.NewNodeId}（通知 {notified} 个网关）");
                }
            }

            if (pendingMigrationSource.TryRemove(msg.ClientSessionId, out var pendingMig)
                && !string.IsNullOrEmpty(pendingMig.SourceNodeId))
            {
                var source = NodeManager.Instance.GetNode(pendingMig.SourceNodeId);
                if (source?.Session != null && source.Session.IsConnected)
                {
                    SendPacketToNode(source.Session, MessageIds.EntityMigrateResult, msg);
                    return;
                }
                Shared.Log.Warning($"实体迁移回源失败：源 Battle 节点不可用 SourceNodeId:{pendingMig.SourceNodeId}");
            }
        });

        // ==== 实体远程调用中继（EntityCall：91001 前向 / 91002 回源，对标 KBE EntityCall 跨进程调用） ====

        // 前向 91001：源 Battle -> 目标 Battle（CallId>0 时记录回源路由；目标不可用直接回失败回执）
        dispatcher.Register<EntityRemoteCall>(async (ctx, msg) =>
        {
            var originNodeId = NodeManager.Instance.GetNodeIdBySession(((CenterSessionContext)ctx).GatewaySession) ?? string.Empty;
            var target = NodeManager.Instance.GetNode(msg.TargetNodeId ?? string.Empty);
            if (target?.Session == null || !target.Session.IsConnected)
            {
                Shared.Log.Warning($"实体远程调用中继失败：目标 Battle 节点不可用 TargetNodeId:{msg.TargetNodeId} EntityId:{msg.EntityId} Method:{msg.MethodName}");
                SendPacketToNode(((CenterSessionContext)ctx).GatewaySession, MessageIds.EntityRemoteCallResult, new EntityRemoteCallResult
                {
                    CallId = msg.CallId,
                    EntityId = msg.EntityId,
                    MethodName = msg.MethodName,
                    Success = false,
                    Result = Array.Empty<byte>()
                });
                return;
            }

            if (msg.CallId != 0 && !string.IsNullOrEmpty(originNodeId))
            {
                pendingEntityCallSource[msg.CallId] = new PendingRoute
                {
                    SourceNodeId = originNodeId,
                    CreatedTicks = DateTime.UtcNow.Ticks
                };
            }
            SendPacketToNode(target.Session, MessageIds.EntityRemoteCall, msg);
            Shared.Log.Info($"Center 中继实体远程调用 CallId:{msg.CallId} -> {msg.TargetNodeId} EntityId:{msg.EntityId} Method:{msg.MethodName}");
        });

        // 回源 91002：目标 Battle -> 源 Battle（调用方经 EntityCallHub.HandleResult 关联完成回执/超时）
        dispatcher.Register<EntityRemoteCallResult>(async (ctx, msg) =>
        {
            if (pendingEntityCallSource.TryRemove(msg.CallId, out var pendingCall) && !string.IsNullOrEmpty(pendingCall.SourceNodeId))
            {
                var source = NodeManager.Instance.GetNode(pendingCall.SourceNodeId);
                if (source?.Session != null && source.Session.IsConnected)
                {
                    SendPacketToNode(source.Session, MessageIds.EntityRemoteCallResult, msg);
                    return;
                }
            }
            Shared.Log.Warning($"实体远程调用回源失败：源节点不可用或未记录 CallId:{msg.CallId}");
        });

        // ==== 实体位置服务（91007 登记 / 91008 注销 / 91009 查询，对标 ET Location 代理，迭代 21） ====
        dispatcher.Register<EntityLocationRegister>(async (ctx, msg) =>
        {
            // 登记方绑定：登记的 NodeId 必须等于发送方认证会话身份，防位置注册表投毒
            // （任意持密钥节点为受害实体登记到自身/任意节点，劫持跨节点调用与迁移）。
            var senderNodeId = NodeManager.Instance.GetNodeIdBySession(((CenterSessionContext)ctx).GatewaySession);
            if (string.IsNullOrEmpty(senderNodeId)
                || !string.Equals(senderNodeId, msg.NodeId ?? string.Empty, StringComparison.Ordinal))
            {
                Shared.Log.Warning($"实体位置登记拒绝：发送方节点身份({senderNodeId})与登记 NodeId({msg.NodeId})不一致 EntityId:{msg.EntityId}");
                return;
            }
            EntityLocationService.Instance.Register(msg.EntityId, msg.NodeId ?? string.Empty);
        });
        dispatcher.Register<EntityLocationUnregister>(async (ctx, msg) =>
        {
            // 注销方绑定：仅实体当前所在节点可注销（防他人清空位置表）。
            var senderNodeId = NodeManager.Instance.GetNodeIdBySession(((CenterSessionContext)ctx).GatewaySession);
            var current = EntityLocationService.Instance.Locate(msg.EntityId);
            if (string.IsNullOrEmpty(senderNodeId)
                || (current != null && !string.Equals(current, senderNodeId, StringComparison.Ordinal)))
            {
                Shared.Log.Warning($"实体位置注销拒绝：发送方节点身份({senderNodeId})与实体当前所在节点({current})不一致 EntityId:{msg.EntityId}");
                return;
            }
            EntityLocationService.Instance.Unregister(msg.EntityId);
        });
        dispatcher.Register<EntityLocateRequest>(async (ctx, msg) =>
        {
            string? nodeId = EntityLocationService.Instance.Locate(msg.EntityId);
            var response = new EntityLocateResponse
            {
                EntityId = msg.EntityId,
                Found = !string.IsNullOrEmpty(nodeId),
                NodeId = nodeId ?? string.Empty
            };
            if (!string.IsNullOrEmpty(nodeId))
            {
                // 附带目标节点直连地址（host/port），供调用方建立 Battle↔Battle 直达链路
                var node = NodeManager.Instance.GetNode(nodeId);
                if (node != null)
                {
                    response.Host = node.Host;
                    response.Port = node.Port;
                }
            }
            var origin = ((CenterSessionContext)ctx).GatewaySession;
            if (origin != null && origin.IsConnected)
            {
                SendPacketToNode(origin, MessageIds.EntityLocateResponse, response);
            }
        });

        return dispatcher;
    }

    /// <summary>进行中的待回源路由（带时间戳，供超时清扫防无界增长）。</summary>
    private sealed class PendingRoute
    {
        /// <summary>源节点（迁移 91003 由认证会话推导 / 实体调用 91001 由认证会话推导），不信任消息体字段。</summary>
        public required string SourceNodeId;
        /// <summary>目标节点（迁移 91004 回源前校验发送方身份 == TargetNodeId，防伪造迁移结果）。</summary>
        public string TargetNodeId = string.Empty;
        public long CreatedTicks;
    }

    /// <summary>进行中的实体远程调用回源路由：CallId -> 源 Battle 节点（91002 回源用）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, PendingRoute> pendingEntityCallSource = new();

    /// <summary>进行中的实体迁移：ClientSessionId -> 源 Battle 节点（91004 回源用）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, PendingRoute> pendingMigrationSource = new();

    /// <summary>
    /// 清扫超时未回执的待回源路由（源节点死掉/回执丢失时防止 pending 表无界增长）。
    /// 由 Center 周期循环调用。
    /// </summary>
    public static void SweepPending(TimeSpan timeout)
    {
        long cutoff = DateTime.UtcNow.Ticks - timeout.Ticks;
        int removed = 0;
        foreach (var kv in pendingEntityCallSource.ToArray())
        {
            if (kv.Value.CreatedTicks < cutoff)
            {
                pendingEntityCallSource.TryRemove(kv.Key, out _);
                removed++;
            }
        }
        foreach (var kv in pendingMigrationSource.ToArray())
        {
            if (kv.Value.CreatedTicks < cutoff)
            {
                pendingMigrationSource.TryRemove(kv.Key, out _);
                removed++;
            }
        }
        if (removed > 0)
        {
            Shared.Log.Warning($"Center 已清扫超时待回源路由 {removed} 条（剩余 EntityCall:{pendingEntityCallSource.Count} Migration:{pendingMigrationSource.Count}）");
        }
    }

    /// <summary>向指定节点会话发送一条内部消息包（[MsgId][MemoryPack 负载]）。</summary>
    private static void SendPacketToNode(Network.ISession session, int msgId, IGameMessage message)
    {
        byte[] payload = message.Serialize();
        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
        try
        {
            session.Send(packet.AsSpan(0, totalLength).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }
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
