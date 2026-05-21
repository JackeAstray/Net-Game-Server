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

        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; } = 4;
    }

    public class CenterCreateRoomResponse : CenterMatchResponse
    {
        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("hasPassword")]
        public bool HasPassword { get; set; }

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("currentPlayers")]
        public int CurrentPlayers { get; set; }
    }

    public static class RoomStatuses
    {
        public const string Waiting = "Waiting";
        public const string Playing = "Playing";
        public const string Closed = "Closed";
    }

    public class RoomMemberInfo
    {
        [JsonPropertyName("userId")]
        public int UserId { get; set; }

        [JsonPropertyName("clientSessionId")]
        public long ClientSessionId { get; set; }

        [JsonPropertyName("isOwner")]
        public bool IsOwner { get; set; }

        [JsonPropertyName("isReady")]
        public bool IsReady { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public class RoomInfo
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("sceneId")]
        public string SceneId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("battleNodeId")]
        public string BattleNodeId { get; set; } = string.Empty;

        [JsonPropertyName("ownerUserId")]
        public int OwnerUserId { get; set; }

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("hasPassword")]
        public bool HasPassword { get; set; }

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("currentPlayers")]
        public int CurrentPlayers { get; set; }

        [JsonPropertyName("roomStatus")]
        public string RoomStatus { get; set; } = RoomStatuses.Waiting;

        [JsonPropertyName("customRules")]
        public Dictionary<string, string> CustomRules { get; set; } = new();

        [JsonPropertyName("members")]
        public RoomMemberInfo[] Members { get; set; } = Array.Empty<RoomMemberInfo>();

        [JsonPropertyName("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; }
    }

    public class CenterListRoomsRequest
    {
        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("includePrivate")]
        public bool IncludePrivate { get; set; }
    }

    public class CenterListRoomsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("rooms")]
        public RoomInfo[] Rooms { get; set; } = Array.Empty<RoomInfo>();
    }

    public class CenterJoinRoomRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public class CenterJoinRoomResponse : CenterMatchResponse
    {
        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("hasPassword")]
        public bool HasPassword { get; set; }

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("currentPlayers")]
        public int CurrentPlayers { get; set; }
    }

    public class CenterCloseRoomRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class CenterLeaveRoomRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class CenterLeaveRoomResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class CenterCloseRoomResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class RoomClosedNotification
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CenterUpdateRoomSettingsRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; } = 4;

        [JsonPropertyName("customRules")]
        public Dictionary<string, string> CustomRules { get; set; } = new();
    }

    public class CenterUpdateRoomSettingsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomSettingsChangedNotification
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CenterStartRoomGameRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class CenterStartRoomGameResponse : CenterMatchResponse
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomGameStartedNotification : CenterMatchResponse
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomMemberListRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class RoomMemberListResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomMemberListChangedNotification
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class RoomReadyRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("isReady")]
        public bool IsReady { get; set; }
    }

    public class RoomReadyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomReadyChangedNotification
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class RoomTransferOwnerRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("targetUserId")]
        public int TargetUserId { get; set; }
    }

    public class RoomTransferOwnerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomOwnerChangedNotification
    {
        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class RoomKickMemberRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("targetUserId")]
        public int TargetUserId { get; set; }
    }

    public class RoomKickMemberResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("room")]
        public RoomInfo? Room { get; set; }
    }

    public class RoomKickedNotification
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CenterRoomChatRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class CenterRoomChatResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CenterRoomChatNotification
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("senderUserId")]
        public int SenderUserId { get; set; }

        [JsonPropertyName("senderUniqueId")]
        public string SenderUniqueId { get; set; } = string.Empty;

        [JsonPropertyName("senderName")]
        public string SenderName { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("sendTimeUtc")]
        public DateTime SendTimeUtc { get; set; }
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

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }

    public class CenterCreateSceneRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("sceneType")]
        public string SceneType { get; set; } = string.Empty;

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; } = 4;
    }

    public class CenterDestroySceneRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;
    }

    public class CenterDestroySceneResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("affectedSessionIds")]
        public long[] AffectedSessionIds { get; set; } = Array.Empty<long>();
    }

    public class CenterRoomPlayerCountSyncRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("currentPlayers")]
        public int CurrentPlayers { get; set; }
    }

    public class CenterRoomPlayerCountSyncResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }

    public class CenterRoomMemberLeaveSyncRequest
    {
        [JsonPropertyName("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonPropertyName("clientSessionId")]
        public long ClientSessionId { get; set; }
    }

    public class CenterRoomMemberLeaveSyncResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }

    public class CenterNodeStatusRequest
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("currentLoad")]
        public int CurrentLoad { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
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
