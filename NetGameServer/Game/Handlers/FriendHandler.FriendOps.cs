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
    /// 好友系统 —— 好友操作模块（添加/删除/备注/列表 + 好友缓存/在线状态）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        /// <summary>
        /// 处理添加好友请求，接收客户端发送的添加好友请求，解析请求内容，并将请求转发给数据库进行处理。处理完成后，向客户端发送响应结果。
        /// </summary>
        /// <param name="sessionBase">当前的网络会话。</param>
        /// <param name="payload">客户端发送的请求数据。</param>
        internal static void HandleAddFriendRequest(ClientSessionWrapper session, AddFriendRequest? req)
        {
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

            if (!TrySendDbRequest(MessageIds.DbAddFriendReq, session, dbReq, session.SessionId, MessageIds.AddFriendRes))
            {
                SendSimpleResponse(session, MessageIds.AddFriendRes, new AddFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理来自客户端的删除好友请求：验证会话与请求格式，检查登录状态和数据库连接，构建并转发删除好友的数据库请求；在失败时返回相应的错误响应。
        /// </summary>
        /// <remarks>方法通过发送消息与数据库服务交互并向客户端发送响应；在会话未绑定、请求无效或 DB 未连接时返回错误响应。</remarks>
        /// <param name="session">会话对象；应为 ClientSessionWrapper，用于获取会话标识并发送响应。</param>
        /// <param name="req">强类型请求对象（由分发层反序列化后直接传入，不再做二次序列化）。</param>
        internal static void HandleRemoveFriendRequest(ClientSessionWrapper session, RemoveFriendRequest? req)
        {
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

            if (!TrySendDbRequest(MessageIds.DbRemoveFriendReq, session, dbReq, session.SessionId, MessageIds.RemoveFriendRes))
            {
                SendSimpleResponse(session, MessageIds.RemoveFriendRes, new RemoveFriendResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理设置好友备注请求：反序列化请求数据，验证会话和参数，检查数据库连接，构建并转发数据库请求；出错时发送失败响应。
        /// </summary>
        /// <remarks>在请求格式无效、会话未登录或未绑定、数据库未连接或 FriendUniqueId 为空时发送 SetFriendRemarkRes 的失败响应。构建
        /// DbSetFriendRemarkRequest（包含 UserId、FriendUniqueId（已修剪）和 Remark）并通过 TrySendDbRequest 转发为
        /// DbSetFriendRemarkReq；若发送失败则返回失败响应。</remarks>
        /// <param name="session">会话接口实例，期望为 ClientSessionWrapper 类型；若非该类型则忽略请求。</param>
        /// <param name="req">强类型请求对象（由分发层反序列化后直接传入）。</param>
        internal static void HandleSetFriendRemarkRequest(ClientSessionWrapper session, SetFriendRemarkRequest? req)
        {
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

            if (!TrySendDbRequest(MessageIds.DbSetFriendRemarkReq, session, dbReq, session.SessionId, MessageIds.SetFriendRemarkRes))
            {
                SendSimpleResponse(session, MessageIds.SetFriendRemarkRes, new SetFriendRemarkResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        /// <summary>
        /// 处理获取好友列表的请求：验证会话类型并反序列化请求负载，检查会话登录状态和数据库连接，必要时向数据库服务转发获取好友请求或返回失败响应。
        /// </summary>
        /// <remarks>在请求格式无效、会话未登录或数据库服务不可用时发送相应的失败响应；在验证通过且数据库可用时构造 DbGetFriendsRequest
        /// 并尝试发送到数据库服务。</remarks>
        /// <param name="session">客户端的网络会话基对象（Network.ISession），方法会将其转换为 ClientSessionWrapper 以继续处理。</param>
        /// <param name="req">强类型请求对象（由分发层反序列化后直接传入）。</param>
        internal static void HandleGetFriendsRequest(ClientSessionWrapper session, GetFriendsRequest? req)
        {
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

            if (!TrySendDbRequest(MessageIds.DbGetFriendsReq, session, dbReq, session.SessionId, MessageIds.GetFriendsRes))
            {
                SendSimpleResponse(session, MessageIds.GetFriendsRes, new GetFriendsResponse { Success = false, Message = "发送DB请求失败", Friends = Array.Empty<FriendInfo>() });
            }
        }

        public static bool IsFriend(int userId, int targetUserId)
        {
            return userId > 0
                && targetUserId > 0
                && FriendCache.TryGetValue(userId, out var friends)
                && friends.ContainsKey(targetUserId);
        }

        public static void WarmupSocialCache(global::Network.ISession gatewaySession, long sessionId, int userId)
        {
            if (sessionId <= 0 || userId <= 0)
            {
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetBlacklistRequest
            {
                UserId = userId
            };
            _ = TrySendDbRequest(MessageIds.DbGetBlacklistReq, gatewaySession, dbReq, sessionId, MessageIds.GetBlacklistRes);

            var friendsReq = new Shared.Messages.Db.DbGetFriendsRequest
            {
                UserId = userId
            };
            _ = TrySendDbRequest(MessageIds.DbGetFriendsReq, gatewaySession, friendsReq, sessionId, MessageIds.GetFriendsRes);
        }

        public static void NotifyFriendOnlineStatus(global::Network.ISession gameSession, long sessionId, int userId, bool isOnline)
        {
            if (gameSession == null || sessionId <= 0 || userId <= 0)
            {
                return;
            }

            string uid = PlayerSessionManager.Instance.GetUidBySessionId(sessionId);
            if (string.IsNullOrWhiteSpace(uid))
            {
                return;
            }

            if (!FriendCache.TryGetValue(userId, out var friendIds) || friendIds.Count == 0)
            {
                return;
            }

            var notif = new FriendOnlineStatusNotification
            {
                UserId = userId,
                UniqueId = uid,
                IsOnline = isOnline
            };

            foreach (var friendId in friendIds.Keys)
            {
                long friendSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(friendId);
                if (friendSessionId > 0)
                {
                    SendResponseBySessionId(gameSession, friendSessionId, MessageIds.FriendOnlineStatusNotif, notif);
                }
            }

            if (isOnline)
            {
                DeliverPendingInvites(gameSession, sessionId, userId);
            }
        }

        private static void SetFriendCache(int userId, FriendInfo[] friends)
        {
            if (userId <= 0)
            {
                return;
            }

            var friendIds = new ConcurrentDictionary<int, byte>();
            if (friends != null)
            {
                foreach (var item in friends)
                {
                    if (item.FriendUserId > 0)
                    {
                        friendIds[item.FriendUserId] = 0;
                    }
                }
            }

            FriendCache[userId] = friendIds;
        }
    }
}
