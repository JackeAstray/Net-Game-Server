// ===== 示例游戏逻辑脚本：Npc（野怪/怪物） =====
// 展示多脚本共存：与 Avatar.csx 同时加载，绑定不同实体类型。
// NPC 逻辑：出生时随机坐标 → 定时器巡逻（正弦移动）→ 受击死亡掉落经验。
// 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。
//
// A1 修复：脚本宿主按类型只实例化一次，因此每实体的随机种子/基座坐标/巡逻定时器
// 一律按 entity.EntityId 键控存储（此前用实例字段会让所有 NPC 共用最后一个的 baseX）。
//
// KBE-Gap-Review 落地：S1/S2/S3/S4

using System;
using System.Collections.Concurrent;
using Framework.Entity;
using Framework.Scripting;
using Framework.Tick;

public class NpcScript : EntityScriptBase
{
    public override string EntityType => "Npc";
    public override int ScriptVersion => 2;

    private const int PatrolIntervalMs = 500; // 0.5s 巡逻一次
    private const int MaxHp = 9999;           // KBE-Gap-Review S3：Hp 上限

    // 每实体状态（A1 修复：按 EntityId 键控）
    private readonly ConcurrentDictionary<long, Random> randoms = new();
    private readonly ConcurrentDictionary<long, float> baseXs = new();
    private readonly ConcurrentDictionary<long, TimerHandle> patrolTimers = new();

    public override void OnCreate(Entity entity)
    {
        var random = new Random((int)(entity.EntityId & 0x7FFFFFFF));
        randoms[entity.EntityId] = random;
        float baseX = random.Next(-100, 100);
        baseXs[entity.EntityId] = baseX;
        entity.Set("Hp", 50);
        entity.Set("MaxHp", 50);
        entity.Set("Score", 0);
        entity.Set("Position", new Float3(baseX, 0, random.Next(-100, 100)));
        // KBE-Gap-Review S4：isDead 跟随 entity（每实例独立），原版用实例字段共享导致后续 Npc 跳过 SetGlobal
        entity.SetSilent("IsDead", false);
        var pos = entity.Get<Float3>("Position");
        Log.Info("Npc", "Npc {EntityId} 出生，Hp=50 Pos=({X:F0}, 0, {Z:F0})",
            entity.EntityId, pos.X, pos.Z);

        patrolTimers[entity.EntityId] = AddTimer(entity, PatrolIntervalMs, () => TickPatrol(entity, 0), repeat: true);
    }

    private void TickPatrol(Entity entity, long frame)
    {
        if (entity.Get<bool>("IsDead")) return;
        var pos = entity.Get<Float3>("Position");
        // 注：原版基于 frame 计算正弦偏移；改为基于实时 tick（ElapseMs）更稳定
        // 保留 frame 参数仅为兼容接口
        float baseX = baseXs.TryGetValue(entity.EntityId, out var bx) ? bx : 0f;
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
        // 仅取消并移除该实体的定时器（A1 修复）
        if (patrolTimers.TryRemove(entity.EntityId, out var timer))
        {
            timer.Cancel();
        }
        randoms.TryRemove(entity.EntityId, out _);
        baseXs.TryRemove(entity.EntityId, out _);
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        // KBE-Gap-Review S4 + A1：只重挂该实体的巡逻定时器
        if (patrolTimers.TryRemove(entity.EntityId, out var oldTimer))
        {
            oldTimer.Cancel();
        }
        if (!entity.Get<bool>("IsDead"))
        {
            patrolTimers[entity.EntityId] = AddTimer(entity, PatrolIntervalMs, () => TickPatrol(entity, 0), repeat: true);
        }
        Log.Info("Npc", "Npc {EntityId} 脚本热更新完成，isDead={Dead}", entity.EntityId, entity.Get<bool>("IsDead"));
    }
}

return new NpcScript();
