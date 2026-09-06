using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Center;

namespace Center.Handlers
{
    public static class MessageRouter
    {
        /// <summary>
        /// 构建并返回一个将消息ID映射到异步处理委托的字典，用于解析消息负载并执行相应的匹配、房间和节点操作。
        /// </summary>
        /// <remarks>各处理程序负责反序列化负载、调用 MatchHandler 的异步或同步方法、向网关发送响应以及在节点注册或状态更新时更新
        /// NodeManager。部分处理程序直接返回已完成的任务。</remarks>
        /// <param name="matchHandler">用于处理匹配及相关请求的 MatchHandler 实例。</param>
        /// <returns>包含消息ID到处理委托映射的字典；委托签名为 Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>。</returns>
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(MatchHandler matchHandler)
        {
            var handlers = new Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>();

            handlers[MessageIds.CenterMatchReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<CenterMatchRequest>(payload.Span);
                if (req != null)
                {
                    var res = await matchHandler.HandleMatchRequestAsync(clientSessionId, req, session, SendToGateway);
                    if (res != null)
                    {
                        SendToGateway(session, clientSessionId, MessageIds.CenterMatchRes, res);
                    }
                }
            };

            handlers[MessageIds.CenterCreateRoomReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int ownerUserId = 0;
                    byte[] cleanPayload;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out cleanPayload))
                    {
                        ownerUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    string ownerUid = string.Empty;
                    if (Shared.RouteMetadata.TryExtractUid(payload, out var extractedUid, out cleanPayload))
                    {
                        ownerUid = extractedUid;
                        payload = cleanPayload;
                    }

                    string ownerNickname = string.Empty;
                    if (Shared.RouteMetadata.TryExtractNickname(payload, out var extractedNickname, out cleanPayload))
                    {
                        ownerNickname = extractedNickname;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleCreateRoomRequestAsync(clientSessionId, ownerUserId, ownerUid, ownerNickname, req);
                        SendToGateway(session, clientSessionId, MessageIds.CenterCreateRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCreateRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCreateRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterListRoomsReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterListRoomsRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleListRoomsRequestAsync(req);
                        SendToGateway(session, clientSessionId, MessageIds.CenterListRoomsRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterListRoomsReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterListRoomsReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterJoinRoomReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    byte[] cleanPayload;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    string requesterUid = string.Empty;
                    if (Shared.RouteMetadata.TryExtractUid(payload, out var extractedUid, out cleanPayload))
                    {
                        requesterUid = extractedUid;
                        payload = cleanPayload;
                    }

                    string requesterNickname = string.Empty;
                    if (Shared.RouteMetadata.TryExtractNickname(payload, out var extractedNickname, out cleanPayload))
                    {
                        requesterNickname = extractedNickname;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterJoinRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleJoinRoomRequestAsync(clientSessionId, requesterUserId, requesterUid, requesterNickname, req);
                        SendToGateway(session, clientSessionId, MessageIds.CenterJoinRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterJoinRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterJoinRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterCloseRoomReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out var cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterCloseRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleCloseRoomRequestAsync(requesterUserId, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.CenterCloseRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCloseRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCloseRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterLeaveRoomReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterLeaveRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleLeaveRoomRequestAsync(clientSessionId, req, session, SendToGateway, SendToGateway, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.CenterLeaveRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterLeaveRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterLeaveRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterUpdateRoomSettingsReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out var cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterUpdateRoomSettingsRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleUpdateRoomSettingsRequestAsync(requesterUserId, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.CenterUpdateRoomSettingsRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterUpdateRoomSettingsReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterUpdateRoomSettingsReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterStartRoomGameReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out var cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterStartRoomGameRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleStartRoomGameRequestAsync(requesterUserId, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.CenterStartRoomGameRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterStartRoomGameReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterStartRoomGameReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterRoomChatReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    byte[] cleanPayload;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    string requesterUid = string.Empty;
                    if (Shared.RouteMetadata.TryExtractUid(payload, out var extractedUid, out cleanPayload))
                    {
                        requesterUid = extractedUid;
                        payload = cleanPayload;
                    }

                    string requesterNickname = string.Empty;
                    if (Shared.RouteMetadata.TryExtractNickname(payload, out var extractedNickname, out cleanPayload))
                    {
                        requesterNickname = extractedNickname;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRoomChatRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleRoomChatRequestAsync(clientSessionId, requesterUserId, requesterUid, requesterNickname, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.CenterRoomChatRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterRoomChatReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterRoomChatReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterCreateSceneRes] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var res = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateSceneResponse>(payload.Span);
                    if (res != null)
                    {
                        matchHandler.HandleCreateSceneResponse(res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCreateSceneRes 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCreateSceneRes 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterDestroySceneRes] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var res = Shared.Json.DeserializeFromUtf8Bytes<CenterDestroySceneResponse>(payload.Span);
                    if (res != null)
                    {
                        matchHandler.HandleDestroySceneResponse(res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterDestroySceneRes 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterDestroySceneRes 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterRoomPlayerCountSyncReq] = (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRoomPlayerCountSyncRequest>(payload.Span);
                    if (req != null)
                    {
                        matchHandler.HandleRoomPlayerCountSync(req);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterRoomPlayerCountSyncReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterRoomPlayerCountSyncReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
                return Task.CompletedTask;
            };

            handlers[MessageIds.CenterRoomMemberLeaveSyncReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRoomMemberLeaveSyncRequest>(payload.Span);
                    if (req != null)
                    {
                        await matchHandler.HandleRoomMemberLeaveSyncAsync(req, session, SendToGateway, SendToGateway, SendToGateway);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterRoomMemberLeaveSyncReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterRoomMemberLeaveSyncReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.RoomMemberListReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<RoomMemberListRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleRoomMemberListRequestAsync(req);
                        SendToGateway(session, clientSessionId, MessageIds.RoomMemberListRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"RoomMemberListReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"RoomMemberListReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.RoomReadyReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    byte[] cleanPayload;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    string requesterUid = string.Empty;
                    if (Shared.RouteMetadata.TryExtractUid(payload, out var extractedUid, out cleanPayload))
                    {
                        requesterUid = extractedUid;
                        payload = cleanPayload;
                    }

                    string requesterNickname = string.Empty;
                    if (Shared.RouteMetadata.TryExtractNickname(payload, out var extractedNickname, out cleanPayload))
                    {
                        requesterNickname = extractedNickname;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<RoomReadyRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleRoomReadyRequestAsync(clientSessionId, requesterUserId, requesterUid, requesterNickname, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.RoomReadyRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"RoomReadyReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"RoomReadyReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.RoomTransferOwnerReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out var cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<RoomTransferOwnerRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleRoomTransferOwnerRequestAsync(requesterUserId, req, session, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.RoomTransferOwnerRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"RoomTransferOwnerReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"RoomTransferOwnerReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.RoomKickMemberReq] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    int requesterUserId = 0;
                    if (Shared.RouteMetadata.TryExtractUserId(payload, out var extractedUserId, out var cleanPayload))
                    {
                        requesterUserId = extractedUserId;
                        payload = cleanPayload;
                    }

                    var req = Shared.Json.DeserializeFromUtf8Bytes<RoomKickMemberRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleRoomKickMemberRequestAsync(requesterUserId, req, session, SendToGateway, SendToGateway, SendToGateway);
                        SendToGateway(session, clientSessionId, MessageIds.RoomKickMemberRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"RoomKickMemberReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"RoomKickMemberReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            // 客户端断线（网关 PlayerDisconnectNotif）：把断线玩家从所有房间与匹配队列移除，
            // 防止断线玩家成为房间幽灵成员/永久占用匹配队列（此前 Center 不处理断线）。
            handlers[MessageIds.PlayerDisconnectNotif] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    if (clientSessionId <= 0)
                    {
                        return;
                    }
                    // 解除客户端→网关路由绑定（防 clientGatewayRoutes 无界增长；绑定本身不可信，断线即失效）
                    NodeManager.Instance.UnbindClientGatewayRoute(clientSessionId);
                    await matchHandler.HandleClientDisconnectAsync(clientSessionId, session, SendToGateway, SendToGateway, SendToGateway);
                    CenterServerApp.Parties?.HandleClientDisconnect(clientSessionId);
                    Shared.Log.Info($"Center 处理客户端断线，已从房间/匹配队列/队伍移除 ClientSessionId:{clientSessionId}");
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"PlayerDisconnectNotif 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterRegisterNodeReq] = (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRegisterNodeRequest>(payload.Span);
                    if (req != null)
                    {
                        if (!VerifyRegisterSignature(req))
                        {
                            Shared.Log.Warning($"CenterRegisterNodeReq 签名校验失败，NodeId:{req.NodeId}");
                            return Task.CompletedTask;
                        }

                        // P3 加固：注册身份必须与内部认证握手的身份一致（防伪造节点注册/接管）。
                        // 仅持有共享密钥还不够——握手声明的 NodeId 必须与注册的 NodeId 相同。
                        if (!TryGetAuthenticatedNodeId(session, out string? authenticatedNodeId)
                            || string.IsNullOrWhiteSpace(authenticatedNodeId)
                            || !string.Equals(authenticatedNodeId, req.NodeId, StringComparison.Ordinal))
                        {
                            Shared.Log.Warning($"CenterRegisterNodeReq 注册身份与握手身份不一致，已拒绝 NodeId:{req.NodeId} 握手身份:{authenticatedNodeId ?? "(none)"} SessionId:{session.SessionId}");
                            return Task.CompletedTask;
                        }

                        NodeManager.Instance.RegisterNode(req.NodeId, req.NodeType, req.Host, req.Port, session,
                            req.InstanceId, req.MachineId, req.SupervisedBy);
                        NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterRegisterNodeReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterRegisterNodeReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
                return Task.CompletedTask;
            };

            handlers[MessageIds.CenterNodeStatusReq] = (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterNodeStatusRequest>(payload.Span);
                    if (req != null && !string.IsNullOrWhiteSpace(req.NodeId))
                    {
                        if (!VerifyStatusSignature(req))
                        {
                            Shared.Log.Warning($"CenterNodeStatusReq 签名校验失败，NodeId:{req.NodeId}");
                            return Task.CompletedTask;
                        }

                        // P3 加固：负载/心跳上报必须来自该节点已注册的会话（防跨连接伪造负载/心跳/保持假节点新鲜）。
                        string? boundNodeId = NodeManager.Instance.GetNodeIdBySession(session);
                        if (boundNodeId == null || !string.Equals(boundNodeId, req.NodeId, StringComparison.Ordinal))
                        {
                            Shared.Log.Warning($"CenterNodeStatusReq 节点不匹配，已拒绝 请求 NodeId:{req.NodeId} 该连接已注册:{(boundNodeId ?? "(none)")} SessionId:{session.SessionId}");
                            return Task.CompletedTask;
                        }

                        NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterNodeStatusReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterNodeStatusReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
                return Task.CompletedTask;
            };

            return handlers;
        }

        /// <summary>读取会话的握手认证身份（P3 加固）。未登记认证过滤器或未通过握手时返回 false。</summary>
        private static bool TryGetAuthenticatedNodeId(Network.ISession session, out string? authenticatedNodeId)
        {
            authenticatedNodeId = null;
            if (!NodeAuthFilters.Registry.TryGetValue(session.SessionId, out var filter) || filter == null || !filter.IsAuthenticated)
            {
                return false;
            }
            authenticatedNodeId = filter.AuthenticatedNodeId;
            return true;
        }

        /// <summary>
        /// 验证注册请求的签名并校验时间戳有效性。
        /// </summary>
        /// <remarks>先通过 IsTimestampValid 验证时间戳，再将 NodeId、NodeType、Host、Port、CurrentLoad、InstanceId、MachineId、SupervisedBy
        /// 和 Timestamp 以管道分隔组合为源字符串，调用 ComputeSignature 生成期望签名，最后使用 FixedTimeEquals 进行常量时间比较以抵抗定时攻击。
        /// 协议扩展（迭代 20）：Machine 注入字段（InstanceId/MachineId/SupervisedBy）参与签名源，旧字段拼串保持原位以保证后向兼容（仅增字段）。</remarks>
        /// <param name="req">要验证签名的 CenterRegisterNodeRequest 实例，包含节点标识、类型、主机、端口、当前负载、机器字段和时间戳。</param>
        /// <returns>如果时间戳有效且签名与按 NodeId|NodeType|Host|Port|CurrentLoad|InstanceId|MachineId|SupervisedBy|Timestamp 计算的期望签名在固定时间比较下相等，则返回 true；否则返回 false。</returns>
        private static bool VerifyRegisterSignature(CenterRegisterNodeRequest req)
        {
            if (!IsTimestampValid(req.Timestamp) || string.IsNullOrWhiteSpace(req.Signature))
            {
                return false;
            }

            string source = $"{req.NodeId}|{req.NodeType}|{req.Host}|{req.Port}|{req.CurrentLoad}|{req.InstanceId}|{req.MachineId}|{req.SupervisedBy}|{req.Timestamp}";
            string expected = ComputeSignature(source);
            return FixedTimeEquals(expected, req.Signature);
        }

        /// <summary>
        /// 验证中心节点状态请求的签名并检查时间戳是否有效。
        /// </summary>
        /// <remarks>使用 ComputeSignature 计算预期签名，并通过 FixedTimeEquals 以固定时间比较防止时序攻击。请求时间戳必须通过
        /// IsTimestampValid 验证。</remarks>
        /// <param name="req">包含 NodeId、CurrentLoad、Timestamp 和 Signature 的状态请求。</param>
        /// <returns>若时间戳有效且签名与根据 NodeId、CurrentLoad 和 Timestamp 计算的值匹配则返回 true，否则返回 false。</returns>
        private static bool VerifyStatusSignature(CenterNodeStatusRequest req)
        {
            if (!IsTimestampValid(req.Timestamp) || string.IsNullOrWhiteSpace(req.Signature))
            {
                return false;
            }

            string source = $"{req.NodeId}|{req.CurrentLoad}|{req.Timestamp}";
            string expected = ComputeSignature(source);
            return FixedTimeEquals(expected, req.Signature);
        }

        /// <summary>
        /// 验证给定的 Unix 时间戳（秒）是否为正且与当前 UTC Unix 时间的绝对差值不超过 120 秒。
        /// </summary>
        /// <remarks>通过 DateTimeOffset.UtcNow.ToUnixTimeSeconds 获取当前 UTC 时间，并比较绝对差值是否小于等于 120 秒。</remarks>
        /// <param name="timestamp">Unix 时间戳，单位为秒。</param>
        /// <returns>若时间戳为正且与当前 UTC Unix 时间的差值不超过 120 秒，则返回 true；否则返回 false。</returns>
        private static bool IsTimestampValid(long timestamp)
        {
            if (timestamp <= 0)
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delta = Math.Abs(now - timestamp);
            return delta <= 120;
        }

        /// <summary>
        /// 计算输入字符串的 HMAC-SHA256 签名并以 Base64 编码返回。
        /// </summary>
        /// <remarks>从配置键 CenterNodeSharedSecret 获取共享密钥（默认 change-this-secret）；对输入使用 UTF-8 编码并用 HMACSHA256
        /// 计算哈希，使用完毕后释放 HMAC 实例。</remarks>
        /// <param name="source">要签名的输入字符串。</param>
        /// <returns>签名的 Base64 编码字符串。</returns>
        private static string ComputeSignature(string source)
        {
            // 安全修复：拒绝占位符密钥。
            string secret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);

            using var hmac = new HMACSHA256(key);
            byte[] hash = hmac.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// 以固定时间比较两个字符串以防止时序侧信道攻击。
        /// </summary>
        /// <remarks>先将字符串编码为 UTF-8 字节数组，先比较长度以避免不必要的固定时间调用，然后使用 CryptographicOperations.FixedTimeEquals
        /// 进行固定时间比较。</remarks>
        /// <param name="expected">预期字符串，使用 UTF-8 编码为字节后参与固定时间比较。</param>
        /// <param name="actual">要比较的字符串，使用 UTF-8 编码为字节后参与固定时间比较。</param>
        /// <returns>当两者长度相等且内容在固定时间比较中相同时返回 true；否则返回 false。</returns>
        private static bool FixedTimeEquals(string expected, string actual)
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        /// <summary>
        /// 将响应序列化为 UTF-8、附加客户端会话 ID 的路由元数据并发送到网关会话，优先使用与指定客户端会话关联的网关会话。
        /// </summary>
        /// <remarks>发送失败的异常将被捕获并记录。构建的字节数组在完成后会归还至 ArrayPool<byte>.Shared。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="gatewaySession">在未找到与客户端关联的网关会话或该会话不可用时用于发送数据的网关会话。</param>
        /// <param name="clientSessionId">目标客户端的会话 ID；若大于 0 则尝试路由到与该客户端关联的网关会话。</param>
        /// <param name="msgId">要发送的数据包的消息标识符。</param>
        /// <param name="response">要序列化为 UTF-8 并发送的响应对象。</param>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response!);
            byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(responsePayload, clientSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);

            try
            {
                if (clientSessionId > 0
                    && NodeManager.Instance.TryGetGatewaySessionByClientSessionId(clientSessionId, out var routedGatewaySession)
                    && routedGatewaySession.IsConnected)
                {
                    routedGatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
                    return;
                }

                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"SendToGateway 发送失败 MsgId:{msgId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}