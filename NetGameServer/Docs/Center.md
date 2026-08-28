# Center 中心节点

> 集群控制平面：节点注册 / 心跳 / 负载均衡 / 匹配 / 实体迁移协调 / EntityCall 中继。
> 所有业务节点（Game/Battle/Login）启动后必须先向 Center 注册才能被集群发现。
>
> 项目总览见 [README.md](../../README.md)。

## 职责边界

- ✅ 节点注册 / 心跳（10s 间隔）/ 心跳过期剔除
- ✅ 平滑加权负载均衡（`GetBestBattleNode`，对标 Nginx SWRR，迭代 14）
- ✅ 房间匹配（`MatchHandler`：按 `SceneType` / `MaxPlayers` / `CustomRules`）
- ✅ 实体迁移协调（91003 中继 + 91004 回执 + 91005 通知 Gateway 切换路由）
- ✅ EntityCall 中继（91001/91002，按 EntityId 查找目标节点并中继）
- ❌ 不做场景内逻辑（场景在 Battle 节点）
- ❌ 不直接处理客户端消息（客户端只走 Gateway）

## 入口与启动

- 启动入口：`Center/Program.cs`（顶级语句）
- 监听端口默认 `31306`（`CenterPort`）
- 启动顺序：DB → **Center**（先于 Login/Game/Battle/Gateway）

## 关键文件

| 文件 | 职责 |
|---|---|
| `Center/Program.cs` | 启动入口 |
| `Center/CenterServerApp.cs` | 节点主类（partial） |
| `Center/Handlers/NodeManager.cs` | 节点注册表 / 心跳 / `GetBestBattleNode`（SWRR） / `pendingEntityCallSource`（EntityCall 中继） |
| `Center/Handlers/CenterDispatcher.cs` | 强类型消息分发（含 91001/91002 中继逻辑） |
| `Center/Handlers/MatchHandler.cs` | 房间匹配（创建/加入/聊天/离开） |
| `Center/Handlers/CenterSessionContext.cs` | 内部消息上下文（带 `GatewaySession` / `RoutedUserId`） |

## 注意事项

- **节点 ID 格式**：`{NodeType}-{Host}:{Port}`（如 `Battle-127.0.0.1:31307`），
  `GetBestBattleNode` 缓存按 NodeId 索引——NodeId 不稳定会导致缓存失效。
- **心跳过期阈值**：`NodeHeartbeatStaleThreshold = 30s`（3 个心跳周期）。
  Battle 节点负载上报也是心跳（`SendNodeStatus` 每 10s）。
- **实体迁移原子性**：源 Battle 发的 91003 经 Center 中继到目标 Battle，
  Center 不知道恢复是否成功——只透传 91004 回执。**不持久化迁移状态**。
- **EntityCall 中继**：`pendingEntityCallSource` 存 `CallId → sourceNodeId`，
  91002 回执时按 CallId 反查目标节点回包；超时由 Battle tick 周期清扫
  （Center 不主动清超时——超时回调必须在调用方）。
- **Leader 选举**：`Framework.Core.LeaderElection`（`Tools/SupervisorVerify` 验证过），
  多 Center 实例时通过文件锁争锁，单实例无需关注。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| Battle 节点注册不上 | Center 未启动 / 内部认证失败 | 看 Battle 日志 `已连接到 Center`；看 Center 日志 `InternalAuth` 拒绝 |
| `GetBestBattleNode` 返 null | 无 Battle 节点 / 全部心跳过期 | 看 `NodeManager.nodes` 数量；看心跳时间 |
| 实体迁移卡住（91004 不回来） | 目标 Battle 场景不存在 / 实体已存在 | 看目标 Battle 日志 `实体迁移恢复失败` |
| EntityCall 超时 | 目标节点宕机 / 方法名拼错 | 看 `EntityCallHub` 周期清扫日志；目标节点是否在线 |
| 匹配房间找不到 | `SceneType` 过滤不匹配 / `MaxPlayers` 满 | 看 `MatchHandler` 日志 |
