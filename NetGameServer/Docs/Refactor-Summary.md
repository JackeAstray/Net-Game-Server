# 重构历史归档

> 本文档归档 P0~P3 阶段的重构里程碑（最终态数据），**作为历史快照保留**。
>
> 当前能力与项目形态请看 [README.md](../../README.md)；
> 与 KBEngine 的对标差异与可优化路线见 [KBE-Gap-Review.md](KBE-Gap-Review.md)。

---

## 一、阶段里程碑

| 阶段 | 目标 | 状态 |
|---|---|---|
| P0 | 声明式协议 + 生成器 + 二进制序列化 + 配置化路由 + 安全加固 | ✅ |
| P1 | 实体框架 + tick 引擎 + KCP + DB 队列 + 跨进程调用 + Dispatcher 迁移 | ✅ |
| P2 | 脚本宿主（游戏逻辑与框架物理分离、热更新） | ✅ |
| P3 | KBE 级可靠性（实体备份/恢复、日志聚合、压测工具） | ✅ |

**最终态（归档数据）**：

- 底层框架 5 个项目（`Framework.Core` / `Framework.Protocol` / `Framework.Entity` / `Framework.Tick` / `Framework.Scripting`）
- `Protogen` 协议代码生成器
- `GameLogic/scripts` 脚本层（5 个示例 csx）
- 6 套验证套件（Protocol / Network / ScriptHost / Logger / Supervisor / Machine）
- 协议 defs 142 条消息
- 全部 **0 错误构建**

---

## 二、已归档文档

以下文档已被新文档覆盖，保留为 git 历史供回溯：

- `Refactor-Plan.md`（P0~P3 蓝图）→ 已被 `KBE-Gap-Review.md` 替代
- `KBE-Gap-Analysis.md`（旧版 gap 分析）→ 已被 `KBE-Gap-Review.md` 替代

---

## 三、新增业务消息/实体/玩法

开发过程中新增内容请直接修改对应文档：

- 新增业务消息：改 `Protocol/defs/*.def` + 重新构建（自动跑 Protogen）→ 在目标节点 Dispatcher 注册
- 新增实体类型：定义 `EntityDef` + 写 `GameLogic/scripts/Xxx.csx` 脚本
- 新增玩法：全部写在 `.csx` 脚本里（`OnTick` / `OnMessage` / `Set`），保存即热更新

详细路径与示例见：

- [README.md §项目结构](../../README.md)
- [Code-Style.md §六 协议与序列化](Code-Style.md)
- [GameLogic/scripts/README.md](../GameLogic/scripts/README.md)
