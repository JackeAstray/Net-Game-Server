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

            // 安全验证: 如果当前没有绑定(测试期)可暂不严格拦截。但如果绑定了或者强需求，则必须验证
            if (realSenderId != 0 && realSenderId != request.SenderId)
            {
                var errorResponse = new SendChatResponse { Success = false, Message = "非法操作：身份伪造。" };
                var errPayload = Json.SerializeToUtf8Bytes(errorResponse);
                var errData = PacketBuilder.BuildSessionWrapperPacket(session.SessionId, MessageIds.ChatMessageRes, errPayload);
                var errWrapper = new byte[errData.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(errWrapper.AsSpan(0, 4), errData.Length);
                errData.CopyTo(errWrapper.AsSpan(4));
                session.Send(errWrapper);
                return;
            }

            // 更新真实姓名和ID（如果从管理器取到）以防伪造
            int actualSenderId = realSenderId != 0 ? realSenderId : request.SenderId;

            // 创建聊天通知
            var notification = new ReceiveChatNotification
            {
                Message = new ChatMessage
                {
                    Id = new Random().Next(), // 生成一个随机的新Id
                    SenderId = actualSenderId,
                    SenderName = request.SenderName, // 可更从 PlayerManager 里取真名
                    ReceiverId = request.ReceiverId,
                    Channel = request.Channel,
                    Content = request.Content,
                    SendTime = DateTime.UtcNow
                }
            };

            // 先返回发送成功的响应
            var response = new SendChatResponse { Success = true, Message = "消息处理成功。" };
            var responsePayload = Json.SerializeToUtf8Bytes(response);
            var responseData = PacketBuilder.BuildSessionWrapperPacket(session.SessionId, MessageIds.ChatMessageRes, responsePayload);

            // Build the gateway wrap length for TcpClientWrapper
            var responseWrapper = new byte[responseData.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(responseWrapper.AsSpan(0, 4), responseData.Length);
            responseData.CopyTo(responseWrapper.AsSpan(4));
            session.Send(responseWrapper);

            var notifPayload = Json.SerializeToUtf8Bytes(notification);

            // 根据频道处理广播目标
            if (request.Channel == ChatChannel.Friend && request.ReceiverId.HasValue)
            {
                // 发送给特定的好友
                long targetSessionId = Game.Managers.PlayerSessionManager.Instance.GetSessionIdByUserId(request.ReceiverId.Value);
                if (targetSessionId != 0)
                {
                    var notifData = PacketBuilder.BuildSessionWrapperPacket(targetSessionId, MessageIds.ChatMessageNotif, notifPayload);
                    var notifWrapper = new byte[notifData.Length + 4];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(notifWrapper.AsSpan(0, 4), notifData.Length);
                    notifData.CopyTo(notifWrapper.AsSpan(4));
                    session.Send(notifWrapper);
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
                var notifData = PacketBuilder.BuildSessionWrapperPacket(0, MessageIds.ChatMessageNotif, notifPayload);

                // Build the gateway wrap length for TcpClientWrapper
                var notifWrapper = new byte[notifData.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(notifWrapper.AsSpan(0, 4), notifData.Length);
                notifData.CopyTo(notifWrapper.AsSpan(4));
                session.Send(notifWrapper);
            }
        }
    }
}