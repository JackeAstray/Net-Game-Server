// ===== 示例游戏逻辑脚本：Npc（野怪/怪物） =====
// 展示多脚本共存：与 Avatar.csx 同时加载，绑定不同实体类型。
// NPC 逻辑：出生时随机坐标 → 定时器巡逻（正弦移动）→ 受击死亡掉落经验。
// 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。
//
// KBE-Gap-Review 落地：S1/S2/S3/S4

using System;
using Framework.Entity;
using Framework.Scripting;
using Framework.Tick;

public class NpcScript : EntityScriptBase
{
    public override string EntityType => "Npc";
    public override int ScriptVersion => 2;

    private const int PatrolIntervalMs = 500; // 0.5s 巡逻一次
    private const int MaxHp = 9999;           // KBE-Gap-Review S3：Hp 上限

    private Random random = new(42);
    private float baseX;
    private TimerHandle? patrolTimer;

    public override void OnCreate(Entity entity)
    {
        random = new Random((int)(entity.EntityId & 0x7FFFFFFF));
        baseX = random.Next(-100, 100);
        entity.Set("Hp", 50);
        entity.Set("MaxHp", 50);
        entity.Set("Score", 0);
        entity.Set("Position", new Float3(baseX, 0, random.Next(-100, 100)));
        // KBE-Gap-Review S4：isDead 跟随 entity（每实例独立），原版用实例字段共享导致后续 Npc 跳过 SetGlobal
        entity.SetSilent("IsDead", false);
        var pos = entity.Get<Float3>("Position");
        Log.Info("Npc", "Npc {EntityId} 出生，Hp=50 Pos=({X:F0}, 0, {Z:F0})",
            entity.EntityId, pos.X, pos.Z);

        patrolTimer = AddTimer(entity, PatrolIntervalMs, () => TickPatrol(entity, 0), repeat: true);
    }

    private void TickPatrol(Entity entity, long frame)
    {
        if (entity.Get<bool>("IsDead")) return;
        var pos = entity.Get<Float3>("Position");
        // 注：原版基于 frame 计算正弦偏移；改为基于实时 tick（ElapseMs）更稳定
        // 保留 frame 参数仅为兼容接口
        float newX = baseX + (float)Math.Sin(Environment.TickCount64 / 1000.0) * 30;
        entity.Set("Position", new Float3(newX, 0, pos.Z));
    }

    public override void OnMessage(Entity entity, string method, object?[] args)
    {
        if (method == "TakeDamage" && args.Length > 0 && args[0] is int dmg)
        {
            int newHp = MathClampAdd(entity, "Hp", -dmg, 0, MaxHp);
            Log.Info("Npc", "Npc {EntityId} 受到 {Dmg} 伤害，Hp={Hp}", entity.EntityId, dmg, newHp);

            if (newHp <= 0 && !entity.Get<bool>("IsDead"))
            {
                entity.Set("IsDead", true);
                var raw = ScriptHost.Current?.GetGlobal("TotalExpDropped");
                int total = raw is int t ? t : 0;
                ScriptHost.Current?.SetGlobal("TotalExpDropped", total + 20);
                Log.Info("Npc", "Npc {EntityId} 死亡，累计掉落经验={Total}", entity.EntityId, total + 20);
                entity.Set("Hp", 0);
            }
        }
    }

    public override void OnDestroy(Entity entity)
    {
        patrolTimer?.Cancel();
        patrolTimer = null;
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        patrolTimer?.Cancel();
        if (!entity.Get<bool>("IsDead"))
        {
            patrolTimer = AddTimer(entity, PatrolIntervalMs, () => TickPatrol(entity, 0), repeat: true);
        }
        Log.Info("Npc", "Npc {EntityId} 脚本热更新完成，isDead={Dead}", entity.EntityId, entity.Get<bool>("IsDead"));
    }
}

return new NpcScript();
