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
        public void RegisterNode(string nodeId, string nodeType, string host, int port, Network.ISession session)
        {
            var info = new ServerNodeInfo
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                Session = session,
                CurrentLoad = 0,
                LastHeartbeat = DateTime.UtcNow
            };

            nodes.AddOrUpdate(nodeId, info, (_, oldInfo) =>
            {
                oldInfo.Session = session;
                oldInfo.LastHeartbeat = DateTime.UtcNow;
                oldInfo.Host = host;
                oldInfo.Port = port;
                return oldInfo;
            });

            Log.Info($"节点已注册: [{nodeType}] {nodeId} at {host}:{port}");
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
        /// 获取当前负载最低的 BattleNode 的节点ID，如果没有可用的 BattleNode 则返回 null。
        /// </summary>
        /// <returns>当前负载最低的 BattleNode 的节点ID，如果没有可用的 BattleNode 则返回 null。</returns>
        public string? GetBestBattleNode()
        {
            var battleNodes = nodes.Values
                .Where(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase) && n.Session.IsConnected)
                .OrderBy(n => n.CurrentLoad)
                .ToList();

            if (battleNodes.Count > 0)
            {
                return battleNodes.First().NodeId;
            }
            return null;
        }

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
                    LastHeartbeat = node.LastHeartbeat,
                    IsConnected = node.Session.IsConnected
                })
                .OrderBy(node => node.NodeType)
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