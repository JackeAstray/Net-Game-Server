using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shared.Messages.Center
{
    public class CenterMatchRequest
    {
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;
    }

    public class CenterMatchResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("battleNodeId")]
        public string BattleNodeId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
