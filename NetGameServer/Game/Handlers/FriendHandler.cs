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

        private sealed class PendingFriendRequest
        {
            public long SessionId { get; set; }
            public int ResponseMsgId { get; set; }
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
        /// 向指定的 MessageRouter 注册好友与黑名单相关的消息处理器。
        /// </summary>
        ///
        /// <remarks>注册以下消息标识符的处理器：MessageIds.AddFriendReq、MessageIds.RemoveFriendReq、MessageIds.SetFriendRemarkReq、MessageIds.GetFriendsReq、MessageIds.InviteGameReq、MessageIds.AddBlacklistReq、MessageIds.RemoveBlacklistReq、MessageIds.GetBlacklistReq。</remarks>
        /// <param name="router">用于注册消息处理器的 MessageRouter 实例。</param>
        public static void Register(MessageRouter router)
        {
            router.RegisterHandler(MessageIds.AddFriendReq, (s, p) => HandleAddFriendRequest(s, p));
            router.RegisterHandler(MessageIds.RemoveFriendReq, (s, p) => HandleRemoveFriendRequest(s, p));
            router.RegisterHandler(MessageIds.SetFriendRemarkReq, (s, p) => HandleSetFriendRemarkRequest(s, p));
            router.RegisterHandler(MessageIds.GetFriendsReq, (s, p) => HandleGetFriendsRequest(s, p));
            router.RegisterHandler(MessageIds.InviteGameReq, (s, p) => HandleInviteGameRequest(s, p));
            router.RegisterHandler(MessageIds.AddBlacklistReq, (s, p) => HandleAddBlacklistRequest(s, p));
            router.RegisterHandler(MessageIds.RemoveBlacklistReq, (s, p) => HandleRemoveBlacklistRequest(s, p));
            router.RegisterHandler(MessageIds.GetBlacklistReq, (s, p) => HandleGetBlacklistRequest(s, p));
            router.RegisterHandler(MessageIds.FriendApplyReq, (s, p) => HandleFriendApplyRequest(s, p));
            router.RegisterHandler(MessageIds.FriendApplyListReq, (s, p) => HandleFriendApplyListRequest(s, p));
            router.RegisterHandler(MessageIds.FriendApplyHandleReq, (s, p) => HandleFriendApplyHandleRequest(s, p));
            router.RegisterHandler(MessageIds.InviteGameAckReq, (s, p) => HandleInviteGameAckRequest(s, p));
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
        private static bool TrySendDbRequest<TRequest>(int dbMsgId, TRequest request, long clientSessionId, int responseMsgId, Action<PendingFriendRequest>? configurePending = null)
        {
            CleanupInviteDedupCache();

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
                    ResponseMsgId = responseMsgId
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
