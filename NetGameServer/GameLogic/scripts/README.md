# GameLogic/scripts 脚本层规范

> 游戏逻辑与底层框架物理分离的脚本层（对标 KBE 的 Python 脚本层）。
> 改玩法只改 `.csx`，框架零改动；文件保存即热更新（ScriptHost 防抖重编译）。
> Battle 服务器在场景创建/玩家加入时自动生成 Npc/Quest/Skill/Item 实体并绑定脚本
> （见 Battle/Entities/GameplayEntityDefs.cs），玩家实体（Player）绑定 Avatar.csx —— 脚本在生产运行时真实生效。

## 一、脚本编写约定

1. **文件格式**：UTF-8，文件名 = 玩法名（如 `Skill.csx`）。
2. **统一头注释**：
   ```csharp
   // ===== 示例游戏逻辑脚本：Xxx（中文描述） =====
   // 展示能力说明...
   // 玩法逻辑一行说明
   // 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。
   ```
3. **脚本结构**（固定三段 + 可选事件回调）：
   ```csharp
   using System;
   using Framework.Entity;
   using Framework.Scripting;

   public class XxxScript : EntityScriptBase
   {
       public override string EntityType => "Xxx";   // 与实体类型 TypeName 匹配（EntityDef.Name）
       // 私有状态字段（注意：热更新会重置，持久状态请放实体属性）...
       public override void OnCreate(Entity entity) { ... }   // 初始化属性
       public override void OnTick(Entity entity, long frame) { ... }  // 周期逻辑
       public override void OnMessage(Entity entity, string method, object?[] args) { ... } // 消息响应
       public override void OnPropertyChanged(Entity entity, string name, object? oldValue, object? newValue) { ... } // 实体属性变更事件（可选）
       public override void OnGlobalChanged(Entity entity, string key, object? value) { ... } // 全局数据变更事件（可选，事件驱动替代轮询）
   }

   return new XxxScript();
   ```
4. **属性约定**：实体属性必须由 `EntityDef` 声明（`entity.Set` 未声明属性会被忽略）；属性名用 PascalCase；
   隐私属性用 `SyncToClient: false`，属主私有属性用 `EntitySyncScope.OwnClient`（只广播给属主客户端）。
5. **全局数据约定**：跨脚本共享状态一律走 `ScriptHost.Current?.GetGlobal/SetGlobal`，键名见下表；新键需登记。
   `SetGlobal` 会触发各脚本的 `OnGlobalChanged` 事件（事件驱动协作），不要用 tick 轮询。
6. **调试输出**：脚本内用 `Console.WriteLine` 输出（框架层用 `Log.Info`）。

## 二、实体类型与属性清单

| 实体类型 | 脚本 | 属性 | 同步权限 | 说明 |
|---|---|---|---|---|
| Player | Avatar.csx | Hp / MaxHp / Score / Position / Rotation / Nickname / Equipment | 公开 / Equipment=OWN_CLIENT | 玩家角色：回血、受伤结算（客户端经 ScriptAction 调用 TakeDamage） |
| Npc | Npc.csx | Hp / MaxHp / Score / Position | 公开 | 野怪：巡逻 AI、受击死亡掉落经验（场景创建时生成 3 只） |
| Quest | Quest.csx | Hp / MaxHp / Score | CELL_PRIVATE（不广播） | 任务：OnGlobalChanged 事件驱动完成（场景创建时生成 1 个） |
| Skill | Skill.csx | Level / CooldownRemaining / Casts | Level=公开，CooldownRemaining=OWN_CLIENT，Casts=不广播 | 技能：冷却管理、事件驱动升级成长（玩家加入时生成） |
| Item | Item.csx | ItemId / Count | OWN_CLIENT | 物品：拾取堆叠、使用消耗、自动掉落（玩家加入时生成） |

## 三、全局数据键清单（脚本间总线）

| 键 | 写入方 | 消费方 | 类型 | 含义 |
|---|---|---|---|---|
| `DamageMultiplier` | 框架/任意脚本 | Avatar / Skill | int | 伤害倍率 |
| `TotalExpDropped` | Npc | Quest | int | 累计掉落经验 |
| `QuestCompleted` | Quest | 框架/客户端 | bool | 任务完成标记 |
| `SkillLevel` | Skill | 任意脚本 | int | 技能当前等级 |
| `SkillTotalDamage` | Skill | 统计/任务脚本 | int | 技能累计总伤害 |
| `ItemTotalPicked` | Item | 统计/任务脚本 | int | 物品累计拾取数 |
| `ItemHealedTotal` | Item | 统计/任务脚本 | int | 物品累计治疗量 |
| `ItemAutoDrops` | Item | 统计/任务脚本 | int | 自动掉落累计数 |

## 四、脚本间协作模式

- **全局数据即总线（事件驱动）**：Npc 产出 `TotalExpDropped`（SetGlobal）→ Quest 的 `OnGlobalChanged` 立即消费，
  无需轮询、无互相引用。
- **实体属性事件**：`Entity.Set` 触发 `OnPropertyChanged`（对标 KBE onPropertyChange 回调链）。
- **客户端调脚本**：客户端发 `ScriptAction(40006)`（EntityId + Method + Args[int32]）→ 框架路由到脚本 `OnMessage`。
- **新增协作数据**：先在"全局数据键清单"登记，再在脚本中读写。
- **新增实体脚本**：写脚本 → 定义 EntityDef（GameplayEntityDefs）→ 在生成器（BattleServerApp.Spawn*）中创建实体。
