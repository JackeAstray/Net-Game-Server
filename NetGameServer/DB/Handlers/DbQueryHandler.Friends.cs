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
    /// DB 查询 Handler —— 好友模块（添加/删除/备注/列表）。
    /// 与 DbQueryHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// </summary>
    public partial class DbQueryHandler
    {
        /// <summary>
        /// 处理添加好友请求。
        /// 检查两者是否已为好友，若不是则在数据库中创建好友记录并返回结果信息。
        /// </summary>
        /// <param name="session">当前网络会话，用于回复数据库处理结果。</param>
        /// <param name="request">包含发起者用户ID、目标好友ID及备注等信息的请求对象。</param>
        /// <returns></returns>
        public static async Task HandleAddFriendRequest(ISession session, DbAddFriendRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 AddFriendRequest，数据无法被反序列化。");
                return;
            }

            if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.FriendUniqueId))
            {
                SendDbResponse(session, Shared.Messages.MessageIds.DbAddFriendRes,
                    new DbAddFriendResponse { Success = false, Message = "用户ID或UniqueId无效" }, requestId);
                return;
            }

            // P2 修复：双向写操作先只读解析目标用户（UniqueId 唯一），再进入规范化成对锁，
            // 使 A→B 与 B→A 的并发写互斥（原实现只按请求方 UserKey 加锁，双向并发会竞态，
            // 一方命中唯一索引 DbUpdateException，报"服务器内部错误"而非干净的"已是好友"）。
            Shared.Data.User? targetUser = null;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();
                targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == request.FriendUniqueId.Trim());
            }
            catch (Exception ex)
            {
                Log.Error($"添加好友目标查询异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbAddFriendRes, "添加好友失败，服务器内部错误");
                return;
            }

            if (targetUser == null)
            {
                SendDbResponse(session, Shared.Messages.MessageIds.DbAddFriendRes,
                    new DbAddFriendResponse { Success = false, Message = "目标用户不存在" }, requestId);
                return;
            }

            await RunPerUser(PairKey(request.UserId, targetUser.Id), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbAddFriendResponse();

                if (targetUser.Id == request.UserId)
                {
                    response.Success = false;
                    response.Message = "不能添加自己为好友";
                }
                else
                {
                    bool hasBlacklistConflict = await dbContext.Blacklists.AnyAsync(b =>
                        (b.UserId == request.UserId && b.BlockedUserId == targetUser.Id)
                        || (b.UserId == targetUser.Id && b.BlockedUserId == request.UserId));
                    if (hasBlacklistConflict)
                    {
                        response.Success = false;
                        response.Message = "存在黑名单关系，无法添加好友";
                    }
                    else
                    {
                        bool forwardExists = await dbContext.Friends.AnyAsync(f => f.UserId == request.UserId && f.FriendUserId == targetUser.Id);
                        bool reverseExists = await dbContext.Friends.AnyAsync(f => f.UserId == targetUser.Id && f.FriendUserId == request.UserId);
                        if (forwardExists && reverseExists)
                        {
                            response.Success = false;
                            response.Message = "已经是好友了";
                        }
                        else
                        {
                            using var transaction = await dbContext.Database.BeginTransactionAsync();

                            if (!forwardExists)
                            {
                                dbContext.Friends.Add(new Shared.Data.Social.Friend
                                {
                                    UserId = request.UserId,
                                    FriendUserId = targetUser.Id,
                                    Remark = request.Remark?.Trim() ?? string.Empty,
                                    AddTime = DateTime.UtcNow
                                });
                            }

                            if (!reverseExists)
                            {
                                dbContext.Friends.Add(new Shared.Data.Social.Friend
                                {
                                    UserId = targetUser.Id,
                                    FriendUserId = request.UserId,
                                    Remark = string.Empty,
                                    AddTime = DateTime.UtcNow
                                });
                            }

                            await dbContext.SaveChangesAsync();
                            await transaction.CommitAsync();

                            response.Success = true;
                            response.Message = "添加成功";
                        }
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbAddFriendRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"添加好友异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbAddFriendRes, "添加好友失败，服务器内部错误");
            }
            });
        }

        /// <summary>
        /// 处理移除好友请求。
        /// 在数据库中查找对应的好友关系，若存在则删除并返回操作结果。
        /// </summary>
        /// <param name="session">当前网络会话，用于发送响应数据。</param>
        /// <param name="request">包含发起者用户ID和要移除的好友用户ID的请求对象。</param>
        /// <returns></returns>
        public static async Task HandleRemoveFriendRequest(ISession session, DbRemoveFriendRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 RemoveFriendRequest，数据无法被反序列化。");
                return;
            }

            if (string.IsNullOrWhiteSpace(request.FriendUniqueId))
            {
                SendDbResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes,
                    new DbRemoveFriendResponse { Success = false, Message = "好友UniqueId无效" }, requestId);
                return;
            }

            // P2 修复：先只读解析目标，再进入规范化成对锁（与 AddFriend 同一把锁），双向并发删除互斥。
            Shared.Data.User? targetUser = null;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();
                targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == request.FriendUniqueId.Trim());
            }
            catch (Exception ex)
            {
                Log.Error($"删除好友目标查询异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes, "删除好友失败，服务器内部错误");
                return;
            }

            if (targetUser == null)
            {
                SendDbResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes,
                    new DbRemoveFriendResponse { Success = false, Message = "好友不存在" }, requestId);
                return;
            }

            await RunPerUser(PairKey(request.UserId, targetUser.Id), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbRemoveFriendResponse();

                var friendPairs = await dbContext.Friends
                    .Where(f => (f.UserId == request.UserId && f.FriendUserId == targetUser.Id)
                        || (f.UserId == targetUser.Id && f.FriendUserId == request.UserId))
                    .ToListAsync();

                if (friendPairs.Count > 0)
                {
                    dbContext.Friends.RemoveRange(friendPairs);
                    await dbContext.SaveChangesAsync();
                    response.Success = true;
                    response.Message = "删除成功";
                }
                else
                {
                    response.Success = false;
                    response.Message = "好友不存在";
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"删除好友异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes, "删除好友失败，服务器内部错误");
            }
            });
        }

        /// <summary>
        /// 处理设置好友备注请求。
        /// 查找指定好友记录并更新备注文本，然后返回操作结果。
        /// </summary>
        /// <param name="session">当前网络会话，用于发送响应。</param>
        /// <param name="request">包含用户ID、好友ID以及新的备注内容的请求对象。</param>
        /// <returns></returns>
        public static async Task HandleSetFriendRemarkRequest(ISession session, DbSetFriendRemarkRequest? request, long? requestId = null)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 SetFriendRemarkRequest，数据无法被反序列化。");
                return;
            }
            // 账号级串行（P1-2）：同一用户的好友备注更新按序执行
            await RunPerUser(UserKey(request.UserId), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbSetFriendRemarkResponse();

                if (string.IsNullOrWhiteSpace(request.FriendUniqueId))
                {
                    response.Success = false;
                    response.Message = "好友UniqueId无效";
                }
                else
                {
                    string uniqueId = request.FriendUniqueId.Trim();
                    var targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == uniqueId);
                    if (targetUser == null)
                    {
                        response.Success = false;
                        response.Message = "好友不存在";
                    }
                    else
                    {
                        var friend = await dbContext.Friends.FirstOrDefaultAsync(f => f.UserId == request.UserId && f.FriendUserId == targetUser.Id);
                        if (friend != null)
                        {
                            friend.Remark = request.Remark ?? string.Empty;
                            await dbContext.SaveChangesAsync();
                            response.Success = true;
                            response.Message = "设置成功";
                        }
                        else
                        {
                            response.Success = false;
                            response.Message = "好友不存在";
                        }
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbSetFriendRemarkRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"设置好友备注异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbSetFriendRemarkRes, "设置好友备注失败，服务器内部错误");
            }
            });
        }

        /// <summary>
        /// 处理获取好友列表的请求。
        /// 查询数据库中指定用户的好友关系并将好友列表作为响应返回。
        /// </summary>
        /// <param name="session">当前网络会话，用于发送查询结果。</param>
        /// <param name="request">包含要查询好友列表的用户ID的请求对象。</param>
        /// <returns></returns>
        public static async Task HandleGetFriendsRequest(ISession session, DbGetFriendsRequest? request, long? requestId = null)
        {
            if (request == null) return;
            // 账号级串行（P1-2）：好友列表读取入队，保证读到先前已排队的写结果（read-your-writes）
            await RunPerUser(UserKey(request.UserId), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbGetFriendsResponse();

                var friendsList = await dbContext.Friends.Where(f => f.UserId == request.UserId).ToListAsync();
                var friendUserIds = friendsList.Select(f => f.FriendUserId).Distinct().ToList();
                var friendUsers = friendUserIds.Count == 0
                    ? new System.Collections.Generic.Dictionary<int, Shared.Data.User>()
                    : await dbContext.Users
                        .Where(u => friendUserIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => u);

                response.Success = true;
                response.Message = "获取成功";
                response.Friends = friendsList.Select(f =>
                {
                    friendUsers.TryGetValue(f.FriendUserId, out var user);
                    return new DbFriendItem
                    {
                        FriendUserId = f.FriendUserId,
                        FriendUniqueId = user?.UniqueId ?? string.Empty,
                        FriendNickname = user?.Nickname ?? string.Empty,
                        Remark = f.Remark,
                        AddTime = f.AddTime
                    };
                }).ToList();

                SendDbResponse(session, Shared.Messages.MessageIds.DbGetFriendsRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"获取好友列表异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbGetFriendsRes, "获取好友列表失败，服务器内部错误");
            }
            });
        }

    }
}
