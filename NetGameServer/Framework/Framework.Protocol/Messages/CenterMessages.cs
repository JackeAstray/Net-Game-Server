using Framework.Protocol;
using MemoryPack;

// ============================================================
// Center protocol (programmatically migrated from Protocol/defs/Center.def).
// Field order matches .def exactly to keep the MemoryPack wire format byte-compatible.
// Declaration (this file) + IGameMessage plumbing (source generator) + [MemoryPackable].
// ============================================================

namespace Framework.Protocol.Generated;

[MemoryPackable]
[GameStruct]
public partial class EntityMigratePayload
{
    public long EntityId { get; set; } = new();
    public string EntityType { get; set; } = string.Empty;
    public byte[] Props { get; set; } = Array.Empty<byte>();
}

[MemoryPackable]
[GameStruct]
public partial class RoomMemberInfo
{
    public int UserId { get; set; } = new();
    public long ClientSessionId { get; set; } = new();
    public bool IsOwner { get; set; } = new();
    public bool IsReady { get; set; } = new();
    public string DisplayName { get; set; } = string.Empty;
}

[MemoryPackable]
[GameStruct]
public partial class RoomInfo
{
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public int OwnerUserId { get; set; } = new();
    public bool IsPrivate { get; set; } = new();
    public bool HasPassword { get; set; } = new();
    public int MaxPlayers { get; set; } = new();
    public int CurrentPlayers { get; set; } = new();
    public string RoomStatus { get; set; } = string.Empty;
    public long CreatedAtUtc { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30001, Target = "Center", Reply = "CenterMatchResult")]
public partial class CenterMatch
{
    public string CategoryId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30002, Target = "Center")]
public partial class CenterMatchResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30003, Target = "Center", Reply = "CenterCreateRoomResult")]
public partial class CenterCreateRoom
{
    public string SceneType { get; set; } = string.Empty;
    public bool IsPrivate { get; set; } = new();
    public string RoomName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30004, Target = "Center")]
public partial class CenterCreateRoomResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public bool HasPassword { get; set; } = new();
    public int MaxPlayers { get; set; } = new();
    public int CurrentPlayers { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30005, Target = "Center", Reply = "CenterListRoomsResult")]
public partial class CenterListRooms
{
    public string SceneType { get; set; } = string.Empty;
    public bool IncludePrivate { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30006, Target = "Center")]
public partial class CenterListRoomsResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public List<RoomInfo> Rooms { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30007, Target = "Center", Reply = "CenterJoinRoomResult")]
public partial class CenterJoinRoom
{
    public string RoomId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30008, Target = "Center")]
public partial class CenterJoinRoomResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public bool HasPassword { get; set; } = new();
    public int MaxPlayers { get; set; } = new();
    public int CurrentPlayers { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30009, Target = "Center", Reply = "CenterCloseRoomResult")]
public partial class CenterCloseRoom
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30010, Target = "Center")]
public partial class CenterCloseRoomResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30011, Target = "Center")]
public partial class RoomClosedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30012, Target = "Center", Reply = "CenterUpdateRoomSettingsResult")]
public partial class CenterUpdateRoomSettings
{
    public string RoomId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = new();
    public bool IsPrivate { get; set; } = new();
    public Dictionary<string, string> CustomRules { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30013, Target = "Center")]
public partial class CenterUpdateRoomSettingsResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30014, Target = "Center")]
public partial class RoomSettingsChangedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30015, Target = "Center", Reply = "CenterStartRoomGameResult")]
public partial class CenterStartRoomGame
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30016, Target = "Center")]
public partial class CenterStartRoomGameResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30017, Target = "Center")]
public partial class RoomGameStartedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30018, Target = "Center", Reply = "RoomMemberListResult")]
public partial class RoomMemberList
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30019, Target = "Center")]
public partial class RoomMemberListResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public RoomInfo Room { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30020, Target = "Center")]
public partial class RoomMemberListChangedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30021, Target = "Center", Reply = "RoomReadyResult")]
public partial class RoomReady
{
    public string RoomId { get; set; } = string.Empty;
    public bool IsReady { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30022, Target = "Center")]
public partial class RoomReadyResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public RoomInfo Room { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30023, Target = "Center")]
public partial class RoomReadyChangedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public int UserId { get; set; } = new();
    public bool IsReady { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30024, Target = "Center", Reply = "RoomTransferOwnerResult")]
public partial class RoomTransferOwner
{
    public string RoomId { get; set; } = string.Empty;
    public int TargetUserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30025, Target = "Center")]
public partial class RoomTransferOwnerResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public RoomInfo Room { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30026, Target = "Center")]
public partial class RoomOwnerChangedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public int NewOwnerUserId { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30027, Target = "Center", Reply = "RoomKickMemberResult")]
public partial class RoomKickMember
{
    public string RoomId { get; set; } = string.Empty;
    public int TargetUserId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30028, Target = "Center")]
public partial class RoomKickMemberResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public RoomInfo Room { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30029, Target = "Center")]
public partial class RoomKickedNotify
{
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30030, Target = "Center", Reply = "CenterRoomChatResult")]
public partial class CenterRoomChat
{
    public string RoomId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30031, Target = "Center")]
public partial class CenterRoomChatResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30032, Target = "Center")]
public partial class CenterRoomChatNotify
{
    public string RoomId { get; set; } = string.Empty;
    public int SenderUserId { get; set; } = new();
    public string SenderNickname { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long SendTime { get; set; } = new();
}

[MemoryPackable]
[GameMessage(30033, Target = "Center", Reply = "CenterLeaveRoomResult")]
public partial class CenterLeaveRoom
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(30034, Target = "Center")]
public partial class CenterLeaveRoomResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90001, Target = "Center", Internal = true)]
public partial class CenterRegisterNode
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = new();
    public int CurrentLoad { get; set; } = new();
    public long Timestamp { get; set; } = new();
    public string Signature { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90002, Target = "Center", Internal = true)]
public partial class CenterRegisterNodeResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90003, Target = "Battle", Reply = "CenterCreateSceneResult", Internal = true)]
public partial class CenterCreateScene
{
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public bool IsPrivate { get; set; } = new();
    public int MaxPlayers { get; set; } = new();
}

[MemoryPackable]
[GameMessage(90004, Target = "Center", Internal = true)]
public partial class CenterCreateSceneResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public string BattleNodeId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90005, Target = "Center", Internal = true)]
public partial class CenterNodeStatus
{
    public string NodeId { get; set; } = string.Empty;
    public int CurrentLoad { get; set; } = new();
    public long Timestamp { get; set; } = new();
    public string Signature { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90006, Target = "Battle", Reply = "CenterDestroySceneResult", Internal = true)]
public partial class CenterDestroyScene
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(90007, Target = "Center", Internal = true)]
public partial class CenterDestroySceneResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<long> AffectedSessionIds { get; set; } = new();
}

[MemoryPackable]
[GameMessage(90008, Target = "Center", Internal = true)]
public partial class CenterRoomPlayerCountSync
{
    public string RoomId { get; set; } = string.Empty;
    public int CurrentPlayers { get; set; } = new();
}

[MemoryPackable]
[GameMessage(90010, Target = "Center", Internal = true)]
public partial class CenterRoomMemberLeaveSync
{
    public string RoomId { get; set; } = string.Empty;
    public long ClientSessionId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(90999, Target = "All", Internal = true)]
public partial class InternalAuth
{
    public string NodeId { get; set; } = string.Empty;
    public long Timestamp { get; set; } = new();
    public string Signature { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(91001, Target = "All", Internal = true)]
public partial class EntityRemoteCall
{
    public string TargetNodeId { get; set; } = string.Empty;
    public long EntityId { get; set; } = new();
    public string MethodName { get; set; } = string.Empty;
    public byte[] Args { get; set; } = Array.Empty<byte>();
    public long CallId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(91002, Target = "All", Internal = true)]
public partial class EntityRemoteCallResult
{
    public long EntityId { get; set; } = new();
    public string MethodName { get; set; } = string.Empty;
    public bool Success { get; set; } = new();
    public byte[] Result { get; set; } = Array.Empty<byte>();
    public long CallId { get; set; } = new();
}

[MemoryPackable]
[GameMessage(91003, Target = "All", Internal = true)]
public partial class EntityMigrateRequest
{
    public string SourceNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public long ClientSessionId { get; set; } = new();
    public long EntityId { get; set; } = new();
    public string EntityType { get; set; } = string.Empty;
    public string SceneId { get; set; } = string.Empty;
    public byte[] Props { get; set; } = Array.Empty<byte>();
    public List<EntityMigratePayload> OwnedEntities { get; set; } = new();
}

[MemoryPackable]
[GameMessage(91004, Target = "All", Internal = true)]
public partial class EntityMigrateResult
{
    public bool Success { get; set; } = new();
    public long ClientSessionId { get; set; } = new();
    public long EntityId { get; set; } = new();
    public string NewNodeId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(91005, Target = "All", Internal = true)]
public partial class EntityMigrateRouted
{
    public long ClientSessionId { get; set; } = new();
    public string NewNodeId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(91006, Target = "Battle", Internal = true)]
public partial class EntityMigrateCommand
{
    public long ClientSessionId { get; set; } = new();
    public string TargetNodeId { get; set; } = string.Empty;
}

