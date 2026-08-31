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
            string actualSenderUid = string.IsNullOrWhiteSpace(realSenderUid) ? (request.SenderUniqueId ?? string.Empty) : realSenderUid;

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
            if (request.Channel != ChatChannel.World && request.Channel != ChatChannel.Friend)
            {
                SendChatError(session, "该频道暂不支持，请使用世界或好友频道。");
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

                // 私聊好友校验：仅当发送者好友列表已加载（缓存 warm）时强制互为好友，防止向任意玩家私聊；
                // 缓存未加载（冷启动/刚登录）时不强制拦截，避免误伤。
                if (targetUserId > 0
                    && Game.Handlers.FriendHandler.IsFriendListLoaded(actualSenderId)
                    && !Game.Handlers.FriendHandler.IsFriend(actualSenderId, targetUserId))
                {
                    SendChatError(session, "只能向好友发送私聊消息。");
                    return;
                }
            }

            var senderName = string.IsNullOrWhiteSpace(request.SenderName)
                ? $"Player_{actualSenderId}"
                : request.SenderName.Trim();

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
                        session.Send(notifData.AsSpan(0, notifLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                    }
                }
            }
            else if (request.Channel == ChatChannel.Team)
            {
            }
            else
            {
                var routedNotifPayload = Shared.RouteMetadata.AttachBroadcast(notifPayload, true);
                var notifData = PacketBuilder.BuildPacket(MessageIds.ChatMessageNotif, routedNotifPayload, out int notifLength);
                try
                {
                    session.Send(notifData.AsSpan(0, notifLength).ToArray());
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