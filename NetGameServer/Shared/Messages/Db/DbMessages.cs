using System;

namespace Shared.Messages.Db
{
    public class GetMaxUidRequest
    {
    }

    public class GetMaxUidResponse
    {
        public long MaxUid { get; set; }
    }

    public class LoginVerifyRequest
    {
        public string Account { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long UserId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime LastLoginTime { get; set; }
        public int LoginCount { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class RegisterVerifyRequest
    {
        public string Account { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public long Uid { get; set; }
    }

    public class RegisterVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class AccountQueryRequest
    {
        public string Account { get; set; } = string.Empty;
    }

    public class AccountQueryResponse
    {
        public bool Exists { get; set; }
        public bool IsOnline { get; set; }
        public bool IsLocked { get; set; }
        public bool IsAdmin { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class OnlineStatsRequest
    {
    }

    public class OnlineStatsResponse
    {
        public int OnlineCount { get; set; }
        public int OfflineCount { get; set; }
        public int TotalCount { get; set; }
    }

    public class UpdateOnlineStateRequest
    {
        public int UserId { get; set; }
        public bool IsOnline { get; set; }
    }

    public class UpdateOnlineStateResponse
    {
        public bool Success { get; set; }
    }

    public class ChangePasswordVerifyRequest
    {
        public int UserId { get; set; }
        public string Account { get; set; } = string.Empty;
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public class ChangePasswordVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ResetPasswordByEmailRequest
    {
        public string Account { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string TemporaryPassword { get; set; } = string.Empty;
    }

    public class ResetPasswordByEmailResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // --- Friend System Models ---
    public class DbAddFriendRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
    }

    public class DbAddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class DbRemoveFriendRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; } = string.Empty;
    }

    public class DbRemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class DbSetFriendRemarkRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
    }

    public class DbSetFriendRemarkResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class DbGetFriendsRequest
    {
        public int UserId { get; set; }
    }

    public class DbFriendItem
    {
        public int FriendUserId { get; set; }
        public string FriendUniqueId { get; set; } = string.Empty;
        public string FriendNickname { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public DateTime AddTime { get; set; }
    }

    public class DbGetFriendsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<DbFriendItem> Friends { get; set; } = new();
    }

    public class DbAddBlacklistRequest
    {
        public int UserId { get; set; }
        public string TargetUniqueId { get; set; } = string.Empty;
    }

    public class DbAddBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int TargetUserId { get; set; }
    }

    public class DbRemoveBlacklistRequest
    {
        public int UserId { get; set; }
        public string TargetUniqueId { get; set; } = string.Empty;
    }

    public class DbRemoveBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int TargetUserId { get; set; }
    }

    public class DbGetBlacklistRequest
    {
        public int UserId { get; set; }
    }

    public class DbBlacklistItem
    {
        public int BlockedUserId { get; set; }
        public string BlockedUniqueId { get; set; } = string.Empty;
        public string BlockedNickname { get; set; } = string.Empty;
        public DateTime AddTime { get; set; }
    }

    public class DbGetBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<DbBlacklistItem> Blacklists { get; set; } = new();
    }

    public class DbResolveUserByUniqueIdRequest
    {
        public string UniqueId { get; set; } = string.Empty;
    }

    public class DbResolveUserByUniqueIdResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
    }

    public class DbResolveUserByUserIdRequest
    {
        public int UserId { get; set; }
    }

    public class DbResolveUserByUserIdResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
    }

    public class DbCreateFriendApplyRequest
    {
        public int RequesterUserId { get; set; }
        public string TargetUniqueId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class DbCreateFriendApplyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long ApplyId { get; set; }
        public int TargetUserId { get; set; }
    }

    public class DbGetFriendApplyListRequest
    {
        public int UserId { get; set; }
    }

    public class DbFriendApplyItem
    {
        public long ApplyId { get; set; }
        public int RequesterUserId { get; set; }
        public string RequesterUniqueId { get; set; } = string.Empty;
        public string RequesterNickname { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreateTimeUtc { get; set; }
    }

    public class DbGetFriendApplyListResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<DbFriendApplyItem> Applies { get; set; } = new();
    }

    public class DbHandleFriendApplyRequest
    {
        public int UserId { get; set; }
        public long ApplyId { get; set; }
        public bool Accept { get; set; }
    }

    public class DbHandleFriendApplyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RequesterUserId { get; set; }
        public int ReceiverUserId { get; set; }
    }
}
