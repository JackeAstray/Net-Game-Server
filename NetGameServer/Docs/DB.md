# DB 数据节点

> 强类型数据访问层（`DbDispatcher` + MemoryPack/JSON 双格式），
> 默认实现为按 EntityType 分目录的文件持久化（用于崩溃恢复 + 单元测试），
> 生产可替换为 MySQL/Redis 后端（接口与实现分离）。
>
> 项目总览见 [README.md](../../README.md)，协议约束见 [Protocol.md](Protocol.md)。

## 职责边界

- ✅ 账号/角色/好友/聊天/邮件/统计等业务数据的存取
- ✅ 强类型消息分发（`DbDispatcher` 注册 20+ 条消息）
- ✅ 实体持久化服务（`EntityPersistenceService`）：按 EntityType 分目录、单条加载 O(1)
- ✅ 请求-响应匹配（`__requestId` 尾部元数据关联）
- ❌ 不做业务校验（业务节点负责）
- ❌ 不直连客户端（DB 只接 Login/Game/Center 等业务节点）

## 入口与启动

- 启动入口：`DB/Program.cs`（顶级语句）
- 监听端口默认 `31305`（`DbPort`）
- 启动顺序：**第一个启动**（其他节点都要连 DB）

## 关键文件

| 文件 | 职责 |
|---|---|
| `DB/Program.cs` | 启动入口 |
| `DB/DbServerApp.cs` | 节点主类（partial） |
| `DB/Handlers/DbDispatcher.cs` | 强类型消息分发（注册 20+ 条 DB 请求） |
| `DB/Handlers/DbQueryHandler.cs` | 业务方法：`(ClientSessionWrapper, XxxRequest?)` 签名，无二次序列化（迭代 13 D1 化） |
| `DB/Routing/RequestContextSession.cs` | DB 链路 `__requestId` 关联上下文 |
| `Framework/Framework.Entity/EntityPersistenceService.cs` | 实体持久化（按 EntityType 分目录文件） |

## 注意事项

- **DB 链路格式**：`[MsgId(4)][Payload(尾部附 __requestId 路由元数据)]`
  （区别于 Gateway 链路的 `[ClientSessionId(8)][...]`），见 [Protocol.md](Protocol.md)。
  业务节点用 `Shared.RouteMetadata.Attach` 附加 `__requestId` 后经 `PacketBuilder.BuildPacket` 发送；
  早期 `BuildDbRequestPacket`/`TryParseDbPacket`（`[RequestId(8)]` 头）为死代码，勿使用。
- **强类型业务方法**：`(ClientSessionWrapper, XxxRequest? req)`，
  框架 `MessageDispatcher` 自动反序列化（`jsonFallback: true` 兼容旧 JSON），
  方法内**不要**再 `Json.Deserialize` 一次。
- **持久化目录**：`EntityPersistenceService` 默认按 `PersistenceDirectory` 配置分目录
  （Player / Npc / Skill / Item / ...），单条加载 O(1) 避免全量目录扫描。
- **后端替换**：当前文件持久化仅供开发/测试用；生产替换为 MySQL/Redis 时
  改 `DbQueryHandler` 内部实现，**不要**改 `DbDispatcher` 强类型注册。
- **RequestId 0**：无需响应匹配时省略 `__requestId`（fire-and-forget），如纯写入场景。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 业务节点连 DB 失败 | DB 未启动 / 端口错 | 看业务节点 `dbClient.OnConnected` 是否触发；`DbPort` 配置 |
| DB 链路格式错误 | 业务节点用了 `[MsgId][Payload]` 简化格式 | 必须附加 `__requestId` 尾部元数据后经 `PacketBuilder.BuildPacket` 发送 |
| 实体持久化加载慢 | 用全量目录扫描（应改单条加载） | 看 `EntityPersistenceService.LoadEntityById`（O(1)） |
| 业务方法重复反序列化 | 业务方法内手动 `Json.Deserialize` | 删掉，方法签名直接收 `TReq` |
