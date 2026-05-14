using System;

namespace Shared.Messages.Db
{
    public class GetMaxUidRequest
    {
    }

    public class GetMaxUidResponse
    {
        public int MaxUid { get; set; }
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

    // --- Friend System Models ---
    public class DbAddFriendRequest
    {
        public int UserId { get; set; }
        public int FriendUserId { get; set; }
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
        public int FriendUserId { get; set; }
    }

    public class DbRemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DbSetFriendRemarkRequest
    {
        public int UserId { get; set; }
        public int FriendUserId { get; set; }
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

    public class DbGetFriendsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<Shared.Data.Social.Friend> Friends { get; set; }
    }
}