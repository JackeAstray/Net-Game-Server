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
        }

        public void BindClientGatewayRoute(long clientSessionId, Network.ISession gatewaySession)
        {
            if (clientSessionId <= 0)
            {
                return;
            }

            clientGatewayRoutes[clientSessionId] = gatewaySession;
        }

        public bool TryGetGatewaySessionByClientSessionId(long clientSessionId, out Network.ISession gatewaySession)
        {
            return clientGatewayRoutes.TryGetValue(clientSessionId, out gatewaySession!);
        }

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
                .Where(n => n.NodeType.Equals("Battle", StringComparison.OrdinalIgnoreCase))
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

        public int GetNodeCount()
        {
            return nodes.Count;
        }

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
    }
}
