# 重构蓝图：Net-Game-Server → KBE 式架构

> 目标：底层引擎与游戏逻辑物理分离（对标 KBEngine），协议声明式定义 + 代码生成，
> 二进制序列化，实体/属性框架，单线程 tick 引擎，脚本宿主。
> 全程保持每个阶段可编译、可运行。

## 当前状态（P0 已完成）

### 已交付

1. **声明式协议定义** `Protocol/defs/*.def`（XML，仿 KBE entity_defs）
   - `Login.def` / `Game.def` / `Center.def` / `Battle.def` / `Db.def`
   - 消息 ID 全局唯一、`target` 声明路由目标、`internal` 标记内部消息、`reply` 关联响应
   - 支持基本类型、`list:T`、`map:K,V`、自定义 `Struct` 引用、`optional` 字段

2. **代码生成器 `Protogen/`**（控制台工具）
   - 解析 def → 生成三份代码到 `Framework/Framework.Protocol/Generated/`：
     - `MessageIds.g.cs` —— 全协议消息 ID 常量
     - `Messages.g.cs` —— 消息/结构体类（MemoryPack 二进制序列化 + `Serialize()`/`Deserialize()`）
     - `RouterTable.g.cs` —— MsgId → (目标服务器, 类型) 配置化路由表
   - MSBuild 集成：`Framework.Protocol.csproj` 构建前自动重跑 Protogen（defs 变更自动生效）

3. **底层框架项目**
   - `Framework/Framework.Core`：日志（Serilog）、配置（appsettings + 环境变量 `NG_` 前缀）、
     安全组件
   - `Framework/Framework.Protocol`：`IGameMessage`、`ProtocolCodec`（帧编解码）、
     `BinaryRouteMetadata`（二进制尾部路由元数据，替代旧 JSON 元数据）

4. **安全加固（P0 核心）**
   - `Security/SessionIdGenerator` —— 加密随机 + 计数器混合，替代可预测的纯递增
     （`Network/SessionIdGenerator.cs` 已接入）
   - `Security/TokenService` —— HMAC-SHA256 无状态签名 Token（签发/验证/过期/防篡改），
     Login 登录响应已接入（替代原 Guid 占位符）
   - `Security/InternalAuthFilter` —— 服务间连接认证握手（节点签名 + 时间戳防重放），
     已接入 Gateway→Login/Game/Center/Battle 与 Login/Game/Battle→Center/DB 全部内部连接

5. **Gateway 配置化路由**
   - `GatewayServerApp.cs` 优先查 `RouterTable`（def 声明的消息按 target 转发），
     未定义消息回退旧区间路由（过渡兼容）
   - 拒绝客户端发送 `internal=true` 的消息

6. **验证工具 `Tests/ProtocolVerify`**
   - 消息 round-trip（含嵌套 Struct/List/Dictionary）、路由表、Token（正常/篡改/过期）、
     内部认证（正确/错误密钥）全部通过

### 结构

```
NetGameServer/
├── Protocol/defs/*.def        # 协议唯一事实来源（声明式）
├── Protogen/                  # 代码生成器
├── Framework/
│   ├── Framework.Core/        # 日志/配置/安全（底层）
│   └── Framework.Protocol/    # 协议运行时 + Generated/（生成代码）
├── Tests/ProtocolVerify/      # 验证工具
├── Network/ Shared/           # 原有底层（逐步迁移）
└── Gateway/ Login/ Center/ Game/ Battle/ DB/   # 业务服（逐步迁移）
```

## 下一阶段计划

### P1：实体/属性框架 + tick 引擎 + 二进制协议迁移（进行中）

- [x] **实体框架** `Framework/Framework.Entity`
  - `EntityDef`（属性/方法描述，仿 KBE ScriptDefModule）—— 已完成
  - `Entity` 基类：属性脏标记 + 增量同步（仿 Witness）—— 已完成
    - `PropertyCodec`：脏属性二进制增量编解码（8 种类型 + Float3 + 列表）
    - `SetSilent`（全量初始化不标记脏）/ `TakeDirtyProperties`（取脏并清空）
  - [ ] `EntityCall`/Mailbox：跨进程实体远程调用
  - [ ] 持久化映射：实体属性 → DB 表（仿 dbmgr entity_table）
- [x] **Battle 单线程 tick 引擎** `Framework/Framework.Tick`
  - `TickEngine`：固定频率主循环（默认 20Hz，可配 BattleTickHertz）+ 定时器（可取消/周期）—— 已完成
  - [ ] 控制器（移动/转向/接近触发）
  - [x] 收包入队、tick 内串行处理（对标 KBE 单线程无锁模型）—— 帧同步已实现
  - [x] 补齐 `BattleFrameSync`：`FrameSyncManager` 每 tick 聚合玩家输入，广播权威帧 —— 已完成
- [x] **Battle 接入实体框架**
  - `PlayerEntityDef`（玩家实体定义）、`EntityManager` 存储 Entity
  - `EntitySyncHandler`：脏属性增量广播（`EntityDeltaSync` 40105）+ 全量快照（`EntitySnapshot` 40106）
  - AOI 基于实体 Position 属性
- [x] **KCP 支持**：`Network/Kcp`（KcpServer/KcpClientWrapper/KcpSession）
  - Kcp 2.7.0 封装：可靠有序 UDP 传输，快速模式（NoDelay），ArrayPool 内存复用
  - Gateway 已启用 KCP 监听（TCP 端口 +1），端到端 10 条消息双向按序验证通过
- [x] **DB 任务队列**：`Framework.Core.OrderedTaskQueue`
  - 按 key（实体 ID/DBID）严格保序串行执行，不同 key 并发（仿 Buffered_DBTasks）
  - 20 任务 × 4 key 保序验证通过
- [x] **BinaryRouteMetadata 迁移**：路由元数据改二进制尾部块
  - `Shared.RouteMetadata` 的 Attach 系列委托 `Framework.Protocol.BinaryRouteMetadata`
  - TryExtract 系列二进制优先、JSON 回退（新旧格式双通，验证通过）
- [x] **EntityCall 跨进程实体调用** `Framework/Framework.Entity/EntityCall.cs`
  - `EntityCall`（Mailbox）：本地/跨节点引用实体，`Call(method, args)` 远程调用
  - `Entity.RegisterMethod/InvokeMethod`：实体方法注册与分发
  - `EntityManager.DispatchRemoteCall`：接收端分发
  - `ArgCodec`：参数二进制编解码（标量/字符串/Float3/null）
  - 协议：`EntityRemoteCall`(91001)/`EntityRemoteCallResult`(91002) 已定义
  - 验证：本地调用/消息分发/7 种参数 round-trip 全部通过
- [x] **MessageDispatcher 配置化分发** `Framework/Framework.Protocol/MessageDispatcher.cs`
  - RouterTable 驱动注册（注册处不写 MsgId 分支），强类型 handler
  - 注册时绑定 MemoryPack 泛型反序列化（零反射），`ISessionContext` 会话抽象
  - 验证：Login/BattleJoin 分发 + 回包 + 未注册回退全部通过
- [x] **业务 handler 迁移**：Battle/Login 已接入 MessageDispatcher
  - `BattleSessionContext`/`LoginSessionContext` 会话适配器
  - **JSON/二进制双格式兼容**（旧客户端 JSON 消息与新生二进制消息自动探测）
  - Battle：BattleJoin/BattleLeaveRoom/EntitySync/BattleFrameSync/PlayerDisconnect 已迁移
  - Login：Login/Register/Logout/PlayerDisconnect 已迁移
- [x] **协议迁移**：生成消息类 + MemoryPack 已全面可用（Dispatcher 驱动），旧消息类保留过渡
- [ ] **协议迁移收尾**：Game/Center/DB 的 Dispatcher 迁移

### P2：脚本宿主（游戏逻辑与框架物理分离）✅ 进行中

- [x] **脚本宿主框架** `Framework/Framework.Scripting`
  - `IEntityScript`/`EntityScriptBase`：脚本实体接口（OnCreate/OnDestroy/OnTick/OnMessage）
  - `ScriptHost`：加载 .csx 脚本（Roslyn Scripting）、编译诊断、**文件变更自动热更新**（防抖）
  - 脚本通过 Entity 属性/方法 API 与框架交互（不改框架即可改玩法）
- [x] **示例脚本** `GameLogic/scripts/Avatar.csx`（玩家角色：创建初始化/每帧回血/受伤处理）
  - 验证：加载、OnCreate、消息分发、40 tick 回血、**热更新（改脚本逻辑立即生效）** 全部通过
- [x] **Battle 接入脚本宿主**：tick 引擎驱动脚本 OnTick；实体创建/销毁通知脚本
- [ ] P2 完善：脚本错误隔离/回滚、脚本间通信、玩法脚本化示例扩展

### P3：KBE 级运维与可靠性

- [ ] 实体周期备份/崩溃恢复（平滑分摊算法，见对比报告）
- [ ] logger 日志聚合进程 + guiconsole 监控
- [ ] bots 压测工具
- [ ] Center 高可用（主备）

## 迁移原则

1. 每个阶段结束 `dotnet build NetGameServer.slnx` 必须 0 错误
2. 协议变更只改 def，生成代码不手改
3. 旧 JSON 消息在 P1 迁移期内保留兼容分支，迁移完成后删除
4. 新增功能优先走新框架，存量代码逐步搬迁
