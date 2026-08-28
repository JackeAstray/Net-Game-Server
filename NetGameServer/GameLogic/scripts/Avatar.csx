// ===== 示例游戏逻辑脚本：Avatar（玩家角色） =====
// 展示玩法脚本的基础模式：实体创建初始化 → 定时器（回血）→ 消息响应（受伤）。
// 伤害结算读取全局数据 DamageMultiplier（框架/其他脚本可调整倍率，无需改本脚本）。
// 实体属性由 EntityDef 声明（见 Battle/Entities/PlayerEntityDef.cs 的字段）。
// 本脚本绑定 Player 实体类型（EntityType => "Player"），玩家加入场景后由框架自动生效：
// 客户端通过 ScriptAction(40006) 消息调用 TakeDamage（args=[伤害]）。
// 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。
//
// KBE-Gap-Review 落地：
//   S1 结构化日志：Log.Info/Warn（基类 Log 属性）
//   S2 定时器回血：AddTimer(1000) 代替 tick % 20 轮询
//   S3 数值边界：MathClampSet/Add 钳制 Hp ∈ [0, MaxHp]
//   S4 热更新钩子：OnReload(oldState) 恢复 timer 句柄；ScriptVersion bump

using System;
using Framework.Entity;
using Framework.Scripting;
using Framework.Tick;

public class AvatarScript : EntityScriptBase
{
    public override string EntityType => "Player";
    public override int ScriptVersion => 2;

    // 内部状态字段——热更新会重置。OnReload 钩子用来显式恢复（KBE-Gap-Review S4）。
    private int damageTaken;
    private TimerHandle? healTimer;

    public override void OnCreate(Entity entity)
    {
        entity.Set("Hp", 100);
        entity.Set("MaxHp", 100);
        entity.Set("Score", 0);

        // KBE-Gap-Review S2：定时器回血代替 tick%N 轮询
        healTimer = AddTimer(entity, 1000, () => TickHeal(entity), repeat: true);

        Log.Info("Avatar", "Avatar {EntityId} 创建，Hp={Hp}", entity.EntityId, entity.Get<int>("Hp"));
    }

    private void TickHeal(Entity entity)
    {
        // KBE-Gap-Review S3：边界钳制避免 MaxHp 改变后溢出
        int hp = entity.Get<int>("Hp");
        int maxHp = entity.Get<int>("MaxHp");
        if (hp < maxHp)
        {
            int newHp = MathClampAdd(entity, "Hp", +1, 0, maxHp);
            // Serilog 内置级别短路，无需脚本额外判断
            Log.Debug("Avatar", "Avatar {EntityId} 每秒回血，Hp={Hp}", entity.EntityId, newHp);
        }
    }

    public override void OnMessage(Entity entity, string method, object?[] args)
    {
        if (method == "TakeDamage" && args.Length > 0 && args[0] is int dmg)
        {
            int multiplier = 1;
            var raw = ScriptHost.Current?.GetGlobal("DamageMultiplier");
            if (raw is int m) multiplier = m;
            // KBE-Gap-Review S3：边界钳制，Hp 不允许 < 0
            int newHp = MathClampAdd(entity, "Hp", -dmg * multiplier, 0, int.MaxValue);
            damageTaken += dmg * multiplier;
            Log.Info("Avatar", "Avatar {EntityId} 受到 {Dmg}x{MP} 伤害，Hp={Hp}", entity.EntityId, dmg, multiplier, newHp);
        }
        else
        {
            Log.Warn("Avatar", "Avatar {EntityId} 未处理消息: {Method}", entity.EntityId, method);
        }
    }

    public override void OnDestroy(Entity entity)
    {
        // 释放定时器，避免 OnDestroy 后回调触发空引用
        healTimer?.Cancel();
        healTimer = null;
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        // KBE-Gap-Review S4：热更新显式状态迁移
        // state 由宿主在重新加载前通过 SetReloadState 注入；这里从 entity 自身恢复
        damageTaken = entity.Get<int>("Score"); // 复用 Score 字段作为累计受伤
        // 重新挂载定时器（旧实例的句柄已无效）
        healTimer = AddTimer(entity, 1000, () => TickHeal(entity), repeat: true);
        Log.Info("Avatar", "Avatar {EntityId} 脚本热更新完成，damagedTaken={Taken}", entity.EntityId, damageTaken);
    }
}

return new AvatarScript();
