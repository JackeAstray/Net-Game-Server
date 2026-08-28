# Game 业务节点

> 通用业务层：背包、公会、社交（好友/聊天/黑名单）、任务等非战斗业务。
> 强类型消息分发（`GameDispatcher` + MemoryPack/JSON 双格式），
> 业务方法按业务域拆 partial（`FriendHandler` / `ChatHandler` / ...）。
>
> 项目总览见 [README.md](../../README.md)，编码规范见 [Code-Style.md](Code-Style.md)。

## 职责边界

- ✅ 账号创建后的业务数据管理（背包/公会/好友/聊天等）
- ✅ 业务消息强类型分发（`RegisterAsync<TReq, TRes>`）
- ✅ 跨业务协调（业务 A 调业务 B 的方法，**同节点内**直接方法调用，**跨节点**走 EntityCall）
- ✅ 与 Battle 节点通过 `clientSessionId` 协作（Battle 持主实体/场景，Game 持业务/持久化）
- ❌ 不做战斗判定（Battle 节点）
- ❌ 不做客户端流量接入（Gateway 节点）

## 入口与启动

- 启动入口：`Game/Program.cs`（顶级语句）
- 监听端口默认 `31304`（`GamePort`）
- 启动依赖：DB（持久化）+ Center（注册）+ 可选 Battle（协同）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Game/Program.cs` | 启动入口 |
| `Game/GameServerApp.cs` | 节点主类（partial） |
| `Game/Handlers/GameDispatcher.cs` | 强类型消息分发（注册业务方法） |
| `Game/Handlers/FriendHandler.cs` | 好友系统（partial 拆 6 段，迭代 12） |
| `Game/Handlers/ChatHandler.cs` | 聊天 |
| `Game/Handlers/GuildHandler.cs` | 公会 |
| `Game/Network/ClientSessionWrapper.cs` | 客户端会话包装（注入 `RoutedUserId/uid/nickname`） |
| `Game/Managers/PlayerSessionManager.cs` | 玩家会话表 |

## 注意事项

- **强类型 vs JSON**：业务方法签名 `(ClientSessionWrapper, XxxRequest? req)`，
  由 `MessageDispatcher` 统一反序列化（MemoryPack/JSON 双格式自动判别），
  **不要**在方法内再 `Json.Deserialize` 一次（迭代 13 已 D1 化）。
- **partial 拆分**：单 Handler > 500 行应按业务域拆 partial（`FriendHandler.Add.cs` / `.Remove.cs` / ...），
  避免单文件 1000+ 行。
- **跨节点 EntityCall**：跨 Battle/Game 节点调方法用 `EntityCall.CallAsync`（迭代 13），
  返回 `CallId`；调用方需在 tick 周期调 `EntityCallHub.SweepExpired`。
- **业务持久化**：走 DB 节点（`DbDispatcher` 强类型），不要在本节点直接连 MySQL/Redis。
- **AOI 同步**：Game 节点不直接管 AOI（Battle 管），如需观察其他玩家属性用 `EntitySync` 订阅。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 客户端发业务消息无响应 | MsgId 不在 `RouterTable` | 看 Gateway 路由日志 `未知的消息路由`；看 `GameDispatcher` 注册列表 |
| 业务方法抛 `Json 反序列化失败` | 客户端发的 JSON 与生成类对不上 | 看 `jsonFallback: true` 路径的日志 |
| 跨节点 EntityCall 超时 | 目标节点宕机 / 方法名拼错 | 看 `EntityCallHub` 超时日志；目标节点是否在 Center 注册 |
| FriendHandler 单文件过大 | 没按业务域拆 partial | 参考 `FriendHandler.Add.cs` / `.Remove.cs` 模板 |
