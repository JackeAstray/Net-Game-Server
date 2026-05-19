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
        public string Account { get; set; }
        public string Password { get; set; }
    }

    public class LoginVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public long UserId { get; set; }
        public string UniqueId { get; set; }
        public string Nickname { get; set; }
        public string Email { get; set; }
        public DateTime LastLoginTime { get; set; }
        public int LoginCount { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class RegisterVerifyRequest
    {
        public string Account { get; set; }
        public string Password { get; set; }
        public string Nickname { get; set; }
        public long Uid { get; set; }
    }

    public class RegisterVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class AccountQueryRequest
    {
        public string Account { get; set; }
    }

    public class AccountQueryResponse
    {
        public bool Exists { get; set; }
        public bool IsOnline { get; set; }
        public bool IsLocked { get; set; }
        public bool IsAdmin { get; set; }
        public string Email { get; set; }
        public string Message { get; set; }
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
        public string Account { get; set; }
        public string OldPassword { get; set; }
        public string NewPassword { get; set; }
    }

    public class ChangePasswordVerifyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class ResetPasswordByEmailRequest
    {
        public string Account { get; set; }
        public string Email { get; set; }
        public string TemporaryPassword { get; set; }
    }

    public class ResetPasswordByEmailResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    // --- Friend System Models ---
    public class DbAddFriendRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; }
        public string Remark { get; set; }
    }

    public class DbAddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DbRemoveFriendRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; }
    }

    public class DbRemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DbSetFriendRemarkRequest
    {
        public int UserId { get; set; }
        public string FriendUniqueId { get; set; }
        public string Remark { get; set; }
    }

    public class DbSetFriendRemarkResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DbGetFriendsRequest
    {
        public int UserId { get; set; }
    }

    public class DbFriendItem
    {
        public int FriendUserId { get; set; }
        public string FriendUniqueId { get; set; }
        public string FriendNickname { get; set; }
        public string Remark { get; set; }
        public DateTime AddTime { get; set; }
    }

    public class DbGetFriendsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<DbFriendItem> Friends { get; set; }
    }

    public class DbAddBlacklistRequest
    {
        public int UserId { get; set; }
        public string TargetUniqueId { get; set; }
    }

    public class DbAddBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int TargetUserId { get; set; }
    }

    public class DbRemoveBlacklistRequest
    {
        public int UserId { get; set; }
        public string TargetUniqueId { get; set; }
    }

    public class DbRemoveBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int TargetUserId { get; set; }
    }

    public class DbGetBlacklistRequest
    {
        public int UserId { get; set; }
    }

    public class DbBlacklistItem
    {
        public int BlockedUserId { get; set; }
        public string BlockedUniqueId { get; set; }
        public string BlockedNickname { get; set; }
        public DateTime AddTime { get; set; }
    }

    public class DbGetBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<DbBlacklistItem> Blacklists { get; set; }
    }

    public class DbResolveUserByUniqueIdRequest
    {
        public string UniqueId { get; set; }
    }

    public class DbResolveUserByUniqueIdResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int UserId { get; set; }
        public string UniqueId { get; set; }
        public string Nickname { get; set; }
    }

    public class DbResolveUserByUserIdRequest
    {
        public int UserId { get; set; }
    }

    public class DbResolveUserByUserIdResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int UserId { get; set; }
        public string UniqueId { get; set; }
        public string Nickname { get; set; }
    }
}
