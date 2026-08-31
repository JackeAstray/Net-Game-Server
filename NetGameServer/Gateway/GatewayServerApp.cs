using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Routing;
using Network.Tcp;
using Serilog;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Gateway
{
    // 网关服务器主应用类
    // 负责启动监听客户端连接的网络服务（TCP/UDP/KCP/WebSocket），
    // 将来自客户端的数据根据消息 ID 路由到后端的 Login 或 Game 服务器，
    // 并处理后端返回的数据转发给相应的客户端会话。
    public static partial class GatewayServerApp
    {
        private static CancellationTokenSource? centerHeartbeatCts;

        /// <summary>断线重连挂起记录（客户端断线后宽限期内可恢复会话）。</summary>
        private sealed class PendingReconnect
        {
            public int UserId;
            public DateTime ExpiresAtUtc;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, PendingReconnect> pendingReconnects = new();

        // ===== 静态分片（对标 KBE cellappmgr 调度）：多 Battle 节点 + 按玩家绑定路由 =====

        // 后端发送器（Login/Game/Center）：在连接发起前创建并订阅 OnConnected，
        // 避免 localhost 快速连上时 OnConnected 先于订阅触发导致 isConnected 永远为 false（消息被静默缓冲）。
        private static BufferedBackendSender? loginSender;
        private static BufferedBackendSender? gameSender;
        private static BufferedBackendSender? centerSender;
        /// <summary>Battle 节点连接：nodeId("Battle-{host}:{port}") -> 客户端。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TcpClientWrapper> battleNodes = new(StringComparer.Ordinal);

        /// <summary>Battle 节点发送器（缓冲队列）：nodeId -> sender。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BufferedBackendSender> battleNodeSenders = new(StringComparer.Ordinal);

        /// <summary>玩家 -> Battle 节点绑定：clientSessionId -> nodeId（从匹配结果学习）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> clientBattleNodeBindings = new();

        /// <summary>默认 Battle 节点（无绑定时的回退目标）。</summary>
        private static volatile string defaultBattleNodeId = string.Empty;

        /// <summary>按玩家绑定（或默认节点）发送到对应 Battle 节点。</summary>
        private static void SendToBattle(byte[] outbound, long clientSessionId)
        {
            // 断线重连：客户端消息携带新会话 ID，但玩家->Battle 节点绑定以旧会话 ID 为键。
            // 必须先做 新->旧 别名解析，否则多 Battle 节点下会回退默认节点导致路由错误。
            long routingKey = Gateway.Managers.GatewaySessionManager.Instance.ResolveSessionId(clientSessionId);
            string nodeId = clientBattleNodeBindings.TryGetValue(routingKey, out var bound)
                ? bound
                : defaultBattleNodeId;
            if (battleNodeSenders.TryGetValue(nodeId, out var sender))
            {
                sender.SendOrBuffer(outbound);
            }
            else if (battleNodeSenders.Count > 0)
            {
                // 绑定节点不存在（节点已下线）：回退默认节点并清除绑定
                clientBattleNodeBindings.TryRemove(routingKey, out _);
                battleNodeSenders.TryGetValue(defaultBattleNodeId, out sender);
                sender?.SendOrBuffer(outbound);
            }
            else
            {
                Shared.Log.Warning($"Gateway 无可用 Battle 节点，丢弃消息 ClientSessionId:{clientSessionId}");
            }
        }

        private sealed class BufferedBackendSender
        {
            private readonly string backendName;
            private readonly Action<ReadOnlyMemory<byte>> sendAction;
            private readonly ConcurrentQueue<byte[]> pendingPackets = new ConcurrentQueue<byte[]>();
            private readonly int maxPending;
            private volatile bool isConnected;

            public BufferedBackendSender(string backendName, Action<ReadOnlyMemory<byte>> sendAction, int maxPending = 512)
            {
                this.backendName = backendName;
                this.sendAction = sendAction;
                this.maxPending = maxPending;
            }

            public void OnConnected()
            {
                isConnected = true;
                Shared.Log.Info($"Gateway->{backendName} 通道已连接，开始冲刷缓冲队列，当前待发:{pendingPackets.Count}");
                FlushPending();
            }

            public void OnDisconnected()
            {
                isConnected = false;
                Shared.Log.Warning($"Gateway->{backendName} 通道断开，后续消息将进入缓冲队列。当前待发:{pendingPackets.Count}");
            }

            public void SendOrBuffer(byte[] packet)
            {
                if (isConnected)
                {
                    Shared.Log.Debug($"Gateway->{backendName} 实时发送 Length:{packet.Length}");
                    sendAction(packet);
                    return;
                }

                pendingPackets.Enqueue(packet);
                Shared.Log.Warning($"Gateway->{backendName} 未连接，消息入缓冲 Length:{packet.Length} 当前待发:{pendingPackets.Count}");
                int droppedCount = 0;
                while (pendingPackets.Count > maxPending && pendingPackets.TryDequeue(out _))
                {
                    droppedCount++;
                }

                if (droppedCount > 0)
                {
                    Shared.Log.Warning($"Gateway->{backendName} 发送缓冲已满，丢弃旧消息 {droppedCount} 条，当前队列上限:{maxPending}");
                }
            }

            private void FlushPending()
            {
                int flushedCount = 0;
                while (isConnected && pendingPackets.TryDequeue(out var packet))
                {
                    flushedCount++;
                    sendAction(packet);
                }

                if (flushedCount > 0)
                {
                    Shared.Log.Info($"Gateway->{backendName} 缓冲冲刷完成，已发送:{flushedCount} 剩余待发:{pendingPackets.Count}");
                }
            }
        }
    }
}
