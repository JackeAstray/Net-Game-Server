using System;
using System.ComponentModel.DataAnnotations;

namespace Shared.Data.Social
{
    /// <summary>
    /// 黑名单关系实体类，表示用户之间的拉黑关系。
    /// </summary>
    public class Blacklist
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        public int BlockedUserId { get; set; }

        public DateTime AddTime { get; set; }
    }
}