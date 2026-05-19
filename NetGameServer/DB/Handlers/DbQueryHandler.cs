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
    /// 处理数据库查询相关操作的处理器类。
    /// </summary>
    public class DbQueryHandler
    {
        /// <summary>
        /// 处理获取最大UID的请求。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="request">获取最大UID的请求数据。</param>
        public static async Task HandleGetMaxUidRequest(ISession session, GetMaxUidRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 GetMaxUidRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                // 从服务提供程序获取 IServiceScopeFactory，以创建依赖注入作用域
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();

                // 从当前作用域解析数据库上下文
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                long maxSequence = 0;
                var uniqueIds = await dbContext.Users
                    .Where(u => !string.IsNullOrWhiteSpace(u.UniqueId))
                    .Select(u => u.UniqueId)
                    .ToListAsync();

                foreach (var uniqueId in uniqueIds)
                {
                    if (!long.TryParse(uniqueId, out long parsedUid))
                    {
                        continue;
                    }

                    long sequence = parsedUid % 100000000L;
                    if (sequence > maxSequence)
                    {
                        maxSequence = sequence;
                    }
                }

                // 构造响应消息格式
                var response = new GetMaxUidResponse
                {
                    MaxUid = maxSequence
                };

                // 将响应模型序列化为JSON UTF-8字节数组
                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);

                // 创建一个足够容纳协议头(4字节)和数据长度的字节数组
                byte[] packet = new byte[data.Length + 4];

                // 写入消息ID
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbGetMaxUidRes);

                // 将序列化后的数据复制到数据包中（从第4字节后开始）
                data.CopyTo(packet.AsSpan(4));

                // 通过网络会话发送响应数据包
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"获取最大UID异常: {ex}");
            }
        }

        /// <summary>
        /// 处理登录验证请求，验证账户和密码是否匹配，并返回验证结果给请求方。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="request">登录验证请求数据。</param>
        /// <returns></returns>
        public static async Task HandleLoginVerifyRequest(ISession session, LoginVerifyRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 LoginVerifyRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);

                bool isEnabled = user?.IsEnabled ?? false;
                bool isLocked = user?.IsLocked ?? false;
                bool passwordMatched = user != null
                                      && isEnabled
                                      && !isLocked
                                      && DB.DbServerApp.IsPbkdf2Hash(user.Password)
                                      && DB.DbServerApp.VerifyPbkdf2Password(request.Password, user.Password);

                if (passwordMatched && user != null)
                {
                    user.IsLoggedIn = true;
                    user.LoginCount += 1;
                    user.LastLoginTime = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }

                string message;
                if (user == null)
                {
                    message = "账号或密码错误";
                }
                else if (!isEnabled)
                {
                    message = "账号未启用";
                }
                else if (isLocked)
                {
                    message = "账号已被锁定";
                }
                else
                {
                    message = passwordMatched ? "登录成功" : "账号或密码错误";
                }

                var response = new LoginVerifyResponse
                {
                    Success = passwordMatched,
                    Message = message,
                    UserId = passwordMatched ? user!.Id : 0,
                    UniqueId = passwordMatched ? user!.UniqueId ?? string.Empty : string.Empty,
                    Nickname = passwordMatched ? user!.Nickname ?? string.Empty : string.Empty,
                    Email = passwordMatched ? user!.Email ?? string.Empty : string.Empty,
                    LastLoginTime = passwordMatched ? user!.LastLoginTime : default,
                    LoginCount = passwordMatched ? user!.LoginCount : 0,
                    IsAdmin = passwordMatched && user!.IsAdmin
                };

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbLoginVerifyRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"验证登录异常: {ex}");
            }
        }

        /// <summary>
        /// 处理注册验证请求，检查账户是否已存在，如果不存在则创建新用户记录，并返回注册结果给请求方。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="request">注册验证请求数据</param>
        /// <returns></returns>
        public static async Task HandleRegisterVerifyRequest(ISession session, RegisterVerifyRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 RegisterVerifyRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                bool exists = await dbContext.Users.AnyAsync(u => u.Account == request.Account);
                var response = new RegisterVerifyResponse();

                if (exists)
                {
                    response.Success = false;
                    response.Message = "账号已存在";
                }
                else
                {
                    var user = new Shared.Data.User
                    {
                        Account = request.Account,
                        Password = DB.DbServerApp.HashPassword(request.Password),
                        Nickname = request.Nickname,
                        UniqueId = request.Uid.ToString(),
                        RegistrationTime = DateTime.UtcNow,
                        LastLoginTime = DateTime.UtcNow
                    };
                    dbContext.Users.Add(user);

                    try
                    {
                        await dbContext.SaveChangesAsync();
                        response.Success = true;
                        response.Message = "注册成功";
                    }
                    catch (DbUpdateException)
                    {
                        response.Success = false;
                        response.Message = "账号或UID已存在";
                    }
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbRegisterVerifyRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"注册账号异常: {ex}");
            }
        }

        /// <summary>
        /// 处理账户查询请求，查询账户是否存在以及相关状态信息。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="request">账户查询请求数据。</param>
        /// <returns></returns>
        public static async Task HandleAccountQueryRequest(ISession session, AccountQueryRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 AccountQueryRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);

                var response = new AccountQueryResponse();
                if (user != null)
                {
                    response.Exists = true;
                    response.IsOnline = user.IsLoggedIn;
                    response.IsLocked = user.IsLocked;
                    response.IsAdmin = user.IsAdmin;
                    response.Email = user.Email ?? string.Empty;
                    response.Message = "查询成功";
                }
                else
                {
                    response.Exists = false;
                    response.Email = string.Empty;
                    response.Message = "账户不存在";
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbAccountQueryRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"查询账户异常: {ex}");
            }
        }

        /// <summary>
        /// 处理在线统计请求，查询当前在线用户数量、离线用户数量以及总用户数量，并返回给请求方。
        /// </summary>
        /// <param name="session">当前的网络会话。</param>
        /// <param name="request">在线统计请求数据。</param>
        /// <returns></returns>
        public static async Task HandleOnlineStatsRequest(ISession session, OnlineStatsRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 OnlineStatsRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                int totalCount = await dbContext.Users.CountAsync();
                int onlineCount = await dbContext.Users.CountAsync(u => u.IsLoggedIn);
                int offlineCount = totalCount - onlineCount;

                var response = new OnlineStatsResponse
                {
                    OnlineCount = onlineCount,
                    OfflineCount = offlineCount,
                    TotalCount = totalCount
                };

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbOnlineStatsRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"查询在线统计异常: {ex}");
            }
        }

        /// <summary>
        /// 处理更新用户在线状态的请求。
        /// 将根据请求中的用户ID查找对应用户并更新其在线状态（IsLoggedIn），
        /// 若设置为离线则更新离线时间（LastLoginTime）。处理完成后返回一个简单的成功响应。
        /// </summary>
        /// <param name="session">当前的网络会话，用于发送响应数据。</param>
        /// <param name="request">包含用户ID和在线状态的更新请求。</param>
        /// <returns></returns>
        public static async Task HandleUpdateOnlineStateRequest(ISession session, UpdateOnlineStateRequest? request)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
                if (user != null)
                {
                    user.IsLoggedIn = request.IsOnline;
                    if (!request.IsOnline)
                    {
                        // 离线时间更新为现在
                        user.LastLoginTime = DateTime.UtcNow;
                    }
                    await dbContext.SaveChangesAsync();
                }

                var response = new UpdateOnlineStateResponse { Success = true };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbUpdateOnlineStateRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"更新在线状态异常: {ex}");
            }
        }

        public static async Task HandleChangePasswordVerifyRequest(ISession session, ChangePasswordVerifyRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ChangePasswordVerifyRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
                var response = new ChangePasswordVerifyResponse();

                if (user == null)
                {
                    user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);
                }

                if (user == null)
                {
                    response.Success = false;
                    response.Message = "用户不存在";
                }
                else if (!string.Equals(user.Account, request.Account, StringComparison.Ordinal))
                {
                    response.Success = false;
                    response.Message = "账号不匹配";
                }
                else
                {
                    if (!DB.DbServerApp.IsPbkdf2Hash(user.Password))
                    {
                        response.Success = false;
                        response.Message = "当前账号密码格式不受支持，请先由管理员重置为PBKDF2密码";
                    }
                    else
                    {
                        bool oldPasswordMatched = DB.DbServerApp.VerifyPbkdf2Password(request.OldPassword, user.Password);

                        if (!oldPasswordMatched)
                        {
                            response.Success = false;
                            response.Message = "旧密码错误";
                        }
                        else
                        {
                            user.Password = DB.DbServerApp.HashPassword(request.NewPassword);
                            await dbContext.SaveChangesAsync();
                            response.Success = true;
                            response.Message = "更改密码成功";
                        }
                    }
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbChangePasswordRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"更改密码异常: {ex}");
            }
        }

        public static async Task HandleResetPasswordByEmailRequest(ISession session, ResetPasswordByEmailRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ResetPasswordByEmailRequest，数据无法被反序列化。");
                return;
            }

            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new ResetPasswordByEmailResponse();
                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);
                if (user == null)
                {
                    response.Success = false;
                    response.Message = "用户不存在";
                }
                else if (!string.Equals(user.Email?.Trim(), request.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    response.Success = false;
                    response.Message = "邮箱与账号不匹配";
                }
                else
                {
                    user.Password = DB.DbServerApp.HashPassword(request.TemporaryPassword);
                    await dbContext.SaveChangesAsync();
                    response.Success = true;
                    response.Message = "验证码校验通过，密码重置成功";
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbResetPasswordByEmailRes);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"邮箱重置密码异常: {ex}");
            }
        }

        // --- Friend系统处理程序 ---

        /// <summary>
        /// 处理添加好友请求。
        /// 检查两者是否已为好友，若不是则在数据库中创建好友记录并返回结果信息。
        /// </summary>
        /// <param name="session">当前网络会话，用于回复数据库处理结果。</param>
        /// <param name="request">包含发起者用户ID、目标好友ID及备注等信息的请求对象。</param>
        /// <returns></returns>
        public static async Task HandleAddFriendRequest(ISession session, DbAddFriendRequest? request, long? requestId = null)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbAddFriendResponse();

                if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.FriendUniqueId))
                {
                    response.Success = false;
                    response.Message = "用户ID或UniqueId无效";
                }
                else
                {
                    string uniqueId = request.FriendUniqueId.Trim();
                    var targetUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == uniqueId);
                    if (targetUser == null)
                    {
                        response.Success = false;
                        response.Message = "目标用户不存在";
                    }
                    else if (targetUser.Id == request.UserId)
                    {
                        response.Success = false;
                        response.Message = "不能添加自己为好友";
                    }
                    else
                    {
                        bool exists = await dbContext.Friends.AnyAsync(f => f.UserId == request.UserId && f.FriendUserId == targetUser.Id);
                        if (exists)
                        {
                            response.Success = false;
                            response.Message = "已经是好友了";
                        }
                        else
                        {
                            var newFriend = new Shared.Data.Social.Friend
                            {
                                UserId = request.UserId,
                                FriendUserId = targetUser.Id,
                                Remark = request.Remark ?? string.Empty,
                                AddTime = DateTime.UtcNow
                            };
                            dbContext.Friends.Add(newFriend);
                            await dbContext.SaveChangesAsync();

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
            }
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
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbRemoveFriendResponse();

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
                            dbContext.Friends.Remove(friend);
                            await dbContext.SaveChangesAsync();
                            response.Success = true;
                            response.Message = "删除成功";
                        }
                        else
                        {
                            response.Success = false;
                            response.Message = "好友不存在";
                        }
                    }
                }

                SendDbResponse(session, Shared.Messages.MessageIds.DbRemoveFriendRes, response, requestId);
            }
            catch (Exception ex)
            {
                Log.Error($"删除好友异常: {ex}");
            }
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
            if (request == null) return;
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
            }
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
            }
        }

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
                            dbContext.Blacklists.Add(new Blacklist
                            {
                                UserId = request.UserId,
                                BlockedUserId = targetUser.Id,
                                AddTime = DateTime.UtcNow
                            });
                            await dbContext.SaveChangesAsync();
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

        public static async Task HandleRemoveBlacklistRequest(ISession session, DbRemoveBlacklistRequest? request, long? requestId = null)
        {
            if (request == null) return;
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

        public static async Task HandleGetBlacklistRequest(ISession session, DbGetBlacklistRequest? request, long? requestId = null)
        {
            if (request == null) return;
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

        public static async Task HandleResolveUserByUniqueIdRequest(ISession session, DbResolveUserByUniqueIdRequest? request, long? requestId = null)
        {
            if (request == null) return;
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

        public static async Task HandleResolveUserByUserIdRequest(ISession session, DbResolveUserByUserIdRequest? request, long? requestId = null)
        {
            if (request == null) return;
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

        private static void SendDbResponse<T>(ISession session, int msgId, T response, long? requestId = null)
        {
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(response);
            if (requestId.HasValue)
            {
                payload = Shared.RouteMetadata.AttachRequestId(payload, requestId.Value);
            }

            byte[] packet = new byte[payload.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), msgId);
            payload.CopyTo(packet.AsSpan(4));
            session.Send(packet);
        }
    }
}
