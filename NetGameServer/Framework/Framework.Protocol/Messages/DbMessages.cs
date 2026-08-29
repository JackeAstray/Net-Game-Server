using Framework.Protocol;
using MemoryPack;

// ============================================================
// Db protocol (programmatically migrated from Protocol/defs/Db.def).
// Field order matches .def exactly to keep the MemoryPack wire format byte-compatible.
// Declaration (this file) + IGameMessage plumbing (source generator) + [MemoryPackable].
// ============================================================

namespace Framework.Protocol.Generated;

[MemoryPackable]
[GameStruct]
public partial class DbFriendInfo
{
    public int FriendUserId { get; set; } = new();
    public string FriendUniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

[MemoryPackable]
[GameStruct]
public partial class DbBlacklistInfo
{
    public int BlockedUserId { get; set; } = new();
    public string BlockedUniqueId { get; set; } = string.Empty;
    public string BlockedNickname { get; set; } = string.Empty;
    public long AddTime { get; set; } = new();
}

[MemoryPackable]
[GameStruct]
public partial class DbFriendApplyInfo
{
    public long ApplyId { get; set; } = new();
    public int RequesterUserId { get; set; } = new();
    public string RequesterUniqueId { get; set; } = string.Empty;
    public string RequesterNickname { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long CreateTimeUtc { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1000, Target = "Db", Reply = "DbGetMaxUidResult", Internal = true)]
public partial class DbGetMaxUid
{
}

[MemoryPackable]
[GameMessage(1100, Target = "Db", Internal = true)]
public partial class DbGetMaxUidResult
{
    public long MaxUid { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1001, Target = "Db", Reply = "DbLoginVerifyResult", Internal = true)]
public partial class DbLoginVerify
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1101, Target = "Db", Internal = true)]
public partial class DbLoginVerifyResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public long UserId { get; set; } = new();
    public string UniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public long LastLoginTime { get; set; } = new();
    public int LoginCount { get; set; } = new();
    public bool IsAdmin { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1002, Target = "Db", Reply = "DbRegisterVerifyResult", Internal = true)]
public partial class DbRegisterVerify
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public long Uid { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1102, Target = "Db", Internal = true)]
public partial class DbRegisterVerifyResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1003, Target = "Db", Reply = "DbAccountQueryResult", Internal = true)]
public partial class DbAccountQuery
{
    public string Account { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1103, Target = "Db", Internal = true)]
public partial class DbAccountQueryResult
{
    public bool Exists { get; set; } = new();
    public bool IsOnline { get; set; } = new();
    public bool IsLocked { get; set; } = new();
    public bool IsAdmin { get; set; } = new();
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1004, Target = "Db", Reply = "DbOnlineStatsResult", Internal = true)]
public partial class DbOnlineStats
{
}

[MemoryPackable]
[GameMessage(1104, Target = "Db", Internal = true)]
public partial class DbOnlineStatsResult
{
    public int OnlineCount { get; set; } = new();
    public int OfflineCount { get; set; } = new();
    public int TotalCount { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1005, Target = "Db", Reply = "DbUpdateOnlineStateResult", Internal = true)]
public partial class DbUpdateOnlineState
{
    public int UserId { get; set; } = new();
    public bool IsOnline { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1105, Target = "Db", Internal = true)]
public partial class DbUpdateOnlineStateResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1006, Target = "Db", Reply = "DbFriendAddResult", Internal = true)]
public partial class DbFriendAdd
{
    public int UserId { get; set; } = new();
    public string FriendUniqueId { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1106, Target = "Db", Internal = true)]
public partial class DbFriendAddResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1007, Target = "Db", Reply = "DbFriendRemoveResult", Internal = true)]
public partial class DbFriendRemove
{
    public int UserId { get; set; } = new();
    public string FriendUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1107, Target = "Db", Internal = true)]
public partial class DbFriendRemoveResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1008, Target = "Db", Reply = "DbFriendSetRemarkResult", Internal = true)]
public partial class DbFriendSetRemark
{
    public int UserId { get; set; } = new();
    public string FriendUniqueId { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1108, Target = "Db", Internal = true)]
public partial class DbFriendSetRemarkResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1009, Target = "Db", Reply = "DbFriendListResult", Internal = true)]
public partial class DbFriendGetList
{
    public int UserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1109, Target = "Db", Internal = true)]
public partial class DbFriendListResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public List<DbFriendInfo> Friends { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1010, Target = "Db", Reply = "DbChangePasswordResult", Internal = true)]
public partial class DbChangePassword
{
    public int UserId { get; set; } = new();
    public string Account { get; set; } = string.Empty;
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1110, Target = "Db", Internal = true)]
public partial class DbChangePasswordResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1011, Target = "Db", Reply = "DbResetPasswordByEmailResult", Internal = true)]
public partial class DbResetPasswordByEmail
{
    public string Account { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1111, Target = "Db", Internal = true)]
public partial class DbResetPasswordByEmailResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1012, Target = "Db", Reply = "DbBlacklistAddResult", Internal = true)]
public partial class DbBlacklistAdd
{
    public int UserId { get; set; } = new();
    public string TargetUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1112, Target = "Db", Internal = true)]
public partial class DbBlacklistAddResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1013, Target = "Db", Reply = "DbBlacklistRemoveResult", Internal = true)]
public partial class DbBlacklistRemove
{
    public int UserId { get; set; } = new();
    public string TargetUniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1113, Target = "Db", Internal = true)]
public partial class DbBlacklistRemoveResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1014, Target = "Db", Reply = "DbBlacklistListResult", Internal = true)]
public partial class DbBlacklistGetList
{
    public int UserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1114, Target = "Db", Internal = true)]
public partial class DbBlacklistListResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public List<DbBlacklistInfo> Blacklists { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1015, Target = "Db", Reply = "DbResolveUserByUniqueIdResult", Internal = true)]
public partial class DbResolveUserByUniqueId
{
    public string UniqueId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1115, Target = "Db", Internal = true)]
public partial class DbResolveUserByUniqueIdResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public int UserId { get; set; } = new();
    public string Nickname { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1016, Target = "Db", Reply = "DbResolveUserByUserIdResult", Internal = true)]
public partial class DbResolveUserByUserId
{
    public int UserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1116, Target = "Db", Internal = true)]
public partial class DbResolveUserByUserIdResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1017, Target = "Db", Reply = "DbFriendApplyCreateResult", Internal = true)]
public partial class DbFriendApplyCreate
{
    public int RequesterUserId { get; set; } = new();
    public string TargetUniqueId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1117, Target = "Db", Internal = true)]
public partial class DbFriendApplyCreateResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(1018, Target = "Db", Reply = "DbFriendApplyListResult", Internal = true)]
public partial class DbFriendApplyList
{
    public int UserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1118, Target = "Db", Internal = true)]
public partial class DbFriendApplyListResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public List<DbFriendApplyInfo> Applies { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1019, Target = "Db", Reply = "DbFriendApplyHandleResult", Internal = true)]
public partial class DbFriendApplyHandle
{
    public long ApplyId { get; set; } = new();
    public int UserId { get; set; } = new();
    public bool Accept { get; set; } = new();
}

[MemoryPackable]
[GameMessage(1119, Target = "Db", Internal = true)]
public partial class DbFriendApplyHandleResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

