using System;
using System.ComponentModel.DataAnnotations;

namespace Shared.Data.Chat
{
    public enum ChatChannel
    {
        World = 1,
        Channel = 2,
        Friend = 3,
        Team = 4,   //对局队友聊天
        Match = 5,   //对局所有人聊天
        Room = 6,     //房间聊天
        Guild = 7    //公会频道（广播给同公会在线成员）
    }

    /// <summary>
    /// 聊天消息实体类，表示一条聊天记录。
    /// </summary>
    public class ChatMessage
    {
        [Key]
        public int Id { get; set; }

        public int SenderId { get; set; }

        public string SenderUniqueId { get; set; } = string.Empty;

        public string SenderName { get; set; } = string.Empty;

        public int? ReceiverId { get; set; }

        public string ReceiverUniqueId { get; set; } = string.Empty;

        public ChatChannel Channel { get; set; }

        public string RoomId { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public DateTime SendTime { get; set; }
    }
}