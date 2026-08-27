// ===== 示例游戏逻辑脚本：Npc（野怪/怪物） =====
// 展示多脚本共存：与 Avatar.csx 同时加载，绑定不同实体类型。
// NPC 逻辑：出生时随机坐标 → 每 tick 巡逻（正弦移动）→ 受击死亡掉落经验。
// 所有逻辑只写在这一个 .csx 里，框架零改动，保存即热更新。

using System;
using Framework.Entity;
using Framework.Scripting;

public class NpcScript : EntityScriptBase
{
    public override string EntityType => "Npc";

    private Random random = new(42);
    private int tickCount;
    private float baseX;
    private bool isDead;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        // 按实体 ID 派生随机种子：避免所有 NPC 出生坐标完全一致（固定种子问题）
        random = new Random((int)(entity.EntityId & 0x7FFFFFFF));
        baseX = random.Next(-100, 100);
        entity.Set("Hp", 50);
        entity.Set("MaxHp", 50);
        entity.Set("Score", 0); // 击杀奖励经验
        entity.Set("Position", new Framework.Entity.Float3(baseX, 0, random.Next(-100, 100)));
        Console.WriteLine($"[脚本] Npc {entity.EntityId} 出生，Hp=50 Pos=({entity.Get<Framework.Entity.Float3>("Position").X:F0}, 0, {entity.Get<Framework.Entity.Float3>("Position").Z:F0})");
    }

    public override void OnTick(Framework.Entity.Entity entity, long frame)
    {
        if (isDead) return;

        tickCount++;
        // 每 10 tick 巡逻：沿 X 轴正弦移动（简单 AI 示例）
        if (tickCount % 10 == 0)
        {
            var pos = entity.Get<Framework.Entity.Float3>("Position");
            float newX = baseX + (float)Math.Sin(frame / 20.0) * 30;
            entity.Set("Position", new Framework.Entity.Float3(newX, 0, pos.Z));
            // Position 是同步属性 → 脏标记 → 自动增量广播给视野内玩家
        }
    }

    public override void OnMessage(Framework.Entity.Entity entity, string method, object?[] args)
    {
        if (method == "TakeDamage" && args.Length > 0 && args[0] is int dmg)
        {
            int hp = entity.Get<int>("Hp") - dmg;
            entity.Set("Hp", Math.Max(0, hp));
            Console.WriteLine($"[脚本] Npc {entity.EntityId} 受到 {dmg} 伤害，Hp={entity.Get<int>("Hp")}");

            if (entity.Get<int>("Hp") <= 0)
            {
                isDead = true;
                // 掉落经验写入全局数据（其他脚本/系统可读取）
                var raw = Framework.Scripting.ScriptHost.Current?.GetGlobal("TotalExpDropped");
                int total = raw is int t ? t : 0;
                Framework.Scripting.ScriptHost.Current?.SetGlobal("TotalExpDropped", total + 20);
                Console.WriteLine($"[脚本] Npc {entity.EntityId} 死亡，累计掉落经验={total + 20}");
                // 通知框架销毁实体（离开场景）
                entity.Set("Hp", 0);
            }
        }
    }
}

return new NpcScript();
