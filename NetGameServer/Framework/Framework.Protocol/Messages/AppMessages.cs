using Framework.Protocol;
using MemoryPack;

// ============================================================
// App protocol — 应用节点（AppServer）消息。
// 应用节点是"游戏服务器 + 应用服务器"并存的通用承载节点：
//   - 与游戏节点一样通过 Gateway 接收客户端消息（Target="App"，ID 段 80000-89999）；
//   - 同时暴露 HTTP REST API（见 App/Controllers）。
// 客户端发送 AppEchoRequest（80001）→ Gateway 路由到 App 节点 → 回包 AppEchoResult（80002）。
// ============================================================

namespace Framework.Protocol.Generated;

[MemoryPackable]
[GameMessage(80001, Target = "App", Reply = "AppEchoResult")]
public partial class AppEchoRequest
{
    public string Text { get; set; } = string.Empty;
}

[MemoryPackable]
[GameMessage(80002, Target = "App")]
public partial class AppEchoResult
{
    public bool Success { get; set; } = new();
    public string Echo { get; set; } = string.Empty;
    public long ClientSessionId { get; set; } = new();
    public string NodeId { get; set; } = string.Empty;
}
