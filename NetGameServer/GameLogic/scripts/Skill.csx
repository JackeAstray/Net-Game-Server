// ===== 示例游戏逻辑脚本：Skill（技能系统） =====
// 展示玩法脚本的典型技能机制（全部写在 .csx 里，框架零改动，保存即热更新）：
// - 主动释放：OnMessage("CastSkill") 检查冷却 → 造成伤害（基础值 × 技能等级 × 全局倍率）
// - 冷却管理：OnTick 逐 tick 递减 CooldownRemaining，冷却中拒绝释放
// - 成长系统：累计释放 3 次自动升级，升级后伤害提升
// - 全局数据：SkillTotalDamage 累计总伤害 / SkillLevel 当前等级，供其他脚本（Quest 类）消费

using System;
using Framework.Entity;
using Framework.Scripting;

public class SkillScript : EntityScriptBase
{
    public override string EntityType => "Skill";

    private const int BaseDamage = 10;   // 1 级基础伤害
    private const int CooldownTicks = 10; // 冷却 10 tick（0.5 秒 @20Hz）
    private const int CastsToLevelUp = 3; // 释放 3 次升 1 级

    private int tickCount;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        entity.Set("Level", 1);
        entity.Set("CooldownRemaining", 0);
        entity.Set("Casts", 0);
        Console.WriteLine($"[脚本] Skill {entity.EntityId} 创建，Level=1");
    }

    public override void OnTick(Framework.Entity.Entity entity, long frame)
    {
        tickCount++;
        int cooldown = entity.Get<int>("CooldownRemaining");
        if (cooldown > 0)
        {
            entity.Set("CooldownRemaining", cooldown - 1);
        }

        // 每 5 tick 检查升级：累计释放次数达标 → 升级并清零计数
        if (tickCount % 5 == 0)
        {
            int casts = entity.Get<int>("Casts");
            if (casts >= CastsToLevelUp)
            {
                int level = entity.Get<int>("Level") + 1;
                entity.Set("Level", level);
                entity.Set("Casts", 0);
                Framework.Scripting.ScriptHost.Current?.SetGlobal("SkillLevel", level);
                Console.WriteLine($"[脚本] Skill {entity.EntityId} 升级到 Lv.{level}，伤害提升！");
            }
        }
    }

    public override void OnMessage(Framework.Entity.Entity entity, string method, object?[] args)
    {
        if (method == "CastSkill")
        {
            if (entity.Get<int>("CooldownRemaining") > 0)
            {
                Console.WriteLine($"[脚本] Skill {entity.EntityId} 冷却中（剩余 {entity.Get<int>("CooldownRemaining")} tick），释放被拒绝");
                return;
            }

            // 基础伤害 × 等级 × 全局伤害倍率（框架/其他脚本可调整，无需改本脚本）
            int level = entity.Get<int>("Level");
            int multiplier = 1;
            var raw = Framework.Scripting.ScriptHost.Current?.GetGlobal("DamageMultiplier");
            if (raw is int m) multiplier = m;
            int damage = BaseDamage * level * multiplier;

            // 累计总伤害写入全局数据（供任务/统计类脚本消费）
            var totalRaw = Framework.Scripting.ScriptHost.Current?.GetGlobal("SkillTotalDamage");
            int total = totalRaw is int t ? t : 0;
            Framework.Scripting.ScriptHost.Current?.SetGlobal("SkillTotalDamage", total + damage);

            entity.Set("Casts", entity.Get<int>("Casts") + 1);
            entity.Set("CooldownRemaining", CooldownTicks);
            Console.WriteLine($"[脚本] Skill {entity.EntityId} 释放技能！Lv.{level} 造成 {damage} 伤害（累计 {total + damage}），进入冷却");
        }
        else if (method == "QueryState")
        {
            Console.WriteLine($"[脚本] Skill {entity.EntityId} 状态: Lv.{entity.Get<int>("Level")} 冷却={entity.Get<int>("CooldownRemaining")} 累计释放={entity.Get<int>("Casts")}");
        }
        else
        {
            Console.WriteLine($"[脚本] Skill {entity.EntityId} 未处理消息: {method}");
        }
    }
}

return new SkillScript();
