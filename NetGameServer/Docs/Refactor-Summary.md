# 重构历史归档

> 本文档归档 P0~P3 阶段的重构里程碑（最终态数据），**作为历史快照保留**。
> 当前能力与项目形态请看 [README.md](../../README.md)；与 KBEngine 的对标差异与
> 可优化路线见 [KBE-Gap-Review.md](KBE-Gap-Review.md)。

---

## 一、阶段里程碑

| 阶段 | 目标 | 状态 |
|---|---|---|
| P0 | 声明式协议 + 生成器 + 二进制序列化 + 配置化路由 + 安全加固 | ✅ |
| P1 | 实体框架 + tick 引擎 + KCP + DB 队列 + 跨进程调用 + Dispatcher 迁移 | ✅ |
| P2 | 脚本宿主（游戏逻辑与框架物理分离、热更新） | ✅ |
| P3 | KBE 级可靠性（实体备份/恢复、日志聚合、压测工具） | ✅ |

**最终态（归档数据）**：
- 底层框架 4 个项目（`Framework.Core` / `Framework.Protocol` / `Framework.Entity` / `Framework.Tick` / `Framework.Scripting`）
- `Protogen` 协议代码生成器
- `GameLogic/scripts` 脚本层（5 个示例 csx）
- 5 套验证套件（Protocol / Network / ScriptHost / Logger / Supervisor）
- 协议 defs 142 条消息
- 全部 **0 错误构建**

---

## 二、已归档文档

以下文档已被新文档覆盖，保留为 git 历史供回溯：

- `Refactor-Plan.md`（P0~P3 蓝图）→ 已被 `KBE-Gap-Review.md` 替代
- `KBE-Gap-Analysis.md`（旧版 gap 分析）→ 已被 `KBE-Gap-Review.md` 替代

---

## 三、迁移指南（旧版节选，仍可参考）

### 客户端接入
1. 引入 `Framework.Protocol/Generated/` 的消息类（生成代码，可直接 include）
2. 按 `RouterTable` 的路由表对接：登录走 `Login`、玩家数据走 `Game`、战斗走 `Battle`
3. 客户端帧格式：`[MsgId(4)][Payload]`，外层长度帧封包（`Network.Routing.PacketBuilder`）

### 新增业务消息
1. 在 `Protocol/defs/Xxx.def` 加 `<Message>` 定义（id、name、target、fields）
2. 重新构建：`dotnet build Framework/Framework.Protocol/`（自动重跑 Protogen）
3. 在目标节点的 `XxxDispatcher` 加注册 + 写业务方法
   ```csharp
   dispatcher.RegisterAsync<XxxRequest, XxxResponse>(
       handler: ctx => MyHandler.HandleXxxAsync(ctx.Session, ctx.Request),
       jsonFallback: true); // 旧客户端 JSON 兼容
   ```
4. 客户端按生成的消息类（MemoryPack）或旧 JSON 格式对接

### 新增实体类型
1. 定义 `EntityDef`（属性声明）
2. 编写 `GameLogic/scripts/Xxx.csx` 脚本类（继承 `EntityScriptBase`），`return new XxxScript();` 结尾
3. 重启 Battle（或脚本热更新自动加载），实体创建时脚本 `OnCreate` 自动生效

### 新玩法（不改框架）
- 全部写在 .csx 脚本里：`OnTick` 做逻辑、`OnMessage` 响应客户端、`Set` 属性触发增量同步
- 脚本变更保存即热更新，无需重新编译/重启（`ScriptHost` 防抖重编译）

---

## 四、关键目录速查

```
Protocol/defs/           协议声明（唯一事实来源）
Protogen/                代码生成器
Framework/               底层框架（Core/Protocol/Entity/Tick/Scripting）
GameLogic/scripts/       游戏逻辑脚本层（可热更新）
Tests/                   验证套件（5 套）
Docs/                    设计/规划/规范文档
```
