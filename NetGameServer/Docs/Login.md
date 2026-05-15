# Login 登录服务器

`Login` 节点主要负责处理用户系统级别的操作。它既能够通过 HTTP 提供无状态的注册和状态校验能力，也能通过接受来自 `Gateway` 建立的 TCP 长连接从而支持 Socket 包驱动登录体系。

## 核心特性
- **双协议支持**:
  - **Socket 路由 (长连接)**: 它通过监听 `TCP` 对应端口接受由于客户端过完 `Gateway` 转发而来的原始认证字节流。根据附带的 `MsgId` 与分发的处理函数对应，比如 `MsgId=10001` 等登录相关封包。
  - **HTTP API**: 采用 ASP.NET Core Web API 实现提供给客户端及 Web 周边工具调用接口。
- **与全局数据库交互**: 包含 UID 生成器的逻辑，在启动向 `DB` 节点发起请求同步最大全局用户 ID，保持分布式主键的正确增量。

## 工作流 (Socket登录举例)
1. 客户端通过长连接向 `Gateway` 发出 `[MsgId][Payload]` 格式的登录请求。
2. `Gateway` 转发给 `Login` 服务器，并在前面加上 `[ClientSessionId(8)]` 变成 `[ClientSessionId(8)][MsgId(4)][Payload]`。
3. `Login` 解析头部的 ID 与 MsgId。
4. 在 `MessageRouter` 或者 `LoginHandler` 根据特定 `MsgId` 进入响应逻辑。
5. 去 `DB` 服务器校验账号密码。
6. `Login` 返回同样的协议包携带应答，并使用 `SessionManager.Instance.SendToGatewayAction` 向原网关吐回数据，最终下发至客户端。

## 启动注意
`Login` 会依赖于 `DB` 系统的先决存在用以申请 `MaxUid` 初始化分发器。请务必优先启动 `DB` 进程。
