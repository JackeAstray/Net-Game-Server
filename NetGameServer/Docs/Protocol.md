# 服务器通讯协议约束

本文档定义网关链路与服务间链路的统一封包协议，所有服务必须严格遵守，不允许兼容分支。

## 1. 客户端与网关
- 客户端 -> 网关: `[MsgId(4)][Payload]`
- 网关 -> 客户端: 仍通过内部打包后转回客户端标准包（长度帧 + MsgId + Payload），业务层视角等价于 `[MsgId][Payload]`

## 2. 网关与业务服（Login/Game/Center/Battle）
- 请求与响应统一: `[ClientSessionId(8)][MsgId(4)][Payload]`
- `ClientSessionId` 由网关维护并用于回包路由

## 3. 业务服与 DB
- 请求与响应统一: `[MsgId(4)][RequestId(8)][Payload]`
- `RequestId` 用于请求-响应匹配；无需匹配时可使用 `0`

## 4. 代码统一入口
- 构建 DB 包: `Network.Routing.PacketBuilder.BuildDbRequestPacket(...)`
- 解析 DB 包: `Network.Routing.PacketBuilder.TryParseDbPacket(...)`

## 5. 禁止项
- 禁止在 DB 链路中继续使用 `[MsgId][Payload]`
- 禁止在网关链路中省略 `ClientSessionId`
- 禁止在单个服务中同时保留多套协议分支
