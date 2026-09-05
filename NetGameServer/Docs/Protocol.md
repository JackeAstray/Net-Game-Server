# 服务器通信协议约束

> 本文档定义客户端、网关、业务服务与 DB 之间的统一封包协议。
> **所有服务必须严格遵守，不允许保留并行兼容分支。**
>
> 关联代码：`Network/Routing/PacketBuilder.cs`、`Network/Routing/RouteMetadata.cs`、`Framework/Framework.Protocol/`。
> 协议声明**唯一事实来源**是 C# `[GameMessage]` / `[GameStruct]`（`Framework/Framework.Protocol/Messages/*.cs`），
> 由 `Framework.Protocol.Generator` 源生成器在编译期产出 `MessageIds` 常量、`RouterTable` 路由表、
> 每个消息的 `IGameMessage` 管线（MsgId/TargetServer/Serialize/Deserialize）与 `ProtocolManifest.json`（供 ClientGen）。
> `Protocol/defs/*.def` 已全部迁移为空占位（仅保留 ID 段约定文档）；原 `.def + Protogen` 管线已删除。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

---

## 0. 链路格式速查

| 链路 | 格式 | 长度前缀 | 关键标识 |
|---|---|---|---|
| 客户端 ↔ Gateway | `[MsgId(4)][Payload]` | 4 字节长度帧 | — |
| Gateway ↔ 后端（Login/Game/Center/Battle） | `[ClientSessionId(8)][MsgId(4)][Payload]` | 4 字节长度帧 | `ClientSessionId`：会话路由 |
| 后端 ↔ DB | `[MsgId(4)][Payload(尾部附 __requestId 路由元数据)]` | 4 字节长度帧 | `__requestId`：请求-响应匹配 |
| 后端 ↔ 后端（内部消息） | `[ClientSessionId(8)][MsgId(4)][Payload]` | 4 字节长度帧 | `internal="true"` 标记（90001~90010 / 90999 / 91001~91010 等），Gateway 拒绝伪造 |

---

## 1. 客户端 ↔ Gateway

- 客户端 → 网关：`[MsgId(4)][Payload]`（外层使用长度帧）
- 网关 → 客户端：`[MsgId(4)][Payload]`（外层使用长度帧）
- `MsgId` 取自 `Shared/Messages/MessageIds.cs`（由 `Framework.Protocol.Generator` 源生成器编译期生成）

## 2. Gateway ↔ 业务服务（Login/Game/Center/Battle）

- 请求/响应统一：`[ClientSessionId(8)][MsgId(4)][Payload]`
- `ClientSessionId` 仅由 Gateway 维护，用于会话映射与回包路由
- Gateway 通过 `RouteMetadata` 注入 `__clientSessionId` / `__userId` / `__uid` / `__broadcast`，后端用 `RouteMetadata.TryExtract*` 解析

## 3. 业务服务 ↔ DB

- 请求/响应统一：`[MsgId(4)][Payload]`，其中 `Payload` 尾部附 `__requestId` 路由元数据
  （`Framework.Protocol.BinaryRouteMetadata`，尾部魔数 `META`；见 `Shared/RouteMetadata.cs` 回退分支）
- `__requestId` 用于请求-响应匹配（服务端 `PendingRequests` 表）；fire-and-forget 时可省略
- 注意：早期设计的 `[MsgId(4)][RequestId(8)][Payload]` 头格式对应的
  `PacketBuilder.BuildDbRequestPacket` / `TryParseDbPacket` 为历史死代码，**未在生产链路使用**；
  实测链路一律以 `__requestId` 尾部元数据为准，勿再按旧文档实现新链路

## 4. 消息分发规则

- 业务服务按 `MsgId` 常量进行显式路由分发（`MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式）
- 登录链路典型请求：`10001`（登录）、`10003`（注册）
- 响应消息应使用与请求对应的响应 `MsgId`
- 新消息：先在 `Framework/Framework.Protocol/Messages/*.cs` 写 `[GameMessage]` 类 → 构建一次 → 用生成的 `MessageIds` / 类型，并在目标节点 Dispatcher 注册

## 5. 代码统一入口

- 请求发送：`Shared.RouteMetadata.Attach...`（`__requestId` 尾部元数据）+ `Network.Routing.PacketBuilder.BuildPacket`
- 解析：`Shared.RouteMetadata.TryExtract*` / `Framework.Protocol.BinaryRouteMetadata.Extract`
- 路由元数据：`Shared/RouteMetadata.cs`、`Framework/Framework.Protocol/BinaryRouteMetadata.cs`

## 6. 禁止项

- 禁止在 DB 链路沿用 `[MsgId(4)][RequestId(8)][Payload]` 旧头格式（历史死代码，见第 3 节）
- 禁止在网关与业务服链路中省略 `ClientSessionId`
- 禁止通过修改服务端协议去兼容客户端异常报文（客户端应匹配服务端协议）
- 禁止在同一链路长期保留多套协议实现（双轨只允许作为迁移期临时手段，由 `jsonFallback` 承担 JSON 兼容）
- 禁止手改 `Framework/Framework.Protocol/Generated/*.g.cs`（构建时重生成）
