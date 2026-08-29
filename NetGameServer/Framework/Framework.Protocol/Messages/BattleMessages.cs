using Framework.Protocol;
using MemoryPack;

// ============================================================
// Battle protocol (programmatically migrated from Protocol/defs/Battle.def).
// Field order matches .def exactly to keep the MemoryPack wire format byte-compatible.
// Declaration (this file) + IGameMessage plumbing (source generator) + [MemoryPackable].
// ============================================================

namespace Framework.Protocol.Generated;

[MemoryPackable]
[GameStruct]
public partial class Vector3
{
    public float X { get; set; } = new();
    public float Y { get; set; } = new();
    public float Z { get; set; } = new();
}

[MemoryPackable]
[GameStruct]
public partial class EntityState
{
    public long EntityId { get; set; } = new();
    public string Nickname { get; set; } = string.Empty;
    public Vector3 Position { get; set; } = new();
    public Vector3 Rotation { get; set; } = new();
    public int Hp { get; set; } = new();
    public int MaxHp { get; set; } = new();
    public int Score { get; set; } = new();
    public List<int> Equipment { get; set; } = new();
}

[MemoryPackable]
[GameStruct]
public partial class PlayerInput
{
    public long InputId { get; set; } = new();
    public int Buttons { get; set; } = new();
    public float MoveX { get; set; } = new();
    public float MoveY { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40001, Target = "Battle", Reply = "BattleJoinResult")]
public partial class BattleJoin
{
    public string RoomId { get; set; } = string.Empty;
    public string SceneName { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = new();
    public Dictionary<string, string> CustomRules { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40002, Target = "Battle")]
public partial class BattleJoinResult
{
    public bool Success { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(40003, Target = "Battle")]
public partial class BattleFrameSync
{
    public int FrameId { get; set; } = new();
    public List<PlayerInput> Inputs { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40004, Target = "Battle", Reply = "BattleLeaveRoomResult")]
public partial class BattleLeaveRoom
{
    public string RoomId { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(40005, Target = "Battle")]
public partial class BattleLeaveRoomResult
{
    public bool Success { get; set; } = new();
    public string RoomId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(40006, Target = "Battle")]
public partial class ScriptAction
{
    public long EntityId { get; set; } = new();
    public string Method { get; set; } = string.Empty;
    public List<int> Args { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40010, Target = "Battle", Reply = "ServerTimeSync")]
public partial class ClientTimeSync
{
    public long ClientSendMs { get; set; } = new();
    public long LastServerSendMs { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40011, Target = "Battle")]
public partial class ServerTimeSync
{
    public long ClientSendMs { get; set; } = new();
    public long ServerRecvMs { get; set; } = new();
    public long ServerSendMs { get; set; } = new();
    public long AuthFrame { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40101, Target = "Battle")]
public partial class EntitySync
{
    public Vector3 Position { get; set; } = new();
    public Vector3 Rotation { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40102, Target = "Battle")]
public partial class EntityEnterViewNotify
{
    public List<EntityState> Entities { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40103, Target = "Battle")]
public partial class EntityLeaveViewNotify
{
    public List<long> EntityIds { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40104, Target = "Battle")]
public partial class EntityStateUpdateNotify
{
    public EntityState State { get; set; } = new();
}

[MemoryPackable]
[GameMessage(40105, Target = "Battle")]
public partial class EntityDeltaSync
{
    public long EntityId { get; set; } = new();
    public byte[] Props { get; set; } = Array.Empty<byte>();
}

[MemoryPackable]
[GameMessage(40106, Target = "Battle")]
public partial class EntitySnapshot
{
    public long EntityId { get; set; } = new();
    public byte[] Props { get; set; } = Array.Empty<byte>();
}

