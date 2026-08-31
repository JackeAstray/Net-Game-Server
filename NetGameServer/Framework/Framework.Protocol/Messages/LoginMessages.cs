using Framework.Protocol;
using MemoryPack;

namespace Framework.Protocol.Generated;

// ============================================================
// 登录链路协议（迁移自 Protocol/defs/Login.def，方案 A 声明即协议）。
// 字段顺序与 .def 完全一致，保证 MemoryPack 线协议逐字节兼容。
// 每个类：声明（本文件）+ IGameMessage 管线（源生成器补齐）+ 序列化（[MemoryPackable]）。
// ============================================================

[MemoryPackable]
[GameMessage(10001, Target = "Login", Reply = "LoginResult")]
public partial class Login
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10002, Target = "Login")]
public partial class LoginResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public long LastLoginTime { get; set; }
    public int LoginCount { get; set; }
    public bool IsAdmin { get; set; }
}

[MemoryPackable]
[GameMessage(10003, Target = "Login", Reply = "RegisterResult")]
public partial class Register
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10004, Target = "Login")]
public partial class RegisterResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10005, Target = "Login", Reply = "LogoutResult")]
public partial class Logout
{
    public int UserId { get; set; }
}

[MemoryPackable]
[GameMessage(10006, Target = "Login")]
public partial class LogoutResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10007, Target = "Login", Reply = "ResetPasswordResult")]
public partial class ResetPassword
{
    public string Account { get; set; } = string.Empty;
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10008, Target = "Login")]
public partial class ResetPasswordResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10009, Target = "Login", Reply = "UpdateNicknameResult")]
public partial class UpdateNickname
{
    public int UserId { get; set; }
    public string NewNickname { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10010, Target = "Login")]
public partial class UpdateNicknameResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10011, Target = "Login")]
public partial class KickedOff
{
    public string Reason { get; set; } = string.Empty;
    public long Time { get; set; }
}

[MemoryPackable]
[GameMessage(10012, Target = "Login", Reply = "FindPasswordWithCodeResult")]
public partial class FindPasswordWithCode
{
    public string Account { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(10013, Target = "Login")]
public partial class FindPasswordWithCodeResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

// 玩家断线通知（网关 -> 各业务服，内部消息）
[MemoryPackable]
[GameMessage(10000, Target = "All", Internal = true)]
public partial class PlayerDisconnect
{
    public long ClientSessionId { get; set; }
}

// 玩家断线重连恢复（网关 -> Battle，内部消息）：实体从挂起转在线
[MemoryPackable]
[GameMessage(10014, Target = "Battle", Internal = true)]
public partial class PlayerSessionResume
{
    public long ClientSessionId { get; set; }
}
