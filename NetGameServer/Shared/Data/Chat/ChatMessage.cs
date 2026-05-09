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
        Match = 5   //对局所有人聊天
    }

    /// <summary>
    /// 聊天消息实体类，表示一条聊天记录。
    /// </summary>
    public class ChatMessage
    {
        [Key]
        public int Id { get; set; }

        public int SenderId { get; set; }

        public string SenderName { get; set; }

        public int? ReceiverId { get; set; }

        public ChatChannel Channel { get; set; }

        public string Content { get; set; }

        public DateTime SendTime { get; set; }
    }
}