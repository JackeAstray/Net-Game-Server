using System;
using System.Collections.Concurrent;
using System.Linq;
using Shared;

namespace Center.Handlers
{
    public class ServerNodeInfo
    {
        public string NodeId { get; set; } = string.Empty;
        public string NodeType { get; set; } = string.Empty; // "Battle", "Game", "Gateway"
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int CurrentLoad { get; set; }
        // ===== Machine 注入字段（KBE machine 化，迭代 20）=====
        public string InstanceId { get; set; } = string.Empty;
        public string MachineId { get; set; } = string.Empty;
        public string SupervisedBy { get; set; } = string.Empty;
        public Network.ISession Session { get; set; } = null!;
        public DateTime LastHeartbeat { get; set; }
    }

    public class NodeSnapshot
    {
        public string NodeId { get; set; } = string.Empty;
        public string NodeType { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int CurrentLoad { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string MachineId { get; set; } = string.Empty;
        public string SupervisedBy { get; set; } = string.Empty;
        public DateTime LastHeartbeat { get; set; }
        public bool IsConnected { get; set; }
    }

    public class NodeManager
    {
        private static readonly Lazy<NodeManager> instance = new(() => new NodeManager());
        public static NodeManager Instance => instance.Value;

        // NodeId => NodeInfo
        private readonly ConcurrentDictionary<string, ServerNodeInfo> nodes = new();
        // ClientSessionId => GatewaySession（用于将 Center 的回包精确路由到正确网关）
        private readonly ConcurrentDictionary<long, Network.ISession> clientGatewayRoutes = new();

        /// <summary>
        /// 注册节点信息，包含节点ID、类型、地址、端口和会话对象。如果节点已存在，则更新其会话和心跳时间。
        /// </summary>
        /// <param name="nodeId">节点ID。</param>
        /// <param name="nodeType">节点类型，如 "Battle"、"Game"、"Gateway"。</param>
        /// <param name="host">节点主机地址。</param>
        /// <param name="port">节点端口号。</param>
        /// <param name="session">节点会话对象。</param>
        /// <param name="instanceId">实例 ID（machine 注入，同类型多实例时由 machine 分配；可空）。</param>
        /// <param name="machineId">托管本节点的 Machine 进程 ID（可空）。</param>
        /// <param name="supervisedBy">托管方类型："machine" / "supervisor" / "none" / 自定义（可空）。</param>
        public void RegisterNode(string nodeId, string nodeType, string host, int port, Network.ISession session,
            string instanceId = "", string machineId = "", string supervisedBy = "")
        {
            var info = new ServerNodeInfo
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                Session = session,
                CurrentLoad = 0,
                InstanceId = instanceId,
                MachineId = machineId,
                SupervisedBy = supervisedBy,
                LastHeartbeat = DateTime.UtcNow
            };

            nodes.AddOrUpdate(nodeId, info, (_, oldInfo) =>
            {
                oldInfo.Session = session;
                oldInfo.LastHeartbeat = DateTime.UtcNow;
                oldInfo.Host = host;
                oldInfo.Port = port;
                oldInfo.InstanceId = instanceId;
                oldInfo.MachineId = machineId;
                oldInfo.SupervisedBy = supervisedBy;
                return oldInfo;
            });

            Log.Info($"节点已注册: [{nodeType}] {nodeId} at {host}:{port} (machine={machineId}, instance={instanceId}, supervisedBy={supervisedBy})");
        }

        /// <summary>
        /// 根据节点会话移除节点信息。如果节点不存在，则忽略该操作。
        /// </summary>
        /// <param name="session">要移除的节点的会话对象。不能为空。</param>
        public void RemoveNodeBySession(Network.ISession session)
        {
            var node = nodes.Values.FirstOrDefault(n => n.Session == session);
            if (node != null)
            {
                nodes.TryRemove(node.NodeId, out _);
                Log.Info($"节点已 断开连接 / 已删除: [{node.NodeType}] {node.NodeId}");
            }

            RemoveClientRoutesByGatewaySession(session);
        }

        /// <summary>
        /// 更新指定节点的当前负载和心跳时间。如果节点不存在，则忽略该更新。
        /// </summary>
        /// <param name="nodeId">要更新的节点的唯一标识符。不能为空。</param>
        /// <param name="load">节点的当前负载值。</param>
        public void UpdateLoad(string nodeId, int load)
        {
            if (nodes.TryGetValue(nodeId, out var node))
            {
                node.CurrentLoad = load;
                node.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                Log.Warning($"更新节点负载失败，节点不存在: {nodeId} Load:{load}");
            }
        }

        /// <summary>
        /// 将指定的客户端会话标识符绑定到网关会话。
        /// </summary>
        /// <remarks>若已存在相同的客户端会话标识符映射，则会被新的网关会话覆盖。</remarks>
        /// <param name="clientSessionId">要绑定的客户端会话标识符；小于或等于 0 时忽略绑定。</param>
        /// <param name="gatewaySession">要与客户端会话绑定的网关会话。</param>
        public void BindClientGatewayRoute(long clientSessionId, Network.ISession gatewaySession)
        {
            if (clientSessionId <= 0)
            {
                Log.Warning("绑定客户端网关路由失败：clientSessionId 无效。");
                return;
            }

            clientGatewayRoutes[clientSessionId] = gatewaySession;
        }

        /// <summary>
        /// 尝试通过客户端会话 ID 获取对应的网关会话。
        /// </summary>
        /// <remarks>基于内部映射 clientGatewayRoutes 进行查找。</remarks>
        /// <param name="clientSessionId">要查找的客户端会话 ID。</param>
        /// <param name="gatewaySession">当返回 true 时包含匹配的网关会话；否则为 null。</param>
        /// <returns>找到匹配的网关会话时返回 true，否则返回 false。</returns>
        public bool TryGetGatewaySessionByClientSessionId(long clientSessionId, out Network.ISession gatewaySession)
        {
            return clientGatewayRoutes.TryGetValue(clientSessionId, out gatewaySession!);
        }

        /// <summary>
        /// 从 clientGatewayRoutes 字典中移除与指定网关会话关联的所有客户端路由。
        /// </summary>
        /// <remarks>枚举 clientGatewayRoutes 并对匹配的键调用
        /// TryRemove。若同一会话关联多个键，则全部移除。枚举为快照，可能不会反映并发修改。</remarks>
        /// <param name="gatewaySession">要移除其关联路由的网关会话。</param>
        private void RemoveClientRoutesByGatewaySession(Network.ISession gatewaySession)
        {
            foreach (var route in clientGatewayRoutes)
            {
                if (route.Value == gatewaySession)
                {
                    clientGatewayRoutes.TryRemove(route.Key, out _);
                }
            }
        }

        /// <summary>
        /// 获取最佳 Battle 节点（平滑加权轮询，对标 KBE cellappmgr 负载均衡 / Nginx SWRR）：
        /// - 权重 = LoadWeightCeiling - CurrentLoad（负载越低权重越高，最低为 1）
        /// - 心跳过期（超过 NodeHeartbeatStaleThreshold）的节点剔除（过期负载惩罚，避免把流量发给负载数据陈旧的节点）
        /// - 每次选择累加各节点权重取当前权重最大者，选中节点减去总权重 → 平滑分布且持续偏向低负载节点
        /// 无新鲜候选时回退到“已连接 + 负载最低”的传统选择。
        /// </summary>
        /// <returns>选中的 Battle 节点 ID；无可用节点返回 null。</returns>
        public string? GetBestBattleNode()
        {
            var now = DateTime.UtcNow;
            var candidates = nodes.Values
                .Where(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase)
                            && n.Session.IsConnected
                            && now - n.LastHeartbeat <= NodeHeartbeatStaleThreshold)
                .ToList();

            if (candidates.Count == 0)
            {
                // 回退：所有候选心跳都过期时，仍按“已连接 + 负载最低”兜底（保留旧行为）
                var fallback = nodes.Values
                    .Where(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase) && n.Session.IsConnected)
                    .OrderBy(n => n.CurrentLoad)
                    .FirstOrDefault();
                return fallback?.NodeId;
            }

            int totalWeight = 0;
            string? best = null;
            int bestWeight = int.MinValue;

            foreach (var node in candidates)
            {
                int weight = Math.Max(1, LoadWeightCeiling - node.CurrentLoad);
                int current = smoothWeights.AddOrUpdate(node.NodeId, weight, (_, w) => w + weight);
                totalWeight += weight;
                if (current > bestWeight)
                {
                    bestWeight = current;
                    best = node.NodeId;
                }
            }

            if (best != null)
            {
                smoothWeights[best] -= totalWeight;
            }

            // 周期性清理已下线/过期节点残留的平滑权重（防字典无限增长）
            if (++selectCount % 32 == 0)
            {
                var live = new HashSet<string>(candidates.Select(n => n.NodeId));
                foreach (var kv in smoothWeights)
                {
                    if (!live.Contains(kv.Key))
                    {
                        smoothWeights.TryRemove(kv.Key, out _);
                    }
                }
            }

            return best;
        }

        /// <summary>负载权重上限（负载越大权重越小）。</summary>
        private const int LoadWeightCeiling = 100;

        /// <summary>节点心跳过期阈值：超过视为负载数据过期，从平滑加权候选剔除。</summary>
        private static readonly TimeSpan NodeHeartbeatStaleThreshold = TimeSpan.FromSeconds(30);

        /// <summary>SWRR 平滑加权轮询的节点当前权重表（NodeId -> currentWeight）。</summary>
        private readonly ConcurrentDictionary<string, int> smoothWeights = new();

        /// <summary>选择次数（周期清理平滑权重用）。</summary>
        private long selectCount;

        /// <summary>
        /// 检索与指定节点标识符关联的服务器节点信息
        /// </summary>
        /// <param name="nodeId">要检索的服务器节点的唯一标识符。不能为空。</param>
        /// <returns>A <see cref="ServerNodeInfo"/> 如果找到节点，则执行实例操作; 否则, <see langword="null"/>.</returns>
        public ServerNodeInfo? GetNode(string nodeId)
        {
            nodes.TryGetValue(nodeId, out var node);
            return node;
        }

        /// <summary>
        /// 根据节点会话反查节点 ID（Center 中继回源时把消息路由回发起节点）。
        /// </summary>
        /// <param name="session">节点会话（注册时记录在 ServerNodeInfo.Session）。</param>
        /// <returns>匹配的节点 ID；无匹配返回 null。</returns>
        public string? GetNodeIdBySession(Network.ISession session)
        {
            var node = nodes.Values.FirstOrDefault(n => n.Session == session);
            return node?.NodeId;
        }

        /// <summary>
        /// 按类型检索第一个在线节点（实体迁移中继/通知用，对标 KBE cellappmgr 节点表）。
        /// </summary>
        /// <param name="nodeType">节点类型（"Battle"/"Game"/"Gateway"）。</param>
        /// <returns>第一个该类型且已连接的节点，无则 null。</returns>
        public ServerNodeInfo? GetNodeByType(string nodeType)
        {
            return nodes.Values.FirstOrDefault(n =>
                n.NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase) && n.Session.IsConnected);
        }

        /// <summary>
        /// 返回节点集合中的元素数量。
        /// </summary>
        /// <returns>集合中的节点数。</returns>
        public int GetNodeCount()
        {
            return nodes.Count;
        }

        /// <summary>
        /// 移除超过指定超时时间未更新心跳的节点。
        /// </summary>
        /// <remarks>使用 UTC 时间比较；对每个成功移除的节点记录警告日志；移除通过 TryRemove 执行以支持并发集合操作。</remarks>
        /// <param name="timeout">用于判断节点最后心跳是否过期的超时时间（TimeSpan）。</param>
        /// <returns>已移除的节点数量。</returns>
        public int RemoveInactiveNodes(TimeSpan timeout)
        {
            int removedCount = 0;
            DateTime now = DateTime.UtcNow;

            foreach (var pair in nodes)
            {
                if (now - pair.Value.LastHeartbeat <= timeout)
                {
                    continue;
                }

                if (nodes.TryRemove(pair.Key, out var removedNode))
                {
                    removedCount++;
                    Log.Warning($"节点心跳超时，已移除: [{removedNode.NodeType}] {removedNode.NodeId}");
                }
            }

            return removedCount;
        }

        /// <summary>
        /// 创建并返回当前节点集合的快照列表，按节点类型然后按节点标识排序。
        /// </summary>
        /// <remarks>返回的是时点快照；后续对原始节点的更改不会影响已返回的 NodeSnapshot 实例。快照由内部节点集合生成，并包含 Session.IsConnected
        /// 的值。</remarks>
        /// <returns>按节点类型和节点标识排序的 IReadOnlyList<NodeSnapshot>，包含节点标识、类型、主机、端口、当前负载、最后心跳时间和连接状态。</returns>
        public IReadOnlyList<NodeSnapshot> GetNodeSnapshots()
        {
            return nodes.Values
                .Select(node => new NodeSnapshot
                {
                    NodeId = node.NodeId,
                    NodeType = node.NodeType,
                    Host = node.Host,
                    Port = node.Port,
                    CurrentLoad = node.CurrentLoad,
                    InstanceId = node.InstanceId,
                    MachineId = node.MachineId,
                    SupervisedBy = node.SupervisedBy,
                    LastHeartbeat = node.LastHeartbeat,
                    IsConnected = node.Session.IsConnected
                })
                .OrderBy(node => node.MachineId)
                .ThenBy(node => node.NodeType)
                .ThenBy(node => node.NodeId)
                .ToList();
        }

        // ===== 注册表持久化（Center 高可用基础：重启后保留节点注册信息） =====

        /// <summary>
        /// 将节点注册表快照保存到文件（JSON）。节点注册/心跳更新时由 Center 周期调用。
        /// </summary>
        public void SaveSnapshotToFile(string filePath)
        {
            try
            {
                var snapshot = new
                {
                    SavedAtUtc = DateTime.UtcNow,
                    Nodes = GetNodeSnapshots()
                };
                string json = Shared.Json.Serialize(snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Log.Error($"节点注册表快照保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从快照文件恢复节点注册信息（仅恢复主机/端口/类型等静态信息；
        /// 会话与心跳由节点重新连接后自动更新）。
        /// </summary>
        /// <returns>恢复的节点数。</returns>
        public int RestoreFromSnapshotFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return 0;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<SnapshotFile>(json);
                if (data?.Nodes == null)
                {
                    return 0;
                }

                int restored = 0;
                foreach (var node in data.Nodes)
                {
                    // 仅当节点尚未注册时恢复静态信息（会话为 null，等待节点重连）
                    nodes.TryAdd(node.NodeId, new ServerNodeInfo
                    {
                        NodeId = node.NodeId,
                        NodeType = node.NodeType,
                        Host = node.Host,
                        Port = node.Port,
                        CurrentLoad = 0,
                        InstanceId = node.InstanceId,
                        MachineId = node.MachineId,
                        SupervisedBy = node.SupervisedBy,
                        Session = null!,
                        LastHeartbeat = DateTime.UtcNow
                    });
                    restored++;
                }

                Log.Info($"节点注册表从快照恢复: {restored} 个节点");
                return restored;
            }
            catch (Exception ex)
            {
                Log.Error($"节点注册表快照恢复失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>快照文件结构（用于反序列化）。</summary>
        private sealed class SnapshotFile
        {
            public DateTime SavedAtUtc { get; set; }
            public List<NodeSnapshot>? Nodes { get; set; }
        }
    }
}