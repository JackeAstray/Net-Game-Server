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
    /// 好友系统处理器，负责处理好友相关的请求，如添加好友、删除好友、设置备注、获取好友列表以及邀请游戏等。
    /// </summary>
    public static class FriendHandler
    {
        private static readonly ConcurrentDictionary<long, PendingFriendRequest> PendingFriendRequests = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> BlacklistCache = new();
        private static long requestIdSeed = DateTime.UtcNow.Ticks;

        private sealed class PendingFriendRequest
        {
            public long SessionId { get; set; }
            public int ResponseMsgId { get; set; }
            public bool IsInviteResolve { get; set; }
            public bool IsInviteSenderResolve { get; set; }
            public int InviteRoomId { get; set; }
            public int InviteTargetUserId { get; set; }
        }

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
        }

        /// <summary>
        /// 处理添加好友请求，接收客户端发送的添加好友请求，解析请求内容，并将请求转发给数据库进行处理。处理完成后，向客户端发送响应结果。
        /// </summary>
        /// <param name="sessionBase">当前的网络会话。</param>
        /// <param name="payload">客户端发送的请求数据。</param>
        private static void HandleAddFriendRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<AddFriendRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbAddFriendRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.TargetUniqueId.Trim(),
                Remark = req.Remark
            };

            if (!TrySendDbRequest(MessageIds.DbAddFriendReq, dbReq, session.SessionId, MessageIds.AddFriendRes))
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleRemoveFriendRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<RemoveFriendRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbRemoveFriendRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.FriendUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbRemoveFriendReq, dbReq, session.SessionId, MessageIds.RemoveFriendRes))
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleSetFriendRemarkRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<SetFriendRemarkRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.FriendUniqueId))
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "好友UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbSetFriendRemarkRequest
            {
                UserId = (int)userId,
                FriendUniqueId = req.FriendUniqueId.Trim(),
                Remark = req.Remark
            };

            if (!TrySendDbRequest(MessageIds.DbSetFriendRemarkReq, dbReq, session.SessionId, MessageIds.SetFriendRemarkRes))
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleGetFriendsRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<GetFriendsRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "请求格式无效", Friends = Array.Empty<FriendInfo>() });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "DB服务未连接", Friends = Array.Empty<FriendInfo>() });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetFriendsRequest
            {
                UserId = (int)userId
            };

            if (!TrySendDbRequest(MessageIds.DbGetFriendsReq, dbReq, session.SessionId, MessageIds.GetFriendsRes))
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "发送DB请求失败", Friends = Array.Empty<FriendInfo>() });
            }
        }

        private static void HandleInviteGameRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
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

            var dbReq = new Shared.Messages.Db.DbResolveUserByUniqueIdRequest
            {
                UniqueId = req.FriendUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbResolveUserByUniqueIdReq, dbReq, session.SessionId, MessageIds.InviteGameRes, pending =>
            {
                pending.IsInviteResolve = true;
                pending.InviteRoomId = req.RoomId;
            }))
            {
                SendSimpleResponse(session, MessageIds.InviteGameRes, new InviteGameResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleAddBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<AddBlacklistRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbAddBlacklistRequest
            {
                UserId = userId,
                TargetUniqueId = req.TargetUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbAddBlacklistReq, dbReq, session.SessionId, MessageIds.AddBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.AddBlacklistRes, new AddBlacklistResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleRemoveBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<RemoveBlacklistRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbRemoveBlacklistRequest
            {
                UserId = userId,
                TargetUniqueId = req.TargetUniqueId.Trim()
            };

            if (!TrySendDbRequest(MessageIds.DbRemoveBlacklistReq, dbReq, session.SessionId, MessageIds.RemoveBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.RemoveBlacklistRes, new RemoveBlacklistResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        private static void HandleGetBlacklistRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<GetBlacklistRequest>(payload.Span);
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "请求格式无效", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "会话未登录或未绑定", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "DB服务未连接", Blacklists = Array.Empty<BlacklistInfo>() });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetBlacklistRequest
            {
                UserId = userId
            };

            if (!TrySendDbRequest(MessageIds.DbGetBlacklistReq, dbReq, session.SessionId, MessageIds.GetBlacklistRes))
            {
                SendSimpleResponse(session, MessageIds.GetBlacklistRes, new GetBlacklistResponse { Success = false, Message = "发送DB请求失败", Blacklists = Array.Empty<BlacklistInfo>() });
            }
        }

        private static bool TrySendDbRequest<TRequest>(int dbMsgId, TRequest request, long clientSessionId, int responseMsgId, Action<PendingFriendRequest>? configurePending = null)
        {
            var dbClient = GameServerApp.DbClient;
            if (dbClient == null)
            {
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
            catch
            {
                PendingFriendRequests.TryRemove(requestId, out _);
                return false;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }

        public static bool TryHandleDbResponse(global::Network.ISession gameSession, int dbMsgId, ReadOnlyMemory<byte> payload)
        {
            if (!Shared.RouteMetadata.TryExtractRequestId(payload, out long requestId, out var cleanPayload))
            {
                return false;
            }

            if (!PendingFriendRequests.TryRemove(requestId, out var pending))
            {
                return false;
            }

            int requesterUserId = PlayerSessionManager.Instance.GetUserIdBySessionId(pending.SessionId);

            switch (dbMsgId)
            {
                case MessageIds.DbAddFriendRes:
                {
                    var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddFriendResponse>(cleanPayload);
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
                case MessageIds.DbResolveUserByUniqueIdRes:
                {
                    var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUniqueIdResponse>(cleanPayload);
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

                    var senderResolveReq = new Shared.Messages.Db.DbResolveUserByUserIdRequest
                    {
                        UserId = requesterUserId
                    };

                    bool sent = TrySendDbRequest(MessageIds.DbResolveUserByUserIdReq, senderResolveReq, pending.SessionId, pending.ResponseMsgId, nextPending =>
                    {
                        nextPending.IsInviteSenderResolve = true;
                        nextPending.InviteRoomId = pending.InviteRoomId;
                        nextPending.InviteTargetUserId = dbRes.UserId;
                    });

                    if (!sent)
                    {
                        inviteRes.Message = "发送DB请求失败";
                        SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                    }

                    return true;
                }
                case MessageIds.DbResolveUserByUserIdRes:
                {
                    var dbRes = Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUserIdResponse>(cleanPayload);
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

                    long targetSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(pending.InviteTargetUserId);
                    if (targetSessionId > 0)
                    {
                        var notif = new InviteGameNotification
                        {
                            InviterUniqueId = inviterUniqueId,
                            InviterNickname = string.IsNullOrWhiteSpace(inviterNickname) ? $"Player_{requesterUserId}" : inviterNickname,
                            RoomId = pending.InviteRoomId
                        };
                        var notifPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(notif), targetSessionId);
                        var notifData = PacketBuilder.BuildPacket(MessageIds.InviteGameNotif, notifPayload, out int notifLength);
                        try
                        {
                            gameSession.Send(notifData.AsSpan(0, notifLength).ToArray());
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                        }

                        inviteRes.Success = true;
                        inviteRes.Message = "邀请已发送";
                    }

                    SendResponseBySessionId(gameSession, pending.SessionId, pending.ResponseMsgId, inviteRes);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static bool IsBlockedByTarget(int targetUserId, int senderUserId)
        {
            return targetUserId > 0
                && senderUserId > 0
                && BlacklistCache.TryGetValue(targetUserId, out var blockedUsers)
                && blockedUsers.ContainsKey(senderUserId);
        }

        private static void AddBlacklistCache(int blockerUserId, int blockedUserId)
        {
            if (blockerUserId <= 0 || blockedUserId <= 0)
            {
                return;
            }

            var blockedUsers = BlacklistCache.GetOrAdd(blockerUserId, _ => new ConcurrentDictionary<int, byte>());
            blockedUsers[blockedUserId] = 0;
        }

        private static void RemoveBlacklistCache(int blockerUserId, int blockedUserId)
        {
            if (blockerUserId <= 0 || blockedUserId <= 0)
            {
                return;
            }

            if (BlacklistCache.TryGetValue(blockerUserId, out var blockedUsers))
            {
                blockedUsers.TryRemove(blockedUserId, out _);
            }
        }

        private static void SetBlacklistCache(int blockerUserId, BlacklistInfo[] blacklists)
        {
            if (blockerUserId <= 0)
            {
                return;
            }

            var blockedUsers = new ConcurrentDictionary<int, byte>();
            if (blacklists != null)
            {
                foreach (var item in blacklists)
                {
                    if (item.BlockedUserId > 0)
                    {
                        blockedUsers[item.BlockedUserId] = 0;
                    }
                }
            }

            BlacklistCache[blockerUserId] = blockedUsers;
        }

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


