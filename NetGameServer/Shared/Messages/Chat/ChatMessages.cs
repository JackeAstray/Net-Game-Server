using System;
using Shared.Data.Chat;

namespace Shared.Messages.Chat
{
    public class SendChatRequest
    {
        public int SenderId { get; set; }
        public string SenderUniqueId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public int? ReceiverId { get; set; }
        public string ReceiverUniqueId { get; set; } = string.Empty;
        public ChatChannel Channel { get; set; }
        public string RoomId { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    public class SendChatResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ReceiveChatNotification
    {
        public ChatMessage Message { get; set; } = new();
    }
}