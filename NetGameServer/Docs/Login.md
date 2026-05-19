# Login 登录服务器

`Login` 服务负责账号体系相关业务，包括登录、注册、密码管理与基础账号资料处理。

## 能力范围
- **HTTP API**：对外提供无状态接口（如账号注册、查询、管理）。
- **Socket 路由处理**：接收 `Gateway` 转发的长连接消息并按 `MsgId` 分发。
- **账号数据协作**：通过 `DB` 服务完成账号校验、写入与状态更新。

## Socket 路由约束
`Login` 从网关接收的数据格式为：
- `[ClientSessionId(8)][MsgId(4)][Payload]`

推荐流程：
1. 解析 `ClientSessionId` 与 `MsgId`。
2. 基于 `MessageIds` 进行显式映射（如 `10001` 登录、`10003` 注册）。
3. 反序列化请求体并交由登录业务处理器执行。
4. 将响应打包为 `[ClientSessionId(8)][ResponseMsgId(4)][Payload]` 回传网关。

## 与 DB 通信协议
- 请求/响应统一：`[MsgId(4)][RequestId(8)][Payload]`

## 启动依赖
`Login` 在启动阶段通常需要与 `DB` 建立可用连接（例如同步 UID 相关初始状态），建议先启动 `DB` 再启动 `Login`。
