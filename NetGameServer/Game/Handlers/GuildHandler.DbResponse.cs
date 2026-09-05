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
                PendingBySession.AddOrUpdate(pending.SessionId, 0, (_, v) => Math.Max(0, v - 1));
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
                        break;
                    case MessageIds.DbGuildMyRes:
                        SendGuildMyResponse(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildJoinRes:
                        SendJson<GuildJoinResponse>(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildLeaveRes:
                        SendJson<GuildLeaveResponse>(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildDisbandRes:
                        SendJson<GuildDisbandResponse>(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildKickRes:
                        SendJson<GuildKickResponse>(pending, cleanPayload);
                        break;
                    case MessageIds.DbGuildTransferRes:
                        SendJson<GuildTransferResponse>(pending, cleanPayload);
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
            if (response != null)
            {
                SendResponseBySessionId(pending.GatewaySession!, pending.SessionId, pending.ResponseMsgId, response!);
            }
        }

        /// <summary>DB 成员项（DbGuildMemberItem）映射为客户端成员项（GuildMemberItem）。</summary>
        private static void SendGuildMyResponse(PendingGuildRequest pending, ReadOnlyMemory<byte> cleanPayload)
        {
            var dbResp = Shared.Json.DeserializeFromUtf8Bytes<DbGuildMyResponse>(cleanPayload.Span);
            if (dbResp == null)
            {
                return;
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
    }
}
