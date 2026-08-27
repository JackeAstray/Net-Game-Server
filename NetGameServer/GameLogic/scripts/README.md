# GameLogic/scripts 脚本层规范

> 游戏逻辑与底层框架物理分离的脚本层（对标 KBE 的 Python 脚本层）。
> 改玩法只改 `.csx`，框架零改动；文件保存即热更新（ScriptHost 防抖重编译）。

## 一、脚本编写约定

1. **文件格式**：UTF-8，文件名 = 玩法名（如 `Skill.csx`）。
2. **统一头注释**：
   ```csharp
   // ===== 示例游戏逻辑脚本：Xxx（中文描述） =====
   // 展示能力说明...
   // 玩法逻辑一行说明
   // 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。
   ```
3. **脚本结构**（固定三段）：
   ```csharp
   using System;
   using Framework.Entity;
   using Framework.Scripting;

   public class XxxScript : EntityScriptBase
   {
       public override string EntityType => "Xxx";   // 与实体 TypeName 匹配（EntityDef.Name）
       // 私有状态字段...
       public override void OnCreate(Entity entity) { ... }   // 初始化属性
       public override void OnTick(Entity entity, long frame) { ... }  // 周期逻辑
       public override void OnMessage(Entity entity, string method, object?[] args) { ... } // 消息响应
   }

   return new XxxScript();
   ```
4. **属性约定**：实体属性必须由 `EntityDef` 声明（`entity.Set` 未声明属性会被忽略）；属性名用 PascalCase。
5. **全局数据约定**：跨脚本共享状态一律走 `ScriptHost.Current?.GetGlobal/SetGlobal`，键名见下表；新键需登记。
6. **调试输出**：脚本内用 `Console.WriteLine` 输出（框架层用 `Log.Info`）。

## 二、实体类型与属性清单

| 实体类型 | 脚本 | 属性 | 说明 |
|---|---|---|---|
| Avatar | Avatar.csx | Hp / MaxHp / Score / Position | 玩家角色：回血、受伤结算 |
| Npc | Npc.csx | Hp / MaxHp / Score / Position | 野怪：巡逻 AI、受击死亡掉落经验 |
| Quest | Quest.csx | Hp / MaxHp / Score | 任务：监听全局经验阈值完成 |
| Skill | Skill.csx | Level / CooldownRemaining / Casts | 技能：冷却管理、升级成长 |
| Item | Item.csx | ItemId / Count | 物品：拾取堆叠、使用消耗、自动掉落 |

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

- **全局数据即总线**：Npc 产出 `TotalExpDropped` → Quest 轮询消费（无互相引用）。
- **新增协作数据**：先在"全局数据键清单"登记，再在脚本中读写。
- **新增实体脚本**：写脚本 → 定义 EntityDef → 创建实体（框架 NotifyCreate 自动生效）。
