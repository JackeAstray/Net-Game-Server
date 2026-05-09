using System;
using Shared.Data.Chat;

namespace Shared.Messages.Chat
{
    public class SendChatRequest
    {
        public int SenderId { get; set; }
        public string SenderName { get; set; }
        public int? ReceiverId { get; set; }
        public ChatChannel Channel { get; set; }
        public string Content { get; set; }
    }

    public class SendChatResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class ReceiveChatNotification
    {
        public ChatMessage Message { get; set; }
    }
}