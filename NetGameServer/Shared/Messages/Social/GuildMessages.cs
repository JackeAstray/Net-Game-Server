using System;
using System.Collections.Generic;

namespace Shared.Messages.Social
{
    // ===== 公会功能客户端消息（Gateway → Game，51001-51099 段）=====

    public class GuildCreateRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Declaration { get; set; } = string.Empty;
    }

    public class GuildCreateResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int GuildId { get; set; }
    }

    public class GuildMyRequest
    {
    }

    public class GuildMyResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int GuildId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int OwnerUserId { get; set; }
        public string Declaration { get; set; } = string.Empty;
        public List<GuildMemberItem> Members { get; set; } = new();
    }

    public class GuildMemberItem
    {
        public int UserId { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    public class GuildJoinRequest
    {
        public int GuildId { get; set; }
    }

    public class GuildJoinResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GuildLeaveRequest
    {
    }

    public class GuildLeaveResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GuildDisbandRequest
    {
    }

    public class GuildDisbandResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GuildKickRequest
    {
        public int TargetUserId { get; set; }
    }

    public class GuildKickResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GuildTransferRequest
    {
        public int TargetUserId { get; set; }
    }

    public class GuildTransferResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class GuildUpdateDeclRequest
    {
        public string Declaration { get; set; } = string.Empty;
    }

    public class GuildUpdateDeclResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>公会变更通知（被踢 / 解散 / 会长变更等，MVP 先用于被踢与解散）。</summary>
    public class GuildChangedNotif
    {
        public string Type { get; set; } = string.Empty; // "kicked" | "disbanded" | "transferred"
        public int GuildId { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
