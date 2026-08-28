using System;
using System.Collections.Concurrent;
using Game.Network;
using Shared;
using Shared.Messages;
using Shared.Messages.Social;
using Game.Managers;
using Network.Routing;
namespace Game.Handlers
{
    /// <summary>
    /// 好友系统处理器 —— 基础模块（字段/请求状态/注册表/共享 DB 请求与响应辅助）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        private static readonly ConcurrentDictionary<long, PendingFriendRequest> PendingFriendRequests = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> BlacklistCache = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> FriendCache = new();
        private static readonly ConcurrentDictionary<string, DateTime> InviteDedupCache = new();
        private static readonly ConcurrentDictionary<int, DateTime> InviteRateLimit = new();
        private static readonly ConcurrentDictionary<long, PendingInvite> PendingInvites = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<long, byte>> PendingInvitesByInvitee = new();
        private static long requestIdSeed = DateTime.UtcNow.Ticks;
        private static long inviteIdSeed = DateTime.UtcNow.Ticks;
        private static readonly TimeSpan InviteMinInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan InviteDedupWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan InviteExpireWindow = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PendingRequestTimeout = TimeSpan.FromSeconds(30);
        private static long lastPendingSweepTicks;

        /// <summary>发送者的好友列表是否已从 DB 加载（缓存 warm）。用于聊天私聊好友校验的 fail-safe：缓存未加载时不强制拦截。</summary>
        public static bool IsFriendListLoaded(int userId) => userId > 0 && FriendCache.ContainsKey(userId);

        /// <summary>
        /// 清理长期未回执的 PendingFriendRequests（DB 无响应/掉线时防无界增长）。
        /// 仅在待处理项超过阈值且距上次清扫 ≥ 5 秒时执行。
        /// </summary>
        private static void SweepExpiredPendingRequests()
        {
            if (PendingFriendRequests.Count < 256) return;
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastPendingSweepTicks);
            if (now - last < TimeSpan.FromSeconds(5).Ticks) return;
            if (Interlocked.CompareExchange(ref lastPendingSweepTicks, now, last) != last) return;

            foreach (var kv in PendingFriendRequests.ToArray())
            {
                if (now - kv.Value.CreatedAtTicks > PendingRequestTimeout.Ticks)
                {
                    if (PendingFriendRequests.TryRemove(kv.Key, out _))
                    {
                        Shared.Log.Warning($"好友 DB 请求超时清理 RequestId:{kv.Key} SessionId:{kv.Value.SessionId} MsgId:{kv.Value.ResponseMsgId}");
                    }
                }
            }
        }

        private sealed class PendingFriendRequest
        {
            public long SessionId { get; set; }
            public int ResponseMsgId { get; set; }
            /// <summary>请求发起时间（超时清理用）。</summary>
            public long CreatedAtTicks { get; set; } = DateTime.UtcNow.Ticks;
            /// <summary>请求发起时的网关会话（用于 DB 回包回发客户端；DB 回包路径不能使用 Game↔DB 连接发送客户端消息）。</summary>
            public global::Network.ISession? GatewaySession { get; set; }
            public bool IsInviteResolve { get; set; }
            public bool IsInviteFriendCheck { get; set; }
            public bool IsInviteSenderResolve { get; set; }
            public bool IsFriendApplyCreate { get; set; }
            public bool IsFriendApplyHandle { get; set; }
            public string InviteRoomId { get; set; } = string.Empty;
            public string InviteSceneType { get; set; } = string.Empty;
            public string InviteRoomName { get; set; } = string.Empty;
            public int InviteTargetUserId { get; set; }
            public string FriendApplyMessage { get; set; } = string.Empty;
            public bool FriendApplyAccept { get; set; }
        }

        private sealed class PendingInvite
        {
            public long InviteId { get; set; }
            public int InviterUserId { get; set; }
            public int InviteeUserId { get; set; }
            public string InviterUniqueId { get; set; } = string.Empty;
            public string InviterNickname { get; set; } = string.Empty;
            public string RoomId { get; set; } = string.Empty;
            public string SceneType { get; set; } = string.Empty;
            public string RoomName { get; set; } = string.Empty;
            public DateTime CreateTimeUtc { get; set; }
            public DateTime ExpireAtUtc { get; set; }
        }

        /// <summary>
        /// 向指定的 MessageRouter 注册好友与黑名单相关的消息处理器（旧 JSON 路由回退层）。
        /// </summary>
        ///
        /// <remarks>注册以下消息标识符的处理器：MessageIds.AddFriendReq、MessageIds.RemoveFriendReq、MessageIds.SetFriendRemarkReq、MessageIds.GetFriendsReq、MessageIds.InviteGameReq、MessageIds.AddBlacklistReq、MessageIds.RemoveBlacklistReq、MessageIds.GetBlacklistReq。</remarks>
        /// <param name="router">用于注册消息处理器的 MessageRouter 实例。</param>
        public static void Register(MessageRouter router)
        {
            RegisterRequest<AddFriendRequest>(router, MessageIds.AddFriendReq, HandleAddFriendRequest);
            RegisterRequest<RemoveFriendRequest>(router, MessageIds.RemoveFriendReq, HandleRemoveFriendRequest);
            RegisterRequest<SetFriendRemarkRequest>(router, MessageIds.SetFriendRemarkReq, HandleSetFriendRemarkRequest);
            RegisterRequest<GetFriendsRequest>(router, MessageIds.GetFriendsReq, HandleGetFriendsRequest);
            RegisterRequest<InviteGameRequest>(router, MessageIds.InviteGameReq, HandleInviteGameRequest);
            RegisterRequest<AddBlacklistRequest>(router, MessageIds.AddBlacklistReq, HandleAddBlacklistRequest);
            RegisterRequest<RemoveBlacklistRequest>(router, MessageIds.RemoveBlacklistReq, HandleRemoveBlacklistRequest);
            RegisterRequest<GetBlacklistRequest>(router, MessageIds.GetBlacklistReq, HandleGetBlacklistRequest);
            RegisterRequest<FriendApplyRequest>(router, MessageIds.FriendApplyReq, HandleFriendApplyRequest);
            RegisterRequest<FriendApplyListRequest>(router, MessageIds.FriendApplyListReq, HandleFriendApplyListRequest);
            RegisterRequest<FriendApplyHandleRequest>(router, MessageIds.FriendApplyHandleReq, HandleFriendApplyHandleRequest);
            RegisterRequest<InviteGameAckRequest>(router, MessageIds.InviteGameAckReq, HandleInviteGameAckRequest);
        }

        /// <summary>
        /// 旧路由适配：反序列化 JSON 负载后调用强类型业务方法（业务方法保留 req==null 校验并回错误响应）。
        /// 该路径仅在新协议 Dispatcher 未注册 MsgId 时作为回退使用。
        /// </summary>
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

        /// <summary>
        /// 将序列化的请求发送到数据库客户端并注册等待响应的条目。
        /// </summary>
        /// <remarks>为请求生成唯一 RequestId，将请求序列化为 UTF-8 并附加路由元数据，再构建并发送数据包；发送失败时会从 PendingFriendRequests
        /// 中移除已注册项；无论成功与否都会将用于构建数据包的数组归还给 ArrayPool。</remarks>
        /// <typeparam name="TRequest">要发送到数据库并序列化的请求类型。</typeparam>
        /// <param name="dbMsgId">数据库端接收消息的消息 ID。</param>
        /// <param name="request">要序列化并发送的请求对象。</param>
        /// <param name="clientSessionId">客户端会话 ID，用于在响应到达时路由回该会话。</param>
        /// <param name="responseMsgId">期望接收的响应消息 ID，用于构建待处理项。</param>
        /// <param name="configurePending">可选操作，用于在注册待处理响应前配置 PendingFriendRequest 的额外字段。</param>
        /// <returns>若成功将请求发送并注册为待处理响应则返回 true；若发生错误或数据库客户端为空则返回 false。</returns>
        private static bool TrySendDbRequest<TRequest>(int dbMsgId, global::Network.ISession? gatewaySession, TRequest request, long clientSessionId, int responseMsgId, Action<PendingFriendRequest>? configurePending = null)
        {
            CleanupInviteDedupCache();
            SweepExpiredPendingRequests();

            var dbClient = GameServerApp.DbClient;
            if (dbClient == null)
            {
                Shared.Log.Error($"Game 向 DB 发送请求失败：DB 连接为空 MsgId:{dbMsgId} SessionId:{clientSessionId}");
                return false;
            }

            long requestId = System.Threading.Interlocked.Increment(ref requestIdSeed);
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] routedPayload = Shared.RouteMetadata.AttachRequestId(payload, requestId);
            byte[] packet = PacketBuilder.BuildPacket(dbMsgId, routedPayload, out int totalLength);

            try
            {
                var pending = new PendingFriendRequest
                {
                    SessionId = clientSessionId,
                    ResponseMsgId = responseMsgId,
                    GatewaySession = gatewaySession
                };
                configurePending?.Invoke(pending);
                PendingFriendRequests[requestId] = pending;
                dbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                return true;
            }
            catch (Exception ex)
            {
                PendingFriendRequests.TryRemove(requestId, out _);
                Shared.Log.Error($"Game 发送 DB 请求失败 MsgId:{dbMsgId} SessionId:{clientSessionId} RequestId:{requestId} Exception:{ex}");
                return false;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        /// <summary>
        /// 序列化响应为 UTF-8 JSON，附加目标会话 ID，并构建发送到指定客户端会话的消息包。
        /// </summary>
        /// <remarks>使用 Shared.RouteMetadata 将目标会话 ID 附加到负载，使用 PacketBuilder 构建字节包。发送后在 finally
        /// 块中将临时缓冲区返回到 System.Buffers.ArrayPool<byte>.Shared。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="session">目标客户端会话的封装，用于发送构建后的数据包。</param>
        /// <param name="msgId">用于构建数据包的消息标识符。</param>
        /// <param name="response">要序列化为 UTF-8 JSON 并作为负载发送的响应对象。</param>
        private static void SendSimpleResponse<T>(ClientSessionWrapper session, int msgId, T response)
        {
            var payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(response), session.SessionId);
            var packet = PacketBuilder.BuildPacket(msgId, payload, out int packetLength);
            try
            {
                session.Send(packet.AsSpan(0, packetLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}
