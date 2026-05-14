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

    public class CenterCreateRoomRequest
    {
        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty; // 可以是 "PVE_Defense", "PVP_DeathMatch" 等
        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }
    }

    public class CenterCreateRoomResponse : CenterMatchResponse
    {
    }
}