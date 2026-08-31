using System;
using Framework.Protocol;
using Framework.Protocol.Generated;
using Network;
using Shared.Messages.Db;
using DB.Routing;

namespace DB.Handlers
{
    /// <summary>
    /// DB 服务器的会话上下文适配（ISessionContext 实现）：
    /// 将 MessageDispatcher 的抽象发送接口适配到 DB 底层网络会话，
    /// 并在 Send 时为出包附加 RequestId 路由元数据（与 RequestContextSession 行为一致，
    /// 保证"先写后读"请求关联不丢失）。
    /// </summary>
    public sealed class DbSessionContext : ISessionContext
    {
        private readonly ISession session;
        private readonly long requestId;

        public DbSessionContext(ISession session, long requestId)
        {
            this.session = session;
            this.requestId = requestId;
        }

        /// <summary>底层网络会话（业务处理器需要它做定向发送/透传）。</summary>
        public ISession Session => session;

        /// <summary>当前请求的路由请求 ID（0 表示无请求关联）。</summary>
        public long RequestId => requestId;

        public long ClientSessionId => session.SessionId;

        public void Send(int msgId, ReadOnlyMemory<byte> payload)
        {
            byte[] routedPayload = requestId > 0
                ? Shared.RouteMetadata.AttachRequestId(payload.ToArray(), requestId)
                : payload.ToArray();
            SendPacket(msgId, routedPayload);
        }

        public void Send(IGameMessage message)
        {
            Send(message.MessageId, message.Serialize());
        }

        public void SendTo(long targetSessionId, int msgId, ReadOnlyMemory<byte> payload)
        {
            // DB 内部消息没有跨客户端会话概念；保留契约实现（附加目标会话 ID 供扩展）。
            byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
            SendPacket(msgId, routedPayload);
        }

        private void SendPacket(int msgId, byte[] routedPayload)
        {
            byte[] packet = global::Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
            try
            {
                session.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }

    /// <summary>
    /// 基于 MessageDispatcher 的强类型处理器（DB 服务器）。
    /// 使用生成的消息类 + MemoryPack 二进制序列化（JSON 兼容回退），消灭手写 MsgId 分支。
    /// 全部 20 个 DB 请求消息已注册；未注册的 MsgId 由调用方回退旧路由。
    ///
    /// 并发/包序修复（P2）：由 RegisterSync（同步包装、异步 handler 被 fire-and-forget 丢弃、
    /// 产生 CS4014 且"先写后读"可能乱序）改为 Register + async/await 显式等待：
    /// 每个请求真正处理完成后 TryDispatch 才返回，配合 AsyncEventGuard 的接收循环内联派发，
    /// 同一连接的后续请求不再越过未完成的前一个请求（消除登录/好友/在线状态等读写的乱序交错）。
    /// </summary>
    public static class DbDispatcher
    {
        /// <summary>
        /// 构建 DB 服务器的配置化分发器（全量 20 条请求消息）。
        /// 处理器模式：生成消息 → 适配为旧请求对象 → 复用现有 DbQueryHandler 业务管线
        /// （响应仍走 SendDbResponse + RequestContextSession 的 RequestId 关联，双格式兼容）。
        /// </summary>
        public static Framework.Protocol.MessageDispatcher BuildDispatcher()
        {
            var dispatcher = new Framework.Protocol.MessageDispatcher();

            // 提取底层会话（带 RequestId 路由）与请求 ID 的辅助函数
            static RequestContextSession Routed(ISessionContext ctx, out long requestId)
            {
                var c = (DbSessionContext)ctx;
                requestId = c.RequestId;
                return new RequestContextSession(c.Session, c.RequestId);
            }

            // ---- 账户/登录类 ----

            // 获取最大 UID
            dispatcher.Register<DbGetMaxUid>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleGetMaxUidRequest(Routed(ctx, out _), new GetMaxUidRequest());
            }, jsonFallback: true);

            // 登录验证
            dispatcher.Register<DbLoginVerify>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleLoginVerifyRequest(Routed(ctx, out _), new LoginVerifyRequest
                {
                    Account = msg.Account,
                    Password = msg.Password
                });
            }, jsonFallback: true);

            // 注册验证
            dispatcher.Register<DbRegisterVerify>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleRegisterVerifyRequest(Routed(ctx, out _), new RegisterVerifyRequest
                {
                    Account = msg.Account,
                    Password = msg.Password,
                    Nickname = msg.Nickname,
                    Uid = msg.Uid
                });
            }, jsonFallback: true);

            // 账户查询
            dispatcher.Register<DbAccountQuery>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleAccountQueryRequest(Routed(ctx, out _), new AccountQueryRequest
                {
                    Account = msg.Account
                });
            }, jsonFallback: true);

            // 在线统计
            dispatcher.Register<DbOnlineStats>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleOnlineStatsRequest(Routed(ctx, out _), new OnlineStatsRequest());
            }, jsonFallback: true);

            // 更新在线状态
            dispatcher.Register<DbUpdateOnlineState>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleUpdateOnlineStateRequest(Routed(ctx, out _), new UpdateOnlineStateRequest
                {
                    UserId = msg.UserId,
                    IsOnline = msg.IsOnline
                });
            }, jsonFallback: true);

            // 更改密码
            dispatcher.Register<DbChangePassword>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleChangePasswordVerifyRequest(Routed(ctx, out _), new ChangePasswordVerifyRequest
                {
                    UserId = msg.UserId,
                    Account = msg.Account,
                    OldPassword = msg.OldPassword,
                    NewPassword = msg.NewPassword
                });
            }, jsonFallback: true);

            // 邮箱重置密码
            dispatcher.Register<DbResetPasswordByEmail>(async (ctx, msg) =>
            {
                await DbQueryHandler.HandleResetPasswordByEmailRequest(Routed(ctx, out _), new ResetPasswordByEmailRequest
                {
                    Account = msg.Account,
                    Email = msg.Email,
                    TemporaryPassword = msg.TemporaryPassword
                });
            }, jsonFallback: true);

            // ---- 好友/黑名单/申请类（带 RequestId 请求关联）----

            // 好友：添加
            dispatcher.Register<DbFriendAdd>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleAddFriendRequest(session, new DbAddFriendRequest
                {
                    UserId = msg.UserId,
                    FriendUniqueId = msg.FriendUniqueId,
                    Remark = msg.Remark
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友：移除
            dispatcher.Register<DbFriendRemove>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleRemoveFriendRequest(session, new DbRemoveFriendRequest
                {
                    UserId = msg.UserId,
                    FriendUniqueId = msg.FriendUniqueId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友：设置备注
            dispatcher.Register<DbFriendSetRemark>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleSetFriendRemarkRequest(session, new DbSetFriendRemarkRequest
                {
                    UserId = msg.UserId,
                    FriendUniqueId = msg.FriendUniqueId,
                    Remark = msg.Remark
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友：获取列表
            dispatcher.Register<DbFriendGetList>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleGetFriendsRequest(session, new DbGetFriendsRequest
                {
                    UserId = msg.UserId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 黑名单：添加
            dispatcher.Register<DbBlacklistAdd>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleAddBlacklistRequest(session, new DbAddBlacklistRequest
                {
                    UserId = msg.UserId,
                    TargetUniqueId = msg.TargetUniqueId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 黑名单：移除
            dispatcher.Register<DbBlacklistRemove>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleRemoveBlacklistRequest(session, new DbRemoveBlacklistRequest
                {
                    UserId = msg.UserId,
                    TargetUniqueId = msg.TargetUniqueId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 黑名单：获取列表
            dispatcher.Register<DbBlacklistGetList>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleGetBlacklistRequest(session, new DbGetBlacklistRequest
                {
                    UserId = msg.UserId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 按 UniqueId 解析用户
            dispatcher.Register<DbResolveUserByUniqueId>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleResolveUserByUniqueIdRequest(session, new DbResolveUserByUniqueIdRequest
                {
                    UniqueId = msg.UniqueId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 按 UserId 解析用户
            dispatcher.Register<DbResolveUserByUserId>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleResolveUserByUserIdRequest(session, new DbResolveUserByUserIdRequest
                {
                    UserId = msg.UserId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友申请：发起
            dispatcher.Register<DbFriendApplyCreate>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleCreateFriendApplyRequest(session, new DbCreateFriendApplyRequest
                {
                    RequesterUserId = msg.RequesterUserId,
                    TargetUniqueId = msg.TargetUniqueId,
                    Message = msg.Message
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友申请：列表查询
            dispatcher.Register<DbFriendApplyList>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleGetFriendApplyListRequest(session, new DbGetFriendApplyListRequest
                {
                    UserId = msg.UserId
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            // 好友申请：处理（接受/拒绝）
            dispatcher.Register<DbFriendApplyHandle>(async (ctx, msg) =>
            {
                var session = Routed(ctx, out long requestId);
                await DbQueryHandler.HandleFriendApplyRequest(session, new DbHandleFriendApplyRequest
                {
                    UserId = msg.UserId,
                    ApplyId = msg.ApplyId,
                    Accept = msg.Accept
                }, requestId > 0 ? requestId : null);
            }, jsonFallback: true);

            return dispatcher;
        }
    }
}
