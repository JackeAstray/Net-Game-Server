using System;

namespace Shared.Messages.Social
{
    public class AddFriendRequest
    {
        public int UserId { get; set; }
        // 可以通过唯一ID或昵称添加好友
        public string TargetUniqueId { get; set; }
        public string TargetNickname { get; set; }
        // 新增直接通过用户ID添加
        public int FriendUserId { get; set; }
        // 新增好友备注
        public string Remark { get; set; }
    }

    public class AddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class RemoveFriendRequest
    {
        public int FriendUserId { get; set; }
    }

    public class RemoveFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class SetFriendRemarkRequest
    {
        public int FriendUserId { get; set; }
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
        public int FriendUserId { get; set; }
        public int RoomId { get; set; }
    }

    public class InviteGameResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class InviteGameNotification
    {
        public int InviterUserId { get; set; }
        public string InviterNickname { get; set; }
        public int RoomId { get; set; }
    }
}
