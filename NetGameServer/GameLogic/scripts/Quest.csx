// ===== 示例游戏逻辑脚本：Quest（任务系统） =====
// 展示脚本层的事件驱动协作（对标 KBE 属性/全局数据回调，替代轮询）：
// - Npc 死亡时把经验写入全局数据（见 Npc.csx 的 TotalExpDropped）
// - Quest 脚本通过 OnGlobalChanged 事件监听全局数据变化，达标**立即**完成任务（无需 tick 轮询）
// - 全局数据作为"脚本间总线"，各玩法脚本松耦合协作，无需互相引用
//
// 玩法：玩家击杀 Npc 累计经验达到阈值 → 任务完成 → 奖励写入全局数据（由框架/客户端读取）
// 客户端通过 ScriptAction(40006) 消息调用 QueryProgress 查询进度。
//
// A1 修复：脚本宿主按类型只实例化一次，完成标记必须按 entity.EntityId 键控
// （此前用实例字段会让一个任务完成后所有任务实体都被判定完成）。
//
// KBE-Gap-Review 落地：S1 结构化日志 + S3 边界 + S4 热更新（Quest 本身已是事件驱动，保留）

using System;
using System.Collections.Concurrent;
using Framework.Entity;
using Framework.Scripting;

public class QuestScript : EntityScriptBase
{
    public override string EntityType => "Quest";
    public override int ScriptVersion => 3;

    private const int ExpThreshold = 20;
    private const int MaxExp = int.MaxValue; // KBE-Gap-Review S3：经验上限

    // 每实体完成标记（A1 修复：按 EntityId 键控）
    private readonly ConcurrentDictionary<long, bool> completedByEntity = new();

    /// <summary>全局数据键按场景/房间命名空间（防跨房间污染）：只响应本场景的经验掉落。
    /// 有 SceneId 用 "Key:SceneId"，无则退回裸键（测试/无场景实体）。</summary>
    private static string SceneScope(Entity entity, string baseKey)
    {
        string sceneId = entity.Get<string>("SceneId") ?? string.Empty;
        return string.IsNullOrEmpty(sceneId) ? baseKey : baseKey + ":" + sceneId;
    }

    public override void OnCreate(Entity entity)
    {
        entity.Set("Hp", 1);
        entity.Set("Score", 0);
        entity.Set("MaxHp", ExpThreshold);
        completedByEntity[entity.EntityId] = false;
        Log.Info("Quest", "Quest {EntityId} 创建，目标经验={Threshold}（作用域 {Scope}）", entity.EntityId, ExpThreshold, SceneScope(entity, "TotalExpDropped"));
    }

    /// <summary>
    /// 全局数据变更事件（KBE 风格回调，事件驱动，不轮询）。
    /// </summary>
    public override void OnGlobalChanged(Entity entity, string key, object? value)
    {
        bool completed = completedByEntity.TryGetValue(entity.EntityId, out var c) && c;
        // 只响应本实体所在场景/房间的经验掉落键（防任一玩家击杀 NPC 完成全节点其他房间任务）
        if (completed || key != SceneScope(entity, "TotalExpDropped")) return;

        int exp = value is int e ? e : 0;
        // KBE-Gap-Review S3：边界钳制
        int clampedExp = Math.Clamp(exp, 0, MaxExp);
        MathClampSet(entity, "Score", Math.Min(clampedExp, ExpThreshold), 0, ExpThreshold);
        if (exp < ExpThreshold) return;

        completedByEntity[entity.EntityId] = true;
        entity.Set("Hp", 0);
        ScriptHost.Current?.SetGlobal(SceneScope(entity, "QuestCompleted"), true);
        Log.Info("Quest", "Quest {EntityId} 完成！奖励已发放（事件驱动，作用域 {Scope}）", entity.EntityId, SceneScope(entity, "QuestCompleted"));
    }

    public override void OnMessage(Entity entity, string method, object?[] args)
    {
        if (method == "QueryProgress")
        {
            Log.Info("Quest", "Quest {EntityId} 进度: {Score}/{Max}",
                entity.EntityId, entity.Get<int>("Score"), entity.Get<int>("MaxHp"));
        }
    }

    public override void OnReload(Entity entity, object? oldState)
    {
        // KBE-Gap-Review S4 + A1：热更新后该实体 completed 状态保留（如未完成则重置）
        bool completed = completedByEntity.TryGetValue(entity.EntityId, out var c) && c;
        if (!completed) entity.Set("Hp", 1);
        Log.Info("Quest", "Quest {EntityId} 脚本热更新完成，completed={Done}", entity.EntityId, completed);
    }
}

return new QuestScript();
