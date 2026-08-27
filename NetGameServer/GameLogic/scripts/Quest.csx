// ===== 示例游戏逻辑脚本：Quest（任务系统） =====
// 展示脚本层协作能力：
// - Npc 死亡时把击杀计数写入全局数据（见 Npc.csx 的 TotalExpDropped）
// - Quest 脚本监听全局数据变化（tick 轮询），完成任务后给玩家发奖励
// - 全局数据作为"脚本间总线"，各玩法脚本松耦合协作，无需互相引用
//
// 玩法：玩家击杀 Npc 累计经验达到阈值 → 任务完成 → 奖励写入全局数据（由框架/客户端读取）

using System;
using Framework.Entity;
using Framework.Scripting;

public class QuestScript : EntityScriptBase
{
    public override string EntityType => "Quest";

    private const int ExpThreshold = 20; // 击杀 1 只 Npc（20 经验）即完成任务

    private int tickCount;
    private bool completed;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        entity.Set("Hp", 1);        // Quest 实体：用 Hp 存完成状态占位
        entity.Set("Score", 0);     // Score 存当前任务进度（经验）
        entity.Set("MaxHp", ExpThreshold); // MaxHp 存任务目标
        Console.WriteLine($"[脚本] Quest {entity.EntityId} 创建，目标经验={ExpThreshold}");
    }

    public override void OnTick(Framework.Entity.Entity entity, long frame)
    {
        if (completed) return;

        tickCount++;
        // 每 5 tick 检查一次全局数据（模拟事件驱动，脚本层无事件订阅用轮询简化）
        if (tickCount % 5 != 0) return;

        var raw = Framework.Scripting.ScriptHost.Current?.GetGlobal("TotalExpDropped");
        int exp = raw is int e ? e : 0;
        entity.Set("Score", Math.Min(exp, ExpThreshold));

        if (exp >= ExpThreshold)
        {
            completed = true;
            entity.Set("Hp", 0); // 标记完成
            // 任务奖励：写入全局数据（框架/客户端可读取）
            Framework.Scripting.ScriptHost.Current?.SetGlobal("QuestCompleted", true);
            Console.WriteLine($"[脚本] Quest {entity.EntityId} 完成！奖励已发放");
        }
    }

    public override void OnMessage(Framework.Entity.Entity entity, string method, object?[] args)
    {
        if (method == "QueryProgress")
        {
            Console.WriteLine($"[脚本] Quest {entity.EntityId} 进度: {entity.Get<int>("Score")}/{entity.Get<int>("MaxHp")}");
        }
    }
}

return new QuestScript();
