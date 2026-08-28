# Battle 战斗节点

> 高实时性战斗场景：AOI、帧同步、玩法实体（Player/Skill/Item/Npc/Quest）、
> 实体迁移 v2（含属主玩法实体随迁 + 孤儿回收 + 玩法实体 ID 节点段）。
> 单线程 tick 引擎驱动，对应 KBE cellapp。

项目总览与能力描述见 [README.md](../../README.md) §模块详解。
本文件聚焦**代码定位、关键文件、注意事项、排错**。

## 职责边界

- ✅ 场景管理（AOI / 帧同步 / 玩法实体生命周期）
- ✅ 玩家主实体 + 属主玩法实体（Skill/Item）同包随迁（迭代 15）
- ✅ EntityCall 接收（91001 中继过来的远端方法调用）
- ✅ 客户端 ScriptAction 路由到 csx 脚本（`Battle/Handlers/MessageRouter.cs`）
- ✅ 玩法实体 ID 节点段防跨节点撞 ID（迭代 15）
- ✅ 单线程 tick 引擎串行处理入站消息（`DrainInboundMessages`）
- ❌ 不做账号/好友/公会（Game 节点）
- ❌ 不做控制平面协调（Center 节点）

## 入口与启动

- 启动入口：`Battle/Program.cs`（顶级语句）
- 监听端口默认 `31307`（`BattlePort`，可多实例，平滑加权分配）
- 启动依赖：DB（可选，崩溃恢复用）+ Center（注册 + 接收 CenterCreateScene）
- **多实例**：Center 按 `GetBestBattleNode`（SWRR）选节点，Battle 之间无直接通信，全部经 Center

## 关键文件

| 文件 | 职责 |
|---|---|
| `Battle/Program.cs` | 启动入口 |
| `Battle/BattleServerApp.cs` | 节点主类（partial）：网络/入站队列/迁移/玩法实体 |
| `Battle/Handlers/MessageRouter.cs` | 强类型 `MessageDispatcher` 注册（含 CenterCreateScene/CenterDestroyScene 内部消息） |
| `Battle/Handlers/RoomHandler.cs` | 加入/离开房间 |
| `Battle/Handlers/EntitySyncHandler.cs` | AOI 脏属性增量同步（All / AOI / OwnClient 作用域） |
| `Battle/Handlers/BattleMainHandler.cs` | 场景创建/销毁 + 玩法实体生成（`SpawnSceneGameplayEntities`） |
| `Battle/Entities/PlayerEntityDef.cs` | 玩家实体定义 |
| `Battle/Entities/GameplayEntityDefs.cs` | 玩法实体定义（Npc/Quest/Skill/Item） |

## 注意事项

- **单线程约定**：实体/场景状态**只在 tick 线程读写**。收包线程只入队（`EnqueueInbound`），
  `DrainInboundMessages` 串行消费。**绝不在 `OnDataReceived` 内动实体**（数据竞争风险）。
- **EntityCall 接收**：`HandleEntityRemoteCallIn` 已在 tick 线程（经入站队列），直接调 `entity.InvokeMethod`。
- **EntityCall 发送**：`SendEntityRemoteCallToCenter` 拼装 91001 消息经 Center 中继；调用方
  在 `EntityCallHub` 注册回调，Battle tick 周期 `SweepExpired`。
- **实体迁移 v2**：`StartEntityMigration` 序列化玩家主实体 + 收集属主玩法实体同包发送；
  目标节点 `RestoreMigratedEntity(entityId, type, sceneId, props, ownerClientId: ...)` 恢复属主绑定。
- **玩法实体 ID**：`NextGameplayEntityId()` 加节点派生段 [32,40)（FNV-1a hash），跨节点不撞 ID。
  NetworkVerify 用 `>= (1L<<40)` 过滤玩法实体——不要改基址。
- **孤儿回收**：`RecycleOwnedEntities(scene, clientSessionId)` 在 `CompleteMigrateOut` /
  `LeaveScene` / `RoomHandler.HandleLeaveRoomRequestAsync` 三路径自动调用。
- **脚本层（csx）**：`GameLogic/scripts/*.csx` 按 EntityType 绑定，**保存即热更新**（`ScriptHost` 防抖）。
  脚本可写 `entity.Mailbox.Call/CallAsync` 调自己或同场景其他实体的方法（迭代 17）。

## 排错

| 症状 | 可能原因 | 排查 |
|---|---|---|
| 客户端进入场景无响应 | 场景未创建 / Center 没中继 90003 | 看 `BattleMainHandler.HandleCreateSceneRequestAsync` 日志 |
| 玩家数据脏属性没广播 | `EntitySyncHandler` 未触发 / `SyncToClient=false` | 看 `IsDirty` / `TakeDirtyProperties` 调用栈 |
| 实体迁移卡住 | 目标场景不存在 / 实体已存在 | 看 `RestoreMigratedEntity` 返回 null 的原因 |
| 玩法实体 ID 撞了 | 没升级到迭代 15（含节点段） | `git log` 确认是迭代 15 之后 |
| csx 修改没生效 | `ScriptHost` 防抖未到 / 编译错误 | 看 Battle 启动日志 `Script` 编译错误；默认防抖 1s |
| 跨节点 EntityCall 收不到 | Center 中继链路断 / 目标场景找不到实体 | 看 `Center/Handlers/CenterDispatcher` 91001 处理日志 |
