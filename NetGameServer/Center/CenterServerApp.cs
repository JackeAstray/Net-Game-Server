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

        /// <summary>
        /// 启动中心调度服务器的网络，注册并配置内部 TCP 服务器、消息路由和事件处理器，并监听指定端口。
        /// </summary>
        /// <remarks>从配置读取端口（默认 31306）。接收并分发网关转发的内部消息，维护会话绑定，并在后台周期性清理超时节点。</remarks>
        /// <returns>表示启动操作完成的异步任务。</returns>
        public static async Task StartNetworkAsync()
        {
            // 例如配置中 CenterPort 默认 31306
            int port = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");

            var matchHandler = new Center.Handlers.MatchHandler();
            handlers = Center.Handlers.MessageRouter.BuildHandlers(matchHandler);

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            var centerDispatcher = Center.Handlers.CenterDispatcher.BuildDispatcher(matchHandler);

            var tcpServer = new TcpServer();

            // 内部连接认证：所有节点连接必须先通过认证握手（InternalAuth），密钥共享。
            string authSecret = ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
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

            tcpServer.OnDataReceived += async (session, data) =>
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

                Log.Info($"Center <- Node 收到消息 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} MsgId:{msgId} PacketLength:{data.Length} PayloadLength:{payloadLength}");
                byte[] payload = data.Slice(4).ToArray();

                long originalSessionId = 0;
                if (Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                {
                    originalSessionId = clientSessionId;
                    payload = cleanPayload;
                }

                if (originalSessionId > 0)
                {
                    Center.Handlers.NodeManager.Instance.BindClientGatewayRoute(originalSessionId, session);
                    Log.Debug($"Center 路由绑定更新 ClientSessionId:{originalSessionId} -> NodeSessionId:{session.SessionId}");
                }

                // 新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容）
                if (await centerDispatcher.TryDispatch(new Center.Handlers.CenterSessionContext(session, originalSessionId), msgId, payload))
                {
                    Log.Debug($"Center 新协议分发完成 MsgId:{msgId} ClientSessionId:{originalSessionId}");
                }
                else if (handlers != null && handlers.TryGetValue(msgId, out var handlerAction))
                {
                    try
                    {
                        Log.Info($"Center 开始处理消息 MsgId:{msgId} SessionId:{session.SessionId} OriginalSessionId:{originalSessionId} PayloadLength:{payload.Length}");
                        await handlerAction(payload, session, originalSessionId);
                        Log.Info($"Center 完成处理消息 MsgId:{msgId} SessionId:{session.SessionId} OriginalSessionId:{originalSessionId}");
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
            };

            await tcpServer.StartAsync(port);
            Log.Info($"Center 调度服务器网络已启动，监听内部端口: {port}");

            _ = Task.Run(async () =>
            {
                TimeSpan timeout = TimeSpan.FromSeconds(30);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
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