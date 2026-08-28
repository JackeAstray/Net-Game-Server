# Net-Game-Server
基于 `.NET 10` 的分布式微服务游戏服务器集群框架。

这是一个高性能、可扩展的分布式游戏服务端解决方案。支持 TCP、UDP、KCP 和 WebSockets 等多种网络协议，适用于开发从卡牌、MMO 到实时竞技类等多种类型的在线游戏应用。

## 核心特性
- **分布式微服务架构**: 服务器根据业务拆分为多个独立节点，降低耦合，易于水平扩展。
- **高性能网络库**: 自定义 `Network` 模块支持 TCP/UDP/KCP/WS 协议，适合不同的网络环境。
- **声明式协议 + 代码生成**: `Protocol/defs/*.def` 声明消息，`Protogen` 自动生成
  消息类/ID/路由表（MemoryPack 二进制序列化），改协议只改 def。
- **统一网关调度**: `Gateway` 节点处理异构客户端连接，按生成的路由表配置化转发。
- **服务发现与状态同步**: 通过 `Center` 服务器进行集群节点管理和注册，以**平滑加权轮询（SWRR）+ 过期负载惩罚**分配战斗节点。
- **实体/属性框架**: `EntityDef` + 脏标记增量同步（对标 KBEngine Witness），
  只广播变更属性；**跨进程实体调用**（`EntityCall` 带 callId/超时/回执，经 Center 中继）。
- **单线程 tick 引擎**: 固定频率主循环驱动帧同步与定时逻辑（对标 KBE gameUpdateHertz）。
- **脚本宿主**: 游戏逻辑写在 `GameLogic/scripts/*.csx`，与底层框架物理分离、可热更新
  （对标 KBE Python 脚本层）。
- **强类型消息分发**: `MessageDispatcher` 配置化注册 + MemoryPack/JSON 双格式兼容，
  业务层直接消费强类型请求对象（无二次序列化，Game/DB 已全量对齐）。
- **安全加固**: 无状态签名 Token、内部连接认证握手、随机会话 ID。

## 迭代里程碑（对标 KBEngine 演进，详见 [KBE-Gap-Review](NetGameServer/Docs/KBE-Gap-Review.md)）
| 迭代 | 主题 | 成果 |
|---|---|---|
| 1-8 | P0/P1 基建 | 协议生成、实体/脏同步、tick 引擎、KCP、DB 队列、脚本层、断线重连、性能热路径 |
| 9 | 实体在线迁移 | 玩家主实体冻结-序列化-搬迁-恢复（Center 协调中继） |
| 10-12 | 工程治理 | 巨型类按业务域拆 partial、并发/压测/热迁移测试、FriendHandler 6 拆 |
| 13 | 强类型化 + EntityCall | FriendHandler 业务层强类型化（去二次序列化）；EntityCall 加 callId/超时表/回执关联 + Center 中继 91001/91002 真实跨进程链路 |
| 14 | 双轨清理 + 负载均衡 | Battle 旧 JSON 路由字典移除（全量迁移强类型分发）；Center 平滑加权轮询 + 过期负载惩罚 |
| 15 | 玩法实体迁移 v2 | 属主 Skill/Item 与玩家同包随迁（EntityMigrateRequest.OwnedEntities）+ 属主绑定 + 三路径孤儿回收（迁移出/离场/离房）+ 玩法实体 ID 节点段防跨节点撞 ID |

> 前 12 轮迭代把 P0/P1/P2 全部落地；当前**五套验证**（Protocol / Network / ScriptHost / Logger / Supervisor）全绿。

## 项目结构
- **[Gateway](NetGameServer/Docs/Gateway.md)**: 网关服务器，统一处理客户端的长短期连接。
- **[Login](NetGameServer/Docs/Login.md)**: 登录服务器，处理账号系统、登录验证相关业务，支持 HTTP 与 Socket 协议。
- **[Center](NetGameServer/Docs/Center.md)**: 全局控制中心服务器，管理服务节点，执行跨服匹配、调度与实体远程调用中继。
- **[Game](NetGameServer/Docs/Game.md)**: 通用游戏逻辑服务器（背包、公会、社交、任务等）。
- **[Battle](NetGameServer/Docs/Battle.md)**: 战斗物理场景服务器，负责核心对局判定和场景状态同步。
- **[DB](NetGameServer/Docs/DB.md)**: 数据库服务数据持久化节点，支持对数据库和缓存代理（Redis/MySQL等）的访问。
- **[Network](NetGameServer/Docs/Network.md)**: 底层网络通信模块，承载 TCP/UDP/KCP/WS 网络连接和传输能力。
- **[Shared](NetGameServer/Docs/Shared.md)**: 共享模块库，提供公用实体、统一常量、配置解析、统一序列化辅助等。
- **Framework/**: 底层框架（Core/Protocol/Entity/Tick/Scripting），与游戏逻辑分离。
- **Protocol/defs/**: 协议声明（唯一事实来源），构建时自动生成代码。
- **GameLogic/scripts/**: 游戏逻辑脚本层（.csx，可热更新）。
- **Protogen/**: 协议代码生成器。
- **Tests/**: 验证套件（ProtocolVerify / ScriptHostVerify / NetworkVerify / LoggerVerify / SupervisorVerify）。

> 详细的重构对照、验证结果与可优化路线见 [Refactor-Summary.md](NetGameServer/Docs/Refactor-Summary.md) 与 [KBE-Gap-Review.md](NetGameServer/Docs/KBE-Gap-Review.md)。

## 环境要求
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 可选 (具体基于业务逻辑扩展)：
  - MySQL / SQL Server
  - Redis

## 快速开始

### 1. 编译项目
使用 Visual Studio 或基于控制台的 `dotnet build` 编译整个解决方案：
```bash
dotnet build NetGameServer.slnx
```

### 2. 节点启动顺序
为了让各服务节点能正常注册与通信，启动应该遵循以下顺序：
1. **DB Server** (数据层)
2. **Center Server** (中心路由和状态控制)
3. **Login Server** (账号及认证服务)
4. **Game / Battle Server** (业务逻辑层)
5. **Gateway Server** (网关层，最后启动以接受外部客户端连接)

你可以直接去各自目录执行 `dotnet run`，或通过 `Tools/Supervisor` 统一拉起与看护各节点进程。

## 架构说明
客户端与服务端通信，所有的外部流量统一打到 `Gateway`，然后由 `Gateway` 将报文解析成 `[SessionId(8)][MsgId(4)][Payload]` 形式，再投递向后方（如 `Login`，`Game` 等）。内部服务器处理完毕后，再通过同一连接投递回应，`Gateway` 根据 `SessionId` 把回应发给指定的客户端应用。

## 贡献
欢迎提交 Issue 和 Pull Request，我们共同打造更好的基础服务端框架。
