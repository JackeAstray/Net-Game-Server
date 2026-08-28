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
    /// 好友系统 —— 游戏邀请模块（发起/回执/待办邀请/去重限流/离线补发）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        /// <summary>
        /// 处理客户端的邀请游戏请求：验证会话与负载，解析 InviteGameRequest，校验好友 UniqueId，向 DB 请求解析好友并设置待处理项，必要时向客户端返回失败响应。
        /// </summary>
        /// <remarks>在校验失败时会向客户端发送失败响应。若校验通过，会向 DB 服务发送 DbResolveUserByUniqueId 请求并在 pending 中设置
        /// IsInviteResolve 与 InviteRoomId。依赖 PlayerSessionManager 和 GameServerApp.DbClient。</remarks>
        /// <param name="sessionBase">客户端会话基类（期望为 ClientSessionWrapper），用于发送响应并获取会话标识。</param>
        /// <param name="payload">请求负载的只读字节内存，反序列化为 InviteGameRequest。</param>
        internal static void HandleInviteGameRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<InviteGameRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.RoomId))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "房间ID不能为空" });
                return;
            }

            string friendUniqueId = req.FriendUniqueId.Trim();
            string roomId = req.RoomId.Trim();
            string sceneType = req.SceneType?.Trim() ?? string.Empty;
            string roomName = req.RoomName?.Trim() ?? string.Empty;

            DateTime now = DateTime.UtcNow;
            if (InviteRateLimit.TryGetValue(userId, out var lastInviteTime) && now - lastInviteTime < InviteMinInterval)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "邀请过于频繁，请稍后再试" });
                return;
            }

            string inviteDedupKey = $"{userId}:{friendUniqueId}:{roomId}";
            if (InviteDedupCache.TryGetValue(inviteDedupKey, out var dedupTime) && now - dedupTime < InviteDedupWindow)
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "重复邀请，请稍后再试" });
                return;
            }

            InviteRateLimit[userId] = now;
            InviteDedupCache[inviteDedupKey] = now;

            var dbReq = new Shared.Messages.Db.DbResolveUserByUniqueIdRequest
            {
                UniqueId = friendUniqueId
            };

            if (!TrySendDbRequest(MessageIds.DbResolveUserByUniqueIdReq, dbReq, session.SessionId, MessageIds.InviteGameRes, pending =>
            {
                pending.IsInviteResolve = true;
                pending.InviteRoomId = roomId;
                pending.InviteSceneType = sceneType;
                pending.InviteRoomName = roomName;
            }))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static PendingInvite CreatePendingInvite(int inviterUserId, int inviteeUserId, string inviterUniqueId, string inviterNickname, string roomId, string sceneType, string roomName)
        {
            var invite = new PendingInvite
            {
                InviteId = System.Threading.Interlocked.Increment(ref inviteIdSeed),
                InviterUserId = inviterUserId,
                InviteeUserId = inviteeUserId,
                InviterUniqueId = inviterUniqueId,
                InviterNickname = string.IsNullOrWhiteSpace(inviterNickname) ? $"Player_{inviterUserId}" : inviterNickname,
                RoomId = roomId ?? string.Empty,
                SceneType = sceneType ?? string.Empty,
                RoomName = roomName ?? string.Empty,
                CreateTimeUtc = DateTime.UtcNow,
                ExpireAtUtc = DateTime.UtcNow.Add(InviteExpireWindow)
            };

            PendingInvites[invite.InviteId] = invite;
            var inviteeIndex = PendingInvitesByInvitee.GetOrAdd(inviteeUserId, _ => new ConcurrentDictionary<long, byte>());
            inviteeIndex[invite.InviteId] = 0;
            return invite;
        }

        private static void SendInviteNotification(global::Network.ISession gameSession, long targetSessionId, PendingInvite invite)
        {
            var notif = new InviteGameNotification
            {
                InviterUniqueId = invite.InviterUniqueId,
                InviterNickname = invite.InviterNickname,
                RoomId = invite.RoomId,
                SceneType = invite.SceneType,
                RoomName = invite.RoomName
            };
            SendResponseBySessionId(gameSession, targetSessionId, MessageIds.InviteGameNotif, notif);
        }

        private static void DeliverPendingInvites(global::Network.ISession gameSession, long inviteeSessionId, int inviteeUserId)
        {
            CleanupExpiredPendingInvites(gameSession);

            if (!PendingInvitesByInvitee.TryGetValue(inviteeUserId, out var inviteIds) || inviteIds.Count <= 0)
            {
                return;
            }

            foreach (var inviteId in inviteIds.Keys)
            {
                if (!PendingInvites.TryGetValue(inviteId, out var invite))
                {
                    inviteIds.TryRemove(inviteId, out _);
                    continue;
                }

                if (invite.ExpireAtUtc <= DateTime.UtcNow)
                {
                    RemovePendingInvite(inviteId);
                    continue;
                }

                SendInviteNotification(gameSession, inviteeSessionId, invite);
            }
        }

        private static void CleanupExpiredPendingInvites(global::Network.ISession gameSession)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var pair in PendingInvites)
            {
                if (pair.Value.ExpireAtUtc > now)
                {
                    continue;
                }

                if (!PendingInvites.TryRemove(pair.Key, out var expiredInvite))
                {
                    continue;
                }

                if (PendingInvitesByInvitee.TryGetValue(expiredInvite.InviteeUserId, out var inviteeIndex))
                {
                    inviteeIndex.TryRemove(pair.Key, out _);
                    if (inviteeIndex.IsEmpty)
                    {
                        PendingInvitesByInvitee.TryRemove(expiredInvite.InviteeUserId, out _);
                    }
                }

                long inviterSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(expiredInvite.InviterUserId);
                if (inviterSessionId > 0)
                {
                    var expireNotif = new InviteGameAckNotification
                    {
                        InviteeUniqueId = string.Empty,
                        InviteeNickname = string.Empty,
                        RoomId = expiredInvite.RoomId,
                        Accept = false,
                        Reason = "邀请已过期"
                    };
                    SendResponseBySessionId(gameSession, inviterSessionId, MessageIds.InviteGameAckNotif, expireNotif);
                }
            }
        }

        private static void RemovePendingInvite(long inviteId)
        {
            if (!PendingInvites.TryRemove(inviteId, out var invite))
            {
                return;
            }

            if (PendingInvitesByInvitee.TryGetValue(invite.InviteeUserId, out var inviteeIndex))
            {
                inviteeIndex.TryRemove(inviteId, out _);
                if (inviteeIndex.IsEmpty)
                {
                    PendingInvitesByInvitee.TryRemove(invite.InviteeUserId, out _);
                }
            }
        }

        /// <summary>
        /// 将响应序列化为 UTF-8 字节、附加目标会话 ID、构建数据包并通过指定会话发送。
        /// </summary>
        /// <remarks>序列化使用 Shared.Json.SerializeToUtf8Bytes，负载中附加路由元数据；数据包由 PacketBuilder.BuildPacket
        /// 构建，发送完成后在 finally 块将用于构建的字节数组归还至 ArrayPool<byte>.Shared。</remarks>
        /// <typeparam name="T">响应对象的类型。</typeparam>
        /// <param name="gameSession">用于发送数据包的会话实例。</param>
        /// <param name="sessionId">目标会话的唯一标识符（会话 ID）。</param>
        /// <param name="msgId">数据包的消息标识符。</param>
        /// <param name="response">要序列化并发送的响应对象。</param>
        private static void SendResponseBySessionId<T>(global::Network.ISession gameSession, long sessionId, int msgId, T response)
        {
            byte[] payload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(response), sessionId);
            byte[] packet = PacketBuilder.BuildPacket(msgId, payload, out int packetLength);
            try
            {
                gameSession.Send(packet.AsSpan(0, packetLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        internal static void HandleInviteGameAckRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<InviteGameAckRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            int inviteeUserId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (inviteeUserId <= 0)
            {
                SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.InviterUniqueId))
            {
                SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse { Success = false, Message = "邀请人UniqueId不能为空" });
                return;
            }

            CleanupExpiredPendingInvites(session);

            PendingInvite? matchedInvite = PendingInvites.Values
                .Where(invite => invite.InviteeUserId == inviteeUserId
                    && string.Equals(invite.InviterUniqueId, req.InviterUniqueId.Trim(), StringComparison.Ordinal)
                    && string.Equals(invite.RoomId, req.RoomId?.Trim() ?? string.Empty, StringComparison.Ordinal))
                .OrderByDescending(invite => invite.CreateTimeUtc)
                .FirstOrDefault();

            if (matchedInvite == null)
            {
                SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse { Success = false, Message = "邀请不存在或已过期" });
                return;
            }

            if (matchedInvite.ExpireAtUtc <= DateTime.UtcNow)
            {
                RemovePendingInvite(matchedInvite.InviteId);
                SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse { Success = false, Message = "邀请已过期" });
                return;
            }

            RemovePendingInvite(matchedInvite.InviteId);

            long inviterSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(matchedInvite.InviterUserId);
            if (inviterSessionId > 0)
            {
                string inviteeUid = PlayerSessionManager.Instance.GetUidBySessionId(session.SessionId);
                var notif = new InviteGameAckNotification
                {
                    InviteeUniqueId = inviteeUid,
                    InviteeNickname = string.IsNullOrWhiteSpace(inviteeUid) ? $"Player_{inviteeUserId}" : inviteeUid,
                    RoomId = req.RoomId?.Trim() ?? string.Empty,
                    Accept = req.Accept,
                    Reason = req.Reason?.Trim() ?? string.Empty
                };
                SendResponseBySessionId(session, inviterSessionId, MessageIds.InviteGameAckNotif, notif);
            }

            SendSimpleResponse(session, MessageIds.InviteGameAckRes, new InviteGameAckResponse
            {
                Success = true,
                Message = req.Accept ? "已接受邀请" : "已拒绝邀请"
            });
        }

        private static void CleanupInviteDedupCache()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var pair in InviteDedupCache)
            {
                if (now - pair.Value > InviteDedupWindow)
                {
                    InviteDedupCache.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
