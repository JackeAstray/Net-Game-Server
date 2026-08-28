# 服务器通信协议约束

> 本文档定义客户端、网关、业务服务与 DB 之间的统一封包协议。
> **所有服务必须严格遵守，不允许保留并行兼容分支。**
>
> 关联代码：`Network/Routing/PacketBuilder.cs`、`Network/Routing/RouteMetadata.cs`、`Framework/Framework.Protocol/`。
> 协议声明唯一事实来源在 `Protocol/defs/*.def`（构建时 Protogen 自动生成强类型消息类 / `MessageIds` 常量 / `RouterTable`）。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

---

## 0. 链路格式速查

| 链路 | 格式 | 长度前缀 | 关键标识 |
|---|---|---|---|
| 客户端 ↔ Gateway | `[MsgId(4)][Payload]` | 4 字节长度帧 | — |
| Gateway ↔ 后端（Login/Game/Center/Battle） | `[ClientSessionId(8)][MsgId(4)][Payload]` | 4 字节长度帧 | `ClientSessionId`：会话路由 |
| 后端 ↔ DB | `[MsgId(4)][RequestId(8)][Payload]` | 4 字节长度帧 | `RequestId`：请求-响应匹配（无需匹配时填 0） |
| 后端 ↔ 后端（内部消息） | `[ClientSessionId(8)][MsgId(4)][Payload]` | 4 字节长度帧 | `internal="true"` 标记（91001~91006 等），Gateway 拒绝伪造 |

---

## 1. 客户端 ↔ Gateway

- 客户端 → 网关：`[MsgId(4)][Payload]`（外层使用长度帧）
- 网关 → 客户端：`[MsgId(4)][Payload]`（外层使用长度帧）
- `MsgId` 取自 `Shared/Messages/MessageIds.cs`（由 `Protocol/defs/*.def` 生成）

## 2. Gateway ↔ 业务服务（Login/Game/Center/Battle）

- 请求/响应统一：`[ClientSessionId(8)][MsgId(4)][Payload]`
- `ClientSessionId` 仅由 Gateway 维护，用于会话映射与回包路由
- Gateway 通过 `RouteMetadata` 注入 `__clientSessionId` / `__userId` / `__uid` / `__broadcast`，后端用 `RouteMetadata.TryExtract*` 解析

## 3. 业务服务 ↔ DB

- 请求/响应统一：`[MsgId(4)][RequestId(8)][Payload]`
- `RequestId` 用于请求-响应匹配；无需匹配时可使用 `0`（fire-and-forget）
- **不要**用简化格式 `[MsgId][Payload]`（与 Gateway 链路冲突，会被 `BuildDbRequestPacket` 拒绝）

## 4. 消息分发规则

- 业务服务按 `MsgId` 常量进行显式路由分发（`MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式）
- 登录链路典型请求：`10001`（登录）、`10003`（注册）
- 响应消息应使用与请求对应的响应 `MsgId`
- 新消息：先改 def → 构建一次 → 用生成的 `MessageIds` / 类型

## 5. 代码统一入口

- 构建：`Network.Routing.PacketBuilder.BuildDbRequestPacket(...)`
- 解析：`Network.Routing.PacketBuilder.TryParseDbPacket(...)`
- 路由元数据：`Network/Routing/RouteMetadata.cs`

## 6. 禁止项

- 禁止在 DB 链路中使用 `[MsgId][Payload]`（必须含 `RequestId(8)`）
- 禁止在网关与业务服链路中省略 `ClientSessionId`
- 禁止通过修改服务端协议去兼容客户端异常报文（客户端应匹配服务端协议）
- 禁止在同一链路长期保留多套协议实现（双轨只允许作为迁移期临时手段，由 `jsonFallback` 承担 JSON 兼容）
- 禁止手改 `Framework/Framework.Protocol/Generated/*.g.cs`（构建时重生成）
