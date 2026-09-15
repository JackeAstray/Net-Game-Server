using System;
using System.Linq;
using Network;
using Shared;
using Shared.Messages;
using Shared.Messages.Db;
using Shared.Messages.Social;

namespace Game.Handlers
{
    /// <summary>
    /// 公会系统处理器 —— DB 回包处理模块（按 RequestId 匹配待处理请求并回发客户端）。
    /// 与 GuildHandler.cs 同属一个 partial class。
    /// </summary>
    public static partial class GuildHandler
    {
        /// <summary>
        /// 处理 DB 节点回包：按尾部 __requestId 匹配待处理请求，校验响应 msgid 后回发客户端。
        /// 返回 true 表示已消费（含无匹配）；被 GameServerApp 的 DB 收包回调调用。
        /// </summary>
        public static bool TryHandleDbResponse(ISession dbSession, int dbMsgId, ReadOnlyMemory<byte> payload)
        {
            if (!Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload))
            {
                Shared.Log.Warning($"Game 收到缺少 RequestId 的公会 DB 回包 MsgId:{dbMsgId}");
                return false;
            }
            if (!PendingGuildRequests.TryGetValue(requestId, out var pending))
            {
                Shared.Log.Warning($"Game 未找到匹配的公会待处理请求 RequestId:{requestId} MsgId:{dbMsgId}");
                return false;
            }
            if (pending.DbResponseMsgId != 0 && pending.DbResponseMsgId != dbMsgId)
            {
                Shared.Log.Warning($"Game 公会 DB 回包 MsgId:{dbMsgId} 与请求期望 {pending.DbResponseMsgId} 不符，RequestId:{requestId}，已拒绝");
                return false;
            }
            if (!PendingGuildRequests.TryRemove(requestId, out pending))
            {
                return false;
            }

            if (pending.SessionId > 0)
            {
                DecrementPendingBySession(pending.SessionId);
            }
            if (pending.GatewaySession == null || pending.SessionId <= 0)
            {
                return true;
            }

            try
            {
                switch (dbMsgId)
                {
                    case MessageIds.DbGuildCreateRes:
                        SendJson<GuildCreateResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildMyRes:
                        SendGuildMyResponse(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildJoinRes:
                        SendJson<GuildJoinResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildLeaveRes:
                        SendJson<GuildLeaveResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildDisbandRes:
                        SendJson<GuildDisbandResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildKickRes:
                        SendJson<GuildKickResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildTransferRes:
                        SendJson<GuildTransferResponse>(pending, cleanPayload);
                        InvalidateGuildCacheForPending(pending);
                        break;
                    case MessageIds.DbGuildUpdateDeclRes:
                        SendJson<GuildUpdateDeclResponse>(pending, cleanPayload);
                        break;
                    default:
                        Shared.Log.Warning($"Game 公会 DB 回包未处理 MsgId:{dbMsgId} RequestId:{requestId}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"Game 公会 DB 回包处理异常 MsgId:{dbMsgId} RequestId:{requestId} Exception:{ex.Message}");
            }
            return true;
        }

        private static void SendJson<T>(PendingGuildRequest pending, ReadOnlyMemory<byte> cleanPayload)
        {
            var response = Shared.Json.DeserializeFromUtf8Bytes<T>(cleanPayload.Span);
            if (response == null)
            {
                // P3 修复：原实现 response == null 时静默不发包 → 客户端只能等到自身超时，
                // 与 FriendHandler 同场景（回 Success=false）行为不一致。
                // 公会各响应类型均只含 Success/Message 两个必需字段，匿名对象可被客户端正确反序列化。
                Shared.Log.Error($"Game 公会 DB 回包反序列化失败（已回显式失败）ResponseMsgId:{pending.ResponseMsgId} SessionId:{pending.SessionId} DbMsg:{typeof(T).Name}");
                SendResponseBySessionId(pending.GatewaySession!, pending.SessionId, pending.ResponseMsgId,
                    new { Success = false, Message = "服务端处理失败，请稍后重试" });
                return;
            }
            SendResponseBySessionId(pending.GatewaySession!, pending.SessionId, pending.ResponseMsgId, response!);
        }

        /// <summary>DB 成员项（DbGuildMemberItem）映射为客户端成员项（GuildMemberItem）。</summary>
        private static void SendGuildMyResponse(PendingGuildRequest pending, ReadOnlyMemory<byte> cleanPayload)
        {
            var dbResp = Shared.Json.DeserializeFromUtf8Bytes<DbGuildMyResponse>(cleanPayload.Span);
            if (dbResp == null)
            {
                // P3 修复：与 SendJson 同理，解析失败时不再静默丢弃（预热请求本身不回包，保持静默）。
                if (!pending.IsGuildMyWarmup)
                {
                    Shared.Log.Error($"Game 公会 DB 回包反序列化失败（已回显式失败）MsgId:{pending.ResponseMsgId} SessionId:{pending.SessionId}");
                    SendResponseBySessionId(pending.GatewaySession!, pending.SessionId, pending.ResponseMsgId,
                        new { Success = false, Message = "服务端处理失败，请稍后重试" });
                }
                return;
            }

            // 写公会成员缓存（供公会频道广播；未加入公会时 GuildId==0 → 空成员列表）
            int userId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);
            if (userId > 0)
            {
                guildMemberCache[userId] = new GuildMemberCacheEntry
                {
                    MemberIds = (dbResp.Members ?? new System.Collections.Generic.List<DbGuildMemberItem>())
                        .Select(m => m.UserId).Where(u => u > 0).ToArray(),
                    // P3 修复：记录公会 ID，供公会频道入口区分"未入会"与"公会仅我一人"
                    GuildId = dbResp.GuildId,
                    LoadedAtUtc = DateTime.UtcNow
                };
            }
            if (pending.IsGuildMyWarmup)
            {
                return; // 登录预热：只写缓存，不回发客户端
            }

            var clientResp = new GuildMyResponse
            {
                Success = dbResp.Success,
                Message = dbResp.Message,
                GuildId = dbResp.GuildId,
                Name = dbResp.Name,
                OwnerUserId = dbResp.OwnerUserId,
                Declaration = dbResp.Declaration,
                Members = (dbResp.Members ?? new System.Collections.Generic.List<DbGuildMemberItem>())
                    .Select(m => new GuildMemberItem { UserId = m.UserId, Nickname = m.Nickname, Role = m.Role })
                    .ToList()
            };
            SendResponseBySessionId(pending.GatewaySession!, pending.SessionId, pending.ResponseMsgId, clientResp);
        }

        /// <summary>
        /// 公会结构变更后失效成员缓存（创建/加入/退出/解散/踢人/转让后调用）。
        ///
        /// P2 修复（授权滞后）：原实现只失效**操作者自己**的缓存，导致
        /// ① 被踢出/已退会/公会已解散的成员自身缓存仍有效 ≤ TTL(60s)，
        ///    而 <c>ChatHandler</c> 的公会频道投递名单正是取自**发送者自己的**缓存列表
        ///    → 该成员在窗口内仍能向全公会广播公会频道消息（踢人/解散本应即时断权）；
        /// ② 其余在线成员的同公会名单同样滞后 ≤60s（收不到新加入/已离开的成员变化）。
        ///
        /// 修法：操作者的缓存条目恰好包含该公会全部成员 userId，因此**先取快照再失效**，
        /// 对名单内每个 userId 一并失效即可覆盖整个公会，无需修改 DB 回包协议
        /// （DbGuild*Response 仅带 Success/Message，不含成员集合）。
        ///
        /// 作用域说明：<c>guildMemberCache</c> 与 <c>PlayerSessionManager</c> 同为 Game 进程内单例，
        /// 公会频道广播本就只覆盖本节点在线成员，故本修复与功能实际作用域一致。
        /// </summary>
        private static void InvalidateGuildCacheForPending(PendingGuildRequest pending)
        {
            int userId = Game.Managers.PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);
            if (userId <= 0)
            {
                return;
            }

            // 先取操作者的成员快照（含全体成员），再失效自己——顺序不可颠倒，失效后就读不到了。
            int[]? memberIds = GetCachedGuildMemberIds(userId);
            InvalidateGuildCache(userId);

            if (memberIds == null || memberIds.Length == 0)
            {
                // 操作者缓存未就绪/已过期（如刚加入公会的新成员）：只能失效其自身，
                // 其余成员的名单将在 TTL 到期后自然刷新。
                return;
            }

            foreach (var memberId in memberIds)
            {
                if (memberId > 0 && memberId != userId)
                {
                    InvalidateGuildCache(memberId);
                }
            }
        }
    }
}
