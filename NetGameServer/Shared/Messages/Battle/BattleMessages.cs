using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shared.Messages.Battle
{
    public class BattleJoinRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneName")]
        public string SceneName { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; } = 100;

        [JsonPropertyName("customRules")]
        public Dictionary<string, string>? CustomRules { get; set; }
    }

    public class BattleJoinResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
