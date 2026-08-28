# Net-Game-Server

基于 **.NET 10** 的分布式微服务游戏服务器集群框架。
对标 KBEngine 的核心能力（实体/属性/迁移/脚本层/认证），结合 .NET 强类型与异步生态重新设计。

支持 TCP / UDP / KCP / WebSockets 多协议接入，可用于卡牌、MMO、实时竞技等多种在线游戏。

---

## 核心能力

| 能力 | 一句话描述 | 详见 |
|---|---|---|
| 分布式微服务 | Gateway / Login / Center / Game / Battle / DB 6 节点，TCP+HMAC 互联 | [架构](#架构) |
| KBE machine 看护 | `Tools/Machine` 读 `topology.json`，按 `dependsOn` 拉起 + replicas + 崩溃指数退避 | [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) |
| 声明式协议 | `Protocol/defs/*.def` 唯一事实来源；Protogen 生成强类型 + 路由表 | [Protocol.md](NetGameServer/Docs/Protocol.md) |
| 强类型分发 | `MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式 | [Code-Style.md](NetGameServer/Docs/Code-Style.md) |
| 统一网关 | 4 协议接入 + 配置化转发 + 路由元数据注入 | [Gateway.md](NetGameServer/Docs/Gateway.md) |
| 实体/属性 | `EntityDef` + 脏标记 + All/AOI/OwnClient 三种同步作用域 | [Battle.md](NetGameServer/Docs/Battle.md) |
| EntityCall | 91001/91002 跨进程调用 + callId + 超时回执 | [Center.md](NetGameServer/Docs/Center.md) |
| 脚本 Mailbox | csx 脚本 `entity.Mailbox.Call/CallAsync` 同进程零开销，跨节点异步回执 | [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) |
| 在线迁移 | 玩家主实体 + 属主玩法实体（Skill/Item）同包随迁 | [Battle.md](NetGameServer/Docs/Battle.md) |
| 单线程 tick | 固定频率主循环串行处理入站消息，状态只在 tick 线程读写 | [Code-Style.md](NetGameServer/Docs/Code-Style.md) |
| 脚本宿主 | 玩法写在 `GameLogic/scripts/*.csx`，保存即热更新 | [GameLogic/scripts/README.md](NetGameServer/GameLogic/scripts/README.md) |
| 平滑加权 LB | `GetBestBattleNode` Nginx-SWRR（权重=100-load） | [Center.md](NetGameServer/Docs/Center.md) |
| 防重放 | SessionGuard 时间窗 + TokenService SessionSeq + NonceService | [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) |
| Bots 压测 | TCP/WS + RTT p50/p95/p99 + 时间同步 offset + ramp-up | [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) |

---

## 架构

```
                ┌──────────────┐
  TCP/UDP/KCP/WS│   Gateway    │  31300
   客户端流量 ─▶│  (统一接入)  │─┬─▶ Login   31302
                └──────────────┘ ├─▶ Game    31304
                                ├─▶ Center  31306
                                └─▶ Battle  31307~n
                ┌──────────────┐
                │    Center    │  31306  (控制平面)
                │ 注册/匹配/迁移 │
                └──┬────────┬──┘
                   │        │
       ┌──────┐ ┌──▼────┐ ┌▼─────┐
       │Login │ │Battle │ │ DB   │  31309
       │31302 │ │场景   │ │强类型│
       └──────┘ └───────┘ └──────┘
```

- 客户端连 Gateway（默认 31300），按协议路由表配置化转发到后端节点
- 后端节点间通过 Center 协调（实体迁移、EntityCall 中继、节点状态）
- 所有节点启动后向 Center 注册并维持心跳（默认 10s 间隔）
- Battle 可多实例，Center 按 SWRR 选节点

---

## 文档导航

### 节点模块
- [Gateway.md](NetGameServer/Docs/Gateway.md) — 统一接入 / 路由 / 会话时间窗
- [Login.md](NetGameServer/Docs/Login.md) — 账号 / Token 签发 / 限流
- [Center.md](NetGameServer/Docs/Center.md) — 注册 / 心跳 / SWRR / 迁移协调 / EntityCall 中继
- [Game.md](NetGameServer/Docs/Game.md) — 背包 / 公会 / 社交 / 任务
- [Battle.md](NetGameServer/Docs/Battle.md) — 场景 / AOI / 帧同步 / 玩法实体 / 迁移
- [DB.md](NetGameServer/Docs/DB.md) — 强类型持久化 / EntityPersistenceService
- [Network.md](NetGameServer/Docs/Network.md) — TCP/UDP/KCP/WS + 零拷贝发送
- [Shared.md](NetGameServer/Docs/Shared.md) — 公共层 / ConfigHelper / Json / 日志

### 设计 / 规范 / 规划
- [Protocol.md](NetGameServer/Docs/Protocol.md) — 协议约束红线（帧格式 / 链路 / 禁止项）
- [Code-Style.md](NetGameServer/Docs/Code-Style.md) — 编码规范与约定（命名 / 入口写法 / 并发 / 错误处理）
- [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) — 与 KBEngine 的能力差异与可优化路线
- [Refactor-Summary.md](NetGameServer/Docs/Refactor-Summary.md) — P0~P3 重构历史归档（只读）
- [GameLogic/scripts/README.md](NetGameServer/GameLogic/scripts/README.md) — 业务脚本层（csx）规范

---

## 环境要求

- 必需：[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 可选：MySQL / SQL Server / PostgreSQL（替换 DB 默认文件持久化）、Redis（缓存/分布式限流）、Nginx / YARP（Gateway 集群反向代理）

---

## 快速开始

### 1. 克隆与编译

```bash
git clone <repo-url>
cd Net-Game-Server
dotnet build NetGameServer.slnx
```

### 2. 节点启动顺序

| 顺序 | 节点 | 默认端口 | 说明 |
|---|---|---|---|
| 1 | DB | 31309 | 数据层 |
| 2 | Center | 31306 | 控制平面 |
| 3 | Login | 31302 | 账号 / Token 签发 |
| 4 | Game / Battle | 31304 / 31307~n | 业务层，Battle 可多实例 |
| 5 | Gateway | 31300 | 接受外部流量，最后启动 |

可直接到各节点目录执行 `dotnet run`，或通过 `Tools/Supervisor` / `Tools/Machine` 统一拉起与看护。

### 3. 验证

构建完成后跑六套验证套件确认全链路：

```bash
dotnet run --project Tests/ProtocolVerify   -c Release   # 协议/分发/EntityCall/迁移/SWRR/防重放
dotnet run --project Tests/NetworkVerify    -c Release   # 真实 Battle 节点集成
dotnet run --project Tests/ScriptHostVerify -c Release   # 脚本宿主 + 玩法脚本
dotnet run --project Tests/LoggerVerify     -c Release   # 日志
dotnet run --project Tests/SupervisorVerify -c Release   # Supervisor 进程看护
dotnet run --project Tests/MachineVerify    -c Release   # Machine 拓扑 + 依赖启动 + replicas + emit-supervisor-config
```

---

## 协议约束（速记）

- **客户端 ↔ Gateway**：`[MsgId(4)][Payload]`，外层长度帧
- **Gateway ↔ 后端**：`[ClientSessionId(8)][MsgId(4)][Payload]`
- **后端 ↔ DB**：`[MsgId(4)][RequestId(8)][Payload]`
- 内部消息（90999 / 91001~91006）走 `internal="true"`，Gateway 拒绝伪造

完整约束与禁止项见 [Protocol.md](NetGameServer/Docs/Protocol.md)。

---

## 贡献

欢迎提交 Issue 和 Pull Request。提交流程见 [Code-Style.md](NetGameServer/Docs/Code-Style.md)。
