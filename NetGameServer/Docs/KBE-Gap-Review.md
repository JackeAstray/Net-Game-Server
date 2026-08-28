# 服务器与 KBEngine 差距核查（KBE-Gap-Review）

> 本文档由最初的「逐文件差距核查」演进为**现状快照 + 可优化路线**。
> 前 16 轮迭代已将原始核查中的绝大多数差距落地（P0/P1/P2 全部处理），
> 本版聚焦两件事：**① 服务器 ↔ KBE 现状对比；② 仍可优化的脚本与设计模式**。
> 涉及文件均给路径/行号，可直接据此迭代。最后更新：迭代 16。

---

## 一、服务器 ↔ KBE 现状对比

| 维度 | KBE 机制 | 本服务器现状 | 状态 |
|---|---|---|---|
| 协议/实体 | defs 声明 + 生成 + 强类型分发 | MemoryPack 生成 + `MessageDispatcher` 强类型（免锁读）；Login/DB/Center/Game 分发层均已迁移 | ✅ |
| 脏同步 | 属性脏标记 + 增量广播 | Entity 脏标记 + PropertyCodec 增量 + 同步权限分级（SyncScope，迭代2） | ✅ |
| 脚本层 | .csx 脚本 + 热更新 + 事件 | 生产生效 + 热更新/错误隔离/回滚 + 属性事件总线（迭代2/11） | ✅ |
| 进程模型 | 8 进程 + machine 看护 | 7 进程 + Supervisor 自动拉起 + 管理台仪表盘（迭代6）；无 machine 配置发现 | ◐ |
| 备份/恢复 | 定期备份 + 崩溃恢复 | 备份防泄漏 + 序列化移出主循环（迭代1/8）；持久化 `LoadEntityById`（迭代1） | ✅ |
| 断线重连 | 会话挂起 + 重连令牌 | 挂起/恢复/超时离场 + TCP 超时踢线（迭代5） | ✅ |
| 实体迁移 | 跨节点搬迁 | 玩家主实体冻结-序列化-搬迁-恢复（迭代7/9）；玩法实体 v2 待做 | ◐ |
| 帧同步 | 场景推进 + 空帧心跳 | 每场景 FrameCounter + 权威帧号/时间戳空帧（迭代3） | ✅ |
| 负载均衡 | 平滑加权分配 | `GetBestBattleNode` 平滑加权轮询（SWRR）+ 过期负载惩罚（迭代14，ProtocolVerify §17b 验证分布/剔除/公平） | ✅ |
| 防重放 | 时间戳窗口 + HMAC | Center 节点 120s 窗 + HMAC + **客户端会话侧 SessionGuard 时间窗（lifetime ≤2h / idle ≤15min）+ TokenService SessionSeq 单调序号 + NonceService 一次性 nonce（迭代16）** | ✅ |
| EntityCall | 跨进程实体调用 + 超时回调 | 91001/91002 跨进程中继（Center 路由）+ callId + 超时表 + 回执关联（迭代13，ProtocolVerify §11b） | ✅ |
| 时间同步 | 客户端-服务端时钟对齐 | 权威帧号/时间戳已具备；**时钟对齐协商缺**（B6） | ✗ |
| 配置 | 模板 + 校验 + 热重载 | reloadOnChange 已开；**无模板校验、无缓存**（B7） | ◐ |
| Profile/告警 | tick 耗时 + 慢消息告警 | TickEngine 统计 + 慢 tick 告警（迭代5） | ✅ |
| 运维 | 管理台 + 自动拉起 + 压测 | 仪表盘 + Supervisor（迭代6）；Bots 压测仅协议级 | ◐ |
| 工程 | 巨型类 + 强类型 + 测试 | 巨类全拆 partial（迭代10/12）；五套件 + 并发/压测/热迁移测试（迭代11） | ✅ |

> ✅ 对齐　◐ 部分对齐（见 §三 剩余缺口）　✗ 未实现

---

## 二、已对齐能力（16 轮迭代成果）

- **P0 数据竞争清零**：消息全部收编 `OrderedTaskQueue`/Channel 单线程串行（迭代3）；备份注册泄漏、join 全量扫盘、派遣器锁竞争修复（迭代1）。
- **性能热路径**：属性名 UTF-8 预缓存、MessageDispatcher 免锁读、备份序列化移出主循环、SceneManager 玩家-场景反索引、零拷贝写队列 + 背压（迭代1/3/4/8）。
- **脚本生产生效**：5 个玩法脚本与实体类型匹配并在 Battle 运行，事件驱动化 + 热更新状态迁移（迭代2/11）。
- **可靠性**：断线重连 + 超时踢线（迭代5）；静态分片 + 玩家实体跨节点迁移（迭代7/9）；Leader 选举（已有）。
- **运维**：Supervisor 进程看护 + 管理台仪表盘 + 慢 tick 告警（迭代6）。
- **工程**：MatchHandler/DbQueryHandler/GatewayServerApp/LoginHandler/FriendHandler 巨型类按业务域拆 partial；Game 分发层强类型化；五套件（协议/脚本/日志/网络/监管）全绿。
- **业务层强类型化（迭代13）**：Game FriendHandler 业务方法改收强类型请求对象，与 DB `DbQueryHandler` 对齐，去掉二次序列化热路径损耗。
- **EntityCall 完整链路（迭代13）**：callId + 超时表 + 回执关联 + Center 中继 91001/91002 真实跨进程调用（对标 KBE EntityCall/Mailbox 回执与超时）。
- **Battle 全量强类型化（迭代14）**：旧 JSON 路由字典整体移除，全部消息经 MessageDispatcher 强类型分发（JSON 兼容由 jsonFallback 承担），双轨归一。
- **Center 平滑加权负载均衡（迭代14）**：SWRR（权重=100-load）+ 心跳过期剔除 + 权重表周期清理，持续偏向低负载 Battle 节点。
- **玩法实体迁移 v2（迭代15）**：属主玩法实体（Skill/Item）与玩家主实体同包随迁 + 属主绑定 + 孤儿回收（CompleteMigrateOut/LeaveScene/离房三条路径），玩法实体 ID 加节点段保证跨节点不撞 ID。
- **客户端会话侧防重放（迭代16）**：SessionGuard 时间窗（lifetime+idle）由 Gateway 客户端入口强制；TokenService 嵌入 SessionSeq 单调序号拒旧 token 重放；NonceService 一次性 nonce 缓存（带 TTL 周期 GC）补齐 TokenService 文档此前承诺。

---

## 三、可优化项（脚本 + 设计模式）★

### A. 脚本层（`GameLogic/scripts/*.csx`）

| # | 项 | 现状 | 建议 |
|---|---|---|---|
| S1 | **脚本 Logger** | 所有 .csx 用 `Console.WriteLine`（Avatar.csx:24,38,54,58；Item.csx:25…），日志散落不可控 | 向脚本注入结构化 Logger（对标 KBE 脚本可 Log*），`EntityScriptBase` 暴露 Log 属性，支持级别/标签过滤 |
| S2 | **回血改框架定时器** | Avatar 用 `tickCount % 20` 轮询计数回血（Avatar.csx:17,29-31） | 改 `AddTimer(1000, …)`，消除每 tick 空转判断，与事件驱动对齐 |
| S3 | **数值边界校验** | Item/Skill 脚本直接改实体属性，无上下限防护 | 在脚本 OnMessage 内做边界钳制（Count≥0、冷却≥0），防负值/溢出/刷取 |
| S4 | **热更新显式钩子** | 热更新靠「状态只存实体属性」约定保证迁移（迭代11已验证状态保持） | 可选：`EntityScriptBase` 增 `OnReload(oldState)` 钩子 + 脚本版本号，迁移规则显式化 |

### B. 设计模式 / 结构

| # | 项 | 现状 | 建议 |
|---|---|---|---|
| D1 | **FriendHandler 业务层强类型化** | ✅ 迭代13 已落地：业务方法改收强类型请求对象（`ClientSessionWrapper session, XxxRequest? req`），去掉 `MemoryPack→反序列化→JSON 再序列化→byte[] 再反序列化` 二次序列化；旧路由仅保留反序列化适配层（`FriendHandler.RegisterRequest`） | 与 DB `DbQueryHandler` 强类型业务层对齐（Game 13 条好友/黑名单/申请/邀请消息） |
| D2 | **Battle 双轨清理** | ✅ 迭代14 已落地：旧 `MessageRouter.BuildHandlers` JSON 字典整体移除，CenterCreateScene/CenterDestroyScene 迁移至强类型 dispatcher（`Battle/Handlers/MessageRouter.cs`），JSON 兼容由 `jsonFallback` 承担 | Battle 全部消息强类型化，无遗留旧路由 |
| D3 | **EntityCall 超时/回执** | ✅ 迭代13 已落地：defs 增 `CallId`（91001/91002）→ `EntityCallHub` 超时表 + 回执关联 + `CallAsync` 回调 → `EntityManager.ExecuteRemoteCall` → Center 中继 91001/91002 真实跨进程链路（`CenterDispatcher` / `BattleServerApp.HandleEntityRemoteCallIn`），Battle tick 每 0.5s 清超时 | 框架/协议/Center/Battle 全链路已接；真实多节点集群联调可在启动全部节点后验证 |
| D4 | **玩法实体迁移 v2** | ✅ 迭代15 已落地：① 玩法实体 ID 加节点段 [32,40)（`BattleServerApp.GetGameplayIdNodePrefix`）保证跨节点迁移不撞 ID；② `EntityMigrateRequest` 增 `OwnedEntities: list<EntityMigratePayload>` 字段，源 Battle `SerializeOwnedEntitiesForMigration` 收集属主 Skill/Item 同包发送；③ 目标 Battle `RestoreMigratedEntity` 支持 ownerClientId 参数完成属主绑定；④ `RecycleOwnedEntities` 在 `CompleteMigrateOut`（已随迁）/ `LeaveScene` / 离房三条路径回收孤儿实体（ProtocolVerify §15.6 + NetworkVerify 全绿） | Skill/Item 随玩家跨 Battle 节点；离场防泄漏 |
| D5 | **负载均衡升级** | ✅ 迭代14 已落地：`GetBestBattleNode`（`Center/Handlers/NodeManager.cs:157`）改平滑加权轮询（权重=100-load）+ 心跳过期剔除 + 周期清理权重表 | 与 Nginx SWRR 对齐，持续偏向低负载节点 |
| D5 | **负载均衡升级** | `GetBestBattleNode` 按 CurrentLoad 升序（`Center/Handlers/NodeManager.cs:157`） | 加平滑加权 / 最小连接 / 过期负载惩罚 |
| D6 | **客户端会话侧防重放** | ✅ 迭代16 已落地：① `SessionGuard.IsSessionValid` 时间窗（lifetime ≤2h / idle ≤15min），Gateway 客户端入口按 `CreatedAt` 强制判定，超窗关连接；② `TokenService` 嵌入 `SessionSeq` 单调序号（payload 5 字段），`Verify(token, AntiReplayState)` 拒绝旧 seq 重放，登录发放 seq=1；③ `NonceService` 一次性 nonce 缓存（带 TTL + 周期 GC，TokenService 文档此前承诺的 NonceService 本轮补齐）；ProtocolVerify §3 全绿 | 客户端会话重放面补齐 |
| D7 | **时间同步协议** | 帧同步有权威帧号/时间戳，但无客户端-服务端时钟对齐 | 加 NTP 式 offset 协商，客户端延迟补偿 / 确定性回放更准 |
| D8 | **Bots 集成压测** | Bots 仅协议级；迭代11 压测为进程内 | Bots 走真实 Gateway→Battle 链路多机器人跑分 |
| D9 | **配置模板/缓存** | ConfigHelper reloadOnChange 已开，但无模板校验、每次 `GetConfig` 重新查节（`Shared/ConfigHelper.cs:29`） | 加配置模板定义 + 校验 + 节缓存 |

---

## 四、修订记录（紧凑）

| 迭代 | 主题 | 一句话成果 |
|---|---|---|
| 1 | P0 修复 + 工程质量 | 备份泄漏 / join 全量扫盘 / 派遣器锁 / 日志门面 / 属性名 UTF-8 缓存 / 反索引（三-2/3/7/8/11/15） |
| 2 | 脚本层对齐 | 玩法脚本生产生效 + 属性事件总线 + 同步权限分级 + 热更新完善（三-5/6/9，C7） |
| 3 | 并发 / 帧同步 | 消息单线程串行收编 + 帧同步空帧心跳 + 零拷贝（P0#1/4，B6，P1#9） |
| 4 | 传输层 | 多协议传输 + 背压写队列 + 发送合并（P1#5） |
| 5 | 断线重连 / Profile | 会话挂起恢复 + TCP 超时踢线 + tick 耗时统计 / 慢 tick 告警（B4/C4/C6） |
| 6 | 运维 | Supervisor 进程看护 + 管理台仪表盘（C1/C5） |
| 7 | 静态分片 | Battle 按场景哈希路由 + Center 路由表下发（C3 前半） |
| 8 | 队列 / 序列化 | OrderedTaskQueue 改 Channel + worker 池；备份序列化移出主循环（三-10/16） |
| 9 | 实体在线迁移 | 玩家实体冻结-序列化-搬迁-恢复，Center 协调中继（C2 第二阶段） |
| 10 | 巨型类拆分 | Match/DbQuery/Gateway/LoginHandler 拆 partial + Login 强类型收尾（三-14） |
| 11 | 补测试 + 真 bug | Battle 压测 / 并发注入 / 热迁移测试；RoomHandler 人数误计玩法实体 bug 修复 |
| 12 | Game 同构拆分 | FriendHandler（1519 行）拆 6 个 partial 按业务域（零逻辑改动） |
| 13 | D1+D3 落地 | FriendHandler 业务层强类型化（去二次序列化，Game 13 条消息，与 DB 对齐）；EntityCall 加 CallId/超时表/回执关联 + Center 中继 91001/91002 真实跨进程链路（ProtocolVerify §11b 通过） |
| 14 | D2+D5 落地 | Battle 双轨归一（移除旧 JSON 路由字典，CenterCreateScene/CenterDestroyScene 迁移强类型分发）；Center 平滑加权轮询 + 过期负载惩罚（ProtocolVerify §17b 通过） |
| 15 | D4 落地 | 玩法实体迁移 v2：EntityMigrateRequest 增 OwnedEntities 同包随迁 + 属主绑定 + RecycleOwnedEntities 三路径孤儿回收 + 玩法实体 ID 节点段防跨节点撞 ID（ProtocolVerify §15.6 + NetworkVerify 全绿） |
| 16 | D6 落地 | 客户端会话侧防重放：SessionGuard 时间窗（lifetime+idle，Gateway 入口强制）+ TokenService SessionSeq 单调序号拒旧重放 + NonceService 一次性 nonce 缓存（ProtocolVerify §3 全绿） |
| 17 | 规划 | Bots 集成压测（D8）、脚本层 entityMailbox 封装（D7） |
