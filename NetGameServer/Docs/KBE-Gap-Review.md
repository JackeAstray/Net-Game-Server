# 服务器与 KBEngine 差距核查报告（代码级 Review）

> 本报告基于对当前仓库源码的逐文件核查（Framework / Battle / Network / Gateway / Center / Login / DB / GameLogic 脚本），
> 对既有文档 `KBE-Gap-Analysis.md` 的每一项声明做**真伪验证**，并补充代码级新发现的差距、可优化脚本与更好的设计建议。
> 核查日期：本迭代。涉及文件均给出路径与行号，可直接据此迭代。

---

## 一、结论速览

| 维度 | 原文档声明 | 核查结果 | 说明 |
|---|---|---|---|
| 协议/实体框架（defs / 脏同步 / EntityCall） | ✓ 基本对齐 | **属实** | 但 EntityCall 无超时回调、属性无同步权限分级 |
| 进程模型 7 vs 8 进程 | ◐ 对标但简化 | **属实** | 无 machine；另有部署脚本但无进程看护 |
| 游戏逻辑层 .csx 脚本 | ✓ 对齐 | **需修正** ⚠️ | 脚本仅在 Battle 加载，且 5 个玩法脚本与生产实体类型（Player）不匹配，**运行时全部无效**，仅测试套件验证 |
| 可靠性（备份/恢复/选举） | ◐ 部分对齐 | **需修正** ⚠️ | 存在真实 Bug：备份服务每 tick 重复注册管理器（内存泄漏）；断线重连仍缺 |
| 负载均衡 C3 | ✗ 无分配算法消费 | **过时** ✅ | `GetBestBattleNode()` 已在 MatchHandler 消费（按负载最低分配 Battle） |
| 防重放 B8 | ✗ 无时间戳窗口 | **过时** ✅ | Center 节点注册/状态上报已有 120s 时间窗 + HMAC + 常量时间比较 |
| 运维工具链 | ✗ 差距最大 | 属实 | Publish 下有 StartServers/KillServers.bat，但无自动拉起/监控面板 |

**核心结论**：与 KBE 的单机机制差距小于文档所述（C3/B8 已落地）；**最大风险不在"缺什么"，而在"声称的单线程模型实际未被强制执行"**——消息在 ThreadPool 上并发处理同一实体，属性字典无锁写，存在数据竞争；热路径上存在大量不必要的日志格式化与字节拷贝，以及一处确定的备份管理器内存泄漏。

---

## 二、原文档声明逐项核查

### A 组（原文档认为"已对标"）——属实，但各有保留

| 项 | 核查结论 |
|---|---|
| 协议生成 / MessageDispatcher | 属实。RouterTable.g.cs 生成路由表，`MessageDispatcher` 强类型分发（MessageDispatcher.cs:33）。但 `TryDispatch` 每次消息 `lock(gate)` 查表（:135-138），单线程模型下无必要；且 Battle 同时保留旧 handlers 字典双轨运行 |
| 实体/脏标记 | 属实。Entity.cs 实现。但 `values` 是 `Dictionary<string, object?>`，**无锁**（:13-16），而写入来自网络线程——见"三、新发现"第 1 条 |
| TickEngine | 属实。固定 hertz + PriorityQueue 定时器。缺 tick 耗时统计（原文档 C6 属实） |
| KCP/多协议传输 | 属实。但所有协议共用同步 `ISession.Send`（见"三、新发现"第 5 条） |
| DB 队列 OrderedTaskQueue | 属实。按键串行、跨键并发。`SweepIdle` 的"移除后重放回"分支是死代码（IsIdle 已保证 pending==0，OrderedTaskQueue.cs:52-65） |
| 脚本宿主+热更新 | 部分属实。热更新/错误隔离/回滚机制齐全（ScriptHost.cs:180-227），**但见"脚本层核查"一节：生产运行时脚本不生效** |
| Logger 聚合 / Bots | 属实。Logger 进程 + UDP 31320；Bots/Program.cs 存在 |

### B 组（部分对标）——逐条修正

| # | 原声明 | 核查结果 |
|---|---|---|
| B1 实体持久化 | 文件落盘，无 SQL 查询 | **属实，且比文档更严重**：`RoomHandler.HandleJoinRequestAsync` 每次加入房间都调用 `RestorePersistedPlayers()` → `RestoreAll("Player")` 全目录扫描 + 读取**所有**玩家文件再 `FirstOrDefault` 匹配（RoomHandler.cs:76-77 → EntityPersistenceService.cs:106-130）。O(全部玩家文件数) 的同步文件 IO 发生在加入路径上。应改用已有的 `LoadEntityById` 单条加载 |
| B2 属性同步权限 | 无分级广播 | 属实。`EntityProperty.SyncToClient` 仅 bool（EntityDef.cs:28），defs 亦无权限标记；Position/Hp/内部状态一律广播给视野内所有人 |
| B3 AOI 视野事件 | 无进入/离开事件 | 部分过时：`CalculateGridDiff` 已产出 enter/leave 列表并被 EntitySyncHandler 消费（发 EntitySnapshot / EntityLeaveViewNotification，EntitySyncHandler.cs:72-97）。但 40102 `EntityEnterViewNotify` 协议定义了**从未发送**（仅生成代码中存在）；且无脚本级 OnEnterView/OnLeaveView 回调 |
| B4 TCP 超时踢线 | 无 | **属实**。Gateway 全仓库无 `LastActivityTime` 超时扫描（grep 无匹配）；`LastActivityTime` 字段只写不读 |
| B5 EntityCall 超时/回调 | 无 | 属实。`EntityCall.Call` 是 fire-and-forget（EntityCall.cs:47-63），无 callId、无超时表、无回执关联 |
| B6 时间同步协议 | 无 | 属实。帧同步无客户端-服务端时钟对齐协议 |
| B7 配置模板/校验/热重载 | 无 | 基本属实。`ConfigHelper` 键值读取，`reloadOnChange: true` 已开但无校验/模板；每次 `GetConfig<T>` 重新查配置节（ConfigHelper.cs:29-32，无缓存） |
| B8 防重放 | 无时间戳窗口 | **过时**。Center 对节点注册/状态上报已做 120s 时间窗 + HMAC-SHA256 + FixedTimeEquals 常量时间比较（Center/Handlers/MessageRouter.cs:560-629）。缺口缩小为：客户端会话侧（Token/SessionId 路径）无防重放 |

### C 组（未实现）——逐条修正

| # | 原声明 | 核查结果 |
|---|---|---|
| C1 machine 进程发现/看护 | 未实现 | 属实（Publish 仅有 StartServers/KillServers/CopyDllsToServers.bat 手工脚本，无自动拉起） |
| C2 实体迁移 | 未实现 | 属实 |
| C3 负载均衡 | "无分配算法消费" | **过时**。`NodeManager.GetBestBattleNode()`（NodeManager.cs:157-169）已在 `MatchHandler.cs:77,163` 消费，按 `CurrentLoad` 升序选 Battle 节点。缺口降级为：算法粗糙（无平滑加权/最小连接/过期负载惩罚），Game 类节点无分流 |
| C4 断线重连 | 未实现 | 属实。断开即持久化+解绑，无重连令牌/会话挂起 |
| C5 管理台 | 未实现 | 属实（Center 有健康接口基础） |
| C6 Profile | 未实现 | 属实。TickEngine 无耗时统计；框架无慢消息告警 |
| C7 属性回调链 | 未实现 | 属实。Quest.csx 靠 5 tick 轮询全局数据（Quest.csx:30-40），无属性级事件 |
| C8 客户端 SDK | 定位外 | 属实 |

---

## 三、代码级新发现的差距与缺陷（按严重度）

### 🔴 P0 —— 正确性/资源泄漏

1. **"单线程模型"未被强制执行 → 实体属性数据竞争**
   - `TcpServer.HandleClientAsync` 为每个连接起独立任务，`OnDataReceived?.Invoke(...)` 不 await 异步处理器（TcpServer.cs:102）→ 同一会话的多个包、以及不同会话的包在 ThreadPool 上**并发**执行。
   - Battle 处理器直接 `entity.Set(...)` 写 `Entity.values`（EntitySyncHandler.cs:60-61），该字典**无锁**（Entity.cs:14，只有 `dirty` 集合加锁）。
   - 结论：`Dictionary<string, object?>` 并发写可能损坏内部结构；设计注释声称"单线程串行"（Entity.cs:9）与实际不符。**这是最需要优先修复的一项**。
   - 修复方向：a) 消息入队 + tick 线程消费（对标 KBE mailbox，推荐）；或 b) 实体级锁 + 明确多线程语义。

2. **EntityBackupService 管理器注册泄漏**
   - `BattleServerApp.OnTick` 中 `backupService.AddManager(scene.EntityManager)` 写在**每 tick、每场景**的循环内（BattleServerApp.cs:125-129），而 `AddManager` 只是 `managers.Add`（EntityBackupService.cs:43-47）。
   - 结果：`managers` 列表每个 tick 增长场景数个条目（1 个场景 ≈ 172 万条/天），且同一 EntityManager 重复出现 → 同一实体每轮被重复序列化、游标轮转失真。
   - 修复：在启动时注册一次；`AddManager` 增加去重（如 `HashSet` 或按实例判重）。

3. **加入房间触发全量持久化扫描**
   - 见 B1：RoomHandler.cs:76-77 每次 join 扫全部存档。玩家量大时加入路径直接卡死。
   - 修复：改用 `persistService.LoadEntityById("Player", clientSessionId)`（接口已存在，EntityPersistenceService.cs:65-81）。

4. **同一会话消息乱序/并发处理**
   - 与第 1 条同源：帧同步输入 `EnqueueInput` 与位置同步、离开消息可能乱序到达 tick 线程与场景；离开时若位置同步仍在途，可能写已移除的实体。

### 🟠 P1 —— 性能（热路径）

5. **同步阻塞发送 + 每包 3~4 次字节拷贝**
   - `TcpSession.Send` 直接 `stream.Write`（TcpSession.cs:39）——**同步写阻塞调用线程**（可能是 tick 线程/收包线程）；无发送队列、无背压、无 Nagle 关闭。对端慢速时主循环被 IO 卡住（KBE 为非阻塞 + 发送缓冲）。
   - 典型路径拷贝链（Gateway 转发，GatewayServerApp.cs:179-201）：`data.Slice(4).ToArray()` → `AttachClientSessionId`（新数组）→ `BuildPacket`（池化）→ `ToArray()`（新数组）→ `Send`；Battle 广播同理（EntitySyncHandler.cs:233-245）。ArrayPool 被 `ToArray` 抵消，池形同虚设。
   - 修复：ISession 增加 `Send(ReadOnlyMemory<byte>)` 直接发送池化缓冲（避免 ToArray），发送侧做写队列 + 异步冲刷。

6. **热路径 Info/Debug 级日志（每包 2~5 条）**
   - Battle 每收到一条消息打 2 条 Info（BattleServerApp.cs:238、260-262），Center 消息 3 条（:373、380-382）；Gateway 每包 1 条 Info（GatewayServerApp.cs:177）。
   - `Log.Info`/`Log.Debug` **无条件**执行 `string.Format` 并触发 `LogSink` 事件（Framework.Core/Log.cs:45-79），即使 Serilog 级别过滤掉也白花 CPU；调用侧 `$"..."` 插值更是在入参阶段就完成。
   - 修复：热路径降级为 Verbose 且加 `IsEnabled` 守卫；消息级日志改为采样/聚合（如每 1000 条一条）。

7. **双日志体系互相覆盖**
   - `Shared.Log`（Shared/Log.cs，静态构造即配置）与 `Framework.Core.Log`（Framework.Core/Log.cs）各自 `Serilog.Log.CloseAndFlush(); Serilog.Log.Logger = ...` 操作**同一个全局 Logger**，谁后配置谁生效，文件路径还不同（logs/log.txt vs logs/framework.log）。框架层代码用 Core.Log，业务层用 Shared.Log，输出不稳定。
   - 修复：统一为单一门面（可保留两个类名做转发），由每个进程启动时配置一次。

8. **Entity 属性装箱分配**
   - `Dictionary<string, object?>` 存值：位置同步每 tick 每玩家产生一次 `Float3` 装箱（EntitySyncHandler.cs:60 → Entity.cs:71），`Get<T>` 泛型运行时类型判断；`PropertyCodec.WriteProperty` 每次序列化都 `Encoding.UTF8.GetBytes(prop.Name)`（PropertyCodec.cs:198，属性名 UTF8 应缓存在 EntityProperty 上）。
   - 修复：属性名 UTF8 预缓存；常用热属性（Position/Rotation）可做类型化字段快路径；列表用 `ArrayPool` 或复用缓冲。

9. **ScriptHost.TickAll 为 O(脚本数 × 实体数)**
   - 每 tick 对每个脚本遍历**全部实体**再按 TypeName 过滤（ScriptHost.cs:83-97）。脚本 10 个、实体 1 万时 = 10 万次比较/ tick。
   - 修复：EntityManager 增加按类型索引（`Dictionary<string, ConcurrentDictionary<long, Entity>>`），按类型直达。

10. **备份序列化在主循环执行**
    - `EntityBackupService.Tick` 每 tick 先 `AddRange` 全量实体列表（EntityBackupService.cs:56-60，每 tick 分配全量 List），再把当批实体**同步序列化**（:87），只有写文件异步。实体量大时主循环抖动。
    - 修复：序列化也移入 OrderedTaskQueue；游标按 manager 分别维护，避免全量列表拷贝。

11. **MessageDispatcher 每次查表加锁**
    - `TryDispatch` 每次消息 `lock(gate)`（MessageDispatcher.cs:135-138）。注册表建好后是只读的，可用 `ConcurrentDictionary` 无锁读或不可变快照。

### 🟡 P2 —— 设计/正确性细节

12. **帧同步：全局单一帧号 + 无空帧**
    - `serverFrame` 是所有场景共享的全局计数器（FrameSyncManager.cs:29,87）：多场景帧号互相跳跃、无输入时不广播（:82-85）——客户端无法对齐服务端节奏（KBE cell 按固定 hertz 每帧推进并广播帧号/心跳帧）。
    - 另：`input.InputId = (int)entry.sessionId`（:78）long→int 截断，会话 ID 超过 int.Max 或负数时错乱。
    - 修复：每场景独立帧计数器；无输入时也发空帧（帧心跳）；InputId 用协议字段显式携带。

13. **EntitySyncHandler 死代码**
    - 单参 `OnPlayerLeave(long)`（EntitySyncHandler.cs:30-41）从未被调用（实际用的是双参重载 :192），且它不通知周边玩家——留作陷阱。

14. **巨型单体类**
    - Center/Handlers/MatchHandler.cs 54KB、Gateway/GatewayServerApp.cs 45KB、Login/Handlers/LoginHandler.cs 41KB、DB/Handlers/DbQueryHandler.cs 71KB。与 MessageDispatcher 迁移方向（强类型、配置化）不一致，建议按业务模块拆分。

15. **SceneManager 玩家-场景关系 O(N) 扫描**
    - `GetPlayerCount` / `GetPlayerSessionIds` / `UnbindPlayersInScene` 全表扫描（SceneManager.cs:101-150）。应在场景对象内维护玩家集合（反索引）。

16. **OrderedTaskQueue 每任务 Task.Run**
    - 每个任务新建 `Task.Run` + 链式 `prev` 引用（OrderedTaskQueue.cs:117-135）：高吞吐下线程池压力大，长队列持有整条任务链引用。可用 Channel/专用 worker 池替代。

---

## 四、脚本层核查（GameLogic/scripts）

### 4.1 关键事实：生产运行中 5 个玩法脚本全部不生效 ⚠️

- `ScriptHost` 仅在 Battle 服务器被实例化（BattleServerApp.cs:117），且按脚本返回的 `EntityType` 注册（"Avatar"/"Npc"/"Quest"/"Skill"/"Item"）。
- 生产环境唯一实体定义是 **"Player"**（PlayerEntityDef.cs:11），代码中没有任何地方创建 Avatar/Npc/Quest/Skill/Item 实体（grep 全仓库：这 5 个类型只出现在 `Tests/ScriptHostVerify/Program.cs` 的测试里）。
- `ScriptHost.TickAll` 按 `entity.TypeName == script.EntityType` 过滤（ScriptHost.cs:87）——"Player" 永远不等于这 5 个类型 → **OnTick/OnMessage 从不触发**。
- 结论：`KBE-Gap-Analysis.md` 中"✓ 游戏逻辑层对齐（Avatar/Npc/Quest/Skill/Item 玩法脚本）"的声明**不成立**——这些脚本目前只是测试样例。真正跑起来需要：a) Battle 创建对应类型实体（Npc 出生/怪物、Quest 实例、技能/物品实体），或 b) 把脚本绑定机制改为"脚本 ↔ EntityDef 类型"注册（Player 也可绑定多个脚本/组件）。

### 4.2 每个脚本的具体问题与优化

| 脚本 | 问题 | 优化建议 |
|---|---|---|
| Avatar.csx | 每 tick 两次 `Get<int>` + 无 tick 时也空转；`Console.WriteLine` 直出；回血靠 `tickCount % 20` 计数 | 回血改为 `TickEngine.AddTimer(1000, ..., repeat:true)` 周期定时器（框架已有该能力，TickEngine.cs:76），tick 内只查状态；日志改用框架注入的 Logger；私有 `tickCount` 属脚本状态，热更新会重置——**一切可变状态放实体属性** |
| Npc.csx | `new Random(42)` 固定种子 → 所有 NPC 出生坐标完全一样；`isDead` 私有字段热更后复活；死亡实体仍每 tick 被遍历 | 每实体种子（用 EntityId 派生）；死亡标记用实体属性（如 Hp==0）+ 框架侧销毁（调用 NotifyDestroy 释放 AOI）；示例已正确使用 `Position` 同步属性 |
| Quest.csx | 轮询全局数据（每 5 tick）——C7 的典型反例；用 Hp/MaxHp/Score 三个战斗属性存任务状态（语义污染，且这些属性 `SyncToClient` 会广播任务内部状态） | 任务状态用专用属性 + `SyncToClient:false`；Npc 死亡时框架触发 `OnEntityKilled` 事件，Quest 事件驱动完成（见"五、设计建议"第 1 条） |
| Skill.csx | 全局计数"读-改-写"（SkillTotalDamage）非原子（当前单线程 tick 内侥幸安全）；升级判定每 5 tick 轮询 `Casts` | 升级判定移到 CastSkill 消息内即时完成（事件驱动，无需轮询）；全局统计走框架原子计数器 |
| Item.csx | 同上轮询/全局读改写；`AutoDropTicks` 每实体独立计时在 20Hz 下对齐差一帧 | 掉落改为定时器；Count 上限校验缺失（int 溢出攻击面）——所有 `Set` 前做数值边界校验 |

### 4.3 脚本宿主级改进

- **注入 Logger**：脚本目前只有 `Console.WriteLine`（README.md:6 还把它当约定），生产日志无法聚合到 Logger 进程。ScriptGlobals 增加 `Log` 访问器。
- **状态热迁移**：热更新替换脚本实例时私有字段全丢（ScriptHost.cs:215 `scripts[typeName] = instance`）。约定改为"状态只存实体属性"，或提供 `OnReload(oldScript)` 迁移钩子。
- **Tick 分发优化**：见三-9，按类型索引。
- **脚本安全沙箱**：csx 编译产物拥有框架全部引用，脚本异常虽被隔离（ScriptHost.cs:88-96），但**没有代码级权限边界**（可访问文件/网络）。对标 KBE 也是全权限 Python，此项可接受，但应在文档中明确信任边界。

---

## 五、更好的设计建议（对标 KBE 的结构性改进）

按投入产出排序：

1. **P0｜强制执行单线程语义（最重要）**
   对 KBE：cellapp 的 mailbox——外部消息进队列，tick 线程单点消费。
   对本项目：`ISessionContext` 层把消息入 `ConcurrentQueue`，TickEngine 每帧出队分发到实体。收益：消灭三-1/三-4 的数据竞争与乱序，Entity 可去掉 `dirty` 锁，EntityManager 可用普通 Dictionary，性能与正确性双赢。

2. **P0｜修复三-2/三-3 两处确定的资源/性能缺陷**（备份注册、join 全量扫盘）——改动各约 5 行。

3. **P1｜传输层发送队列**
   对标 KBE 发送缓冲：每会话 `Channel<byte[]>` + 单写线程异步冲刷 + 背压丢弃策略；Nagle 关闭；`Send(ReadOnlyMemory<byte>)` 直接吃池化缓冲。位置同步 20Hz 广播是最大流量来源，拷贝减半收益明显。

4. **P1｜属性同步权限分级（原 B2）**
   `EntityPropertyType` 旁增加 `SyncScope { AllClients, OwnClient, CellPublic, CellPrivate }`（对标 KBE Witness 四级），defs/EntityDef 声明，广播时按目标过滤。隐私属性（Quest 内部状态、冷却）立刻不泄密，也是断线重连恢复的前提。

5. **P1｜脚本事件总线替代轮询（原 C7）**
   Entity 增加属性变更事件（`Set` 后回调已注册脚本的 `OnPropertyChanged(entity, name, old, new)`）+ 全局数据变更通知（`SetGlobal` 触发订阅者）。Quest/Skill 样例直接改造为事件驱动，删掉所有轮询。

6. **P2｜帧同步按场景推进 + 空帧心跳（原 B6）**
   每场景 `FrameCounter`；每 tick 无论有无输入都广播帧（无输入时空帧，携带服务端帧号与时间戳），客户端可做延迟补偿与确定性回放。

7. **P2｜实体在线迁移（原 C2）路线**
   先做静态分片（Battle 按场景 ID 哈希路由到指定 Battle 节点，Center 路由表下发 Gateway），再演进为实体迁移（冻结-序列化-搬迁-恢复，EntityPersistenceService 已有序列化基础）。

8. **P2｜断线重连（原 C4）**
   Gateway 断线时生成 `ReconnectToken`（签名+过期），Battle 实体"挂起"不销毁（保留 AOI 席位 30s），重连后凭令牌恢复会话绑定。依赖第 4 条的同步分级（挂起期间只收 OwnClient）。

9. **P3｜运维三件套（原 C5/C6/C1 轻量版）**
   - Supervisor：Center 已有注册表+心跳，加"注册过但断开"的节点自动拉起（Publish 已有 exe 布局）；
   - TickEngine 内置耗时统计（tick 均线/最大/慢 tick 告警，对标 KBE perf）；
   - 管理台：Center 暴露节点快照 HTTP（已有 CenterController 雏形）。

10. **P3｜工程治理**
    - 统一日志门面（修三-7）；
    - 巨型类拆分（MatchHandler/LoginHandler/DbQueryHandler/GatewayServerApp），全部迁移 MessageDispatcher 风格；
    - 补测试：Battle 集成压测（Bots 目前仅协议级）、并发注入测试（验证 P0 修复）、热更新状态迁移测试。

---

## 六、建议迭代顺序

```
迭代 1（本周，约 1~2 人日）✅ 已完成（见文末"修订记录"）
  □ 修复 BackupService 注册泄漏（三-2）         ✅
  □ 修复 join 全量扫盘 → LoadEntityById（三-3） ✅
  □ 热路径日志降级 + 加开关（三-6）             ✅
  □ 单线程语义：消息入队 + tick 消费（五-1，工作量最大，建议单独迭代）

迭代 2（约 3~5 人日）
  □ 脚本层落地：Player 实体绑定脚本 / 创建 Npc 等实体，让 5 个脚本在生产生效（四-1）
  □ EntityDef SyncScope 分级广播（五-4）
  □ 脚本事件总线替代轮询（五-5）

迭代 3（约 5~8 人日）
  □ 传输发送队列 + 零拷贝发送（五-3）
  □ 帧同步按场景推进 + 空帧（五-6）
  □ EntityManager 类型索引 + TickAll 优化（三-9）

迭代 4（P2/P3）
  □ 断线重连令牌（五-8）  □ 实体迁移（五-7）  □ Supervisor/Profile/管理台（五-9）
```

---

## 修订记录

### 2025 迭代 1（已落地）

- **三-2 备份注册泄漏**：`EntityBackupService.AddManager` 改为幂等（锁 + Contains 去重），
  每 tick 每场景重复注册不再累积 `managers` 列表，也不再重复备份同一实体（EntityBackupService.cs:42-56）。
- **三-3 join 全量扫盘**：新增 `BattleServerApp.LoadPersistedPlayer(clientSessionId)`（单条 `LoadEntityById`，O(1) 文件访问），
  `RoomHandler.HandleJoinRequestAsync` 改用之；`RestorePersistedPlayers` 保留为启动/运维用全量恢复接口（RoomHandler.cs:73-87、BattleServerApp.cs:41-77）。
- **三-6 热路径日志**：
  - `Framework.Core.Log` 与 `Shared.Log` 全部方法增加级别守卫（级别未启用时零成本返回，不再无条件 string.Format / 触发 LogSink），
    并新增 `IsDebugEnabled` / `IsVerboseEnabled` / `IsInfoEnabled` 供调用方守卫高开销构造（如 Hex 预览）；
  - `Configure` 新增 `minimumLevel` 参数，6 个服务器入口读取 `appsettings.json` 的 `Logging:MinimumLevel`（默认 **Information**，
    原默认 Debug 会写全量每包日志到文件）；
  - 全部服务器每包"收到/开始/完成"日志由 Info/插值 降级为 **Debug 模板形式**（Battle 9 处、Gateway 17 处、Center 3 处、Login 10 处、DB 1 处、Game 3 处），
    其中 Game 的 Hex/UTF8 载荷预览仅在 `IsDebugEnabled` 时构造。
  - 恢复调试：在各 `Publish/*/appsettings.json` 增加 `"Logging": { "MinimumLevel": "Debug" }` 即可回到全量包日志。

### 迭代 2（已落地）——脚本层生产生效 + 属性同步分级 + 事件总线

- **四-1 脚本层落地（最大修正）**：
  - 新增 `Battle/Entities/GameplayEntityDefs.cs`：Npc/Quest/Skill/Item 实体定义；
  - `BattleServerApp.SpawnSceneGameplayEntities`（场景创建时生成 3 只 Npc + 1 个 Quest）与
    `SpawnPlayerGameplayEntities`（玩家加入时生成 Skill/Item 并绑定属主），经 `SceneManager.SceneCreated` 事件挂载
    （SceneManager.cs:30-50、BattleServerApp.cs:90-135）；
  - `Avatar.csx` 绑定类型由 "Avatar" 改为 **"Player"**（玩家实体即 Avatar，加入场景即生效）；
  - 协议新增 `ScriptAction(40006)`（EntityId + Method + Args[int32]），客户端可调用脚本 OnMessage
    （TakeDamage/CastSkill/Pickup/UseItem 等），MessageRouter 注册分发（MessageRouter.cs:270-280）；
  - **顺带修复 ScriptHost 错误簿记键不一致 bug**：失败按文件名登记、成功却按类型名清除，
    文件名≠类型名（Avatar.csx → Player）时旧错误永不消除（ScriptHost.cs 加载成功路径现按文件名+类型名双键清除）。
- **五-4 属性同步分级（原 B2，对标 KBE Witness 四级）**：
  - `EntitySyncScope { AllClients, OwnClient, CellPublic, CellPrivate }`（EntityDef.cs:20-34）；
  - `EntityProperty.SyncScope` + `EntityDef.Add` 重载；`Entity.OwnerClientId` 属主标记（Entity.cs:28-38）；
  - 演示用法：Player.Equipment / Skill.CooldownRemaining / Item.Count = OWN_CLIENT（仅属主可见），Quest 全属性 CELL_PRIVATE（不广播）；
  - 广播按权限分组：`EntitySyncHandler.BroadcastDirty` 将脏属性按 AllClients→视野内玩家、OwnClient→属主 分组下发（EntitySyncHandler.cs:120-160）。
- **五-5 脚本事件总线（原 C7）**：
  - `IEntityScript` 新增 `OnPropertyChanged(entity, name, old, new)`（Entity.Set 触发）与
    `OnGlobalChanged(entity, key, value)`（ScriptHost.SetGlobal 触发），默认空实现（IEntityScript.cs:38-50）；
  - `ScriptHost.RegisterEntityManager` + 订阅/退订实体属性事件（ScriptHost.cs:120-175）；
  - `Quest.csx` 改为事件驱动完成（删除轮询）、`Skill.csx` 升级改为释放时立即判定（删除轮询）、`Npc.csx` 随机种子按实体 ID 派生；
  - `scripts/README.md` 更新为新约定（事件回调、同步权限、ScriptAction 调用方式）。
- **配套的 Witness 每 tick 广播**：`EntitySyncHandler.TickWitness()` 由 TickEngine 驱动，
  脚本/AI 驱动的属性变化（NPC 巡逻、回血、冷却、掉落）无需客户端上报即增量广播；
  广播目标限定为玩家会话（NPC/玩法实体不参与收包），AOI 网格在实体移动时同步更新（EntitySyncHandler.cs:76-110、226-268）。
- 验证：`dotnet build` 0 错误；ProtocolVerify / ScriptHostVerify / LoggerVerify 三套件全部通过
  （ScriptHostVerify 覆盖：Player 绑定、事件驱动 Quest、Skill 立即升级、热更新、错误隔离/恢复）。

### 迭代 3（已落地）——单线程消息队列 + 帧同步按场景推进 + 类型索引

- **三-1/五-1 单线程语义落地（P0 最大项）**：
  - Battle 收包管线改为 mailbox 模型：认证与路由元数据解析留在收包线程，业务消息一律入队
    （`ConcurrentQueue<InboundMessage>`，上限 16384 防无界增长），TickEngine 主循环每帧开头
    `DrainInboundMessages()` 串行消费（BattleServerApp.cs:70-160、416-475）；
  - Center 下发消息与客户端消息共用同一队列（同一 tick 线程），实体/场景状态从此只在主循环被读写，
    `Entity.values` 无锁字典的数据竞争（三-1）与同会话消息乱序（三-4）一并消除；
  - 全部 Battle 处理器均同步完成（Task.FromResult/CompletedTask），tick 线程内 `GetResult()` 串行执行无死锁。
- **五-6/三-12 帧同步按场景推进 + 空帧**：
  - 帧号改为**每场景独立计数器**（`sceneFrames`，FrameSyncManager.cs:44-50），多场景不再互相跳跃；
  - 有玩家的场景每 tick **必然广播权威帧**（无输入时广播空帧）——确定性帧同步，客户端可对齐服务端帧节奏；
  - `PlayerInput.InputId` 由 int32 改为 **int64**（Battle.def），会话 ID 不再截断（原 long→int 截断缺陷修复）。
- **三-9 EntityManager 类型索引**：
  - `EntityManager` 维护 `TypeName -> (EntityId -> Entity)` 二级索引（EntityManager.cs:15-19），
    `GetAllEntitiesByType` O(该类型实体数)；
  - `ScriptHost.TickAll` 与 `NotifyGlobalChanged` 按类型直达（ScriptHost.cs:100-116、186-205），
    消灭原 O(脚本数×实体数) 的全量遍历。
- 验证：`dotnet build` 0 错误；三套件全部通过。传输层发送队列+零拷贝（五-3）涉及全部进程共享的
  Network 层，建议单独一轮 + 集成压测验证后再动。

### 迭代 4（已落地）——传输发送队列 + 零拷贝 + 网络集成压测

- **五-3/三-5 传输发送队列（TcpSession 重写）**：
  - 写侧改为**非阻塞发送队列**：`Channel<QueuedPacket>` + 单写者异步冲刷任务（TcpSession.cs:30-60），
    调用线程（tick/收包线程）不再被慢对端阻塞；每包原子入队，多线程并发 Send 不再出现字节交错损坏帧；
  - `NoDelay = true`：禁用 Nagle，20Hz 小包（位置/帧同步）延迟显著降低；
  - **零拷贝**：新增 `SendFromPool(byte[] buffer, int count)`（缓冲所有权移交会话，写入后自动归还 ArrayPool）
    与 `Network.PacketSender.Send` 助手（TcpSession/TcpClientWrapper 直传，其他会话回退拷贝+归还）；
    Battle 全部发送点（EntitySyncHandler / FrameSyncManager / MessageRouter / Center 同步与注册）与
    Gateway 四处回包转发均改为零拷贝，去掉每包一次 ToArray 堆拷贝；
  - **背压**：队列上限 8192 包，超限丢包 + 节流告警 + 关闭连接（慢客户端保护，对端重连恢复）。
- **Witness 属主可见性修复**：`GetBroadcastTargets` 现在**始终包含属主**（含实体自身）——
  玩家受击掉血、冷却、背包等变更必须回发属主客户端（对标 KBE：owner 永远在自身 witness 内）。
- **新增 Tests/NetworkVerify 集成压测（四套件之一）**：
  - 传输层：TcpServer 回显 + 4 客户端 × 200 包并发发送，验证写队列下单包原子性与按客户端顺序（200/200 全过）；
  - Battle 进程内全协议链路：认证握手 → 加入房间 → 全量快照识别 NPC → NPC 巡逻 Witness 自动广播 →
    EntitySync 自身增量回发 → ScriptAction 玩家掉血（Hp=90）/ 击杀 NPC（Hp=0）→ Quest 事件驱动完成，
    端到端验证脚本层/事件总线/消息队列/零拷贝发送在生产代码路径上真实生效。
- 验证：`dotnet build` 0 错误；ProtocolVerify / ScriptHostVerify / LoggerVerify / NetworkVerify 四套件全部通过。

### 迭代 5（已落地）——断线重连 + TCP 超时踢线 + 性能 Profile

- **四-3/C4 断线重连（挂起/恢复）**：
  - 协议新增 `PlayerSessionResume(10014)`（Login.def，内部消息）；
  - Battle：`HandleDisconnect` 改为**实体挂起**（保留场景/AOI 席位，其他玩家看到冻结化身），
    宽限期 `ReconnectGraceSeconds`（默认 30s，配置 ≤0 关闭）内收到 PlayerSessionResume 即恢复在线，
    超时由 TickEngine 定时器完整离场（BattleServerApp.cs SuspendPlayerOnDisconnect/ResumePlayer/LeaveScene）；
  - Gateway：断线时对已绑定用户记录挂起表（宽限同步），重新登录成功后 `ResumeSession` 把新会话身份绑定
    迁移到旧会话 ID（后端按旧 ID 续接实体），并发送 PlayerSessionResume 通知 Battle（GatewaySessionManager.cs:262-296）；
  - NetworkVerify 新增端到端验证：断线 → 挂起 → 重连恢复（新连接收到自身增量）→ 二次断线 → 宽限超时离场
    （新玩家快照不再包含旧实体）。
- **B4 TCP 空闲超时踢线**：
  - `TcpServer` 收包时刷新 `LastActivityTime`；Gateway 每 30s 清扫：TCP 客户端会话空闲超过
    `GatewayTcpTimeoutSeconds`（默认 300s）即断开（对标 KBE 心跳超时；UDP/KCP 已有各自 5 分钟超时），
    同时清理过期的重连挂起记录。
- **C6 性能 Profile**：
  - `TickEngine` 增加 tick 耗时统计（last/avg/max）+ 慢 tick 告警（阈值 `SlowTickThresholdMs` 默认 200ms，5s 节流）；
  - `MessageDispatcher` 增加慢消息处理告警（`SlowHandlerThresholdMs` 默认 200ms）；
  - Battle 每 5 秒输出 tick 统计日志（含入站队列深度）。
- 验证：`dotnet build` 0 错误；四套件全部通过。

### 迭代 6（已落地）——进程看护 Supervisor + 管理台

- **C1 进程看护（Supervisor）**：
  - 新增 `Tools/Supervisor`：JSON 配置驱动（进程名/路径/参数/工作目录/重启延迟），
    异常退出（code≠0）自动重启（指数退避，上限 30s），正常退出（code=0）不重启；
  - 输出带机器可读标记（START/RESTART/EXIT_OK/SUMMARY）供验证断言；`--test-duration` 测试模式；
    Ctrl+C 优雅停机（先 CloseMainWindow 再 Kill 兜底）；子进程 stdout/stderr 落盘；
  - `supervisor.sample.json` 覆盖 Publish 布局全进程（Redis/DB/Center/Login/Game/Battle/Gateway）。
- **C5 管理台（guiconsole Web 简化版）**：
  - Center HTTP 新增 `/api/center/rooms`（房间快照，MatchHandler.GetRoomsSnapshot + CenterServerApp.Match）；
  - 根路径 `/` 提供无依赖单页仪表盘：节点表（类型/负载/心跳/在线）、房间表、健康汇总，
    JS 轮询 health/nodes/summary/rooms 每 5 秒自动刷新（CenterHttpServer.cs:30-32、DashboardHtml）。
- **新增 Tests/SupervisorVerify**：崩溃进程（退出码 1）被反复重启（指数退避验证）、
  正常退出进程不重启、汇总输出齐全。
- 验证：`dotnet build` 0 错误；五套件（Protocol/ScriptHost/Logger/Network/Supervisor）全部通过；
  Center 冒烟：/api/center/health|nodes|rooms|summary 与首页 HTML 均 200 正常。

### 迭代 7（已落地）——静态分片：Gateway 多 Battle 节点 + 按玩家绑定路由（C2 第一阶段）

- **C2 静态分片（对标 KBE cellappmgr 调度）**：
  - Gateway 支持多 Battle 节点：配置 `BattleNodes=["host:port",...]`（缺省回退单节点 BattleHost:BattlePort），
    每节点独立连接 + 缓冲发送器（GatewayServerApp.cs battleNodes/battleNodeSenders）；
  - **按玩家绑定路由**：匹配成功回包（CenterMatchResult）携带 BattleNodeId → Gateway 嗅探并记录
    `clientSessionId → nodeId`（clientBattleNodeBindings），后续该玩家的战斗消息（40000-49999）按绑定
    路由到对应节点；无绑定走默认节点；绑定节点不可用时回退默认并清除绑定；
  - 断线通知广播到全部 Battle 节点；重连恢复通知（PlayerSessionResume）按绑定路由到玩家所在节点；
    玩家断开时清除绑定；
  - **顺带修复真实竞态 bug**：Login/Game/Center 的 BufferedBackendSender 原在连接发起后才订阅
    OnConnected，localhost 快速连上时 isConnected 永远为 false → 消息被静默缓冲永不冲刷；
    现改为在 ConnectAsync 之前创建并订阅发送器（connect-before-subscribe 竞态）。
- **NetworkVerify 新增分片端到端验证**：进程内启动 Gateway + 两个伪 Battle 节点 + 伪 Center：
  无绑定消息路由到默认节点 A → 匹配回包学习绑定（节点 B）→ 绑定后消息路由到节点 B（全链路标记回显）。
- 验证：`dotnet build` 0 错误；五套件全部通过。

### 迭代 8（已落地）——P1/P2 性能与工程质量批量清理（三-7/三-8/三-10/三-11/三-15/三-16）

- **三-7 统一日志门面（修复双日志互相覆盖）**：
  - `Framework.Core.Log` 成为**唯一配置源**（持有 Configure / 全局 Serilog Logger / LogSink 聚合事件），
    新增 `Fatal`、`Warning` 别名、`CloseAndFlush`；
  - `Shared.Log` 重写为**纯转发门面**：删除静态构造函数（此前任何代码首次触碰 Shared.Log 就会把全局
    Logger 重置回 logs/log.txt 默认路径，静默覆盖进程启动配置，正是"谁后配置谁生效"的根因），
    全部方法转发到 Framework.Core.Log，进程仍只需在 Program 启动时调用一次 Configure；
  - **顺带修复日志聚合缺口**：此前业务层经 Shared.Log 打的日志不触发 LogSink（只有 Framework.Core.Log
    触发），转发后业务日志同样上报 Logger 聚合进程。
- **三-11 MessageDispatcher 免锁读**：`handlers` 由 `Dictionary + lock(gate)` 改为 `ConcurrentDictionary`，
  注册表启动期填满后只读，每次分发 `TryGetValue` 无竞争锁（MessageDispatcher.cs:35-40、140-146）。
- **三-8 属性名 UTF8 预缓存**：`EntityProperty.Utf8Name`（懒加载缓存），`PropertyCodec.WriteValueRaw`
  复用缓存字节，消灭每属性每包一次 `Encoding.UTF8.GetBytes(prop.Name)`（EntityDef.cs:50-56、
  PropertyCodec.cs:214-219）。
- **三-10 备份序列化移出主循环**：
  - `Entity.CopyValues()`：主循环线程浅拷贝快照（O(属性数)，List&lt;int&gt; 深拷贝防后台竞争）；
  - `EntityBackupService.Tick`：主循环只做总数计算 + O(快照) 脱离，**序列化（UTF8 编码）与落盘全部
    移入 OrderedTaskQueue 后台线程**（EntityBackupService.cs:100-139）；备份格式不变，恢复逻辑不动；
  - 全量实体列表按总数缓存（仅实体数量变化时重建），消灭每 tick O(总实体数) 的 List 分配；
  - `BattleServerApp` 的 `backupService.AddManager(scene.EntityManager)` 从每 tick 每场景循环移到
    `SceneCreated` 事件（创建时注册一次，消除每 tick 的 Contains 幂等扫描）。
- **三-15 SceneManager 玩家-场景反索引**：新增 `sceneToPlayers` 二级索引，`GetPlayerCount` /
  `GetPlayerSessionIds` / `UnbindPlayersInScene` 由 O(全体玩家) 全表扫描降为 O(该场景玩家数)
  （SceneManager.cs:25-31、Bind/Unbind 维护反索引，空集合自动清理）。
- **三-16 OrderedTaskQueue 重写（Channel + 固定 worker 池 + 按 key FIFO 队列）**：
  - 弃用"每任务 Task.Run + 链式 prev 引用"（线程池压力 + 长队列持有整条任务链）与
    **SemaphoreSlim 方案（不保证等待者 FIFO，实测同 key 乱序，已废弃）**；
  - 最终方案：每 key 锁保护的 FIFO 队列 + 固定 worker 池经 Channel 派发令牌；key 空闲→忙碌时才派发
    一个令牌，worker 一次串行清空该 key 队列（严格 FIFO），队列空交还 Running=false；
    `SweepIdle` 仅在无排队且无执行中任务时安全回收（OrderedTaskQueue.cs）。
- 验证：`dotnet build` 0 错误；五套件（Protocol/ScriptHost/Logger/Network/Supervisor）全部通过
  （ProtocolVerify 覆盖 OrderedTaskQueue 20 任务/4 key 严格保序 + 实体备份/恢复回环；NetworkVerify
  覆盖 Battle 全链路 + 断线重连 + 静态分片）。

### 迭代 9 —— 实体在线迁移 C2 第二阶段（v1 完成）

- **五-7/C2 实体在线迁移（第二阶段）**：在迭代 7 静态分片之上补齐实体在线迁移
  （冻结-序列化-搬迁-恢复，Center 协调中继，对标 KBE cellapp 实体搬迁）。
  - 协议（Protocol/defs/Center.def，内部消息 target=All/Battle）：91003 EntityMigrateRequest
    （SourceNodeId/TargetNodeId/ClientSessionId/EntityId/EntityType/SceneId/Props bytes）→
    91004 EntityMigrateResult → 91005 EntityMigrateRouted（Gateway 重绑定）→
    91006 EntityMigrateCommand（管理侧触发迁移）。
  - 流程：源 Battle 冻结会话 + 序列化全部属性（含 CELL_PRIVATE 内部状态）→ 91003 → Center 中继到
    目标 Battle → 目标恢复实体（场景绑定/AOI/脚本 OnCreate）→ 回 91004 → Center 回源 + 成功时
    91005 通知 Gateway 切换 clientSessionId → 新 Battle 节点绑定；源 Battle 收到成功结果移除本地
    实体（失败回滚解冻）。
  - 关键点：Battle 单线程约束——`RunOnTick` 排队到 tick 线程执行；迁移中会话入站消息冻结暂缓
    （migratingSessions 集合）；`PropertyCodec.SerializeAllValues` 增加 onlySyncToClient:false
    全属性序列化（迁移负载）；Center 用 NodeManager 节点表做中继回源（pendingMigrationSource 记录
    源节点）；Gateway 在 centerClient OnDataReceived 早于路由元数据提取处处理 91005。
  - v1 范围：仅迁移玩家主实体（EntityId = ClientSessionId，规避跨节点玩法 ID 碰撞）；Skill/Item/Npc
    等玩法实体暂不跨节点搬迁（孤儿项接受为 v1 限制，后续版本补玩法实体迁移与断线重连衔接）。
- 验证：`dotnet build` 0 错误；五套件全部通过（ProtocolVerify 覆盖实体迁移属性全量序列化→恢复回环
  Props=76B；NetworkVerify Part5 伪 Center 下发 91005 后 Gateway 玩家消息 A→B 重绑定）。

### 迭代 10（规划）——路线剩余项

- **三-14 巨型单体类拆分**：MatchHandler（54KB）/ LoginHandler（41KB）/ DbQueryHandler（71KB）/
  GatewayServerApp（45KB）按业务模块拆分，并全部迁移 MessageDispatcher 强类型风格。

