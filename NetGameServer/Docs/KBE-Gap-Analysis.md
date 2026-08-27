# 与 KBEngine 的差距分析（Gap Analysis）

> 本文档对照 [KBEngine 官方架构](http://kbengine.github.io//cn/docs/concepts/layout.html)（[引擎概览](https://www.kbelab.com/tutorial/manual/engine-overview.html)），
> 逐项评估本服务器（Net-Game-Server）与 KBE 的差距，按 **✓ 已对标 / ◐ 部分对标 / ✗ 未实现** 分级，并给出优先级建议。
> 已完成的机制细节见 `Refactor-Summary.md`（26 项对标条目）。

---

## 一、总体结论

| 维度 | 评估 | 说明 |
|---|---|---|
| 协议/实体框架 | ✓ 基本对齐 | defs 声明式协议、实体属性脏标记增量同步、EntityCall 跨进程调用均已落地 |
| 进程模型 | ◐ 对标但简化 | 7 进程对应 KBE 8 进程，缺少 machine 发现与 dbmgr 数据库实体表 |
| 游戏逻辑层 | ✓ 对齐 | .csx 脚本宿主 + 热更新，对标 KBE Python 脚本层 |
| 可靠性 | ◐ 部分对齐 | 有备份/恢复/Leader 选举，但缺实体自动迁移与断线重连 |
| 运维工具链 | ✗ 差距最大 | 无部署编排、管理台、性能 Profile |

核心差距不在"单机机制"，而在 **分布式编排（发现/迁移/负载均衡）** 与 **运维工具链**。

---

## 二、进程模型对比

### KBE 标准进程（8 类）

```
machine         机器守护：进程发现/拉起/看护（每个物理机一个）
interfaces      账号/数据库接口：注册、验证、收费等（可脚本化）
dbmgr           数据库管理：entity_table 实体持久化（MySQL/MongoDB）
baseappmgr      baseapp 调度：负载均衡、实体迁移决策
cellappmgr      cellapp 调度：空间划分、cell 迁移决策
baseapp         玩家逻辑：代理实体、登录入口、持久化交互（对应"玩家专属逻辑"）
cellapp         空间逻辑：实体计算、AOI、属性广播（对应"场景逻辑"）
loginapp        登录入口：负责登录流程
logger          日志聚合  |  bots 压测  |  guiconsole 管理台  |  tools 工具
```

### 本实现进程（7 类）

```
Gateway     客户端接入（TCP/KCP/UDP/WS）+ 消息路由转发     ← loginapp + 路由层
Login       登录/账号/HTTP API                             ← loginapp + interfaces（简化）
DB          数据库服务（EF Core/MySQL + 文件实体存储）     ← dbmgr + interfaces（简化）
Center      节点注册/心跳/Leader 选举/房间调度             ← baseappmgr + cellappmgr + machine（简化）
Game        社交逻辑（好友/聊天/邀请）                     ← baseapp（简化，仅部分能力）
Battle      场景实体/tick 引擎/帧同步/AOI                  ← cellapp（简化）
Logger      日志聚合（UDP 31320）                          ✓ 对齐
Bots        压测工具                                      ✓ 对齐
```

**差距**：KBE 的 baseapp（玩家代理逻辑）与 cellapp（空间逻辑）在物理上可独立水平扩展，
且由 baseappmgr/cellappmgr 做动态负载均衡与实体迁移；本实现 Game/Battle 为静态进程，
Center 只维护注册表与心跳，**不具备实体级迁移与扩容能力**。

---

## 三、机制差距明细

### A. 已对标（✓）——见 Refactor-Summary.md 条目 1-26

协议生成、实体/属性脏同步、TickEngine、KCP、DB 队列、EntityCall、
MessageDispatcher、安全加固（SessionId/Token/内部认证）、脚本宿主+热更新、
错误隔离、全局数据、备份/恢复、持久化、Center 快照、Leader 选举、全量 Dispatcher 迁移、
Logger 聚合、Bots、玩法脚本（Avatar/Npc/Quest/Skill/Item）。

### B. 部分对标（◐）

| # | 机制 | KBE 能力 | 本实现现状 | 差距点 | 建议优先级 |
|---|---|---|---|---|---|
| B1 | 实体持久化 | dbmgr 按实体属性表存 MySQL/MongoDB，支持查询/索引/事务 | `EntityPersistenceService` 文件落盘（按类型分目录） | 无 SQL 查询能力、无跨服一致性 | **P1**：接入 DB 服务的 MySQL（已有 EF Core） |
| B2 | 属性同步权限 | Witness 按 `ALL_CLIENTS/OWN_CLIENT/CELL_PUBLIC/CELL_PRIVATE` 分级广播 | 全量脏属性增量广播 | 无归属/权限分级，隐私属性也会广播 | P1：EntityDef 增加同步标记（见协议 defs 的 internal 思路） |
| B3 | AOI | 空间格子 + 实体 enter/leave 视野事件 + 范围查询 | `GridAoiManager` 九宫格索引 | 无视野进入/离开事件、无范围查询 API 消费 | P2：补 OnEnterView/OnLeaveView 事件 |
| B4 | 心跳/超时 | 客户端/节点心跳 + 超时踢线 + 断线恢复 | UDP/KCP 5 分钟超时踢线；Center 节点 30s 心跳清理；TCP 无踢线 | TCP 无超时清理；无断线重连状态恢复 | P2：TCP 会话超时 + 断线重连令牌 |
| B5 | 跨进程调用 | Mailbox/EntityCall 全链路（含回调、超时） | `EntityCall` 本地/远程调用 + 参数编解码 | 无调用超时/回调链，跨节点路由依赖 Center 静态表 | P2：补超时与目标节点动态解析 |
| B6 | 时间同步 | gameUpdateHertz + 客户端-服务器时间同步协议 | TickEngine 固定频率 | 无客户端时间同步协议（帧同步依赖玩家输入时间戳） | P2 |
| B7 | 服务器配置 | server_config.xml + 按进程粒度配置 | `ConfigHelper` 键值读取 | 无配置文件模板/校验/热重载 | P3 |
| B8 | 安全 | 账号密码 + 会话验证 + 防重放 | HMAC Token + 内部认证 + SessionId 混淆 | 无防重放（时间戳窗口）、无封禁/风控 | P3 |

### C. 未实现（✗）

| # | 机制 | KBE 能力 | 差距影响 | 建议优先级 |
|---|---|---|---|---|
| C1 | machine 进程发现 | 物理机守护进程，自动发现/拉起/看护服务器进程 | 部署需手工编排，无进程崩溃自动拉起 | **P1**：轻量 Supervisor（看护 + 自动重启） |
| C2 | 实体迁移 | baseapp/cellapp 负载不均衡时实体在线迁移（无损切换） | 无法在线扩容/缩容，单点容量受限 | **P2**：先做静态分片，再实体迁移 |
| C3 | 负载均衡 | baseappmgr 按负载分配 baseapp/cellapp | Center 注册表含 CurrentLoad 但无分配算法消费 | P2：Gateway 登录分流到 Game（按负载） |
| C4 | 断线重连 | 客户端断线后在超时窗口内重连并恢复实体上下文 | 断线即丢失会话状态（存档已落盘，可恢复但体验断档） | P2：重连令牌 + 会话挂起 |
| C5 | 管理台/工具 | guiconsole 可视化监控、kbe_services 启停脚本、watchdog | 无监控面板，运维靠日志 | P3：Center 健康接口已暴露 isLeader/节点表，可扩展 Web 面板 |
| C6 | 性能 Profile | 服务器内建耗时统计（tick 耗时/消息耗时/内存）输出 | 无性能剖析数据，压测只能看吞吐 | P3：tick 耗时统计 + 慢消息告警 |
| C7 | 实体属性回调链 | def 属性 change 事件驱动（onPropertyChange 回调） | 脚本层靠 tick 轮询全局数据（Quest 示例），无属性级事件 | P3：Entity 属性 Set 后触发脚本回调 |
| C8 | 客户端 SDK | KBE 官方客户端插件（Unity/UE） | 仅服务端 + Bots，客户端需自行对接协议 | P3（项目定位外） |

---

## 四、差距优先级路线图

```
P1（可靠性地基，建议下一迭代）
  B1 实体持久化接入 MySQL（复用 DB 服务 EF Core，实体表自动建表）
  C1 轻量进程看护（Supervisor：崩溃自动拉起，复用 Center 注册表）

P2（分布式能力）
  B2 属性同步权限分级（EntityDef 同步标记 → Witness 式分级广播）
  B3 AOI 视野事件（OnEnterView/OnLeaveView）
  B4 TCP 超时踢线 + 断线重连令牌
  B5 EntityCall 调用超时
  C2 实体迁移（先静态分片 → 在线迁移）
  C3 登录分流负载均衡（Gateway → Game 按 Center 负载表）

P3（运维与体验）
  B6 时间同步协议  B7 配置模板/热重载  B8 防重放
  C4 断线重连恢复  C5 管理台  C6 Profile  C7 属性回调链
```

---

## 五、结论

- **架构对标度**：核心机制（协议/实体/脚本/同步/传输/可靠性）约 **80% 已对标**，单服务器能力接近 KBE。
- **最大差距**：分布式编排（machine 发现、实体迁移、负载均衡）与运维工具链（管理台、Profile、部署脚本）。
- **最小投入路径**：P1 两项（MySQL 持久化 + 进程看护）即可显著提升生产可用性，建议作为下一迭代目标。
