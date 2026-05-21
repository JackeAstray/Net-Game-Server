using System;

namespace Shared.Messages.Social
{
    public class AddFriendRequest
    {
        public string TargetUniqueId { get; set; }
        public string Remark { get; set; }
    }

    public class AddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class RemoveFriendRequest
    {
        public string FriendUniqueId { get; set; }
    }

    public class RemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class SetFriendRemarkRequest
    {
        public string FriendUniqueId { get; set; }
        public string Remark { get; set; }
    }

    public class SetFriendRemarkResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class GetFriendsRequest
    {
    }

    public class FriendInfo
    {
        public int FriendUserId { get; set; }
        public string FriendUniqueId { get; set; }
        public string Nickname { get; set; }
        public string Remark { get; set; }
        public bool IsOnline { get; set; }
    }

    public class GetFriendsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public FriendInfo[] Friends { get; set; }
    }

    public class InviteGameRequest
    {
        public string FriendUniqueId { get; set; }
        public string RoomId { get; set; }
        public string SceneType { get; set; }
        public string RoomName { get; set; }
    }

    public class InviteGameResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class InviteGameNotification
    {
        public string InviterUniqueId { get; set; }
        public string InviterNickname { get; set; }
        public string RoomId { get; set; }
        public string SceneType { get; set; }
        public string RoomName { get; set; }
    }

    public class AddBlacklistRequest
    {
        public string TargetUniqueId { get; set; }
    }

    public class AddBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class RemoveBlacklistRequest
    {
        public string TargetUniqueId { get; set; }
    }

    public class RemoveBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class GetBlacklistRequest
    {
    }

    public class BlacklistInfo
    {
        public int BlockedUserId { get; set; }
        public string BlockedUniqueId { get; set; }
        public string BlockedNickname { get; set; }
        public DateTime AddTime { get; set; }
    }

    public class GetBlacklistResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public BlacklistInfo[] Blacklists { get; set; }
    }
}
