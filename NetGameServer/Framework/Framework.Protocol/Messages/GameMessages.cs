using Framework.Protocol;
using MemoryPack;

namespace Framework.Protocol.Generated;

// ============================================================
// 游戏服协议（迁移自 Protocol/defs/Game.def，方案 A 声明即协议）。
// 字段顺序与 .def 完全一致，保证 MemoryPack 线协议逐字节兼容。
// 每个消息：声明（本文件）+ IGameMessage 管线（源生成器补齐）+ 序列化（[MemoryPackable]）。
// optional 字段用 [GameField(Optional = true)] 标注（不改线格式，勿用可空值类型）。
// ============================================================

// ==== 聊天 60000-69999 ====

[MemoryPackable]
[GameMessage(60001, Target = "Game", Reply = "ChatSendResult")]
public partial class ChatSend
{
    public int SenderId { get; set; }
    public string SenderUniqueId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    [GameField(Optional = true)]
    public int ReceiverId { get; set; }
    public string ReceiverUniqueId { get; set; } = string.Empty;
    public int Channel { get; set; }
    public string RoomId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(60002, Target = "Game")]
public partial class ChatSendResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(60003, Target = "Game")]
public partial class ChatNotify
{
    public long MessageId { get; set; }
    public int SenderId { get; set; }
    public string SenderUniqueId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public int ReceiverId { get; set; }
    public string ReceiverUniqueId { get; set; } = string.Empty;
    public int Channel { get; set; }
    public string Content { get; set; } = string.Empty;
    public long SendTime { get; set; }
}

// ==== 好友 50000-59999 ====

[MemoryPackable]
[GameMessage(50001, Target = "Game", Reply = "FriendAddResult")]
public partial class FriendAdd
{
    public string TargetUniqueId { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50002, Target = "Game")]
public partial class FriendAddResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50003, Target = "Game", Reply = "FriendRemoveResult")]
public partial class FriendRemove
{
    public string FriendUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50004, Target = "Game")]
public partial class FriendRemoveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50005, Target = "Game", Reply = "FriendSetRemarkResult")]
public partial class FriendSetRemark
{
    public string FriendUniqueId { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50006, Target = "Game")]
public partial class FriendSetRemarkResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50007, Target = "Game", Reply = "FriendListResult")]
public partial class FriendGetList
{
}

[MemoryPackable]
[GameMessage(50008, Target = "Game")]
public partial class FriendListResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<FriendInfo> Friends { get; set; } = new();
}

[MemoryPackable]
[GameMessage(50009, Target = "Game", Reply = "FriendInviteGameResult")]
public partial class FriendInviteGame
{
    public string FriendUniqueId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50010, Target = "Game")]
public partial class FriendInviteGameResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50011, Target = "Game")]
public partial class FriendInviteGameNotify
{
    public string InviterUniqueId { get; set; } = string.Empty;
    public string InviterNickname { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50012, Target = "Game", Reply = "BlacklistAddResult")]
public partial class BlacklistAdd
{
    public string TargetUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50013, Target = "Game")]
public partial class BlacklistAddResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50014, Target = "Game", Reply = "BlacklistRemoveResult")]
public partial class BlacklistRemove
{
    public string TargetUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50015, Target = "Game")]
public partial class BlacklistRemoveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50016, Target = "Game", Reply = "BlacklistListResult")]
public partial class BlacklistGetList
{
}

[MemoryPackable]
[GameMessage(50017, Target = "Game")]
public partial class BlacklistListResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<BlacklistInfo> Blacklists { get; set; } = new();
}

[MemoryPackable]
[GameMessage(50018, Target = "Game", Reply = "FriendApplyResult")]
public partial class FriendApply
{
    public string TargetUniqueId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50019, Target = "Game")]
public partial class FriendApplyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50020, Target = "Game")]
public partial class FriendApplyNotify
{
    public long ApplyId { get; set; }
    public int RequesterUserId { get; set; }
    public string RequesterUniqueId { get; set; } = string.Empty;
    public string RequesterNickname { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long CreateTimeUtc { get; set; }
}

[MemoryPackable]
[GameMessage(50021, Target = "Game", Reply = "FriendApplyListResult")]
public partial class FriendApplyList
{
}

[MemoryPackable]
[GameMessage(50022, Target = "Game")]
public partial class FriendApplyListResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<FriendApplyInfo> Applies { get; set; } = new();
}

[MemoryPackable]
[GameMessage(50023, Target = "Game", Reply = "FriendApplyHandleResult")]
public partial class FriendApplyHandle
{
    public long ApplyId { get; set; }
    public bool Accept { get; set; }
}

[MemoryPackable]
[GameMessage(50024, Target = "Game")]
public partial class FriendApplyHandleResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50025, Target = "Game")]
public partial class FriendOnlineStatusNotify
{
    public int UserId { get; set; }
    public string UniqueId { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
}

[MemoryPackable]
[GameMessage(50026, Target = "Game", Reply = "FriendInviteGameAckResult")]
public partial class FriendInviteGameAck
{
    public string InviterUniqueId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public bool Accept { get; set; }
    public string Reason { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50027, Target = "Game")]
public partial class FriendInviteGameAckResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(50028, Target = "Game")]
public partial class FriendInviteGameAckNotify
{
    public string InviteeUniqueId { get; set; } = string.Empty;
    public string InviteeNickname { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public bool Accept { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// ==== 社交数据结构（内部引用类型） ====

[MemoryPackable]
[GameStruct]
public partial class FriendInfo
{
    public int FriendUserId { get; set; }
    public string FriendUniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
}

[MemoryPackable]
[GameStruct]
public partial class BlacklistInfo
{
    public int BlockedUserId { get; set; }
    public string BlockedUniqueId { get; set; } = string.Empty;
    public string BlockedNickname { get; set; } = string.Empty;
    public long AddTime { get; set; }
}

[MemoryPackable]
[GameStruct]
public partial class FriendApplyInfo
{
    public long ApplyId { get; set; }
    public int RequesterUserId { get; set; }
    public string RequesterUniqueId { get; set; } = string.Empty;
    public string RequesterNickname { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long CreateTimeUtc { get; set; }
}
