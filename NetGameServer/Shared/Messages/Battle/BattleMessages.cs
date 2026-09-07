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

    public class BattleLeaveRoomRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class BattleLeaveRoomResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class BattleSpectateRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;
    }

    public class BattleSpectateResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class BattleReplayRequest
    {
        [JsonPropertyName("sceneId")]
        public string SceneId { get; set; } = string.Empty;

        [JsonPropertyName("maxFrames")]
        public int MaxFrames { get; set; }
    }

    public class BattleReplayEntitySnapshot
    {
        public long EntityId { get; set; }
        public byte[] Props { get; set; } = Array.Empty<byte>();
    }

    public class BattleReplayFrame
    {
        public long FrameId { get; set; }
        public long TimeMs { get; set; }
        public List<BattleReplayEntitySnapshot> Snapshots { get; set; } = new();
    }

    public class BattleReplayResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("sceneId")]
        public string SceneId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("frames")]
        public List<BattleReplayFrame> Frames { get; set; } = new();
    }
}
