using System;

namespace Shared.Messages.Social
{
    public class AddFriendRequest
    {
        public int UserId { get; set; }
        // 可以通过唯一ID或昵称添加好友
        public string TargetUniqueId { get; set; }
        public string TargetNickname { get; set; }
    }

    public class AddFriendResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
