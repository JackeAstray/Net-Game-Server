using System;

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
    /// 聊天消息载荷（**纯网络传输 DTO**，非持久化实体）。
    /// 说明（清理项）：此前带 EF 的 <c>[Key]</c> 特性，但全仓并无 <c>DbSet&lt;ChatMessage&gt;</c>
    /// （DefaultDbContext 只登记 User/UidCounter/Friend/Blacklist/FriendRequest/Guild/GuildMember），
    /// 属聊天记录持久化方案移除后的遗留标记，会误导读者以为该类型会落库，故删除。
    ///
    /// 另注：<see cref="Id"/> 是 Game 进程内单调自增（跨节点/重启不唯一），仅供客户端排序提示，
    /// 不可当全局唯一键使用；<see cref="SendTime"/> 沿用既有字段名（属线上 JSON 契约，
    /// 重命名为 <c>…Utc</c> 会破坏与客户端的兼容，故保留）。
    /// </summary>
    public class ChatMessage
    {
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