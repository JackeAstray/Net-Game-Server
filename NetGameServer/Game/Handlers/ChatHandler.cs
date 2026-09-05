using Shared.Messages;
using Shared.Messages.Chat;
using Shared.Data.Chat;
using Shared;
using Network;
using Network.Routing;
using Game.Network;

namespace Game.Handlers
{
    public class ChatHandler
    {
        private readonly NetworkManager networkManager;

        // 安全加固：聊天频率限制（每会话最小发送间隔）与内容上限
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> lastChatAt = new();
        private const int MaxChatContentLength = 200;
        private const long MinChatIntervalMs = 1000;

        public ChatHandler(NetworkManager networkManager)
        {
            this.networkManager = networkManager;
        }

        /// <summary>
        /// 会话断开时清理聊天频率限制状态（V5/V13 修复：防 lastChatAt 无界增长）。
        /// </summary>
        public static void RemoveSession(long clientSessionId) => lastChatAt.TryRemove(clientSessionId, out _);

        /// <summary>清理超过 16 个最小间隔（约 16s）未活动的限频记录（V13 兜底）。</summary>
        private static void SweepStaleLastChatAt()
        {
            long cutoff = Environment.TickCount64 - MinChatIntervalMs * 16;
            foreach (var kv in lastChatAt)
            {
                if (kv.Value < cutoff)
                {
                    lastChatAt.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>
        /// 注册消息处理器，将ChatMessageReq消息绑定到HandleSendChatRequestRaw方法
        /// </summary>
        /// <param name="router"></param>
        public void Register(MessageRouter router)
        {
            router.RegisterHandler(MessageIds.ChatMessageReq, HandleSendChatRequestRaw);
        }

        /// <summary>
        /// 处理发送聊天消息的原始请求，解析消息内容并调用具体的处理方法
        /// </summary>
        /// <param name="session"></param>
        /// <param name="payload"></param>
        private void HandleSendChatRequestRaw(ISession session, ReadOnlyMemory<byte> payload)
        {
            var jsonString = System.Text.Encoding.UTF8.GetString(payload.Span);
            var request = Json.Deserialize<SendChatRequest>(jsonString);
            if (request != null)
            {
                HandleSendChatRequest(session, request);
            }
        }

        /// <summary>
        /// 处理发送聊天消息的请求，创建聊天通知并广播给相关玩家
        /// </summary>
        /// <param name="session">发送请求的会话</param>
        /// <param name="request">发送聊天消息的请求对象</param>
        public void HandleSendChatRequest(ISession session, SendChatRequest request)
        {
            int realSenderId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            string realSenderUid = Game.Managers.PlayerSessionManager.Instance.GetUidBySessionId(session.SessionId);

            if (realSenderId <= 0)
            {
                var errorResponse = new SendChatResponse { Success = false, Message = "会话未登录或未绑定。" };
                var errPayload = Json.SerializeToUtf8Bytes(errorResponse);
                var routedErrPayload = Shared.RouteMetadata.AttachTargetSessionId(errPayload, session.SessionId);
                var errData = PacketBuilder.BuildPacket(MessageIds.ChatMessageRes, routedErrPayload, out int errLength);
                try
                {
                    session.Send(errData.AsSpan(0, errLength).ToArray());
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(errData);
                }
                return;
            }

            if (request.SenderId > 0 && realSenderId != request.SenderId)
            {
                var errorResponse = new SendChatResponse { Success = false, Message = "非法操作：身份伪造。" };
                var errPayload = Json.SerializeToUtf8Bytes(errorResponse);
                var routedErrPayload = Shared.RouteMetadata.AttachTargetSessionId(errPayload, session.SessionId);
                var errData = PacketBuilder.BuildPacket(MessageIds.ChatMessageRes, routedErrPayload, out int errLength);
                try
                {
                    session.Send(errData.AsSpan(0, errLength).ToArray());
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(errData);
                }
                return;
            }

            int actualSenderId = realSenderId;

            // 安全加固：频道/内容/频率校验。
            // 非法频道值若不做校验会落入下方默认分支导致"世界广播"（无效枚举即全员可见）。
            if (!Enum.IsDefined(typeof(ChatChannel), request.Channel))
            {
                SendChatError(session, "非法的聊天频道。");
                return;
            }

            // V16 修复：Game 节点只有世界/好友频道有可用的投递目标（房间/匹配成员关系在 Center/Battle）。
            // 此前 Team 频道静默丢弃（却回"成功"），Room/Match 频道落入 else 分支被当作全员广播（隐私泄露）。
            // 现在对这些频道显式拒绝，绝不回退成世界广播。
            if (request.Channel != ChatChannel.World && request.Channel != ChatChannel.Friend && request.Channel != ChatChannel.Guild)
            {
                SendChatError(session, "该频道暂不支持，请使用世界、公会或好友频道。");
                return;
            }

            // 公会频道预检：成员缓存未就绪时触发异步加载并拒绝本次投递（不回退其他频道，防串频）。
            if (request.Channel == ChatChannel.Guild && GuildHandler.GetCachedGuildMemberIds(actualSenderId) == null)
            {
                GuildHandler.WarmupGuildCache(session, session.SessionId, actualSenderId);
                SendChatError(session, "公会信息加载中，请稍后重试。");
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                SendChatError(session, "消息内容不能为空。");
                return;
            }
            if ((request.Content?.Length ?? 0) > MaxChatContentLength)
            {
                SendChatError(session, $"消息内容过长（上限 {MaxChatContentLength} 字）。");
                return;
            }

            long nowTick = Environment.TickCount64;
            // V13 修复：偶发清理超期限频记录（断开时已主动删除，这里兜底防无界增长）
            if (lastChatAt.Count >= 1024 && (lastChatAt.Count & 255) == 0)
            {
                SweepStaleLastChatAt();
            }
            if (lastChatAt.TryGetValue(session.SessionId, out long lastTick) && nowTick - lastTick < MinChatIntervalMs)
            {
                SendChatError(session, "发送消息过于频繁，请稍后再试。");
                return;
            }
            lastChatAt[session.SessionId] = nowTick;

            long targetSessionId = 0;
            int targetUserId = 0;
            string targetUid = string.Empty;
            if (request.Channel == ChatChannel.Friend)
            {
                if (!string.IsNullOrWhiteSpace(request.ReceiverUniqueId))
                {
                    targetUid = request.ReceiverUniqueId.Trim();
                    targetSessionId = Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUid(targetUid);
                    if (targetSessionId != 0)
                    {
                        targetUserId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(targetSessionId);
                    }
                }
                else if (request.ReceiverId.HasValue)
                {
                    targetUserId = request.ReceiverId.Value;
                    targetSessionId = Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(targetUserId);
                    if (targetSessionId != 0)
                    {
                        targetUid = Game.Managers.PlayerSessionManager.Instance.GetUidBySessionId(targetSessionId);
                    }
                }

                if (targetUserId > 0 && Game.Handlers.FriendHandler.IsBlockedByTarget(targetUserId, actualSenderId))
                {
                    var blockedResponse = new SendChatResponse { Success = false, Message = "对方已将你拉黑。" };
                    var blockedPayload = Json.SerializeToUtf8Bytes(blockedResponse);
                    var routedBlockedPayload = Shared.RouteMetadata.AttachTargetSessionId(blockedPayload, session.SessionId);
                    var blockedData = PacketBuilder.BuildPacket(MessageIds.ChatMessageRes, routedBlockedPayload, out int blockedLength);
                    try
                    {
                        session.Send(blockedData.AsSpan(0, blockedLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(blockedData);
                    }
                    return;
                }

                // H1 修复：私聊好友校验 fail-closed。好友列表未加载（冷启动/刚登录/预热被延迟/预热失败）
                // 时无法确认好友关系，拒绝投递而非放行——此前"缓存未加载不强制拦截"导致可向任意在线玩家私聊。
                if (targetUserId > 0 && !Game.Handlers.FriendHandler.IsFriendListLoaded(actualSenderId))
                {
                    SendChatError(session, "好友关系校验中，请稍后重试。");
                    return;
                }
                if (targetUserId > 0 && !Game.Handlers.FriendHandler.IsFriend(actualSenderId, targetUserId))
                {
                    SendChatError(session, "只能向好友发送私聊消息。");
                    return;
                }
            }

            // H2 修复：发送者身份服务器权威化。昵称/UID 一律取会话绑定值，绝不采用客户端字段，
            // 防止伪造 SenderName（如"系统公告"/"GM"）与 SenderUniqueId 进行身份冒用。
            string actualSenderUid = realSenderUid ?? string.Empty;
            var senderName = string.IsNullOrWhiteSpace(actualSenderUid)
                ? $"Player_{actualSenderId}"
                : actualSenderUid;

            var notification = new ReceiveChatNotification
            {
                Message = new ChatMessage
                {
                    Id = Random.Shared.Next(),
                    SenderId = actualSenderId,
                    SenderUniqueId = actualSenderUid,
                    SenderName = senderName,
                    ReceiverId = targetUserId > 0 ? targetUserId : request.ReceiverId,
                    ReceiverUniqueId = targetUid,
                    Channel = request.Channel,
                    Content = request.Content ?? string.Empty,
                    SendTime = DateTime.UtcNow
                }
            };

            // 消息内容属用户隐私，仅 Debug 级别记录（带级别守卫，避免热路径开销）
            if (Log.IsDebugEnabled)
            {
                Log.Debug("聊天消息 SenderId:{SenderId} SenderUid:{SenderUid} SenderName:{SenderName} ReceiverId:{ReceiverId} ReceiverUid:{ReceiverUid} Channel:{Channel} Content:{Content}",
                    actualSenderId,
                    actualSenderUid ?? string.Empty,
                    senderName ?? string.Empty,
                    notification.Message.ReceiverId ?? 0,
                    notification.Message.ReceiverUniqueId ?? string.Empty,
                    request.Channel,
                    request.Content ?? string.Empty);
            }

            var response = new SendChatResponse { Success = true, Message = "消息处理成功。" };
            var responsePayload = Json.SerializeToUtf8Bytes(response);
            var routedResponsePayload = Shared.RouteMetadata.AttachTargetSessionId(responsePayload, session.SessionId);
            var responseData = PacketBuilder.BuildPacket(MessageIds.ChatMessageRes, routedResponsePayload, out int responseLength);
            try
            {
                session.Send(responseData.AsSpan(0, responseLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(responseData);
            }

            var notifPayload = Json.SerializeToUtf8Bytes(notification);

            if (request.Channel == ChatChannel.Friend)
            {
                if (targetSessionId != 0)
                {
                    var routedNotifPayload = Shared.RouteMetadata.AttachTargetSessionId(notifPayload, targetSessionId);
                    var notifData = PacketBuilder.BuildPacket(MessageIds.ChatMessageNotif, routedNotifPayload, out int notifLength);
                    try
                    {
                        // 跨网关修复：接收者可能位于另一网关，优先解析其所在网关会话（与 SendResponseBySessionId/SendKickedOff 一致），
                        // 不能沿用发送者来源网关——否则接收者在别的网关时消息被发错网关而丢失。
                        var targetSession = GameServerApp.ResolveGatewayForClient(targetSessionId) ?? session;
                        targetSession.Send(notifData.AsSpan(0, notifLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                    }
                }
            }
            else if (request.Channel == ChatChannel.Guild)
            {
                // 公会频道：向同公会在线成员定向投递（各成员可能在不同网关，逐会话解析网关）。
                var memberIds = Game.Handlers.GuildHandler.GetCachedGuildMemberIds(actualSenderId);
                if (memberIds != null)
                {
                    foreach (var memberId in memberIds)
                    {
                        if (memberId <= 0 || memberId == actualSenderId)
                        {
                            continue; // 发送者已收 response 回执
                        }
                        long memberSessionId = Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(memberId);
                        if (memberSessionId <= 0)
                        {
                            continue; // 成员不在线
                        }
                        var routedNotifPayload = Shared.RouteMetadata.AttachTargetSessionId(notifPayload, memberSessionId);
                        var notifData = PacketBuilder.BuildPacket(MessageIds.ChatMessageNotif, routedNotifPayload, out int notifLength);
                        try
                        {
                            var targetSession = GameServerApp.ResolveGatewayForClient(memberSessionId) ?? session;
                            targetSession.Send(notifData.AsSpan(0, notifLength).ToArray());
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                        }
                    }
                }
            }
            else
            {
                var routedNotifPayload = Shared.RouteMetadata.AttachBroadcast(notifPayload, true);
                var notifData = PacketBuilder.BuildPacket(MessageIds.ChatMessageNotif, routedNotifPayload, out int notifLength);
                try
                {
                    // 跨网关修复：世界频道广播必须投递给所有活跃网关（各网关再广播给其下客户端），
                    // 原实现只沿发送者来源网关发送，多网关部署下其他网关的玩家收不到世界消息。
                    var gatewaySessions = GameServerApp.GetAllActiveGatewaySessions();
                    if (gatewaySessions.Length > 0)
                    {
                        foreach (var gatewaySession in gatewaySessions)
                        {
                            gatewaySession.Send(notifData.AsSpan(0, notifLength).ToArray());
                        }
                    }
                    else
                    {
                        // 兜底：无活跃网关索引时退回发送者来源网关（单网关部署场景）
                        session.Send(notifData.AsSpan(0, notifLength).ToArray());
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                }
            }
        }

        /// <summary>向发送者返回聊天错误响应（统一错误回包，消除重复代码）。</summary>
        private static void SendChatError(ISession session, string message)
        {
            var errorResponse = new SendChatResponse { Success = false, Message = message };
            var errPayload = Json.SerializeToUtf8Bytes(errorResponse);
            var routedErrPayload = Shared.RouteMetadata.AttachTargetSessionId(errPayload, session.SessionId);
            var errData = PacketBuilder.BuildPacket(MessageIds.ChatMessageRes, routedErrPayload, out int errLength);
            try
            {
                session.Send(errData.AsSpan(0, errLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(errData);
            }
        }
    }
}