using System;

namespace Shared.Messages.Social
{
    public class AddFriendRequest
    {
        public string TargetUniqueId { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
    }

    public class AddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class RemoveFriendRequest
    {
        public string FriendUniqueId { get; set; } = string.Empty;
    }

    public class RemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class SetFriendRemarkRequest
    {
        public string FriendUniqueId { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
    }

    public class SetFriendRemarkResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GetFriendsRequest
    {
    }

    public class FriendInfo
    {
        public int FriendUserId { get; set; }
        public string FriendUniqueId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
    }

    public class GetFriendsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public FriendInfo[] Friends { get; set; } = Array.Empty<FriendInfo>();
    }

    public class InviteGameRequest
    {
        public string FriendUniqueId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public string SceneType { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
    }

    public class InviteGameResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class InviteGameNotification
    {
        public string InviterUniqueId { get; set; } = string.Empty;
        public string InviterNickname { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public string SceneType { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
    }

    public class AddBlacklistRequest
    {
        public string TargetUniqueId { get; set; } = string.Empty;
    }

    public class AddBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class RemoveBlacklistRequest
    {
        public string TargetUniqueId { get; set; } = string.Empty;
    }

    public class RemoveBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GetBlacklistRequest
    {
    }

    public class BlacklistInfo
    {
        public int BlockedUserId { get; set; }
        public string BlockedUniqueId { get; set; } = string.Empty;
        public string BlockedNickname { get; set; } = string.Empty;
        public DateTime AddTime { get; set; }
    }

    public class GetBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public BlacklistInfo[] Blacklists { get; set; } = Array.Empty<BlacklistInfo>();
    }

    public class FriendApplyRequest
    {
        public string TargetUniqueId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class FriendApplyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class FriendApplyNotification
    {
        public long ApplyId { get; set; }
        public int RequesterUserId { get; set; }
        public string RequesterUniqueId { get; set; } = string.Empty;
        public string RequesterNickname { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime CreateTimeUtc { get; set; }
    }

    public class FriendApplyListRequest
    {
    }

    public class FriendApplyInfo
    {
        public long ApplyId { get; set; }
        public int RequesterUserId { get; set; }
        public string RequesterUniqueId { get; set; } = string.Empty;
        public string RequesterNickname { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreateTimeUtc { get; set; }
    }

    public class FriendApplyListResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public FriendApplyInfo[] Applies { get; set; } = Array.Empty<FriendApplyInfo>();
    }

    public class FriendApplyHandleRequest
    {
        public long ApplyId { get; set; }
        public bool Accept { get; set; }
    }

    public class FriendApplyHandleResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class FriendOnlineStatusNotification
    {
        public int UserId { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
    }

    public class InviteGameAckRequest
    {
        public string InviterUniqueId { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public bool Accept { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class InviteGameAckResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class InviteGameAckNotification
    {
        public string InviteeUniqueId { get; set; } = string.Empty;
        public string InviteeNickname { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public bool Accept { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
