using System;
using System.ComponentModel.DataAnnotations;

namespace Shared.Data.Social
{
    public class FriendRequest
    {
        [Key]
        public long Id { get; set; }

        public int RequesterUserId { get; set; }

        public int ReceiverUserId { get; set; }

        public string Message { get; set; } = string.Empty;

        public string Status { get; set; } = "Pending";

        public DateTime CreateTimeUtc { get; set; }

        public DateTime? HandleTimeUtc { get; set; }
    }
}
