using System.Collections.Concurrent;
using Shared;

namespace Center.Handlers;

/// <summary>
/// 实体位置服务（迭代 21，对标 ET Location 代理）：
/// 维护 entityId → 所在节点 nodeId 的实时注册表，供跨节点实体调用（EntityCall）解决"迁移后路由变旧"问题。
/// - Register / Unregister：Battle 在实体生成/绑定/迁移完成/离开/销毁时上报
/// - Locate：调用方不知道（或缓存失效）实体在哪个节点时向 Center 查询
/// - TTL + Sweep：长时间未刷新的条目视为过期清理（防迁移异常导致的位置泄漏）
/// 协议：91007 登记 / 91008 注销 / 91009 查询 / 91010 响应（见 CenterMessages.cs）。
/// </summary>
public sealed class EntityLocationService
{
    /// <summary>全局单例（Center 进程内）。</summary>
    public static EntityLocationService Instance { get; } = new();

    /// <summary>位置条目默认 TTL（无刷新则过期；正常生命周期会显式注销，TTL 只是安全网）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<long, LocationEntry> locations = new();

    public int Count => locations.Count;

    private sealed class LocationEntry
    {
        public required string NodeId;
        public long UpdatedTicks;
    }

    /// <summary>登记（覆盖）。</summary>
    public void Register(long entityId, string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return;
        }
        locations[entityId] = new LocationEntry { NodeId = nodeId, UpdatedTicks = DateTime.UtcNow.Ticks };
        Shared.Log.Debug("实体位置登记 EntityId:{EntityId} -> {NodeId}", entityId, nodeId);
    }

    /// <summary>注销（不存在时静默成功）。</summary>
    public bool Unregister(long entityId) => locations.TryRemove(entityId, out _);

    /// <summary>查询当前所在节点；不存在/已过期返回 null。</summary>
    public string? Locate(long entityId)
    {
        if (!locations.TryGetValue(entityId, out var entry))
        {
            return null;
        }
        if (DateTime.UtcNow.Ticks - entry.UpdatedTicks > DefaultTtl.Ticks)
        {
            locations.TryRemove(entityId, out _);
            return null;
        }
        return entry.NodeId;
    }

    /// <summary>批量查询（迁移恢复/批量寻址用）。</summary>
    public IEnumerable<(long EntityId, string NodeId)> LocateMany(IEnumerable<long> entityIds)
    {
        foreach (var id in entityIds)
        {
            var node = Locate(id);
            if (node != null)
            {
                yield return (id, node);
            }
        }
    }

    /// <summary>清扫过期条目（Center 周期调用），返回清理数。</summary>
    public int SweepExpired(DateTime now)
    {
        int removed = 0;
        long cutoff = now.Ticks - DefaultTtl.Ticks;
        foreach (var kv in locations.ToArray())
        {
            if (kv.Value.UpdatedTicks < cutoff && locations.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }
        if (removed > 0)
        {
            Shared.Log.Warning($"实体位置服务清扫过期条目 {removed} 个（当前总数 {locations.Count}）");
        }
        return removed;
    }
}
