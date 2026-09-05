using System;
using System.Collections.Concurrent;
using System.Threading;
using Game.Managers;
using Game.Network;
using Network.Routing;
using Shared;
using Shared.Messages;
using Shared.Messages.Db;
using Shared.Messages.Social;

namespace Game.Handlers
{
    /// <summary>
    /// 公会系统处理器 —— 基础模块（请求状态/注册表/共享 DB 请求辅助）。
    /// 与 GuildHandler.DbResponse.cs 同属一个 partial class。
    /// 数据流：客户端请求 → 构造 Shared.Messages.Db DTO → TrySendDbRequest 发 DB 节点 →
    /// DB 回包经 TryHandleDbResponse 按 RequestId 匹配并回发客户端。
    /// </summary>
    public static partial class GuildHandler
    {
        private static readonly ConcurrentDictionary<long, PendingGuildRequest> PendingGuildRequests = new();
        private static readonly ConcurrentDictionary<long, int> PendingBySession = new();
        private static long requestIdSeed = DateTime.UtcNow.Ticks;
        private static long lastPendingSweepTicks;

        private const int MaxPendingPerSession = 16;
        private const int MaxTotalPending = 512;
        private static readonly TimeSpan PendingRequestTimeout = TimeSpan.FromSeconds(30);

        private sealed class PendingGuildRequest
        {
            public long SessionId { get; set; }
            public int ResponseMsgId { get; set; }
            /// <summary>期望的 DB 响应 msgid（= 请求 + 100），接收端校验不符即拒绝。</summary>
            public int DbResponseMsgId { get; set; }
            public global::Network.ISession? GatewaySession { get; set; }
            public long CreatedAtTicks { get; set; } = DateTime.UtcNow.Ticks;
        }

        /// <summary>向 Game 的 MessageRouter 注册全部公会客户端消息。</summary>
        public static void Register(MessageRouter router)
        {
            RegisterRequest<GuildCreateRequest>(router, MessageIds.GuildCreateReq, HandleGuildCreateRequest);
            RegisterRequest<GuildMyRequest>(router, MessageIds.GuildMyReq, HandleGuildMyRequest);
            RegisterRequest<GuildJoinRequest>(router, MessageIds.GuildJoinReq, HandleGuildJoinRequest);
            RegisterRequest<GuildLeaveRequest>(router, MessageIds.GuildLeaveReq, HandleGuildLeaveRequest);
            RegisterRequest<GuildDisbandRequest>(router, MessageIds.GuildDisbandReq, HandleGuildDisbandRequest);
            RegisterRequest<GuildKickRequest>(router, MessageIds.GuildKickReq, HandleGuildKickRequest);
            RegisterRequest<GuildTransferRequest>(router, MessageIds.GuildTransferReq, HandleGuildTransferRequest);
            RegisterRequest<GuildUpdateDeclRequest>(router, MessageIds.GuildUpdateDeclReq, HandleGuildUpdateDeclRequest);
        }

        private static void RegisterRequest<TReq>(MessageRouter router, int msgId, Action<ClientSessionWrapper, TReq> handler)
            where TReq : class
        {
            router.RegisterHandler(msgId, (s, p) =>
            {
                if (s is not ClientSessionWrapper session)
                {
                    return;
                }
                handler(session, Shared.Json.DeserializeFromUtf8Bytes<TReq>(p.Span)!);
            });
        }

        private static int GetUserId(ClientSessionWrapper session)
            => PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);

        private static bool TryRequireLogin(ClientSessionWrapper session, int responseMsgId, out int userId)
        {
            userId = GetUserId(session);
            if (userId <= 0)
            {
                SendSimpleResponse(session, responseMsgId, new { Success = false, Message = "会话未登录或未绑定" });
                return false;
            }
            if (GameServerApp.DbClient == null || !GameServerApp.DbClient.IsConnected)
            {
                SendSimpleResponse(session, responseMsgId, new { Success = false, Message = "DB服务未连接" });
                return false;
            }
            return true;
        }

        // ===== 创建公会 =====
        internal static void HandleGuildCreateRequest(ClientSessionWrapper session, GuildCreateRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GuildCreateRes, new GuildCreateResponse { Success = false, Message = "请求格式无效" });
                return;
            }
            if (!TryRequireLogin(session, MessageIds.GuildCreateRes, out int userId))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                SendSimpleResponse(session, MessageIds.GuildCreateRes, new GuildCreateResponse { Success = false, Message = "公会名称不能为空" });
                return;
            }

            var dbReq = new DbGuildCreateRequest
            {
                UserId = userId,
                Name = req.Name.Trim(),
                Declaration = req.Declaration?.Trim() ?? string.Empty
            };
            TrySendDbRequest(MessageIds.DbGuildCreateReq, session, dbReq, MessageIds.GuildCreateRes);
        }

        // ===== 我的公会 =====
        internal static void HandleGuildMyRequest(ClientSessionWrapper session, GuildMyRequest? req)
        {
            if (!TryRequireLogin(session, MessageIds.GuildMyRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildMyReq, session, new DbGuildMyRequest { UserId = userId }, MessageIds.GuildMyRes);
        }

        // ===== 加入公会 =====
        internal static void HandleGuildJoinRequest(ClientSessionWrapper session, GuildJoinRequest? req)
        {
            if (req == null || req.GuildId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GuildJoinRes, new GuildJoinResponse { Success = false, Message = "参数无效" });
                return;
            }
            if (!TryRequireLogin(session, MessageIds.GuildJoinRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildJoinReq, session, new DbGuildJoinRequest { UserId = userId, GuildId = req.GuildId }, MessageIds.GuildJoinRes);
        }

        // ===== 退出公会 =====
        internal static void HandleGuildLeaveRequest(ClientSessionWrapper session, GuildLeaveRequest? req)
        {
            if (!TryRequireLogin(session, MessageIds.GuildLeaveRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildLeaveReq, session, new DbGuildLeaveRequest { UserId = userId }, MessageIds.GuildLeaveRes);
        }

        // ===== 解散公会 =====
        internal static void HandleGuildDisbandRequest(ClientSessionWrapper session, GuildDisbandRequest? req)
        {
            if (!TryRequireLogin(session, MessageIds.GuildDisbandRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildDisbandReq, session, new DbGuildDisbandRequest { UserId = userId }, MessageIds.GuildDisbandRes);
        }

        // ===== 踢出成员 =====
        internal static void HandleGuildKickRequest(ClientSessionWrapper session, GuildKickRequest? req)
        {
            if (req == null || req.TargetUserId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GuildKickRes, new GuildKickResponse { Success = false, Message = "参数无效" });
                return;
            }
            if (!TryRequireLogin(session, MessageIds.GuildKickRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildKickReq, session, new DbGuildKickRequest { OperatorUserId = userId, TargetUserId = req.TargetUserId }, MessageIds.GuildKickRes);
        }

        // ===== 转让会长 =====
        internal static void HandleGuildTransferRequest(ClientSessionWrapper session, GuildTransferRequest? req)
        {
            if (req == null || req.TargetUserId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GuildTransferRes, new GuildTransferResponse { Success = false, Message = "参数无效" });
                return;
            }
            if (!TryRequireLogin(session, MessageIds.GuildTransferRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildTransferReq, session, new DbGuildTransferRequest { OperatorUserId = userId, TargetUserId = req.TargetUserId }, MessageIds.GuildTransferRes);
        }

        // ===== 修改宣言 =====
        internal static void HandleGuildUpdateDeclRequest(ClientSessionWrapper session, GuildUpdateDeclRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GuildUpdateDeclRes, new GuildUpdateDeclResponse { Success = false, Message = "请求格式无效" });
                return;
            }
            if (!TryRequireLogin(session, MessageIds.GuildUpdateDeclRes, out int userId))
            {
                return;
            }
            TrySendDbRequest(MessageIds.DbGuildUpdateDeclReq, session, new DbGuildUpdateDeclRequest
            {
                UserId = userId,
                Declaration = req.Declaration?.Trim() ?? string.Empty
            }, MessageIds.GuildUpdateDeclRes);
        }

        // ===== 发送 DB 请求 + 待处理注册 =====
        private static void TrySendDbRequest<TRequest>(int dbMsgId, ClientSessionWrapper session, TRequest request, int responseMsgId)
        {
            SweepExpiredPendingRequests();

            var dbClient = GameServerApp.DbClient;
            if (dbClient == null || !dbClient.IsConnected)
            {
                SendSimpleResponse(session, responseMsgId, new { Success = false, Message = "DB服务未连接" });
                return;
            }

            long clientSessionId = session.SessionId;
            long requestId = Interlocked.Increment(ref requestIdSeed);

            int pendingCount = PendingBySession.AddOrUpdate(clientSessionId, 1, (_, v) => v + 1);
            if (pendingCount > MaxPendingPerSession || PendingGuildRequests.Count >= MaxTotalPending)
            {
                PendingBySession.AddOrUpdate(clientSessionId, 0, (_, v) => Math.Max(0, v - 1));
                SendSimpleResponse(session, responseMsgId, new { Success = false, Message = "请求过于频繁，请稍后重试" });
                return;
            }

            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request!);
            byte[] routedPayload = Shared.RouteMetadata.AttachRequestId(payload, requestId);
            byte[] packet = PacketBuilder.BuildPacket(dbMsgId, routedPayload, out int totalLength);

            try
            {
                PendingGuildRequests[requestId] = new PendingGuildRequest
                {
                    SessionId = clientSessionId,
                    ResponseMsgId = responseMsgId,
                    DbResponseMsgId = dbMsgId + 100,
                    GatewaySession = session
                };
                dbClient.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                if (PendingGuildRequests.TryRemove(requestId, out var failed) && failed != null && failed.SessionId > 0)
                {
                    PendingBySession.AddOrUpdate(failed.SessionId, 0, (_, v) => Math.Max(0, v - 1));
                }
                Shared.Log.Error($"公会 DB 请求发送失败 MsgId:{dbMsgId} SessionId:{clientSessionId} Exception:{ex}");
                SendSimpleResponse(session, responseMsgId, new { Success = false, Message = "发送DB请求失败" });
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>每 5 秒清理超时待处理请求并回失败响应（防无界增长 + 客户端挂起）。</summary>
        private static void SweepExpiredPendingRequests()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastPendingSweepTicks);
            if (now - last < TimeSpan.FromSeconds(5).Ticks)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref lastPendingSweepTicks, now, last) != last)
            {
                return;
            }
            foreach (var kv in PendingGuildRequests.ToArray())
            {
                if (now - kv.Value.CreatedAtTicks > PendingRequestTimeout.Ticks)
                {
                    if (PendingGuildRequests.TryRemove(kv.Key, out var removed) && removed != null)
                    {
                        if (removed.SessionId > 0)
                        {
                            PendingBySession.AddOrUpdate(removed.SessionId, 0, (_, v) => Math.Max(0, v - 1));
                        }
                        TrySendTimeoutResponse(removed);
                    }
                }
            }
        }

        private static void TrySendTimeoutResponse(PendingGuildRequest pending)
        {
            if (pending.GatewaySession == null || pending.SessionId <= 0)
            {
                return;
            }
            try
            {
                SendResponseBySessionId(pending.GatewaySession, pending.SessionId, pending.ResponseMsgId,
                    new { Success = false, Message = "服务器处理超时，请重试" });
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"公会超时响应发送异常 SessionId:{pending.SessionId} Exception:{ex.Message}");
            }
        }

        /// <summary>经网关会话向指定客户端会话发送响应（目标会话 ID 路由）。</summary>
        private static void SendResponseBySessionId(global::Network.ISession gatewaySession, long clientSessionId, int msgId, object response)
        {
            byte[] payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(response), clientSessionId);
            byte[] packet = PacketBuilder.BuildPacket(msgId, payload, out int totalLength);
            try
            {
                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>直接向客户端会话发送简单响应（失败路径，不经过 DB）。</summary>
        private static void SendSimpleResponse<T>(ClientSessionWrapper session, int msgId, T response)
        {
            try
            {
                SendResponseBySessionId(session, session.SessionId, msgId, response!);
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"公会简单响应发送异常 SessionId:{session.SessionId} Exception:{ex.Message}");
            }
        }
    }
}
