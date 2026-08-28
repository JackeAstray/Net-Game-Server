using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shared;
using Network;
using Network.Tcp;

namespace Center
{
    public static class CenterServerApp
    {
        private static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>? handlers;

        /// <summary>匹配/房间处理器实例（管理台房间接口用）。</summary>
        public static Center.Handlers.MatchHandler? Match { get; private set; }

        /// <summary>Leader 选举实例（主备高可用：仅 Leader 处理业务）。</summary>
        public static Framework.Core.LeaderElection? LeaderElection { get; private set; }

        /// <summary>当前是否为主节点（Leader）。</summary>
        public static bool IsLeader => LeaderElection?.IsLeader ?? true;

        /// <summary>
        /// 启动中心调度服务器的网络，注册并配置内部 TCP 服务器、消息路由和事件处理器，并监听指定端口。
        /// </summary>
        /// <remarks>从配置读取端口（默认 31306）。接收并分发网关转发的内部消息，维护会话绑定，并在后台周期性清理超时节点。
        /// 支持主备：配置 LeaderLockFile 后启动 Leader 选举，仅 Leader 处理业务消息，Standby 节点保持监听等待接管。</remarks>
        /// <returns>表示启动操作完成的异步任务。</returns>
        public static async Task StartNetworkAsync()
        {
            // 例如配置中 CenterPort 默认 31306
            int port = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");

            // Leader 选举（配置 LeaderLockFile 启用主备；未配置时单机模式始终为 Leader）
            string? leaderLockFile = ConfigHelper.GetConfig<string>("LeaderLockFile");
            if (!string.IsNullOrWhiteSpace(leaderLockFile))
            {
                string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
                LeaderElection = new Framework.Core.LeaderElection(leaderLockFile, $"Center-{centerHost}:{port}");
                LeaderElection.LeadershipChanged += isLeader =>
                {
                    Log.Warning($"Center 主备状态变化: IsLeader={isLeader}");
                };
            }

            var matchHandler = new Center.Handlers.MatchHandler();
            Match = matchHandler;
            handlers = Center.Handlers.MessageRouter.BuildHandlers(matchHandler);

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            var centerDispatcher = Center.Handlers.CenterDispatcher.BuildDispatcher(matchHandler);

            var tcpServer = new TcpServer();

            // 内部连接认证：所有节点连接必须先通过认证握手（InternalAuth），密钥共享。
            // 安全修复：拒绝占位符密钥。
            string authSecret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            var nodeAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点已连接到中心服: {session.RemoteEndPoint}");
                nodeAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, $"Center-{ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1"}:{port}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                Log.Info($"节点从中心服断开，原因: {reason}");
                nodeAuthFilters.TryRemove(session.SessionId, out _);
                Center.Handlers.NodeManager.Instance.RemoveNodeBySession(session);
            };

            // 安全修复：使用 AsyncEventGuard 包装 async lambda，避免 async void 异常冒泡到 AppDomain
            tcpServer.OnDataReceived += Network.AsyncEventGuard.Wrap(async (session, data) =>
            {
                if (data.Length < 4)
                {
                    Log.Warning($"Center 收到无效数据，长度不足4 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                int payloadLength = data.Length - 4;

                // 内部连接认证：未认证连接只接受认证握手消息。
                if (nodeAuthFilters.TryGetValue(session.SessionId, out var authFilter))
                {
                    if (!authFilter.IsAuthenticated)
                    {
                        if (Framework.Core.Security.InternalAuthFilter.IsAuthMessage(msgId))
                        {
                            byte[] authPayload = data.Slice(4).ToArray();
                            if (authFilter.TryAuthenticate(authPayload))
                            {
                                Log.Info($"Center <- Node 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            }
                            else
                            {
                                Log.Warning($"Center <- Node 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                session.Close();
                                return;
                            }
                            return;
                        }

                        Log.Warning($"Center 拒绝未认证连接的消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                        return;
                    }
                }
                else
                {
                    // 未注册认证过滤器 = 未认证连接：默认 fail-closed 拒绝（安全修复，杜绝"过滤器缺失即放行"）。
                    if (!Shared.ConfigHelper.GetConfig<bool>("AllowUnauthenticatedInternal"))
                    {
                        Log.Warning($"Center 拒绝无认证过滤器连接的消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}（fail-closed）");
                        session.Close();
                        return;
                    }
                }

                Log.Debug("Center <- Node 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);
                byte[] payload = data.Slice(4).ToArray();

                long originalSessionId = 0;
                int routedUserId = 0;
                string routedUid = string.Empty;
                string routedNickname = string.Empty;
                if (Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                {
                    originalSessionId = clientSessionId;
                    payload = cleanPayload;
                }
                // 提取玩家身份元数据（房间操作类消息需要：准备/踢人/转让房主等）
                if (Shared.RouteMetadata.TryExtractUserId(payload, out int extractedUserId, out var payloadNoUser))
                {
                    routedUserId = extractedUserId;
                    payload = payloadNoUser;
                }
                if (Shared.RouteMetadata.TryExtractUid(payload, out string extractedUid, out var payloadNoUid))
                {
                    routedUid = extractedUid;
                    payload = payloadNoUid;
                }
                if (Shared.RouteMetadata.TryExtractNickname(payload, out string extractedNickname, out var payloadNoNick))
                {
                    routedNickname = extractedNickname;
                    payload = payloadNoNick;
                }

                if (originalSessionId > 0)
                {
                    Center.Handlers.NodeManager.Instance.BindClientGatewayRoute(originalSessionId, session);
                    Log.Debug($"Center 路由绑定更新 ClientSessionId:{originalSessionId} -> NodeSessionId:{session.SessionId}");
                }

                // 主备：非 Leader 节点拒绝业务消息（节点注册/心跳仍接受，便于 Standby 恢复后快速同步）
                if (!IsLeader && msgId < 90000)
                {
                    Log.Warning($"Center (Standby) 拒绝业务消息 MsgId:{msgId}（等待接管为 Leader）");
                    return;
                }

                // 新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容）
                var centerCtx = new Center.Handlers.CenterSessionContext(session, originalSessionId)
                {
                    RoutedUserId = routedUserId,
                    RoutedUid = routedUid,
                    RoutedNickname = routedNickname
                };
                if (await centerDispatcher.TryDispatch(centerCtx, msgId, payload))
                {
                    Log.Debug($"Center 新协议分发完成 MsgId:{msgId} ClientSessionId:{originalSessionId}");
                }
                else if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                {
                    try
                    {
                        Log.Debug("Center 开始处理消息 MsgId:{MsgId} SessionId:{SessionId} OriginalSessionId:{OriginalSessionId} PayloadLength:{PayloadLength}", msgId, session.SessionId, originalSessionId, payload.Length);
                        await handlerAction(payload, session, originalSessionId);
                        Log.Debug("Center 完成处理消息 MsgId:{MsgId} SessionId:{SessionId} OriginalSessionId:{OriginalSessionId}", msgId, session.SessionId, originalSessionId);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Center 处理消息 ({msgId}) 发生异常: " + ex);
                    }
                }
                else
                {
                    Log.Warning($"Center 收到未知 MsgId {msgId}");

                    if (originalSessionId > 0 && msgId >= 30000 && msgId < 40000)
                    {
                        int responseMsgId = msgId switch
                        {
                            Shared.Messages.MessageIds.CenterMatchReq => Shared.Messages.MessageIds.CenterMatchRes,
                            Shared.Messages.MessageIds.CenterCreateRoomReq => Shared.Messages.MessageIds.CenterCreateRoomRes,
                            Shared.Messages.MessageIds.CenterListRoomsReq => Shared.Messages.MessageIds.CenterListRoomsRes,
                            Shared.Messages.MessageIds.CenterJoinRoomReq => Shared.Messages.MessageIds.CenterJoinRoomRes,
                            Shared.Messages.MessageIds.CenterCloseRoomReq => Shared.Messages.MessageIds.CenterCloseRoomRes,
                            Shared.Messages.MessageIds.CenterUpdateRoomSettingsReq => Shared.Messages.MessageIds.CenterUpdateRoomSettingsRes,
                            Shared.Messages.MessageIds.CenterStartRoomGameReq => Shared.Messages.MessageIds.CenterStartRoomGameRes,
                            _ => 0
                        };

                        if (responseMsgId > 0)
                        {
                            object unknownResponse = responseMsgId switch
                            {
                                Shared.Messages.MessageIds.CenterListRoomsRes => new Shared.Messages.Center.CenterListRoomsResponse
                                {
                                    Success = false,
                                    Message = $"未支持的中心消息类型: {msgId}",
                                    Rooms = Array.Empty<Shared.Messages.Center.RoomInfo>()
                                },
                                Shared.Messages.MessageIds.CenterCloseRoomRes => new Shared.Messages.Center.CenterCloseRoomResponse
                                {
                                    Success = false,
                                    Message = $"未支持的中心消息类型: {msgId}"
                                },
                                _ => new Shared.Messages.Center.CenterMatchResponse
                                {
                                    Success = false,
                                    Message = $"未支持的中心消息类型: {msgId}"
                                }
                            };
                            byte[] unknownPayload = Shared.Json.SerializeToUtf8Bytes(unknownResponse);
                            byte[] routedUnknownPayload = Shared.RouteMetadata.AttachClientSessionId(unknownPayload, originalSessionId);
                            byte[] unknownPacket = Network.Routing.PacketBuilder.BuildPacket(responseMsgId, routedUnknownPayload, out int unknownLength);
                            try
                            {
                                session.Send(unknownPacket.AsSpan(0, unknownLength).ToArray());
                            }
                            finally
                            {
                                System.Buffers.ArrayPool<byte>.Shared.Return(unknownPacket);
                            }
                        }
                    }
                }
            });

            await tcpServer.StartAsync(port);
            Log.Info($"Center 调度服务器网络已启动，监听内部端口: {port}");

            // 注册表持久化（Center 高可用基础）：启动时从快照恢复静态节点信息，周期保存快照
            string snapshotFile = Shared.ConfigHelper.GetConfig<string>("NodeSnapshotFile")
                ?? Path.Combine(AppContext.BaseDirectory, "data", "node_snapshot.json");
            Center.Handlers.NodeManager.Instance.RestoreFromSnapshotFile(snapshotFile);

            _ = Task.Run(async () =>
            {
                TimeSpan timeout = TimeSpan.FromSeconds(30);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    // 周期保存注册表快照（节点注册/心跳变化后持久化）
                    Center.Handlers.NodeManager.Instance.SaveSnapshotToFile(snapshotFile);
                    int removedCount = Center.Handlers.NodeManager.Instance.RemoveInactiveNodes(timeout);
                    if (removedCount > 0)
                    {
                        Log.Warning($"Center 已清理超时节点数: {removedCount}，当前剩余节点数: {Center.Handlers.NodeManager.Instance.GetNodeCount()}");
                    }
                }
            });
        }
    }
}