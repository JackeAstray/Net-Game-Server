using System;
using System.ComponentModel.DataAnnotations;

namespace Shared.Data.Social
{
    /// <summary>
    /// 好友关系实体类，表示用户之间的好友关系。
    /// </summary>
    public class Friend
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        public int FriendUserId { get; set; }

        public DateTime AddTime { get; set; }
    }
}