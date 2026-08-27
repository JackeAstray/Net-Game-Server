// ===== 示例游戏逻辑脚本：Avatar（玩家角色） =====
// 说明：这是"游戏逻辑层"，与底层框架物理分离。
// 修改本文件后无需重新编译框架——ScriptHost 检测到变更会自动热更新。
// 实体属性由 EntityDef 声明（见 Battle/Entities/PlayerEntityDef.cs 的字段）。

using System;
using Framework.Entity;
using Framework.Scripting;

public class AvatarScript : EntityScriptBase
{
    public override string EntityType => "Avatar";

    private int tickCount;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        entity.Set("Hp", 100);
        entity.Set("MaxHp", 100);
        entity.Set("Score", 0);
        Console.WriteLine($"[脚本] Avatar {entity.EntityId} 创建，Hp={entity.Get<int>("Hp")}");
    }

    public override void OnTick(Framework.Entity.Entity entity, long frame)
    {
        tickCount++;
        // 每 20 tick（约 1 秒 @20Hz）回复 1 点生命
        if (tickCount % 20 == 0)
        {
            int hp = entity.Get<int>("Hp");
            int maxHp = entity.Get<int>("MaxHp");
            if (hp < maxHp)
            {
                entity.Set("Hp", hp + 1);
                Console.WriteLine($"[脚本] Avatar {entity.EntityId} 每帧回复，Hp={entity.Get<int>("Hp")}");
            }
        }
    }

    public override void OnMessage(Framework.Entity.Entity entity, string method, object?[] args)
    {
        if (method == "TakeDamage" && args.Length > 0 && args[0] is int dmg)
        {
            // 从全局数据读取伤害倍率（框架/其他脚本可调整，无需改本脚本）
            // 注：通过 ScriptHost.Current 静态访问全局数据（脚本类内无法直接访问 globals 参数）
            int multiplier = 1;
            var raw = Framework.Scripting.ScriptHost.Current?.GetGlobal("DamageMultiplier");
            if (raw is int m) multiplier = m;
            int hp = entity.Get<int>("Hp") - dmg * multiplier;
            entity.Set("Hp", Math.Max(0, hp));
            Console.WriteLine($"[脚本] Avatar {entity.EntityId} 受到 {dmg}x{multiplier} 伤害，Hp={entity.Get<int>("Hp")}");
        }
        else
        {
            Console.WriteLine($"[脚本] Avatar {entity.EntityId} 未处理消息: {method}");
        }
    }
}

return new AvatarScript();
