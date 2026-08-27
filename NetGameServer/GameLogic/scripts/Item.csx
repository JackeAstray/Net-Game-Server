// ===== 示例游戏逻辑脚本：Item（物品系统） =====
// 展示玩法脚本的典型物品机制（全部写在 .csx 里，框架零改动，保存即热更新）：
// - 拾取：OnMessage("Pickup", itemId, count) 物品堆叠计数
// - 使用：OnMessage("UseItem") 消耗物品并回复生命（治疗量写入全局数据）
// - 掉落：OnTick 周期性"自动掉落"（模拟怪物掉宝），与 Npc 掉落经验形成对照
// - 全局数据：ItemTotalPicked / ItemHealedTotal / ItemAutoDrops 供统计/任务类脚本消费

using System;
using Framework.Entity;
using Framework.Scripting;

public class ItemScript : EntityScriptBase
{
    public override string EntityType => "Item";

    private const int HealPerItem = 10;   // 每个物品回复 10 点生命
    private const int AutoDropTicks = 30; // 每 30 tick 自动掉落 1 个物品（模拟掉宝）

    private int tickCount;

    public override void OnCreate(Framework.Entity.Entity entity)
    {
        entity.Set("ItemId", 1);
        entity.Set("Count", 0);
        Console.WriteLine($"[脚本] Item {entity.EntityId} 创建，ItemId=1 背包空");
    }

    public override void OnTick(Framework.Entity.Entity entity, long frame)
    {
        tickCount++;
        // 周期性"怪物掉宝"：背包自动增加物品（模拟 Npc 死亡掉落被拾取）
        if (tickCount % AutoDropTicks == 0)
        {
            entity.Set("Count", entity.Get<int>("Count") + 1);
            var dropsRaw = Framework.Scripting.ScriptHost.Current?.GetGlobal("ItemAutoDrops");
            int drops = dropsRaw is int d ? d : 0;
            Framework.Scripting.ScriptHost.Current?.SetGlobal("ItemAutoDrops", drops + 1);
            Console.WriteLine($"[脚本] Item {entity.EntityId} 自动掉落 1 个物品，背包={entity.Get<int>("Count")}");
        }
    }

    public override void OnMessage(Framework.Entity.Entity entity, string method, object?[] args)
    {
        if (method == "Pickup" && args.Length >= 2 && args[0] is int itemId && args[1] is int itemCount)
        {
            if (itemCount <= 0)
            {
                Console.WriteLine($"[脚本] Item {entity.EntityId} 拾取数量无效: {itemCount}");
                return;
            }

            entity.Set("ItemId", itemId);
            entity.Set("Count", entity.Get<int>("Count") + itemCount);
            var pickedRaw = Framework.Scripting.ScriptHost.Current?.GetGlobal("ItemTotalPicked");
            int picked = pickedRaw is int p ? p : 0;
            Framework.Scripting.ScriptHost.Current?.SetGlobal("ItemTotalPicked", picked + itemCount);
            Console.WriteLine($"[脚本] Item {entity.EntityId} 拾取 {itemId}×{itemCount}，背包={entity.Get<int>("Count")}，累计拾取={picked + itemCount}");
        }
        else if (method == "UseItem")
        {
            int count = entity.Get<int>("Count");
            if (count <= 0)
            {
                Console.WriteLine($"[脚本] Item {entity.EntityId} 背包为空，无法使用");
                return;
            }

            entity.Set("Count", count - 1);
            var healedRaw = Framework.Scripting.ScriptHost.Current?.GetGlobal("ItemHealedTotal");
            int healed = healedRaw is int h ? h : 0;
            Framework.Scripting.ScriptHost.Current?.SetGlobal("ItemHealedTotal", healed + HealPerItem);
            Console.WriteLine($"[脚本] Item {entity.EntityId} 使用物品回复 {HealPerItem} 生命，剩余={entity.Get<int>("Count")}，累计治疗={healed + HealPerItem}");
        }
        else
        {
            Console.WriteLine($"[脚本] Item {entity.EntityId} 未处理消息: {method}");
        }
    }
}

return new ItemScript();
