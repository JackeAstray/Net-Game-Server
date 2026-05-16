using System;
using System.Threading.Tasks;
using Game.Network;
using Shared;
using Shared.Messages;
using Shared.Messages.Social;
using Game.Managers;
using System.Linq;
using Network.Routing;

namespace Game.Handlers
{
    /// <summary>
    /// 好友系统处理器，负责处理好友相关的请求，如添加好友、删除好友、设置备注、获取好友列表以及邀请游戏等。
    /// </summary>
    public static class FriendHandler
    {
        public static void Register(MessageRouter router)
        {
            router.RegisterHandler(MessageIds.AddFriendReq, (s, p) => HandleAddFriendRequest(s, p));
            router.RegisterHandler(MessageIds.RemoveFriendReq, (s, p) => HandleRemoveFriendRequest(s, p));
            router.RegisterHandler(MessageIds.SetFriendRemarkReq, (s, p) => HandleSetFriendRemarkRequest(s, p));
            router.RegisterHandler(MessageIds.GetFriendsReq, (s, p) => HandleGetFriendsRequest(s, p));
            router.RegisterHandler(MessageIds.InviteGameReq, (s, p) => HandleInviteGameRequest(s, p));
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

            // Generate DB request
            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);

            if (GameServerApp.DbClient != null)
            {
                var dbReq = new Shared.Messages.Db.DbAddFriendRequest
                {
                    UserId = (int)userId,
                    FriendUserId = req.FriendUserId,
                    Remark = req.Remark
                };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(dbReq);
                byte[] packet = PacketBuilder.BuildPacket(MessageIds.DbAddFriendReq, data, out int totalLength);
                GameServerApp.DbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var res = new AddFriendResponse { Success = true, Message = "已发送请求并等待DB处理" };
            var resPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(res), session.SessionId);
            var resData = PacketBuilder.BuildPacket(MessageIds.AddFriendRes, resPayload, out int resLength);
            try
            {
                session.Send(resData.AsSpan(0, resLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(resData);
            }
        }

        private static void HandleRemoveFriendRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<RemoveFriendRequest>(payload.Span);
            
            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (GameServerApp.DbClient != null)
            {
                var dbReq = new Shared.Messages.Db.DbRemoveFriendRequest
                {
                    UserId = (int)userId,
                    FriendUserId = req.FriendUserId
                };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(dbReq);
                byte[] packet = PacketBuilder.BuildPacket(MessageIds.DbRemoveFriendReq, data, out int totalLength);
                GameServerApp.DbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var res = new RemoveFriendResponse { Success = true, Message = "已发送请求并等待DB处理" };
            var resPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(res), session.SessionId);
            var resData = PacketBuilder.BuildPacket(MessageIds.RemoveFriendRes, resPayload, out int resLength);
            try
            {
                session.Send(resData.AsSpan(0, resLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(resData);
            }
        }

        private static void HandleSetFriendRemarkRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<SetFriendRemarkRequest>(payload.Span);
            
            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (GameServerApp.DbClient != null)
            {
                var dbReq = new Shared.Messages.Db.DbSetFriendRemarkRequest
                {
                    UserId = (int)userId,
                    FriendUserId = req.FriendUserId,
                    Remark = req.Remark
                };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(dbReq);
                byte[] packet = PacketBuilder.BuildPacket(MessageIds.DbSetFriendRemarkReq, data, out int totalLength);
                GameServerApp.DbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var res = new SetFriendRemarkResponse { Success = true, Message = "已发送请求并等待DB处理" };
            var resPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(res), session.SessionId);
            var resData = PacketBuilder.BuildPacket(MessageIds.SetFriendRemarkRes, resPayload, out int resLength);
            try
            {
                session.Send(resData.AsSpan(0, resLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(resData);
            }
        }

        private static void HandleGetFriendsRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<GetFriendsRequest>(payload.Span);
            
            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (GameServerApp.DbClient != null)
            {
                var dbReq = new Shared.Messages.Db.DbGetFriendsRequest
                {
                    UserId = (int)userId
                };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(dbReq);
                byte[] packet = PacketBuilder.BuildPacket(MessageIds.DbGetFriendsReq, data, out int totalLength);
                GameServerApp.DbClient.Send(packet.AsSpan(0, totalLength).ToArray());
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var res = new GetFriendsResponse { Success = true, Message = "已发送请求并等待DB处理" }; // 在真实项目中最好异步等待或者依赖DB回到给客户端
            var resPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(res), session.SessionId);
            var resData = PacketBuilder.BuildPacket(MessageIds.GetFriendsRes, resPayload, out int resLength);
            try
            {
                session.Send(resData.AsSpan(0, resLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(resData);
            }
        }

        private static void HandleInviteGameRequest(global::Network.ISession sessionBase, ReadOnlyMemory<byte> payload)
        {
            if (sessionBase is not ClientSessionWrapper session) return;
            var req = Shared.Json.DeserializeFromUtf8Bytes<InviteGameRequest>(payload.Span);
            var userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            var res = new InviteGameResponse { Success = false, Message = "不在线或无法邀请" };

            if (userId > 0 && req != null)
            {
                long targetSessionId = PlayerSessionManager.Instance.GetSessionIdByUserId(req.FriendUserId);
                if (targetSessionId > 0)
                {
                    var notif = new InviteGameNotification
                    {
                        InviterUserId = userId,
                        InviterNickname = $"Player_{userId}",
                        RoomId = req.RoomId
                    };
                    var notifPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(notif), targetSessionId);
                    var notifData = PacketBuilder.BuildPacket(MessageIds.InviteGameNotif, notifPayload, out int notifLength);
                    try
                    {
                        session.Send(notifData.AsSpan(0, notifLength).ToArray());
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(notifData);
                    }
                    res.Success = true;
                    res.Message = "邀请已发送";
                }
            }

            var resPayload = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(res), session.SessionId);
            var resData = PacketBuilder.BuildPacket(MessageIds.InviteGameRes, resPayload, out int resLength);
            try
            {
                session.Send(resData.AsSpan(0, resLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(resData);
            }
        }
    }
}


