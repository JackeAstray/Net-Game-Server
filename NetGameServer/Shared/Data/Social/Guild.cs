using System;
using System.ComponentModel.DataAnnotations;

namespace Shared.Data.Social
{
    /// <summary>公会实体：会长、名称（唯一）、宣言。</summary>
    public class Guild
    {
        [Key]
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int OwnerUserId { get; set; }

        public string Declaration { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>公会成员实体：角色 Owner/Member（MVP 仅会长与成员）。</summary>
    public class GuildMember
    {
        [Key]
        public int Id { get; set; }

        public int GuildId { get; set; }

        public int UserId { get; set; }

        public string Role { get; set; } = "Member";

        public DateTime JoinedAt { get; set; }
    }
}
