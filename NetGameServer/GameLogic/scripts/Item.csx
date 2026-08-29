// ===== 示例游戏逻辑脚本：Item（物品系统） =====
// 展示玩法脚本的典型物品机制（全部写在 .csx 里，框架零改动，保存即热更新）：
// - 拾取：OnMessage("Pickup", itemId, count) 物品堆叠计数
// - 使用：OnMessage("UseItem") 消耗物品并回复生命（治疗量写入全局数据）
// - 掉落：定时器周期"自动掉落"（模拟怪物掉宝），与 Npc 掉落经验形成对照
// - 全局数据：ItemTotalPicked / ItemHealedTotal / ItemAutoDrops 供统计/任务类脚本消费
//
// KBE-Gap-Review 落地：S1 结构化日志 + S2 定时器 + S3 边界 + S4 热更新钩子

using System;
using Framework.Entity;
using Framework.Scripting;
using Framework.Tick;

public class ItemScript : EntityScriptBase
{
    public override string EntityType => "Item";
    public override int ScriptVersion => 2;

    private const int HealPerItem = 10;
    private const int AutoDropIntervalMs = 1500; // 1.5s 自动掉落 1 个
    private const int MaxStack = 99;             // 背包上限（KBE-Gap-Review S3）

    private int pickedTotal;
    private TimerHandle? autoDropTimer;

    public override void OnCreate(Entity entity)
    {
        entity.Set("ItemId", 1);
        entity.Set("Count", 0);

        // KBE-Gap-Review S2：定时器掉落代替 tick%N 轮询
        autoDropTimer = AddTimer(entity, AutoDropIntervalMs, () => TickAutoDrop(entity), repeat: true);

        Log.Info("Item", "Item {EntityId} 创建，ItemId=1 背包空", entity.EntityId);
    }

    private void TickAutoDrop(Entity entity)
    {
        int old = entity.Get<int>("Count");
        if (old >= MaxStack)
        {
            Log.Debug("Item", "Item {EntityId} 背包已满（{Count}/{Max}），停止掉落", entity.EntityId, old, MaxStack);
            return;
        }
        MathClampAdd(entity, "Count", +1, 0, MaxStack);
        var drops = ScriptHost.Current?.GetGlobal("ItemAutoDrops");
        int total = drops is int d ? d : 0;
        ScriptHost.Current?.SetGlobal("ItemAutoDrops", total + 1);
        Log.Debug("Item", "Item {EntityId} 自动掉落 1 个物品，背包={Count}", entity.EntityId, entity.Get<int>("Count"));
    }

    public override void OnMessage(Entity entity, string method, object?[] args)
    {
        if (method == "Pickup" && args.Length >= 2 && args[0] is int itemId && args[1] is int itemCount)
        {
            if (itemCount <= 0)
            {
                Log.Warn("Item", "Item {EntityId} 拾取数量无效: {Count}", entity.EntityId, itemCount);
                return;
            }
            int old = entity.Get<int>("Count");
            if (old >= MaxStack)
            {
                Log.Warn("Item", "Item {EntityId} 背包已满（{Old}/{Max}），拾取 {Add} 失败", entity.EntityId, old, MaxStack, itemCount);
                return;
            }
            entity.Set("ItemId", itemId);
            int newCount = MathClampAdd(entity, "Count", itemCount, 0, MaxStack);
            pickedTotal += itemCount;
            var pickedRaw = ScriptHost.Current?.GetGlobal("ItemTotalPicked");
            int picked = pickedRaw is int p ? p : 0;
            ScriptHost.Current?.SetGlobal("ItemTotalPicked", picked + itemCount);
            Log.Info("Item", "Item {EntityId} 拾取 {ItemId}x{Count}，背包={NewCount}，累计拾取={Total}",
                entity.EntityId, itemId, itemCount, newCount, picked + itemCount);
        }
        else if (method == "UseItem")
        {
            int count = entity.Get<int>("Count");
            if (count <= 0)
            {
                Log.Warn("Item", "Item {EntityId} 背包为空，无法使用", entity.EntityId);
                return;
            }
            MathClampAdd(entity, "Count", -1, 0, MaxStack);
            var healedRaw = ScriptHost.Current?.GetGlobal("ItemHealedTotal");
            int healed = healedRaw is int h ? h : 0;
            ScriptHost.Current?.SetGlobal("ItemHealedTotal", healed + HealPerItem);
            Log.Info("Item", "Item {EntityId} 使用物品回复 {Heal} 生命，剩余={Remaining}，累计治疗={Total}",
                entity.EntityId, HealPerItem, entity.Get<int>("Count"), healed + HealPerItem);
        }
        else
        {
            Log.Warn("Item", "Item {EntityId} 未处理消息: {Method}", entity.EntityId, method);
        }
    }

    public override void OnDestroy(Entity entity)
    {
        autoDropTimer?.Cancel();
        autoDropTimer = null;
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        // KBE-Gap-Review S4：热更新后恢复状态 + 重新挂定时器
        // 安全修复（P1）：先取消旧实例的定时器句柄，避免热更新后 repeat 定时器叠加导致掉落速率随热更次数线性放大
        autoDropTimer?.Cancel();
        autoDropTimer = null;
        pickedTotal = entity.Get<int>("Count");
        autoDropTimer = AddTimer(entity, AutoDropIntervalMs, () => TickAutoDrop(entity), repeat: true);
        Log.Info("Item", "Item {EntityId} 脚本热更新完成", entity.EntityId);
    }
}

return new ItemScript();
