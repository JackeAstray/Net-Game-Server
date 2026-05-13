using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shared.Messages.Battle
{
    public class BattleJoinRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class BattleJoinResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
