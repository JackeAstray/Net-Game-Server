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
| 声明式协议 | C# `[GameMessage]`（Roslyn 源生成器）声明；编译期产出强类型 + 路由表 | [Protocol.md](NetGameServer/Docs/Protocol.md) |
| 强类型分发 | `MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式 | [Code-Style.md](NetGameServer/Docs/Code-Style.md) |
| 统一网关 | 4 协议接入 + 配置化转发 + 路由元数据注入 | [Gateway.md](NetGameServer/Docs/Gateway.md) |
| 实体/属性 | `EntityDef` + 脏标记 + All/AOI/OwnClient 三种同步作用域 | [Battle.md](NetGameServer/Docs/Battle.md) |
| EntityCall | 91001/91002 跨进程调用 + callId + 超时回执 | [Center.md](NetGameServer/Docs/Center.md) |
| 脚本 Mailbox | csx 脚本 `entity.Mailbox.Call/CallAsync` 同进程零开销，跨节点异步回执 | [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md) |
| 在线迁移 | 玩家主实体 + 属主玩法实体（Skill/Item）同包随迁 | [Battle.md](NetGameServer/Docs/Battle.md) |
| 实体位置路由 | 91007~91010 位置登记/查询 + EntityCallRouter 缓存，迁移后修正 stale 路由、支持 Battle 直达（对标 ET Location） | [Center.md](NetGameServer/Docs/Center.md) |
| 可插拔持久化 | `IEntityPersistenceStore` 抽象 + File/MySQL/PostgreSQL/Redis 实现 + 批量落库（对标 GeekServer 脏状态自动保存） | [DB.md](NetGameServer/Docs/DB.md) |
| 优雅关闭/健康检查 | 全节点 SIGINT/SIGTERM 排空 + 关服 flush，`/healthz` `/readyz` 健康端口（端口+10000） | [Shared.md](NetGameServer/Docs/Shared.md) |
| AOI 九宫格 | 视野半径可配（3x3/5x5/7x7）+ 2000 实体一致性压测（网格索引 vs 暴力枚举） | [Battle.md](NetGameServer/Docs/Battle.md) |
| Docker 一键集群 | MySQL/Redis(+Postgres 可选) + 六节点 compose 编排，含实体持久化 SQL 后端实时验证 | [deploy/README-docker.md](deploy/README-docker.md) |
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
       │Login │ │Battle │ │ DB   │  31305
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
- 可选：[Docker + docker compose](https://docs.docker.com/compose/)（`deploy/docker-compose.yml` 一键集群，自带 MySQL/Redis）

---

## 快速开始

### 1. 克隆与编译

```bash
git clone <repo-url>
cd Net-Game-Server
dotnet build NetGameServer.slnx
```

### 2. 配置集群共享密钥（必做）

所有节点之间通过 **TCP + HMAC 握手**（`CenterNodeSharedSecret`）认证，必须共用同一份密钥（≥16 字符），否则任何节点都拒绝启动或拒绝互联：

- **一键启动**：`NetGameServer/Publish/StartServers.bat` 首次运行会自动生成随机密钥并保存到 `Publish/.cluster_secret`，随后为全部子节点注入环境变量，无需手动配置。
- **手动 / `dotnet run`**：先设置环境变量再启动节点：
  ```bash
  # PowerShell
  $env:CenterNodeSharedSecret = "<32位以上强随机串>"
  # CMD
  set CenterNodeSharedSecret=<32位以上强随机串>
  ```
  也可写入各节点 `appsettings.json`（`Security:CenterNodeSharedSecret`）。
- 使用 `Tools/Machine` 拉起时，可在 `machine.json` 顶层配置 `"sharedSecret": "..."`，或让 Machine 进程继承上面的环境变量。
- 密钥缺失 / 过短 / 为占位符时，节点启动会立即报错并提示配置方法（不会静默使用默认密钥）。

### 3. 节点启动顺序

| 顺序 | 节点 | 默认端口 | 说明 |
|---|---|---|---|
| 1 | DB | 31305 | 数据层 |
| 2 | Center | 31306 | 控制平面 |
| 3 | Login | 31302 | 账号 / Token 签发 |
| 4 | Game / Battle | 31304 / 31307~n | 业务层，Battle 可多实例 |
| 5 | Gateway | 31300 | 接受外部流量，最后启动 |

可直接到各节点目录执行 `dotnet run`（先按第 2 步配置共享密钥），或通过 `Tools/Supervisor` / `Tools/Machine` 统一拉起与看护。

### 4. 验证

构建完成后跑七套验证套件确认全链路：

```bash
dotnet run --project Tests/ProtocolVerify   -c Release   # 协议/分发/EntityCall/迁移/SWRR/防重放/位置路由/AOI 压测
dotnet run --project Tests/NetworkVerify    -c Release   # 真实 Battle 节点集成
dotnet run --project Tests/ScriptHostVerify -c Release   # 脚本宿主 + 玩法脚本
dotnet run --project Tests/LoggerVerify     -c Release   # 日志
dotnet run --project Tests/SupervisorVerify -c Release   # Supervisor 进程看护
dotnet run --project Tests/MachineVerify    -c Release   # Machine 拓扑 + 依赖启动 + replicas + emit-supervisor-config
dotnet run --project Tests/LifecycleVerify  -c Release   # 可插拔持久化/批量落库/健康检查/优雅关闭（迭代 21）
```

### 5. Docker 一键集群（可选）

```bash
docker compose -f deploy/docker-compose.yml up -d --build   # MySQL/Redis + 六节点
curl http://127.0.0.1:41306/healthz                          # 存活检查
```
详见 [deploy/README-docker.md](deploy/README-docker.md)。

### 6. Bots 压测（AOI/广播/时间同步链路）

`Bots` 模拟机器人连真实 Gateway，登录 + 加入战斗 + 周期 EntitySync 移动（打穿 Battle AOI 广播）
+ 时间同步，统计收发速率 / RTT 分位（p50/p95/p99）/ offset 漂移：

```bash
# 本地六节点：200 机器人，battle 场景（高频移动 → AOI 视野广播），ramp-up 50ms/bot
Bots --count 200 --host 127.0.0.1 --port 31300 --duration 10 --scene battle --rampup 50
# 对 Docker 集群：主机 31300 已映射到 gateway 容器，命令相同
```
AOI 网格自身的正确性与性能由 `Tests/ProtocolVerify` 第 15.9 节覆盖（2000 实体 vs 暴力枚举一致性）。

---

## 协议约束（速记）

- **客户端 ↔ Gateway**：`[MsgId(4)][Payload]`，外层长度帧
- **Gateway ↔ 后端**：`[ClientSessionId(8)][MsgId(4)][Payload]`
- **后端 ↔ DB**：`[MsgId(4)][Payload(尾部附 __requestId 路由元数据)]`，请求-响应经 `__requestId` 关联
- 内部消息（90999 / 91001~91010）走 `internal="true"`，Gateway 拒绝伪造

完整约束与禁止项见 [Protocol.md](NetGameServer/Docs/Protocol.md)。

---

## 贡献

欢迎提交 Issue 和 Pull Request。提交流程见 [Code-Style.md](NetGameServer/Docs/Code-Style.md)。
