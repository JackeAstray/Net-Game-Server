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
    /// 好友系统 —— DB 响应处理模块（好友/黑名单/申请/邀请各类 DB 回包统一入口）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        /// <summary>
        /// 根据数据库回包的消息标识解析并处理响应，向匹配的会话发送响应或通知，必要时更新缓存并触发后续 DB 请求。
        /// </summary>
        /// <remarks>根据载荷中的 RequestId 匹配待处理项，匹配失败或载荷缺少 RequestId 时记录警告并返回 false。对不同 dbMsgId
        /// 执行相应的反序列化、发送会话响应或通知、更新好友/黑名单缓存，并在邀请流程中可能发起后续 DB 请求；该方法会移除已处理的待处理请求并调用 PlayerSessionManager
        /// 与相关缓存操作。</remarks>
        /// <param name="gameSession">用于发送响应、通知和转发数据的当前网络会话。</param>
        /// <param name="dbMsgId">数据库返回的消息标识，用于选择对应的解析与处理逻辑。</param>
        /// <param name="payload">包含路由元数据和序列化响应体的原始只读字节序列。</param>
        /// <returns>已成功识别并处理该 DB 回包则返回 true；未处理或匹配失败则返回 false。</returns>
        public static bool TryHandleDbResponse(global::Network.ISession gameSession, int dbMsgId, ReadOnlyMemory<byte> payload)
        {
            if (!Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload))
            {
                Shared.Log.Warning($"Game 收到缺少 RequestId 的 DB 回包 MsgId:{dbMsgId}");
                return false;
            }

            if (!PendingFriendRequests.TryRemove(requestId, out var pending))
            {
                Shared.Log.Warning($"Game 未找到匹配的待处理 DB 请求 RequestId:{requestId} MsgId:{dbMsgId}");
                return false;
            }

            int requesterUserId = PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);

            switch (dbMsgId)
            {
                case MessageIds.DbAddFriendRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddFriendResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析添加好友 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new AddFriendResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "添加好友失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbRemoveFriendRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveFriendResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析删除好友 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new RemoveFriendResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "删除好友失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbSetFriendRemarkRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbSetFriendRemarkResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析设置备注 DB 回包失败 RequestId:{requestId}");
                        }
                        var res = new SetFriendRemarkResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "设置备注失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbGetFriendsRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetFriendsResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析获取好友列表 DB 回包失败 RequestId:{requestId}");
                        }

                        if (pending.IsInviteFriendCheck)
                        {
                            var inviteRes = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };
                            if (dbRes?.Success != true)
                            {
                                inviteRes.Message = dbRes?.Message ?? "获取好友列表失败";
                                SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                                return true;
                            }

                            bool isFriend = dbRes.Friends?.Exists(f => f.FriendUserId == pending.InviteTargetUserId) == true;
                            if (!isFriend)
                            {
                                inviteRes.Message = "仅可邀请好友";
                                SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                                return true;
                            }

                            var senderResolveReq = new Shared.Messages.Db.DbResolveUserByUserIdRequest
                            {
                                UserId = requesterUserId
                            };

                            bool sent = TrySendDbRequest(MessageIds.DbResolveUserByUserIdReq, senderResolveReq, pending.SessionId, pending.ResponseMsgId, nextPending =>
                            {
                                nextPending.IsInviteSenderResolve = true;
                                nextPending.InviteRoomId = pending.InviteRoomId;
                                nextPending.InviteTargetUserId = pending.InviteTargetUserId;
                                nextPending.InviteSceneType = pending.InviteSceneType;
                                nextPending.InviteRoomName = pending.InviteRoomName;
                            });

                            if (!sent)
                            {
                                inviteRes.Message = "发送DB请求失败";
                                SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            }

                            return true;
                        }

                        var friends = dbRes?.Friends == null
                            ? Array.Empty<FriendInfo>()
                            : dbRes.Friends.ConvertAll(f => new FriendInfo
                            {
                                FriendUserId = f.FriendUserId,
                                FriendUniqueId = f.FriendUniqueId,
                                Nickname = f.FriendNickname,
                                Remark = f.Remark,
                                IsOnline = PlayerSessionManager.Instance.GetSessionIdByUserId(f.FriendUserId) > 0
                            }).ToArray();

                        if (requesterUserId > 0)
                        {
                            SetFriendCache(requesterUserId, friends);
                        }

                        var res = new GetFriendsResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "获取好友列表失败",
                            Friends = friends
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbAddBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析添加黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        if (dbRes?.Success == true && requesterUserId > 0 && dbRes.TargetUserId > 0)
                        {
                            AddBlacklistCache(requesterUserId, dbRes.TargetUserId);
                        }

                        var res = new AddBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "添加黑名单失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbRemoveBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析移除黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        if (dbRes?.Success == true && requesterUserId > 0 && dbRes.TargetUserId > 0)
                        {
                            RemoveBlacklistCache(requesterUserId, dbRes.TargetUserId);
                        }

                        var res = new RemoveBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "移除黑名单失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbGetBlacklistRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetBlacklistResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析获取黑名单 DB 回包失败 RequestId:{requestId}");
                        }
                        var blacklists = dbRes?.Blacklists == null
                            ? Array.Empty<BlacklistInfo>()
                            : dbRes.Blacklists.ConvertAll(b => new BlacklistInfo
                            {
                                BlockedUserId = b.BlockedUserId,
                                BlockedUniqueId = b.BlockedUniqueId,
                                BlockedNickname = b.BlockedNickname,
                                AddTime = b.AddTime
                            }).ToArray();

                        if (dbRes?.Success == true && requesterUserId > 0)
                        {
                            SetBlacklistCache(requesterUserId, blacklists);
                        }

                        var res = new GetBlacklistResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "获取黑名单失败",
                            Blacklists = blacklists
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbCreateFriendApplyRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbCreateFriendApplyResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析创建好友申请 DB 回包失败 RequestId:{requestId}");
                        }

                        if (pending.IsFriendApplyCreate && dbRes?.Success == true && dbRes.TargetUserId > 0)
                        {
                            long targetSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(dbRes.TargetUserId);
                            if (targetSessionId > 0)
                            {
                                int requesterUserIdFromSession = PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);
                                string requesterUid = PlayerSessionManager.Instance.GetUidBySessionId(pending.SessionId);
                                var notif = new FriendApplyNotification
                                {
                                    ApplyId = dbRes.ApplyId,
                                    RequesterUserId = requesterUserIdFromSession,
                                    RequesterUniqueId = requesterUid,
                                    RequesterNickname = string.IsNullOrWhiteSpace(requesterUid) ? $"Player_{requesterUserIdFromSession}" : requesterUid,
                                    Message = pending.FriendApplyMessage,
                                    CreateTimeUtc = DateTime.UtcNow
                                };
                                SendResponseBySessionId(gameSession, targetSessionId, MessageIds.FriendApplyNotif, notif);
                            }
                        }

                        var res = new FriendApplyResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "发送好友申请失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbGetFriendApplyListRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetFriendApplyListResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析获取好友申请列表 DB 回包失败 RequestId:{requestId}");
                        }

                        var applies = dbRes?.Applies == null
                            ? Array.Empty<FriendApplyInfo>()
                            : dbRes.Applies.ConvertAll(a => new FriendApplyInfo
                            {
                                ApplyId = a.ApplyId,
                                RequesterUserId = a.RequesterUserId,
                                RequesterUniqueId = a.RequesterUniqueId,
                                RequesterNickname = a.RequesterNickname,
                                Message = a.Message,
                                Status = a.Status,
                                CreateTimeUtc = a.CreateTimeUtc
                            }).ToArray();

                        var res = new FriendApplyListResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "获取好友申请列表失败",
                            Applies = applies
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbHandleFriendApplyRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbHandleFriendApplyResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析处理好友申请 DB 回包失败 RequestId:{requestId}");
                        }

                        if (pending.IsFriendApplyHandle && dbRes?.Success == true && dbRes.RequesterUserId > 0)
                        {
                            long requesterSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(dbRes.RequesterUserId);
                            if (requesterSessionId > 0)
                            {
                                int receiverUserId = PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);
                                string receiverUid = PlayerSessionManager.Instance.GetUidBySessionId(pending.SessionId);
                                var notif = new InviteGameAckNotification
                                {
                                    InviteeUniqueId = receiverUid,
                                    InviteeNickname = string.IsNullOrWhiteSpace(receiverUid) ? $"Player_{receiverUserId}" : receiverUid,
                                    RoomId = string.Empty,
                                    Accept = pending.FriendApplyAccept,
                                    Reason = pending.FriendApplyAccept ? "对方已同意你的好友申请" : "对方已拒绝你的好友申请"
                                };
                                SendResponseBySessionId(gameSession, requesterSessionId, MessageIds.InviteGameAckNotif, notif);
                            }
                        }

                        var res = new FriendApplyHandleResponse
                        {
                            Success = dbRes?.Success == true,
                            Message = dbRes?.Message ?? "处理好友申请失败"
                        };
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, res);
                        return true;
                    }
                case MessageIds.DbResolveUserByUniqueIdRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUniqueIdResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析按 UniqueId 解析用户 DB 回包失败 RequestId:{requestId}");
                        }
                        if (!pending.IsInviteResolve)
                        {
                            return false;
                        }

                        var inviteRes = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };

                        if (requesterUserId <= 0)
                        {
                            inviteRes.Message = "会话未登录或未绑定";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (dbRes?.Success != true || dbRes.UserId <= 0)
                        {
                            inviteRes.Message = dbRes?.Message ?? "目标用户不存在";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (dbRes.UserId == requesterUserId)
                        {
                            inviteRes.Message = "不能邀请自己";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (IsBlockedByTarget(dbRes.UserId, requesterUserId))
                        {
                            inviteRes.Message = "对方已将你拉黑";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        if (IsBlockedByTarget(requesterUserId, dbRes.UserId))
                        {
                            inviteRes.Message = "你已将对方拉黑，无法邀请";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        var friendCheckReq = new Shared.Messages.Db.DbGetFriendsRequest
                        {
                            UserId = requesterUserId
                        };

                        bool sentFriendCheck = TrySendDbRequest(MessageIds.DbGetFriendsReq, friendCheckReq, pending.SessionId, pending.ResponseMsgId, nextPending =>
                        {
                            nextPending.IsInviteFriendCheck = true;
                            nextPending.InviteTargetUserId = dbRes.UserId;
                            nextPending.InviteRoomId = pending.InviteRoomId;
                            nextPending.InviteSceneType = pending.InviteSceneType;
                            nextPending.InviteRoomName = pending.InviteRoomName;
                        });

                        if (!sentFriendCheck)
                        {
                            inviteRes.Message = "发送DB请求失败";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                        }

                        return true;
                    }
                case MessageIds.DbResolveUserByUserIdRes:
                    {
                        var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUserIdResponse>(cleanPayload);
                        if (dbRes == null)
                        {
                            Shared.Log.Warning($"Game 解析按 UserId 解析用户 DB 回包失败 RequestId:{requestId}");
                        }
                        if (!pending.IsInviteSenderResolve)
                        {
                            return false;
                        }

                        var inviteRes = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };
                        if (requesterUserId <= 0)
                        {
                            inviteRes.Message = "会话未登录或未绑定";
                            SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                            return true;
                        }

                        string inviterUniqueId = dbRes?.Success == true ? (dbRes.UniqueId ?? string.Empty) : string.Empty;
                        string inviterNickname = dbRes?.Success == true ? (dbRes.Nickname ?? string.Empty) : string.Empty;

                        var pendingInvite = CreatePendingInvite(requesterUserId, pending.InviteTargetUserId, inviterUniqueId, inviterNickname, pending.InviteRoomId, pending.InviteSceneType, pending.InviteRoomName);
                        CleanupExpiredPendingInvites(gameSession);

                        long targetSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(pending.InviteTargetUserId);
                        if (targetSessionId > 0)
                        {
                            SendInviteNotification(gameSession, targetSessionId, pendingInvite);
                            inviteRes.Success = true;
                            inviteRes.Message = "邀请已发送";
                        }
                        else
                        {
                            inviteRes.Success = true;
                            inviteRes.Message = "对方离线，邀请已暂存";
                        }

                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                        return true;
                    }
                default:
                    return false;
            }
        }
    }
}
