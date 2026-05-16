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

        [JsonPropertyName("sceneId")]
        public string SceneId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;
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

    public class CenterRegisterNodeRequest
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("nodeType")]
        public string NodeType { get; set; } = string.Empty; // "Battle", "Game"

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("currentLoad")]
        public int CurrentLoad { get; set; }
    }

    public class CenterCreateSceneRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }
    }

    public class CenterNodeStatusRequest
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("currentLoad")]
        public int CurrentLoad { get; set; }
    }

    public class CenterCreateSceneResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneId")]
        public string SceneId { get; set; } = string.Empty;

        [JsonPropertyName("battleNodeId")]
        public string BattleNodeId { get; set; } = string.Empty;
    }
}
