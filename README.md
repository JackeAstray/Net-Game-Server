# Net-Game-Server
基于 `.NET 10` 的分布式微服务游戏服务器集群框架。

高性能、可扩展的分布式游戏服务端解决方案。支持 TCP、UDP、KCP 和 WebSockets 等多种网络协议，
适用于开发从卡牌、MMO 到实时竞技类等多种类型的在线游戏应用。框架对标 KBEngine 的核心能力
（实体/属性/迁移/脚本层/认证），结合 .NET 平台的强类型与异步生态做了重新设计。

---

## 核心特性

- **分布式微服务架构**：服务按业务域拆为独立节点（Gateway / Login / Center / Game / Battle / DB），
  节点通过 TCP + 内部认证握手互联，水平扩展时只需加节点。
- **声明式协议 + 代码生成**：`Protocol/defs/*.def` 是协议唯一事实来源，构建时 Protogen 自动生成
  强类型消息类、`MessageIds` 常量、`RouterTable` 路由表（MemoryPack 二进制）。改协议只改 def。
- **强类型消息分发**：`MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式兼容（`jsonFallback`），
  业务层直接消费强类型请求对象，无二次序列化（Game/DB 全量对齐）。
- **统一网关接入**：`Gateway` 节点同时承接 TCP/UDP/KCP/WS 四种客户端连接，按生成的路由表
  配置化转发到 Login/Game/Center/Battle；统一注入会话路由元数据（`__clientSessionId`/`__userId`/`__uid`）。
- **实体/属性框架**：`EntityDef` + 脏标记增量同步（对标 KBEngine Witness），只广播变更属性；
  支持 `All` / `AOI` / `OwnClient` 三种同步作用域，CELL_PRIVATE 内部状态不参与广播。
- **跨进程实体调用（EntityCall）**：调用方通过 `EntityCall.CallAsync` 发起，自动分配 callId、
  注册到 `EntityCallHub` 超时表，调用经 Center 中继到目标节点（91001/91002），目标节点执行后
  携带同一 callId 回执，框架自动关联回执或超时（Battle tick 周期清扫超时）。
- **脚本层 Mailbox（csx 友好）**：`entity.Mailbox.Call/CallAsync(method, args, cb)` 在 csx 脚本中
  直接调实体方法：同进程零开销同步执行（Local 路径），跨节点走 EntityCall 异步回执 +
  超时清扫（Remote 路径）。`EntityManager` 注册实体时自动挂 Local Mailbox；
  `AttachMailbox` 显式挂 Remote（迁移后源节点视角）。对标 KBE entityMailboxComponent / cellMailbox。
- **实体在线迁移**：玩家主实体 + **属主玩法实体（Skill/Item）** 同包随迁
  （`EntityMigrateRequest.OwnedEntities`），目标节点原子恢复并完成属主绑定；源节点迁移成功后
  回收本地副本；离场/离房路径自动回收孤儿（无主玩法实体）。玩法实体 ID 含节点派生段，
  保证不同 Battle 节点生成的 ID 跨节点不冲突。
- **单线程 tick 引擎**：固定频率主循环串行处理入站消息、驱动帧同步与定时器，对标 KBE
  gameUpdateHertz；所有实体/场景状态只在 tick 线程读写，彻底消除"声称单线程、实际并发写"的数据竞争。
- **脚本宿主**：游戏逻辑写在 `GameLogic/scripts/*.csx`，与底层框架物理分离、可热更新
  （对标 KBE Python 脚本层）；脚本通过 `Entity.Set/Get` + 事件总线（`OnPropertyChanged`）响应。
- **Center 平滑加权负载均衡**：`GetBestBattleNode` 采用 Nginx-SWRR（权重 = 100 - CurrentLoad），
  持续偏向低负载节点；心跳过期（>30s）的节点从候选剔除（过期负载惩罚）；
  平滑权重表周期性清理防膨胀。
- **客户端会话防重放**：Gateway 入口按 `SessionGuard.IsSessionValid` 强制时间窗
  （`MaxSessionLifetime` ≤ 2h / `MaxIdleSeconds` ≤ 15min），超窗直接关连接；
  `TokenService` 嵌入 `SessionSeq` 单调序号（Verify 时严格 `seq > lastSeq` 拒绝旧 token 重放）；
  `NonceService` 提供一次性 nonce 缓存（带 TTL 周期 GC），业务层可对敏感操作附加挑战码。
- **安全加固**：无状态 HMAC-SHA256 签名 Token、内部连接 HMAC 认证握手 + 120s 时间戳窗、
  加密随机 + 计数器混合的不可预测 SessionId、登录限流。

---

## 节点拓扑

```
                 ┌──────────────┐
   TCP/UDP/KCP/WS│   Gateway    │  31300  (默认)
   客户端流量 ──▶│  (统一接入)  │──┬──▶ Login   31302
                 └──────────────┘  ├──▶ Game    31304
                                    ├──▶ Center  31306
                                    └──▶ Battle  31307~n
                 ┌──────────────┐
                 │    Center    │  31306  (全局控制平面)
                 │ 节点注册/匹配 │
                 │ 实体迁移协调 │
                 │ 实体调用中继 │
                 └──────┬───────┘
                        │
   ┌──────────────┐  ┌──┴───────┐
   │     Login    │  │  Battle  │  31307 (可多实例，平滑加权分配)
   │  31302       │  │ 场景/玩法│
   │ 账号/限流/Token│ │ 实体同步 │
   └──────┬───────┘  └──────────┘
          │
   ┌──────▼───────┐
   │      DB      │  31309
   │  31309 强类型│
   │ 持久化 + 缓存│
   └──────────────┘
```

外部客户端连接 Gateway（默认 31300），Gateway 按协议路由表把消息分发到对应后端节点；
后端节点间通过 Center 协调（实体迁移、EntityCall 中继、节点状态同步）。
所有节点启动后向 Center 注册并维持心跳（默认 10s 间隔）。

---

## 项目结构

```
NetGameServer/
├── Gateway/                网关节点：客户端接入 + 统一路由 + 会话时间窗强制
│   ├── GatewayServerApp.*  网络/会话/后端客户端拆分
│   └── Managers/           GatewaySessionManager（会话/路由/建立时间）
├── Login/                  登录节点：账号、密码、限流、Token 签发
│   └── Handlers/           LoginHandler
├── Center/                 控制平面：节点注册、匹配、实体迁移协调、EntityCall 中继
│   ├── Handlers/           NodeManager（注册/心跳/平滑加权）/ CenterDispatcher / MatchHandler
│   └── Managers/
├── Game/                   通用业务节点：背包/公会/社交/任务
│   ├── Handlers/           强类型 GameDispatcher + 业务 partial 类
│   ├── Managers/           PlayerSessionManager
│   └── Network/            ClientSessionWrapper
├── Battle/                 战斗节点：场景、AOI、帧同步、玩法实体（Skill/Item/Npc/Quest）
│   ├── BattleServerApp.*   网络/迁移/入站队列/分布式场景
│   ├── Handlers/           RoomHandler / EntitySyncHandler / BattleMainHandler / MessageRouter
│   ├── Entities/           PlayerEntityDef + GameplayEntityDefs（4 种玩法实体定义）
│   └── Scripting/          脚本加载与 OnMessage 派发
├── DB/                     数据节点：强类型 DbDispatcher + 持久化（支持 Redis/MySQL 代理）
│   ├── Handlers/           DbQueryHandler / DbDispatcher
│   └── Routing/
├── Network/                底层网络：TCP/UDP/KCP/WS 服务器与客户端，零拷贝池化发送
│   ├── Tcp/  Udp/  Kcp/  WebSockets/
│   ├── PacketSender        共享池化发送（零拷贝 vs 拷贝自动选择）
│   ├── SessionIdGenerator  不可预测会话 ID
│   └── Routing/            PacketBuilder / 路由元数据
├── Shared/                 公共层：消息 DTO、ConfigHelper、Json 工具、RouteMetadata
├── Framework/              底层框架（与游戏逻辑物理分离）
│   ├── Framework.Core      Security（Token/Nonce/SessionGuard/InternalAuthFilter）+ LeaderElection
│   ├── Framework.Protocol  MessageDispatcher + IGameMessage + Protogen 运行时
│   ├── Framework.Entity    Entity/EntityDef/EntityManager/PropertyCodec/EntityCall/EntityCallHub
│   ├── Framework.Tick      TickEngine（固定频率主循环 + 定时器）
│   └── Framework.Scripting ScriptHost（csx 加载/热更新/OnCreate/OnDestroy/OnMessage）
├── Protocol/defs/          协议声明（*.def，唯一事实来源）
├── Protogen/               def → 强类型消息代码生成器
├── GameLogic/scripts/      业务脚本（.csx，可热更新）
├── Bots/                   集成压测客户端（连真网关链路打负载）
├── Tools/Supervisor/       进程看护与统一拉起
└── Tests/                  验证套件
    ├── ProtocolVerify/     协议/MessageDispatcher/EntityCall/迁移/SWRR/防重放 全链路
    ├── NetworkVerify/      真实 Battle 节点集成（NPC 巡逻/玩家同步/并发/tick 排空）
    ├── ScriptHostVerify/   脚本宿主 + 玩法脚本验证
    ├── LoggerVerify/       日志/结构化字段
    └── SupervisorVerify/   进程看护
```

---

## 模块详解

### Gateway（统一接入）
- 同时承接 TCP/UDP/KCP/WS 四种客户端协议，底层 `Network.Tcp/Udp/Kcp/WebSockets` 统一抽象为 `ISession`。
- 客户端帧格式 `[MsgId(4)][Payload]`，Gateway 解析后按 `RouterTable` 配置化路由
  （优先 def 声明的回退到旧区间路由，兼容过渡期）。
- **会话时间窗强制**：每个客户端连接的 `SessionGuard.IsSessionValid` 在 `onDataReceived` 入口校验，
  超过 `MaxSessionLifetime`（默认 2h）直接 `Close`，防止 SessionId 长期重放。
- `GatewaySessionManager` 维护 `sessionId → ISession`、`sessionId → userId/uid/nickname`、
  以及本次新增的 `sessionId → CreatedAt`（供 SessionGuard 查询）。

### Login（认证）
- `LoginHandler.HandleLoginRequestAsync` 走账号/密码校验 + 登录限流（按账号维度，
  防止爆破），成功后签发 HMAC-SHA256 签名 Token（含 `SessionSeq=1`）返回。
- `TokenService` 嵌入 `SessionSeq` 单调序号（payload 5 字段），后续续签/重连可递增 seq 增强防重放。
- `NonceService` 提供一次性 nonce（带 TTL 周期 GC），业务可对敏感操作（重置密码、绑定手机等）附加 challenge。
- 限流与登录失败记录按账号维度本地计数（可替换为分布式实现）。

### Center（控制平面）
- **节点注册 + 心跳**：所有业务节点启动后向 Center 注册（NodeId + NodeType + Host + Port），
  每 10s 上报一次 `CurrentLoad`（Battle 节点报告场景实体数）。
- **平滑加权负载均衡（对标 Nginx SWRR）**：`GetBestBattleNode` 按权重（= 100 - load）累加，
  取最大者选中并减去总权重 → 持续偏向低负载节点；心跳过期（>30s）的节点剔除。
- **匹配服务**：`MatchHandler` 支持按 `SceneType` 过滤、`MaxPlayers` 容量检查、`CustomRules` 透传。
- **实体迁移协调**：源 Battle 发起 91003 → Center 中继到目标 Battle → 目标 91004 回 Center → Center
  91005 通知 Gateway 切换 `clientSessionId` 路由 → Center 91004 回源 → 源 Battle 移除本地实体。
- **EntityCall 中继**：91001 实体远程调用经 Center 中继到目标 Battle（按 EntityId 查找场景），
  91002 回执携带同一 CallId 回到源节点。

### Game（通用业务）
- 强类型 `GameDispatcher` 覆盖背包/公会/社交/任务等业务消息，业务层按业务域拆 partial 类
  （如 `FriendHandler` 6 拆），请求对象直传业务方法，无二次 JSON 序列化。
- `ClientSessionWrapper` 注入 `RoutedUserId/uid/nickname` 等路由元数据，业务方法按需读取。
- 与 Battle 节点通过 `clientSessionId` 协作（Battle 持主实体/场景，Game 持业务/持久化）。

### Battle（场景与战斗）
- **场景管理**：`BattleMainHandler` 接收 Center 的 `CenterCreateScene` 创建场景、挂载 AOI、生成场景级玩法实体
  （3 NPC + 1 Quest）；玩家加入时生成玩家私有玩法实体（Skill/Item，绑定 OwnerClientId）。
- **AOI 同步**：`EntitySyncHandler` 按脏标记增量广播给视野内玩家；`OwnClient` 作用域只发给属主客户端。
- **帧同步**：`FrameSyncManager` 接收客户端 `BattleFrameSync`（输入），按 tick 批量广播。
- **实体迁移 v2**：玩家迁移时 `SerializeOwnedEntitiesForMigration` 收集属主 Skill/Item 同包发送
  （`EntityMigrateRequest.OwnedEntities`），目标节点 `RestoreMigratedEntity` 原子恢复并绑定属主；
  源节点 `CompleteMigrateOut` 回收本地副本；`LeaveScene`/离房路径 `RecycleOwnedEntities` 回收孤儿。
- **玩法实体 ID**：含节点派生段（`FNV-1a(CurrentNodeId)` 取 [32,40) 位），
  保证不同 Battle 节点生成的 ID 跨节点迁移不撞 ID。
- **入站消息单线程化**：所有收包只入队（`inboundQueue`），`TickEngine` 串行消费，
  实体状态读写完全在单线程进行。

### DB（持久化）
- 强类型 `DbDispatcher` 覆盖 20+ 条 DB 请求消息（账号/好友/聊天/邮件等），`DbQueryHandler`
  业务方法接 `(ClientSessionWrapper, XxxRequest?)`，无二次序列化。
- `EntityPersistenceService` 按 EntityType 分目录的轻量文件持久化（崩溃恢复用），单条加载 O(1)
  替代全量目录扫描，玩家量大时加入路径不再线性变慢。
- 可替换为 MySQL/Redis 后端（接口与实现分离）。

### Network（底层传输）
- `TcpServer/UdpServer/KcpServer/WebSocketServer` 统一暴露 `OnDataReceived/OnConnected/OnDisconnected`。
- `PacketSender.Send(ISession, byte[], int)` 零拷贝池化发送：支持 `TcpSession` 直接使用
  `ArrayPool` 借出的缓冲区（不复制），其他会话类型自动复制后归还。
- `PacketBuilder` 构建带 4 字节长度前缀的帧。

### Framework（底层框架）
- **Framework.Core.Security**：`TokenService`（HMAC-SHA256 + SessionSeq 单调序号）/
  `NonceService`（一次性 nonce + TTL 周期 GC）/ `SessionGuard`（时间窗 + AntiReplayState 无锁 CAS）/
  `InternalAuthFilter`（节点间 HMAC + 120s 时间戳防重放）/ `LeaderElection`（主备争锁）。
- **Framework.Protocol**：`MessageDispatcher`（配置化注册 + MemoryPack/JSON 双格式）/
  `IGameMessage`（统一序列化接口）/ `ProtocolCodec`（帧解析）。
- **Framework.Entity**：`Entity`（属性 + 脏标记 + 方法注册 + **Mailbox 脚本入口**）/
  `EntityDef`（属性声明 + 同步作用域）/
  `EntityManager`（O(1) 增删查 + 按类型二级索引）/ `PropertyCodec`（全量/增量序列化）/
  `EntityCall`（callId 分配 + CallAsync 回调）/
  `EntityCallHub`（静态 pending-call 表 + 超时清扫）/
  **`EntityMailbox`**（Local/Remote 双路径，csx 脚本入口）。
- **Framework.Tick**：`TickEngine`（固定频率主循环 + 微秒级 timer）。
- **Framework.Scripting**：`ScriptHost`（csx 加载/重载/OnCreate/OnDestroy/OnMessage/OnPropertyChanged 事件总线）。

### GameLogic（业务脚本层）
- `*.csx` 文件按 EntityType 一一对应（`Player.csx` / `Npc.csx` / `Quest.csx` / `Skill.csx` / `Item.csx`）。
- 脚本通过 `OnCreate` 设置初始属性、`OnMessage` 响应客户端 ScriptAction（如 `TakeDamage` 扣血、`UseItem` 回复生命）。
- 与框架物理分离，修改 csx 后 `ScriptHost.Reload` 热更新，无需重启 Battle 节点。

### Bots（集成压测）
- 走真实 Gateway 链路（TCP/KCP/WS 任选），模拟 N 个 Bot 并发加入/战斗/离开，统计
  RTT/吞吐量/错误率，适合上线前基准与回归基线。

---

## 环境要求
- 必需：[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 可选（按业务扩展）：
  - MySQL / SQL Server / PostgreSQL（替换 DB 默认文件持久化）
  - Redis（缓存/分布式限流计数器）
  - 反向代理（Nginx / YARP）做 Gateway 集群负载均衡

## 快速开始

### 1. 克隆与编译
```bash
git clone <repo-url>
cd Net-Game-Server
dotnet build NetGameServer.slnx
```

### 2. 节点启动顺序
为了让节点间正常注册与路由，启动遵循：
1. **DB Server**（31309，数据层）
2. **Center Server**（31306，控制平面）
3. **Login Server**（31302，账号/Token 签发）
4. **Game Server**（31304）+ **Battle Server**（31307~n，业务层；Battle 可多实例）
5. **Gateway Server**（31300，统一接入，最后启动以接受外部客户端）

可直接到各节点目录执行 `dotnet run`，或通过 `Tools/Supervisor` 统一拉起与看护。

### 3. 验证
构建完成后跑五套验证套件确认全链路：
```bash
dotnet run --project Tests/ProtocolVerify  -c Release   # 协议/分发/EntityCall/迁移/SWRR/防重放
dotnet run --project Tests/NetworkVerify   -c Release   # 真实 Battle 节点集成
dotnet run --project Tests/ScriptHostVerify -c Release   # 脚本宿主 + 玩法脚本
dotnet run --project Tests/LoggerVerify    -c Release   # 日志
dotnet run --project Tests/SupervisorVerify -c Release  # 进程看护
```

---

## 架构说明

### 通信协议
- **客户端 ↔ Gateway**：`[MsgId(4)][Payload]`，MsgId 由 `Protocol/defs/*.def` 统一编号。
- **Gateway ↔ 后端**：在 Payload 前注入路由元数据（`__clientSessionId`/`__userId`/`__uid`/`__broadcast`），
  后端通过 `RouteMetadata.TryExtract*` 解析后处理。
- **后端 ↔ 后端**：内部连接走 HMAC 认证握手（`InternalAuth`，90999，120s 时间戳窗），
  之后按 `MessageIds` 直接发 `EntityRemoteCall`(91001) / `EntityRemoteCallResult`(91002) /
  `EntityMigrateRequest`(91003) / `EntityMigrateResult`(91004) 等。

### 一致性与并发模型
- Battle 节点单线程 tick 引擎串行处理入站消息与定时器，实体/场景状态只在 tick 线程被读写。
- Gateway/Center 等控制平面节点用 `ConcurrentDictionary` + 无锁原语处理高并发连接。
- 跨节点调用（EntityCall/迁移）用 callId + 超时表保证最终一致；超时由 tick 周期清扫并回调。

### 文档导航
各模块的职责/关键文件/注意事项/排错见：
- [Gateway.md](NetGameServer/Docs/Gateway.md) / [Login.md](NetGameServer/Docs/Login.md) /
  [Center.md](NetGameServer/Docs/Center.md) / [Game.md](NetGameServer/Docs/Game.md) /
  [Battle.md](NetGameServer/Docs/Battle.md) / [DB.md](NetGameServer/Docs/DB.md) /
  [Network.md](NetGameServer/Docs/Network.md) / [Shared.md](NetGameServer/Docs/Shared.md)

设计 / 规范 / 规划：
- [Protocol.md](NetGameServer/Docs/Protocol.md) — 协议约束红线（帧格式/链路/禁止项）
- [Code-Style.md](NetGameServer/Docs/Code-Style.md) — 编码规范与约定（命名/入口写法/并发/错误处理）
- [Refactor-Summary.md](NetGameServer/Docs/Refactor-Summary.md) — P0~P3 重构历史归档
- [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) — 对标 KBEngine 的能力差异与可优化路线
- [GameLogic/scripts/README.md](NetGameServer/GameLogic/scripts/README.md) — 业务脚本层（csx）规范

---

## 贡献
欢迎提交 Issue 和 Pull Request，我们共同打造更好的基础服务端框架。
