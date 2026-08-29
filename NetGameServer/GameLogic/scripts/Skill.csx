// ===== 示例游戏逻辑脚本：Skill（技能系统） =====
// 展示玩法脚本的典型技能机制（全部写在 .csx 里，框架零改动，保存即热更新）：
// - 主动释放：OnMessage("CastSkill") 检查冷却 → 造成伤害（基础值 × 技能等级 × 全局倍率）
// - 冷却管理：定时器到点清零冷却（事件驱动，代替 tick 递减）
// - 成长系统：累计释放达标**立即**升级（事件驱动，无需轮询）
// - 全局数据：SkillTotalDamage 累计总伤害 / SkillLevel 当前等级，供其他脚本（Quest 类）消费
// - 同步权限：Level 公开广播；CooldownRemaining 为属主私有（OWN_CLIENT）；Casts 服务端内部
// 客户端通过 ScriptAction(40006) 消息调用 CastSkill / QueryState。
//
// A1 修复：脚本宿主按类型只实例化一次，所有"每实体状态"按 entity.EntityId 键控存储。
// 同时修复热更新冷却 Bug：按剩余冷却毫秒重挂定时器，而非从完整 CD 重新计时。
//
// KBE-Gap-Review 落地：S1/S2/S3/S4

using System;
using System.Collections.Concurrent;
using Framework.Entity;
using Framework.Scripting;
using Framework.Tick;

public class SkillScript : EntityScriptBase
{
    public override string EntityType => "Skill";
    public override int ScriptVersion => 2;

    private const int BaseDamage = 10;
    private const int CooldownMs = 500;       // 0.5s 冷却（KBE-Gap-Review S2：用真实毫秒代替 tick 计数）
    private const int CastsToLevelUp = 3;
    private const int MaxLevel = 99;         // KBE-Gap-Review S3：等级上限

    // 每实体冷却定时器（A1 修复：按 EntityId 键控，跨实体互不串扰）
    private readonly ConcurrentDictionary<long, TimerHandle> cooldownTimers = new();

    public override void OnCreate(Entity entity)
    {
        entity.Set("Level", 1);
        entity.Set("CooldownRemaining", 0);
        entity.Set("Casts", 0);
        Log.Info("Skill", "Skill {EntityId} 创建，Level=1", entity.EntityId);
    }

    private void OnCooldownEnd(Entity entity)
    {
        // KBE-Gap-Review S3：边界钳制冷却下限 0
        MathClampSet(entity, "CooldownRemaining", 0, 0, int.MaxValue);
        Log.Debug("Skill", "Skill {EntityId} 冷却结束", entity.EntityId);
    }

    public override void OnMessage(Entity entity, string method, object?[] args)
    {
        if (method == "CastSkill")
        {
            if (entity.Get<int>("CooldownRemaining") > 0)
            {
                Log.Warn("Skill", "Skill {EntityId} 冷却中（剩余 {Remaining} ms），释放被拒绝",
                    entity.EntityId, entity.Get<int>("CooldownRemaining"));
                return;
            }
            int level = entity.Get<int>("Level");
            int multiplier = 1;
            var raw = ScriptHost.Current?.GetGlobal("DamageMultiplier");
            if (raw is int m) multiplier = m;
            int damage = BaseDamage * level * multiplier;

            var totalRaw = ScriptHost.Current?.GetGlobal("SkillTotalDamage");
            int total = totalRaw is int t ? t : 0;
            ScriptHost.Current?.SetGlobal("SkillTotalDamage", total + damage);

            int casts = entity.Get<int>("Casts") + 1;
            entity.Set("Casts", casts);
            if (casts >= CastsToLevelUp)
            {
                int newLevel = MathClampAdd(entity, "Level", +1, 1, MaxLevel);
                entity.Set("Casts", 0);
                ScriptHost.Current?.SetGlobal("SkillLevel", newLevel);
                Log.Info("Skill", "Skill {EntityId} 升级到 Lv.{Level}，伤害提升！", entity.EntityId, newLevel);
            }

            // KBE-Gap-Review S2：定时器到点清零冷却（事件驱动，不在 tick 递减）
            CancelCooldownTimer(entity.EntityId);
            MathClampSet(entity, "CooldownRemaining", CooldownMs, 0, int.MaxValue);
            cooldownTimers[entity.EntityId] = AddTimer(entity, CooldownMs, () => OnCooldownEnd(entity), repeat: false);

            Log.Info("Skill", "Skill {EntityId} 释放技能！Lv.{Level} 造成 {Damage} 伤害（累计 {Total}），进入 {CD}ms 冷却",
                entity.EntityId, entity.Get<int>("Level"), damage, total + damage, CooldownMs);
        }
        else if (method == "QueryState")
        {
            Log.Info("Skill", "Skill {EntityId} 状态: Lv.{Level} 冷却={CD} 累计释放={Casts}",
                entity.EntityId, entity.Get<int>("Level"), entity.Get<int>("CooldownRemaining"), entity.Get<int>("Casts"));
        }
        else
        {
            Log.Warn("Skill", "Skill {EntityId} 未处理消息: {Method}", entity.EntityId, method);
        }
    }

    public override void OnDestroy(Entity entity)
    {
        CancelCooldownTimer(entity.EntityId);
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        // KBE-Gap-Review S4 + A1：只清空并重挂该实体的冷却定时器
        CancelCooldownTimer(entity.EntityId);
        // 修复：按剩余冷却毫秒重挂，而不是从完整 CD 重新计时
        int remainingMs = entity.Get<int>("CooldownRemaining");
        if (remainingMs > 0)
        {
            cooldownTimers[entity.EntityId] = AddTimer(entity, remainingMs, () => OnCooldownEnd(entity), repeat: false);
        }
        Log.Info("Skill", "Skill {EntityId} 脚本热更新完成", entity.EntityId);
    }

    private void CancelCooldownTimer(long entityId)
    {
        if (cooldownTimers.TryRemove(entityId, out var timer))
        {
            timer.Cancel();
        }
    }
}

return new SkillScript();
