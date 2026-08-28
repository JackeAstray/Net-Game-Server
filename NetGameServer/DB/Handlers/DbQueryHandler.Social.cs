using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Shared.Messages.Db;
using Shared;
using DB;
using Shared.Data.Social;
namespace DB.Handlers
{
    /// <summary>
    /// DB 查询 Handler —— 社交模块（黑名单/用户解析/好友申请）。
    /// 与 DbQueryHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// </summary>
    public partial class DbQueryHandler
    {
        /// <summary>
        /// 将指定目标用户添加到发起用户的黑名单并发送数据库响应。
        /// </summary>
        /// <remarks>执行输入验证（用户ID 和 TargetUniqueId）；查找目标用户并防止将自己加入黑名单；检测并避免重复黑名单项；将新黑名单记录持久化并发送
        /// DbAddBlacklistResponse；发生异常时记录错误日志。</remarks>
        /// <param name="session">会话对象，用于与客户端通信并发送数据库响应。</param>
        /// <param name="request">包含发起用户ID和目标用户 UniqueId 的添加黑名单请求；为 null 时不执行任何操作。</param>
        /// <param name="requestId">可选的请求标识，用于在响应中关联请求。</param>
        /// <returns>表示异步操作完成的任务。</returns>
        public static async Task HandleAddBlacklistRequest(ISession session, DbAddBlacklistRequest? request, long? requestId = null)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbAddBlacklistResponse();

                if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.TargetUniqueId))
                {
                    response.Success = false;
                    response.Message = "用户ID或UniqueId无效";
                }
                else
                {
                    string targetUniqueId = request.TargetUniqueId.Trim();
                    var targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == targetUniqueId);
                    if (targetUser == null)
                    {
                        response.Success = false;
                        response.Message = "目标用户不存在";
                    }
                    else if (targetUser.Id == request.UserId)
                    {
                        response.Success = false;
                        response.Message = "不能拉黑自己";
                    }
                    else
                    {
                        response.TargetUserId = targetUser.Id;
                        bool exists = await dbContext.Blacklists.AnyAsync(b => b.UserId == request.UserId && b.BlockedUserId == targetUser.Id);
                        if (exists)
                        {
                            response.Success = false;
                            response.Message = "目标已在黑名单中";
                        }
                        else
                        {
                            using var transaction = await dbContext.Database.BeginTransactionAsync();

                            dbContext.Blacklists.Add(new Blacklist
                            {
                                UserId = request.UserId,
                                BlockedUserId = targetUser.Id,
                                AddTime = DateTime.UtcNow
                            });

                            var friendPairs = await dbContext.Friends
                                .Where(f => (f.UserId == request.UserId && f.FriendUserId == targetUser.Id)
                                    || (f.UserId == targetUser.Id && f.FriendUserId == request.UserId))
                                .ToListAsync();
                            if (friendPairs.Count > 0)
                            {
                                dbContext.Friends.RemoveRange(friendPairs);
                            }

                            await dbContext.SaveChangesAsync();
                            await transaction.CommitAsync();
                            response.Success = true;
                            response.Message = "拉黑成功";
                        }
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbAddBlacklistRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"添加黑名单异常: {ex}");
            }
        }

        /// <summary>
        /// 处理移除黑名单的异步请求：验证输入、查找目标用户、从数据库移除对应黑名单条目并发送结果响应。
        /// </summary>
        /// <remarks>对请求进行校验、查询并修改数据库中的黑名单记录，最后通过 SendDbResponse 发送
        /// DbRemoveBlacklistResponse；发生异常时记录错误。</remarks>
        /// <param name="session">用于与客户端通信的会话实例，用于发送数据库响应。</param>
        /// <param name="request">包含移除黑名单所需的数据；可能为 null，表示请求无效或无法反序列化。</param>
        /// <param name="requestId">可选请求标识，用于在发送响应时关联原始请求。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleRemoveBlacklistRequest(ISession session, DbRemoveBlacklistRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 RemoveBlacklistRequest，数据无法被反序列化。");
                return;
            }
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbRemoveBlacklistResponse();

                if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.TargetUniqueId))
                {
                    response.Success = false;
                    response.Message = "用户ID或UniqueId无效";
                }
                else
                {
                    string targetUniqueId = request.TargetUniqueId.Trim();
                    var targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == targetUniqueId);
                    if (targetUser == null)
                    {
                        response.Success = false;
                        response.Message = "目标用户不存在";
                    }
                    else
                    {
                        response.TargetUserId = targetUser.Id;
                        var blacklist = await dbContext.Blacklists.FirstOrDefaultAsync(b => b.UserId == request.UserId && b.BlockedUserId == targetUser.Id);
                        if (blacklist == null)
                        {
                            response.Success = false;
                            response.Message = "目标不在黑名单中";
                        }
                        else
                        {
                            dbContext.Blacklists.Remove(blacklist);
                            await dbContext.SaveChangesAsync();
                            response.Success = true;
                            response.Message = "移除成功";
                        }
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbRemoveBlacklistRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"移除黑名单异常: {ex}");
            }
        }

        /// <summary>
        /// 处理获取黑名单的异步请求：验证请求、查询数据库并返回黑名单列表。
        /// </summary>
        /// <remarks>对请求进行校验，查询数据库中的黑名单记录，并通过 SendDbResponse 发送 DbGetBlacklistResponse；发生异常时记录错误。</remarks>
        /// <param name="session">用于与客户端通信的会话实例，用于发送数据库响应。</param>
        /// <param name="request">包含获取黑名单所需的数据；可能为 null，表示请求无效或无法反序列化。</param>
        /// <param name="requestId">可选请求标识，用于在发送响应时关联原始请求。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleGetBlacklistRequest(ISession session, DbGetBlacklistRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 GetBlacklistRequest，数据无法被反序列化。");
                return;
            }
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbGetBlacklistResponse();

                var blacklistRows = await dbContext.Blacklists.Where(b => b.UserId == request.UserId).ToListAsync();
                var blockedUserIds = blacklistRows.Select(b => b.BlockedUserId).Distinct().ToList();
                var blockedUsers = blockedUserIds.Count == 0
                    ? new System.Collections.Generic.Dictionary<int, Shared.Data.User>()
                    : await dbContext.Users
                        .Where(u => blockedUserIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => u);

                response.Success = true;
                response.Message = "获取成功";
                response.Blacklists = blacklistRows.Select(b =>
                {
                    blockedUsers.TryGetValue(b.BlockedUserId, out var user);
                    return new DbBlacklistItem
                    {
                        BlockedUserId = b.BlockedUserId,
                        BlockedUniqueId = user?.UniqueId ?? string.Empty,
                        BlockedNickname = user?.Nickname ?? string.Empty,
                        AddTime = b.AddTime
                    };
                }).ToList();

                SendDbResponse(session, Shared.Messages.MessageIds.DbGetBlacklistRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"获取黑名单异常: {ex}");
            }
        }

        /// <summary>
        /// 按 UniqueId 查找用户并将解析结果以 DbResolveUserByUniqueIdResponse 发回请求方。
        /// </summary>
        /// <remarks>request 为 null 或 UniqueId 无效时记录警告并返回；成功时填充响应并发送。发生异常时记录错误日志。</remarks>
        /// <param name="session">当前会话，用于向请求方发送数据库响应。</param>
        /// <param name="request">包含待解析 UniqueId 的请求对象；可为 null。</param>
        /// <param name="requestId">可选请求标识，用于将响应关联到原始请求。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleResolveUserByUniqueIdRequest(ISession session, DbResolveUserByUniqueIdRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ResolveUserByUniqueIdRequest，数据无法被反序列化。");
                return;
            }
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbResolveUserByUniqueIdResponse();

                if (string.IsNullOrWhiteSpace(request.UniqueId))
                {
                    response.Success = false;
                    response.Message = "UniqueId不能为空";
                }
                else
                {
                    string uniqueId = request.UniqueId.Trim();
                    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == uniqueId);
                    if (user == null)
                    {
                        response.Success = false;
                        response.Message = "目标用户不存在";
                    }
                    else
                    {
                        response.Success = true;
                        response.Message = "解析成功";
                        response.UserId = user.Id;
                        response.UniqueId = user.UniqueId ?? string.Empty;
                        response.Nickname = user.Nickname ?? string.Empty;
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbResolveUserByUniqueIdRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"按UniqueId解析用户异常: {ex}");
            }
        }

        /// <summary>
        /// 根据用户 ID 从数据库解析用户并通过会话发送数据库响应。
        /// </summary>
        /// <remarks>在独立的服务作用域中使用 DefaultDbContext 查询用户；对无效的 UserId 或不存在的用户返回带有错误信息的响应，成功时返回包含
        /// UserId、UniqueId 和 Nickname 的响应；发生异常时记录错误日志。</remarks>
        /// <param name="session">会话对象，用于发送数据库响应。</param>
        /// <param name="request">包含要解析的目标用户 ID 的请求对象；为 null 时记录警告并忽略。</param>
        /// <param name="requestId">可选的请求标识，用于将响应关联到原始请求。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleResolveUserByUserIdRequest(ISession session, DbResolveUserByUserIdRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ResolveUserByUserIdRequest，数据无法被反序列化。");
                return;
            }
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbResolveUserByUserIdResponse();

                if (request.UserId <= 0)
                {
                    response.Success = false;
                    response.Message = "UserId无效";
                }
                else
                {
                    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
                    if (user == null)
                    {
                        response.Success = false;
                        response.Message = "目标用户不存在";
                    }
                    else
                    {
                        response.Success = true;
                        response.Message = "解析成功";
                        response.UserId = user.Id;
                        response.UniqueId = user.UniqueId ?? string.Empty;
                        response.Nickname = user.Nickname ?? string.Empty;
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbResolveUserByUserIdRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"按UserId解析用户异常: {ex}");
            }
        }

        public static async Task HandleCreateFriendApplyRequest(ISession session, DbCreateFriendApplyRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 CreateFriendApplyRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbCreateFriendApplyResponse();

                if (request.RequesterUserId <= 0 || string.IsNullOrWhiteSpace(request.TargetUniqueId))
                {
                    response.Success = false;
                    response.Message = "请求参数无效";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                string targetUniqueId = request.TargetUniqueId.Trim();
                var targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == targetUniqueId);
                if (targetUser == null)
                {
                    response.Success = false;
                    response.Message = "目标用户不存在";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                response.TargetUserId = targetUser.Id;

                if (targetUser.Id == request.RequesterUserId)
                {
                    response.Success = false;
                    response.Message = "不能向自己发送申请";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                bool hasBlacklistConflict = await dbContext.Blacklists.AnyAsync(b =>
                    (b.UserId == request.RequesterUserId && b.BlockedUserId == targetUser.Id)
                    || (b.UserId == targetUser.Id && b.BlockedUserId == request.RequesterUserId));
                if (hasBlacklistConflict)
                {
                    response.Success = false;
                    response.Message = "存在黑名单关系，无法发送申请";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                bool isFriend = await dbContext.Friends.AnyAsync(f =>
                    (f.UserId == request.RequesterUserId && f.FriendUserId == targetUser.Id)
                    || (f.UserId == targetUser.Id && f.FriendUserId == request.RequesterUserId));
                if (isFriend)
                {
                    response.Success = false;
                    response.Message = "对方已是你的好友";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                bool hasPendingApply = await dbContext.FriendRequests.AnyAsync(r =>
                    r.RequesterUserId == request.RequesterUserId
                    && r.ReceiverUserId == targetUser.Id
                    && r.Status == "Pending");
                if (hasPendingApply)
                {
                    response.Success = false;
                    response.Message = "已有待处理申请，请勿重复发送";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                bool reversePending = await dbContext.FriendRequests.AnyAsync(r =>
                    r.RequesterUserId == targetUser.Id
                    && r.ReceiverUserId == request.RequesterUserId
                    && r.Status == "Pending");
                if (reversePending)
                {
                    response.Success = false;
                    response.Message = "对方已向你发送申请，请先处理";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
                    return;
                }

                var apply = new FriendRequest
                {
                    RequesterUserId = request.RequesterUserId,
                    ReceiverUserId = targetUser.Id,
                    Message = request.Message?.Trim() ?? string.Empty,
                    Status = "Pending",
                    CreateTimeUtc = DateTime.UtcNow
                };

                dbContext.FriendRequests.Add(apply);
                await dbContext.SaveChangesAsync();

                response.Success = true;
                response.Message = "申请已发送";
                response.ApplyId = apply.Id;
                SendDbResponse(session, Shared.Messages.MessageIds.DbCreateFriendApplyRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"创建好友申请异常: {ex}");
            }
        }

        public static async Task HandleGetFriendApplyListRequest(ISession session, DbGetFriendApplyListRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 GetFriendApplyListRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbGetFriendApplyListResponse();

                if (request.UserId <= 0)
                {
                    response.Success = false;
                    response.Message = "UserId无效";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbGetFriendApplyListRes, response, requestId);
                    return;
                }

                var applyRows = await dbContext.FriendRequests
                    .Where(r => r.ReceiverUserId == request.UserId && r.Status == "Pending")
                    .OrderByDescending(r => r.CreateTimeUtc)
                    .ToListAsync();

                var requesterIds = applyRows.Select(r => r.RequesterUserId).Distinct().ToList();
                var requesters = requesterIds.Count == 0
                    ? new System.Collections.Generic.Dictionary<int, Shared.Data.User>()
                    : await dbContext.Users.Where(u => requesterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u);

                response.Success = true;
                response.Message = "获取成功";
                response.Applies = applyRows.Select(r =>
                {
                    requesters.TryGetValue(r.RequesterUserId, out var requester);
                    return new DbFriendApplyItem
                    {
                        ApplyId = r.Id,
                        RequesterUserId = r.RequesterUserId,
                        RequesterUniqueId = requester?.UniqueId ?? string.Empty,
                        RequesterNickname = requester?.Nickname ?? string.Empty,
                        Message = r.Message ?? string.Empty,
                        Status = r.Status ?? string.Empty,
                        CreateTimeUtc = r.CreateTimeUtc
                    };
                }).ToList();

                SendDbResponse(session, Shared.Messages.MessageIds.DbGetFriendApplyListRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"获取好友申请列表异常: {ex}");
            }
        }

        public static async Task HandleFriendApplyRequest(ISession session, DbHandleFriendApplyRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 HandleFriendApplyRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbHandleFriendApplyResponse();

                if (request.UserId <= 0 || request.ApplyId <= 0)
                {
                    response.Success = false;
                    response.Message = "参数无效";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbHandleFriendApplyRes, response, requestId);
                    return;
                }

                var apply = await dbContext.FriendRequests.FirstOrDefaultAsync(r => r.Id == request.ApplyId && r.ReceiverUserId == request.UserId);
                if (apply == null)
                {
                    response.Success = false;
                    response.Message = "申请不存在";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbHandleFriendApplyRes, response, requestId);
                    return;
                }

                response.RequesterUserId = apply.RequesterUserId;
                response.ReceiverUserId = apply.ReceiverUserId;

                if (!string.Equals(apply.Status, "Pending", StringComparison.Ordinal))
                {
                    response.Success = false;
                    response.Message = "申请已处理";
                    SendDbResponse(session, Shared.Messages.MessageIds.DbHandleFriendApplyRes, response, requestId);
                    return;
                }

                using var transaction = await dbContext.Database.BeginTransactionAsync();

                if (request.Accept)
                {
                    bool hasBlacklistConflict = await dbContext.Blacklists.AnyAsync(b =>
                        (b.UserId == apply.RequesterUserId && b.BlockedUserId == apply.ReceiverUserId)
                        || (b.UserId == apply.ReceiverUserId && b.BlockedUserId == apply.RequesterUserId));
                    if (hasBlacklistConflict)
                    {
                        response.Success = false;
                        response.Message = "存在黑名单关系，无法同意";
                        SendDbResponse(session, Shared.Messages.MessageIds.DbHandleFriendApplyRes, response, requestId);
                        return;
                    }

                    bool forwardExists = await dbContext.Friends.AnyAsync(f => f.UserId == apply.RequesterUserId && f.FriendUserId == apply.ReceiverUserId);
                    bool reverseExists = await dbContext.Friends.AnyAsync(f => f.UserId == apply.ReceiverUserId && f.FriendUserId == apply.RequesterUserId);

                    if (!forwardExists)
                    {
                        dbContext.Friends.Add(new Friend
                        {
                            UserId = apply.RequesterUserId,
                            FriendUserId = apply.ReceiverUserId,
                            Remark = string.Empty,
                            AddTime = DateTime.UtcNow
                        });
                    }

                    if (!reverseExists)
                    {
                        dbContext.Friends.Add(new Friend
                        {
                            UserId = apply.ReceiverUserId,
                            FriendUserId = apply.RequesterUserId,
                            Remark = string.Empty,
                            AddTime = DateTime.UtcNow
                        });
                    }

                    apply.Status = "Accepted";
                    apply.HandleTimeUtc = DateTime.UtcNow;

                    var reversePendings = await dbContext.FriendRequests
                        .Where(r => r.RequesterUserId == apply.ReceiverUserId
                            && r.ReceiverUserId == apply.RequesterUserId
                            && r.Status == "Pending")
                        .ToListAsync();
                    foreach (var reverse in reversePendings)
                    {
                        reverse.Status = "Accepted";
                        reverse.HandleTimeUtc = DateTime.UtcNow;
                    }

                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();

                    response.Success = true;
                    response.Message = "已同意好友申请";
                }
                else
                {
                    apply.Status = "Rejected";
                    apply.HandleTimeUtc = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();

                    response.Success = true;
                    response.Message = "已拒绝好友申请";
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbHandleFriendApplyRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"处理好友申请异常: {ex}");
            }
        }

    }
}
