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
    /// 好友系统 —— 好友申请模块（发起/列表/接受拒绝）。
    /// 与 FriendHandler.cs 同属一个 partial class，按业务域分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public static partial class FriendHandler
    {
        internal static void HandleFriendApplyRequest(ClientSessionWrapper session, FriendApplyRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyRes, new FriendApplyResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            int userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyRes, new FriendApplyResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyRes, new FriendApplyResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (string.IsNullOrWhiteSpace(req.TargetUniqueId))
            {
                SendSimpleResponse(session, MessageIds.FriendApplyRes, new FriendApplyResponse { Success = false, Message = "目标UniqueId不能为空" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbCreateFriendApplyRequest
            {
                RequesterUserId = userId,
                TargetUniqueId = req.TargetUniqueId.Trim(),
                Message = req.Message?.Trim() ?? string.Empty
            };

            if (!TrySendDbRequest(MessageIds.DbCreateFriendApplyReq, session, dbReq, session.SessionId, MessageIds.FriendApplyRes, pending =>
            {
                pending.IsFriendApplyCreate = true;
                pending.FriendApplyMessage = dbReq.Message;
            }))
            {
                SendSimpleResponse(session, MessageIds.FriendApplyRes, new FriendApplyResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        internal static void HandleFriendApplyListRequest(ClientSessionWrapper session, FriendApplyListRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyListRes, new FriendApplyListResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            int userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyListRes, new FriendApplyListResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyListRes, new FriendApplyListResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbGetFriendApplyListRequest
            {
                UserId = userId
            };

            if (!TrySendDbRequest(MessageIds.DbGetFriendApplyListReq, session, dbReq, session.SessionId, MessageIds.FriendApplyListRes))
            {
                SendSimpleResponse(session, MessageIds.FriendApplyListRes, new FriendApplyListResponse { Success = false, Message = "发送DB请求失败" });
            }
        }

        internal static void HandleFriendApplyHandleRequest(ClientSessionWrapper session, FriendApplyHandleRequest? req)
        {
            if (req == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyHandleRes, new FriendApplyHandleResponse { Success = false, Message = "请求格式无效" });
                return;
            }

            int userId = PlayerSessionManager.Instance.GetUserIdBySessionId(session.SessionId);
            if (userId <= 0)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyHandleRes, new FriendApplyHandleResponse { Success = false, Message = "会话未登录或未绑定" });
                return;
            }

            if (GameServerApp.DbClient == null)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyHandleRes, new FriendApplyHandleResponse { Success = false, Message = "DB服务未连接" });
                return;
            }

            if (req.ApplyId <= 0)
            {
                SendSimpleResponse(session, MessageIds.FriendApplyHandleRes, new FriendApplyHandleResponse { Success = false, Message = "申请ID无效" });
                return;
            }

            var dbReq = new Shared.Messages.Db.DbHandleFriendApplyRequest
            {
                UserId = userId,
                ApplyId = req.ApplyId,
                Accept = req.Accept
            };

            if (!TrySendDbRequest(MessageIds.DbHandleFriendApplyReq, session, dbReq, session.SessionId, MessageIds.FriendApplyHandleRes, pending =>
            {
                pending.IsFriendApplyHandle = true;
                pending.FriendApplyAccept = req.Accept;
            }))
            {
                SendSimpleResponse(session, MessageIds.FriendApplyHandleRes, new FriendApplyHandleResponse { Success = false, Message = "发送DB请求失败" });
            }
        }
    }
}
