# 重构成果：Net-Game-Server → KBE 式架构（最终版）

> 本文档总结重构的最终状态：**做了什么、对标 KBE 的哪些机制、验证结果、剩余工作与迁移指南**。
> 配套文档：`Docs/Refactor-Plan.md`（阶段路线）、`Docs/Protocol.md`（协议约定）。

---

## 一、重构总览

| 阶段 | 目标 | 状态 |
|---|---|---|
| P0 | 声明式协议 + 生成器 + 二进制序列化 + 配置化路由 + 安全加固 | ✅ 完成 |
| P1 | 实体框架 + tick 引擎 + KCP + DB 队列 + 跨进程调用 + Dispatcher 迁移 | ✅ 完成 |
| P2 | 脚本宿主（游戏逻辑与框架物理分离、热更新） | ✅ 完成 |
| P3 | KBE 级可靠性（实体备份/恢复、日志聚合、压测工具） | ✅ 完成 |

**代码规模**：新增 `Framework/*` 4 个底层项目 + `Protogen` 生成器 + `GameLogic` 脚本层 + 2 个验证套件，
底层框架 19 个源文件，协议 defs 142 条消息，全部 **0 错误构建**。

---

## 二、对标 KBE 的机制实现

### 1. 协议定义 → 自动生成（对标 KBE entity_defs + interfaces）

```
Protocol/defs/*.def（XML 声明式，140 条消息）
        │  Protogen（MSBuild 集成，构建前自动重跑）
        ▼
Framework.Protocol/Generated/
  ├─ MessageIds.g.cs     （消息 ID 常量）
  ├─ Messages.g.cs       （消息/结构体类，MemoryPack 二进制序列化）
  └─ RouterTable.g.cs    （MsgId → 目标服务器/类型 配置化路由表）
```

- 改协议只改 def，生成代码不手改（对标 KBE "def 是契约"）
- 支持 `list:T` / `map:K,V` / 嵌套 `Struct` / `optional` / `internal` 标记

### 2. 实体/属性框架（对标 KBE ScriptDefModule + Witness）

`Framework/Framework.Entity/`：
- `EntityDef`：属性声明（类型/同步标记），对标 `ScriptDefModule`/`PropertyDescription`
- `Entity`：属性存储 + **脏标记**，`Set` 值变化才标记，`SetSilent` 全量初始化不标记
- `PropertyCodec`：脏属性二进制增量编解码（对标 Witness 只发变更属性）
- `EntityManager`：实体集合 + 远程调用分发

**Battle 已接入**：位置上报 → 更新属性 → 只广播脏属性增量（`EntityDeltaSync`），
进场景下发全量快照（`EntitySnapshot`）——替代原来整包 JSON 全量广播。

### 3. Battle 单线程 tick 引擎（对标 KBE gameUpdateHertz）

`Framework/Framework.Tick/TickEngine.cs`：
- 固定频率主循环（默认 20Hz，可配 `BattleTickHertz`），所有逻辑 tick 内串行（无锁）
- 可取消/周期定时器
- `FrameSyncManager`：客户端输入入队 → tick 聚合 → 广播权威帧（帧同步真正落地）

### 4. KCP 可靠传输（对标 KBE kcp_packet_*）

`Network/Kcp/`：KcpServer / KcpSession / KcpClientWrapper
- UDP 之上可靠有序传输，快速模式（NoDelay 1,10,2,1），ArrayPool 内存复用
- Gateway 已启用 KCP 监听（TCP:31300 / **KCP:31301** / UDP:31302 / WS:31303）
- 端到端 10 条消息双向按序验证通过

### 5. DB 任务队列（对标 KBE Buffered_DBTasks）

`Framework/Core/OrderedTaskQueue.cs`：按 key（实体 ID/DBID）严格保序串行，不同 key 并发，
后台线程池执行不阻塞主循环。20 任务 × 4 key 保序验证通过。

### 6. EntityCall 跨进程调用（对标 KBE Mailbox/EntityCallAbstract）

`Framework/Entity/EntityCall.cs`：
- 本地/跨节点实体引用，`Call(method, args)` 远程调用
- 参数 `ArgCodec` 二进制编解码（7 种类型）
- 协议 `EntityRemoteCall`(91001)/`EntityRemoteCallResult`(91002)
- 验证：本地调用/消息分发/参数 round-trip 全部通过

### 7. 配置化消息分发（对标 KBE 自动生成处理器注册表）

`Framework/Protocol/MessageDispatcher.cs`：
- 注册强类型 handler，MsgId 自动取自生成代码（零手写分支）
- MemoryPack 编译期 formatter + **JSON 兼容回退**（新旧客户端双格式）
- Battle（5 消息）/ Login（4 消息）已迁移，`ISessionContext` 会话抽象

### 8. 安全加固（对标 KBE 认证体系）

| 漏洞 | 修复 |
|---|---|
| SessionId 纯递增可预测 | 加密随机 + 计数器混合（`SessionIdGenerator`） |
| Token 是 Guid 占位符 | HMAC-SHA256 无状态签名 Token（`TokenService`，含过期/防篡改） |
| 内部端口无认证可伪造身份 | `InternalAuthFilter` 握手（HMAC + 时间戳），全部服务间连接已接入 |

### 9. 脚本宿主（对标 KBE Python 脚本层）✅ 核心

`Framework/Scripting/` + `GameLogic/scripts/*.csx`：
- `IEntityScript`：OnCreate / OnDestroy / OnTick / OnMessage（对标 Python 实体回调）
- `ScriptHost`：Roslyn Scripting 编译 .csx，**文件变更自动热更新**（防抖）
- **游戏逻辑与底层框架物理分离**：改玩法只改 .csx，框架零改动
- Battle 已接入：tick 驱动脚本 OnTick，实体创建/销毁通知脚本
- 验证：加载/事件/tick/消息/**热更新（改脚本立即生效）** 全部通过

### 10. 实体备份/恢复（对标 KBE backuper + archiver + restore）

`Framework/Entity/EntityBackupService.cs`：
- **平滑分摊**：每 tick 只备份 `entitiesCount/periodInTicks + remainder` 个实体（避免 IO 尖峰）
- 异步落盘（OrderedTaskQueue），`RestoreFromFile` 恢复属性
- 验证：10 实体 4 tick 一轮备份，恢复 10/10，属性正确

### 11. DB 任务队列接入（对标 KBE Buffered_DBTasks 落地）

`DB/Routing/MessageRouter.cs`：所有 DB 请求经 `OrderedTaskQueue` 按**会话 ID 保序**执行——
同一调用方（Login/Game）的请求严格串行（先写后读不乱序），不同调用方并发执行。
修复了原实现并发下"好友/黑名单重复插入"的隐患。

### 12. 脚本错误隔离/回滚

`Framework/Scripting/ScriptHost.cs`：
- 脚本编译失败时**保留旧实例继续运行**，错误记录到 `LastLoadErrors`（可查询）
- 修复脚本后自动清除错误并加载新版本
- 验证：写入编译错误脚本 → 旧实例保留 + 错误记录；修复 → 错误清除 + 新逻辑生效

### 13. Game Chat Dispatcher 迁移

`Game/Handlers/GameDispatcher.cs` + `GameSessionContext`：
- ChatSend 消息迁移到强类型分发（JSON/二进制双格式兼容），复用现有业务逻辑
- Game 收包入口 Dispatcher 优先、旧路由回退

### 14. 脚本全局共享数据（对标 KBE KBEngine.globalData）

`Framework/Scripting/ScriptHost.cs`：
- `GetGlobal/SetGlobal/GlobalKeys`：框架与脚本共享状态（配置、全局开关、跨实体数据）
- 脚本类内通过 `Framework.Scripting.ScriptHost.Current?.GetGlobal("Key")` 静态访问
- 验证：框架设置 `DamageMultiplier=2` → 脚本自动读取倍率生效（70-10×2=50）✓

### 15. Center Dispatcher 迁移

`Center/Handlers/CenterDispatcher.cs` + `CenterSessionContext`：
- CenterListRooms / RoomMemberList 迁移到强类型分发（双格式兼容）
- Center 收包入口 Dispatcher 优先、旧字典回退；def 字段与旧协议对齐（IncludePrivate/Room）

### 16. 玩法脚本示例扩展（多脚本共存）

`GameLogic/scripts/`：
- `Avatar.csx`：玩家角色（创建初始化/每帧回血/受伤处理/全局倍率）
- `Npc.csx`：野怪（出生随机坐标/正弦巡逻 AI/受击死亡/经验掉落写入全局数据）
- 验证：两脚本同时加载互不干扰；Npc 巡逻位置变化、死亡掉落 `TotalExpDropped=20` ✓

### 17. 压测工具（对标 KBE bots）

`Bots/Bots.csproj`：模拟多个客户端连接 Gateway，发送登录/实体同步消息，统计吞吐与延迟。
用法：`Bots --count 100 --host 127.0.0.1 --port 31300 --duration 10`
（需服务器已启动：DB → Center → Login → Game/Battle → Gateway）

### 18. 日志聚合（对标 KBE logger）

`Logger/Logger.csproj` + `Framework/Core/RemoteLogClient.cs` + `Shared/RemoteLog.cs`：
- **LoggerServer**：独立日志聚合进程（UDP 监听 31320），按节点分文件落盘（滚动按天）+ 控制台实时输出
- **RemoteLogClient**：各服务器订阅 `Log.LogSink`，日志异步批量上报（500ms/64 条），失败静默降级不影响业务
- **接入**：全部 6 个服务器 Program.cs 已接入 `RemoteLog.Initialize`（配置 `LoggerHost/LoggerPort` 后生效）
- 验证：Info/Warn/Error 三类日志端到端上报 + 落盘全部通过

### 19. 实体自动持久化 + 崩溃恢复（对标 KBE entity_table + restore_entity_handler）

`Framework/Entity/EntityPersistenceService.cs`：
- **属性声明驱动自动存取**：EntityDef 属性 → 序列化落盘（无手写 SQL/字段映射），按类型分目录
- `SaveEntity/LoadEntity/LoadEntityById/DeleteEntity/RestoreAll`：单实体与全量恢复
- **Battle 接入**：玩家加入时自动恢复存档（`[已恢复存档]` 标记）、离开/断开时自动保存
- 验证：5 实体保存 → 模拟崩溃重启 → 全量恢复 5/5 属性正确 + 单实体加载/删除 ✓

### 20. Center 注册表持久化（HA 基础）

`Center/Handlers/NodeManager.cs`：
- `SaveSnapshotToFile/RestoreFromSnapshotFile`：节点注册表 JSON 快照落盘/恢复
- Center 启动时恢复静态节点信息（会话/心跳由节点重连自动更新），周期 10s 保存快照

### 21. Center 回调类消息 Dispatcher 迁移

`Center/Handlers/CenterDispatcher.cs`：
- `CenterSessionContext` 扩展：`RoutedUserId/RoutedUid/RoutedNickname`（收包入口注入身份元数据）+ `Notify()`（多网关路由通知广播，对标旧 SendToGateway）
- 新增迁移：RoomReady（准备）、RoomTransferOwner（房主转移）、RoomKickMember（踢人）——带通知广播回调的消息已全部迁移
- def 补齐：RoomReadyResult/RoomTransferOwnerResult/RoomKickMemberResult 增加 Room 字段

### 22. Center 全量客户端消息迁移（13/13 完成）

`Center/Handlers/CenterDispatcher.cs`：
- 新增迁移 8 个消息：CenterMatch（匹配）、CenterCreateRoom、CenterJoinRoom、CenterCloseRoom、
  CenterLeaveRoom、CenterUpdateRoomSettings、CenterStartRoomGame、CenterRoomChat
- **Center 全部 13 个客户端消息已迁移到强类型分发**（查询类 2 + 回调类 3 + 操作类 8），
  旧字典仅保留内部节点消息（注册/心跳/场景创建回执）
- 集成验证：创建房间 → 加入 → 聊天 → 离开 全链路通过（MatchHandler 真实业务执行）

### 23. Game FriendHandler 迁移（13/13 完成）

`Game/Handlers/GameDispatcher.cs`：
- 新增迁移 5 个消息：FriendApply（申请发起）、FriendApplyList（申请列表）、FriendApplyHandle（申请处理）、FriendInviteGame（游戏邀请）、FriendInviteGameAck（邀请回执）
- **Game 全部 13 个请求类消息已迁移**（Chat 1 + Friend/Blacklist/Apply/Invite 12），旧路由仅剩回退路径
- 模式：生成消息类 → 复用现有 FriendHandler 入口（身份映射 + DB 转发 + 异步响应管线）

### 24. Center 主备 Leader 选举（HA）

`Framework/Core/LeaderElection.cs`：
- 基于独占文件锁的选举：同一时刻仅一个 Leader；Leader 心跳续约；Standby 周期尝试抢占（故障自动接管）
- Center 接入：配置 `LeaderLockFile` 启用主备；非 Leader 拒绝业务消息（注册/心跳仍接受）；健康接口暴露 `isLeader`
- 验证：争锁唯一性（A=True/B=False）→ A 故障 B 自动接管 → B 让出后 A 重新选举 全部通过

### 22. Quest 任务脚本示例（三脚本协作）

`GameLogic/scripts/Quest.csx`：
- 任务系统：监听全局数据 `TotalExpDropped`（Npc 击杀经验），达到阈值 → 任务完成 → 奖励写回全局数据
- 展示"全局数据即脚本间总线"的松耦合协作（Npc 产出 → Quest 消费，无互相引用）
- 验证：Avatar + Npc + Quest 三脚本共存；击杀 2 只 Npc（40 经验）→ Quest 自动完成 ✓

### 25. DB 全量消息 Dispatcher 迁移（20/20 完成）

`Protocol/defs/Db.def` + `DB/Handlers/DbDispatcher.cs` + `DB/Handlers/DbSessionContext.cs`（同文件）：
- **defs 字段对齐**：DbFriendAdd/DbFriendRemove/DbFriendSetRemark 改用 `FriendUniqueId`、DbBlacklistAdd/DbBlacklistRemove 改用 `TargetUniqueId`、
  DbChangePassword 增加 `UserId`、DbFriendApplyCreate 改用 `TargetUniqueId`——生成类与旧 JSON 协议字段语义完全一致（双格式兼容基础）
- **DbDispatcher**：20 条 DB 请求消息全量注册强类型分发（MemoryPack + JSON 兼容回退），
  处理器模式 = 生成消息 → 适配旧请求对象 → 复用现有 DbQueryHandler 业务管线（响应仍走 SendDbResponse，零业务改动）
- **DbSessionContext**：ISessionContext 适配，Send 时自动附加 RequestId 路由元数据（与 RequestContextSession 等价）
- **RequestContextSession 提升**：从 MessageRouter 私有嵌套提升为公共类，新旧管线共用
- **收包管线**：认证后 Dispatcher 优先，未注册 MsgId 回退旧路由（对标 Center/Game 迁移模式）
- 验证：注册数 20/20、生成类 round-trip（FriendUniqueId 等字段）、JSON 旧格式兼容、RequestId 路由（4242 往返）全部通过 ✓

### 26. 玩法脚本扩展：Skill / Item（五脚本共存）

`GameLogic/scripts/Skill.csx` + `Item.csx`：
- **Skill.csx**：技能系统——主动释放（OnMessage CastSkill）按 `基础伤害 × 等级 × 全局倍率` 结算、
  冷却管理（OnTick 递减 CooldownRemaining，冷却中拒绝释放）、成长系统（累计 3 次释放升级）、
  全局数据 `SkillTotalDamage`/`SkillLevel` 供任务类脚本消费
- **Item.csx**：物品系统——拾取堆叠（OnMessage Pickup）、使用消耗（UseItem 回复生命并累计治疗量）、
  周期自动掉落（OnTick 模拟怪物掉宝）、全局数据 `ItemTotalPicked`/`ItemHealedTotal`/`ItemAutoDrops`
- 验证：五脚本（Avatar+Npc+Quest+Skill+Item）共存；技能 3 次释放 → 升级 → 伤害翻倍（累计 50）✓；
  物品拾取 5 → 使用 3 → 自动掉落 1 → 再使用 1（累计治疗 40）✓

---

## 三、验证结果汇总

| 套件 | 覆盖 | 结果 |
|---|---|---|
| `Tests/ProtocolVerify` | 序列化/路由/Token/认证/实体/增量/tick/KCP/队列/元数据/Dispatcher/备份/Leader 选举/DB 分发（18 组） | ✅ 全部通过 |
| `Tests/ScriptHostVerify` | 脚本加载/事件/tick/热更新/错误隔离/多脚本/全局数据/Skill/Item（14 组） | ✅ 全部通过 |
| `Tests/LoggerVerify` | 日志聚合端到端（上报/接收/落盘） | ✅ 全部通过 |
| 解决方案构建 | 16 个项目 | ✅ 0 错误 |

---

## 四、剩余工作（✅ 已全部完成）

### P1 收尾 ✅
- [x] Center 匹配/房间操作类消息（带 sendToGatewayFunc 回调）的 Dispatcher 迁移（见条目 22：Center 13/13 完成）
- [x] Game FriendHandler / DB DbQueryHandler 剩余消息的 Dispatcher 迁移（Game 见条目 23：13/13；DB 见条目 25：20/20）

### P2 完善 ✅
- [x] 更多玩法脚本示例（Skill/Item/Quest）（Quest 见条目 22；Skill/Item 见条目 26，五脚本共存）

### P3 完善 ✅
- [x] Center 主备（见条目 24：Leader 选举 + 注册表快照持久化，故障自动接管验证通过）

---

## 五、迁移指南（新玩法/新协议开发流程）

### 新增一条消息
1. 在 `Protocol/defs/<Server>.def` 添加 `<Message>` 声明（id/name/target/字段）
2. 构建自动触发 Protogen 重新生成（或手动 `dotnet run --project Protogen -- Protocol/defs Framework/Protocol/Generated`）
3. 在目标服务器的 `BuildDispatcher` 注册强类型 handler：
   ```csharp
   dispatcher.RegisterSync<YourMessage>((ctx, msg) => { /* 业务 */ ctx.Send(new YourResult { ... }); },
       jsonFallback: true); // 旧客户端 JSON 兼容
   ```
4. 客户端按生成的消息类（MemoryPack）或旧 JSON 格式对接

### 新增实体类型
1. 定义 `EntityDef`（属性声明）
2. 编写 `GameLogic/scripts/Xxx.csx` 脚本类（实现 IEntityScript），`return new XxxScript();` 结尾
3. 重启 Battle（或热更新自动加载），实体创建时脚本 OnCreate 自动生效

### 新玩法（不改框架）
- 全部写在 .csx 脚本里：OnTick 做逻辑、OnMessage 响应客户端、Set 属性触发增量同步
- 脚本变更保存即热更新，无需重新编译/重启

### 关键目录速查
```
Protocol/defs/           协议声明（唯一事实来源）
Protogen/                代码生成器
Framework/               底层框架（Core/Protocol/Entity/Tick/Scripting）
GameLogic/scripts/       游戏逻辑脚本层（可热更新）
Tests/                   验证套件（ProtocolVerify / ScriptHostVerify）
Docs/                    文档（Refactor-Plan.md / 本文件）
```
