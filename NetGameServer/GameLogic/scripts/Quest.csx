// ===== 示例游戏逻辑脚本：Quest（任务系统） =====
// 展示脚本层的事件驱动协作（对标 KBE 属性/全局数据回调，替代轮询）：
// - Npc 死亡时把经验写入全局数据（见 Npc.csx 的 TotalExpDropped）
// - Quest 脚本通过 OnGlobalChanged 事件监听全局数据变化，达标**立即**完成任务（无需 tick 轮询）
// - 全局数据作为"脚本间总线"，各玩法脚本松耦合协作，无需互相引用
//
// 玩法：玩家击杀 Npc 累计经验达到阈值 → 任务完成 → 奖励写入全局数据（由框架/客户端读取）
// 客户端通过 ScriptAction(40006) 消息调用 QueryProgress 查询进度。

using System;
using Framework.Entity;
using Framework.Scripting;

public class QuestScript : EntityScriptBase
{
    public override string EntityType => "Quest";

    private const int ExpThreshold = 20; // 击杀 1 只 Npc（20 经验）即完成任务

    private bool completed;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        entity.Set("Hp", 1);        // Quest 实体：用 Hp 存完成状态占位
        entity.Set("Score", 0);     // Score 存当前任务进度（经验）
        entity.Set("MaxHp", ExpThreshold); // MaxHp 存任务目标
        Console.WriteLine($"[脚本] Quest {entity.EntityId} 创建，目标经验={ExpThreshold}");
    }

    /// <summary>
    /// 全局数据变更事件（框架在 ScriptHost.SetGlobal 后触发，对本类型每个实体各调用一次）：
    /// 事件驱动完成任务，替代原每 5 tick 轮询全局数据的做法。
    /// </summary>
    public override void OnGlobalChanged(Framework.Entity.Entity entity, string key, object? value)
    {
        if (completed || key != "TotalExpDropped") return;

        int exp = value is int e ? e : 0;
        entity.Set("Score", Math.Min(exp, ExpThreshold));
        if (exp < ExpThreshold) return;

        completed = true;
        entity.Set("Hp", 0); // 标记完成
        // 任务奖励：写入全局数据（框架/客户端可读取）
        Framework.Scripting.ScriptHost.Current?.SetGlobal("QuestCompleted", true);
        Console.WriteLine($"[脚本] Quest {entity.EntityId} 完成！奖励已发放（事件驱动）");
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
