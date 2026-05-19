# 服务器通信协议约束

本文档定义客户端、网关、业务服务与 DB 之间的统一封包协议。所有服务必须严格遵守，不允许保留并行兼容分支。

## 1. 客户端 <-> 网关
- 客户端 -> 网关：`[MsgId(4)][Payload]`（外层使用长度帧）
- 网关 -> 客户端：`[MsgId(4)][Payload]`（外层使用长度帧）

## 2. 网关 <-> 业务服务（Login/Game/Center/Battle）
- 请求/响应统一：`[ClientSessionId(8)][MsgId(4)][Payload]`
- `ClientSessionId` 仅由网关维护，用于会话映射与回包路由。

## 3. 业务服务 <-> DB
- 请求/响应统一：`[MsgId(4)][RequestId(8)][Payload]`
- `RequestId` 用于请求-响应匹配；无需匹配时可使用 `0`。

## 4. 消息分发规则
- 业务服务按 `MsgId` 常量进行显式路由分发。
- 登录链路典型请求：`10001`（登录）、`10003`（注册）。
- 响应消息应使用与请求对应的响应 `MsgId`。

## 5. 代码统一入口（DB链路）
- 构建：`Network.Routing.PacketBuilder.BuildDbRequestPacket(...)`
- 解析：`Network.Routing.PacketBuilder.TryParseDbPacket(...)`

## 6. 禁止项
- 禁止在 DB 链路中使用 `[MsgId][Payload]`。
- 禁止在网关与业务服链路中省略 `ClientSessionId`。
- 禁止通过修改服务端协议去兼容客户端异常报文。
- 禁止在同一链路长期保留多套协议实现。
