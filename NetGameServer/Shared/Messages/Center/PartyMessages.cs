using System;
using System.Collections.Generic;

namespace Shared.Messages.Center
{
    // ===== 队伍功能（Center 协调，客户端 → Gateway → Center，31001-31015 段）=====

    public class PartyMemberInfo
    {
        public long ClientSessionId { get; set; }
        public int UserId { get; set; }
        public string Nickname { get; set; } = string.Empty;
        public bool Ready { get; set; }
    }

    public class PartyCreateRequest
    {
    }

    public class PartyCreateResponse
    {
        public bool Success { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class PartyJoinRequest
    {
        public string PartyId { get; set; } = string.Empty;
    }

    public class PartyJoinResponse
    {
        public bool Success { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class PartyLeaveRequest
    {
    }

    public class PartyLeaveResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PartyDisbandRequest
    {
    }

    public class PartyDisbandResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PartyMyRequest
    {
    }

    public class PartyMyResponse
    {
        public bool Success { get; set; }
        public string PartyId { get; set; } = string.Empty;
        public long OwnerClientSessionId { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<PartyMemberInfo> Members { get; set; } = new();
    }

    public class PartyKickRequest
    {
        public long TargetClientSessionId { get; set; }
    }

    public class PartyKickResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PartyReadyRequest
    {
        public bool Ready { get; set; }
    }

    public class PartyReadyResponse
    {
        public bool Success { get; set; }
        public bool Ready { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PartyMemberNotification
    {
        public string PartyId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // member_joined/member_left/kicked/ready_changed/disbanded/created
        public long TargetClientSessionId { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<PartyMemberInfo> Members { get; set; } = new();
    }
}
