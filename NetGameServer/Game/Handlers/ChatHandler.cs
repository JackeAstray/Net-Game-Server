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

        public ChatHandler(NetworkManager networkManager)
        {
            this.networkManager = networkManager;
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
        private void HandleSendChatRequest(ISession session, SendChatRequest request)
        {
            int realSenderId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);

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

            if (request.Channel == ChatChannel.Friend && request.ReceiverId.HasValue)
            {
                if (Game.Handlers.FriendHandler.IsBlockedByTarget(request.ReceiverId.Value, actualSenderId))
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
            }

            // 创建聊天通知
            var notification = new ReceiveChatNotification
            {
                Message = new ChatMessage
                {
                    Id = new Random().Next(), // 生成一个随机的新Id
                    SenderId = actualSenderId,
                    SenderName = $"Player_{actualSenderId}",
                    ReceiverId = request.ReceiverId,
                    Channel = request.Channel,
                    Content = request.Content,
                    SendTime = DateTime.UtcNow
                }
            };

            // 先返回发送成功的响应
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

            // 根据频道处理广播目标
            if (request.Channel == ChatChannel.Friend && request.ReceiverId.HasValue)
            {
                // 发送给特定的好友
                long targetSessionId = Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(request.ReceiverId.Value);
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
                // TODO: 从组队系统获取所有SessionID进行遍历投递。这里演示暂不实现具体业务调用
                // foreach(var memberSessionId in teamManager.GetTeamSessionIds(actualSenderId))
            }
            else // World or Channel
            {
                // 发送通知给所有的客户端（广播）, 通过 SessionId = 0 指示网关广播
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
    }
}