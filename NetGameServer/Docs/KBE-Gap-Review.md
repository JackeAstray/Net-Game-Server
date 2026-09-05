# 服务器与 KBEngine 差距核查（KBE-Gap-Review）

> 现状快照 + 可优化路线。前 20 轮迭代已将原始核查的差距基本落地。
>
> 本版聚焦两件事：① 服务器 ↔ KBE 现状对比（迭代 21 全部 ✅）；
> ② 关键能力落地路径的可追溯索引（指向代码 / 文档）。
> 涉及文件均给路径/行号，可直接据此迭代。最后更新：迭代 21。

---

## 一、服务器 ↔ KBE 现状对比（迭代 21）

> ✅ 对齐　◐ 部分对齐　✗ 未实现

| 维度 | KBE 机制 | 本服务器现状 | 状态 |
|---|---|---|---|
| 协议/实体 | defs 声明 + 生成 + 强类型分发 | MemoryPack 生成 + `MessageDispatcher` 强类型（免锁读）；Login/DB/Center/Game/Battle 分发层均已迁移 | ✅ |
| 脏同步 | 属性脏标记 + 增量广播 | Entity 脏标记 + PropertyCodec 增量 + 同步权限分级（SyncScope，迭代2） | ✅ |
| 脚本层 | .csx 脚本 + 热更新 + 事件 | 生产生效 + 热更新/错误隔离/回滚 + 属性事件总线 + OnReload 钩子 + ScriptVersion 跟踪（迭代2/11/19） | ✅ |
| 脚本层 Logger | KBE 脚本可调 `KBEngine.INFO/DEBUG` | `EntityScriptBase.Log` 结构化模板日志（迭代19 S1，Serilog 转发，Tag 过滤） | ✅ |
| 脚本层定时器 | KBE `addTimer` 全局定时器 | `AddTimer(entity, ms, cb, repeat)`（迭代19 S2，框架 TickEngine 驱动） | ✅ |
| 脚本层边界 | KBE 业务自行处理 | `MathClampSet/MathClampAdd`（迭代19 S3，防负值/溢出/上限） | ✅ |
| 脚本热更新 | KBE reload 实体 + 状态保留 | `OnReload(oldState)` 钩子 + `ScriptVersion` 跟踪（迭代19 S4） | ✅ |
| **进程模型 / Machine** | 8 进程 + machine 看护（kbengine.xml 拓扑 + 依赖启动 + 崩溃重启） | **Tools/Machine（topology.json 依赖拓扑 + replicas 多实例 + TCP 探针就绪 + 崩溃指数退避 + machine/instance 字段注入）+ Center 节点注册协议 3 字段扩展 + 管理台『机器/进程总览』页（/api/center/cluster）+ `--emit-supervisor-config` 兼容老 Supervisor（迭代20）** | **✅** |
| 备份/恢复 | 定期备份 + 崩溃恢复 | 备份防泄漏 + 序列化移出主循环（迭代1/8）；持久化 `LoadEntityById`（迭代1） | ✅ |
| 断线重连 | 会话挂起 + 重连令牌 | 挂起/恢复/超时离场 + TCP 超时踢线（迭代5） | ✅ |
| 实体迁移 | 跨节点搬迁 | 玩家主实体冻结-序列化-搬迁-恢复（迭代7/9）；玩法实体 v2（迭代15） | ✅ |
| 帧同步 | 场景推进 + 空帧心跳 | 每场景 FrameCounter + 权威帧号/时间戳空帧（迭代3） | ✅ |
| 时间同步 | KBE time sync（clock align） | 客户端-服务端 NTP 式协商（迭代19 D7，40010/40011，TimeSyncManager） | ✅ |
| 配置 | KBE res + 校验 | reloadOnChange + 内存节缓存 + IConfigValidator 模板校验 + OnConfigChanged 热重载钩子（迭代19 D9） | ✅ |
| 负载均衡 | 平滑加权分配 | `GetBestBattleNode` 平滑加权轮询（SWRR）+ 过期负载惩罚（迭代14） | ✅ |
| 防重放 | 时间戳窗口 + HMAC | Center 节点 120s 窗 + HMAC + SessionGuard 时间窗 + TokenService SessionSeq + NonceService（迭代16） | ✅ |
| EntityCall | 跨进程实体调用 + 超时回调 | 91001/91002 跨进程中继（Center 路由）+ callId + 超时表 + 回执关联（迭代13） | ✅ |
| EntityMailbox | entityMailboxComponent / cellMailbox | `EntityMailbox.Local/Remote` + `Entity.Mailbox` 懒属性，csx 脚本 Call/CallAsync（迭代17） | ✅ |
| Profile/告警 | tick 耗时 + 慢消息告警 | TickEngine 统计 + 慢 tick 告警（迭代5） | ✅ |
| Bots 压测 | bots 多机器人跑分 | TCP/WS 协议 + RTT/P50/P95 + 时间同步 offset 分布 + ramp-up（迭代19 D8） | ✅ |
| 运维 | 管理台 + 自动拉起 + 压测 | 仪表盘 + Supervisor + Machine（迭代6/20） | ✅ |
| 工程 | 巨型类 + 强类型 + 测试 | 巨类全拆 partial（迭代10/12）；**八套件（新增 MachineVerify / LifecycleVerify / ClientGenVerify）+ 压测/热迁移/时间同步/Machine 化/持久化/客户端生成测试（迭代11/19/20/21）** | ✅ |

**总览**：迭代 19 时 21 维对比中 1 项 ◐（machine 配置发现）；**迭代 20 后全部 21 项 ✅**，迭代 21 补齐实体位置路由（对标 ET Location）与持久化/客户端生成验证。

### 迭代 20 增量（Machine 化落点）

- `Tools/Machine/Program.cs`：topology.json 依赖解析 + replicas 展开 + TCP 探针 + 指数退避
- `Shared/NodeLaunchArgs.cs` + 6 节点 Program.cs 接受 `--port/--host/--center-host/--node-id/--instance-id/--machine-id/--supervised-by`
- `Shared/ConfigHelper.SetRuntimeOverride` 运行时覆盖（最高优先级内存配置源）
- 协议扩展：`CenterRegisterNodeRequest` 增 `InstanceId/MachineId/SupervisedBy` 三字段（参与签名，验签路径已扩展）
- `Center/Handlers/NodeManager.cs`：`ServerNodeInfo` 持久化 3 字段
- `Center/Controllers/CenterController.cs`：`/api/center/cluster` 按 MachineId 聚合（另含 `/health` `/nodes` `/summary` `/rooms`）
- 管理台『机器/进程总览』页（前端）
- `--emit-supervisor-config` 把 topology 渲染成 supervisor.json 保持老 Supervisor 路径可用

### 迭代 21 增量（实体位置路由 + 持久化/客户端生成收口）

- 实体位置服务（对标 ET Location）：Battle 实体生成/绑定/迁移完成发 `91007` 登记，销毁/迁出发 `91008` 注销；
  `91009` 查询 → Center 回 `91010`（含目标节点 host/port 供 Battle 直达）；TTL 120s 周期清扫。
  Battle 侧 `EntityCallRouter` 缓存 entityId→nodeId，迁移路由完成（91005）与位置响应（91010）刷新缓存；
  `EntityCallDirectRouting=true` 时优先 `EntityCallDirectRouter` 直发，失败自动回退 Center 中继。
- `Framework/Framework.Persistence/`（`PersistenceStoreFactory` + MySql / PostgreSql / Redis / File 四类实体存储）
- `Tests/LifecycleVerify` 第七套件：可插拔持久化 / 批量落库 / 健康检查（`/healthz` `/readyz`，端口+10000）/ 优雅关闭
- `Tools/ClientGen`（从 `ProtocolManifest.json` 生成 Unity C# / UE C++ codec）+ `Tests/ClientGenVerify` 第八套件（与服务器 MemoryPack 逐字节双向互验）
- `Shared/HealthServer.cs` 全节点 `/healthz` `/readyz`；DB `SchemaDoctor/SchemaMigrator`；Center 主备 `LeaderElection.IsLeader`

---

## 二、可优化项索引（按代码定位）

> 此处只列**当前仍在演进**或**值得在生产前再核验**的项；✅ 已收口项见 §一 状态表。

| # | 项 | 状态 | 代码定位 / 文档 |
|---|---|---|---|
| S1 | 脚本 Logger（`EntityScriptBase.Log` + Tag 过滤） | ✅ 迭代19 | `Framework/Framework.Scripting/IEntityScript.cs`（含 `EntityScriptBase`）；[GameLogic/scripts/README.md](../GameLogic/scripts/README.md) |
| S2 | 脚本定时器（`AddTimer`） | ✅ 迭代19 | `EntityScriptBase.AddTimer`；`Battle/BattleServerApp.cs`（ScriptHost 创建 + tick 注入） |
| S3 | 脚本边界钳制（`MathClampSet/Add`） | ✅ 迭代19 | `EntityScriptBase.MathClamp*` |
| S4 | 热更新 OnReload + ScriptVersion | ✅ 迭代19 | `Framework.Scripting.IEntityScript.OnReload` + `ScriptVersion` |
| D1 | FriendHandler 业务层强类型化 | ✅ 迭代13 | `Game/Handlers/FriendHandler.*.cs`（partial 6 段） |
| D2 | Battle 双轨归一 | ✅ 迭代14 | `Battle/Handlers/MessageRouter.cs`（旧 JSON 路由字典已移除） |
| D3 | EntityCall 超时/回执 | ✅ 迭代13 | `Framework/Framework.Entity/EntityCallHub.cs` + `Center/Handlers/CenterDispatcher.cs` + `Battle/Handlers/MessageRouter.cs`（91001/91002） |
| D4 | 玩法实体迁移 v2 | ✅ 迭代15 | `BattleServerApp.GetGameplayIdNodePrefix` + `SerializeOwnedEntitiesForMigration` + `RestoreMigratedEntity` + `RecycleOwnedEntities` |
| D5 | 负载均衡升级 | ✅ 迭代14 | `Center/Handlers/NodeManager.cs:157`（SWRR） |
| D6 | 客户端会话侧防重放 | ✅ 迭代16 | `Framework/Framework.Core/Security/SessionGuard.cs` + `TokenService.cs` + `NonceService.cs` |
| D7 | 脚本层 entityMailbox 封装 | ✅ 迭代17 | `Framework/Framework.Entity/EntityMailbox.cs` + `Entity.Mailbox` 懒属性 |
| D7' | 时间同步协议 | ✅ 迭代19 | `Framework/Framework.Protocol/Messages/BattleMessages.cs`（40010/40011）+ `Battle/Handlers/TimeSyncManager.cs` |
| D8 | Bots 集成压测 | ✅ 迭代19 | `Bots/Program.cs`（TCP/WS + ramp-up + RTT 分布 + time sync offset） |
| D9 | 配置模板/缓存 | ✅ 迭代19 | `Shared/ConfigHelper.cs`（节缓存 + `IConfigValidator` + `OnConfigChanged`） |

---

## 三、修订记录（紧凑）

| 迭代 | 主题 | 一句话成果 |
|---|---|---|
| 1 | P0 修复 + 工程质量 | 备份泄漏 / join 全量扫盘 / 派遣器锁 / 日志门面 / 属性名 UTF-8 缓存 / 反索引 |
| 2 | 脚本层对齐 | 玩法脚本生产生效 + 属性事件总线 + 同步权限分级 + 热更新完善 |
| 3 | 并发 / 帧同步 | 消息单线程串行收编 + 帧同步空帧心跳 + 零拷贝 |
| 4 | 传输层 | 多协议传输 + 背压写队列 + 发送合并 |
| 5 | 断线重连 / Profile | 会话挂起恢复 + TCP 超时踢线 + tick 耗时统计 / 慢 tick 告警 |
| 6 | 运维 | Supervisor 进程看护 + 管理台仪表盘 |
| 7 | 静态分片 | Battle 按场景哈希路由 + Center 路由表下发 |
| 8 | 队列 / 序列化 | OrderedTaskQueue 改 Channel + worker 池；备份序列化移出主循环 |
| 9 | 实体在线迁移 | 玩家实体冻结-序列化-搬迁-恢复，Center 协调中继 |
| 10 | 巨型类拆分 | Match/DbQuery/Gateway/LoginHandler 拆 partial + Login 强类型收尾 |
| 11 | 补测试 + 真 bug | Battle 压测 / 并发注入 / 热迁移测试；RoomHandler 人数误计玩法实体 bug 修复 |
| 12 | Game 同构拆分 | FriendHandler（1519 行）拆 6 个 partial 按业务域（零逻辑改动） |
| 13 | D1+D3 落地 | FriendHandler 业务层强类型化；EntityCall 加 CallId/超时表/回执关联 + Center 中继 91001/91002 |
| 14 | D2+D5 落地 | Battle 双轨归一；Center 平滑加权轮询 + 过期负载惩罚 |
| 15 | D4 落地 | 玩法实体迁移 v2：属主随迁 + 属主绑定 + 孤儿回收 + 玩法实体 ID 节点段 |
| 16 | D6 落地 | SessionGuard 时间窗 + TokenService SessionSeq + NonceService 一次性 nonce |
| 17 | D7 落地 | 脚本层 entityMailbox：EntityMailbox.Local/Remote + Entity.Mailbox 懒属性 |
| 18 | 规划 | Bots 集成压测（D8）、脚本层 entityMailbox 跨节点真实集成 |
| 19 | S1-S4 + D7'/D8/D9 落地 | 脚本层结构化日志/定时器/边界/热更新钩子 + 客户端-服务端时间同步 + Bots 增强 + ConfigHelper 模板校验 |
| 20 | KBE machine 化 | Tools/Machine + 节点注册协议 3 字段 + 管理台机器视图 + MachineVerify 第六套件 |
| 21 | 实体位置路由 + 收口 | 91007~91010 位置登记/查询（对标 ET Location）+ EntityCallDirectRouter 直达/回退 + Framework.Persistence 四存储 + LifecycleVerify 第七套件 + ClientGen/ClientGenVerify 第八套件 |

> 历史归档（含 P0~P3 阶段数据快照）见 [Refactor-Summary.md](Refactor-Summary.md)。
