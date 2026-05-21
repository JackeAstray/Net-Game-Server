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
                        Email = string.Empty,
                        UniqueId = request.Uid.ToString(),
                        RegistrationTime = DateTime.UtcNow,
                        LastLoginTime = DateTime.UtcNow,
                        IsEnabled = true
                    };
                    dbContext.Users.Add(user);

                    try
                    {
                        await dbContext.SaveChangesAsync();
                        response.Success = true;
                        response.Message = "注册成功";
                    }
                    catch (DbUpdateException ex)
                    {
                        response.Success = false;
                        string errorText = ex.InnerException?.Message ?? ex.Message;
                        Log.Error(ex, $"注册账号保存失败: {errorText}");

                        if (errorText.IndexOf("UniqueId", StringComparison.OrdinalIgnoreCase) >= 0
                            || errorText.IndexOf("uid", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            response.Message = "UID已存在";
                        }
                        else if (errorText.IndexOf("Account", StringComparison.OrdinalIgnoreCase) >= 0
                                 || errorText.IndexOf("account", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            response.Message = "账号已存在";
                        }
                        else
                        {
                            response.Message = "账号或UID已存在";
                        }
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
                else
                {
                    Log.Warning($"更新在线状态失败，用户不存在 UserId:{request.UserId} IsOnline:{request.IsOnline}");
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

        /// <summary>
        /// 验证并处理更改密码请求：校验请求与用户信息、验证旧密码的 PBKDF2 哈希，成功时更新并持久化新密码，然后向会话发送结果响应。
        /// </summary>
        /// <remarks>方法访问数据库查找用户、验证账号匹配与密码格式，使用 PBKDF2
        /// 验证旧密码并在通过时哈希并保存新密码；在任何失败或异常情况下记录相应日志并发送失败响应。</remarks>
        /// <param name="session">用于发送响应的会话连接。</param>
        /// <param name="request">包含要验证的用户标识、账号、旧密码与新密码；为 null 时记录警告并返回。</param>
        /// <returns>表示异步操作的任务。</returns>
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
                    Log.Warning($"更改密码失败，用户不存在 UserId:{request.UserId} Account:{request.Account}");
                }
                else if (!string.Equals(user.Account, request.Account, StringComparison.Ordinal))
                {
                    response.Success = false;
                    response.Message = "账号不匹配";
                    Log.Warning($"更改密码失败，账号不匹配 UserId:{request.UserId} RequestAccount:{request.Account} StoredAccount:{user.Account}");
                }
                else
                {
                    if (!DB.DbServerApp.IsPbkdf2Hash(user.Password))
                    {
                        response.Success = false;
                        response.Message = "当前账号密码格式不受支持，请先由管理员重置为PBKDF2密码";
                        Log.Warning($"更改密码失败，密码格式不受支持 UserId:{request.UserId} Account:{request.Account}");
                    }
                    else
                    {
                        bool oldPasswordMatched = DB.DbServerApp.VerifyPbkdf2Password(request.OldPassword, user.Password);

                        if (!oldPasswordMatched)
                        {
                            response.Success = false;
                            response.Message = "旧密码错误";
                            Log.Warning($"更改密码失败，旧密码错误 UserId:{request.UserId} Account:{request.Account}");
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

        /// <summary>
        /// 处理通过邮箱的重置密码请求：校验请求和用户邮箱，匹配后将临时密码哈希写入数据库，发送响应并记录日志。
        /// </summary>
        /// <remarks>通过依赖注入获取 DbContext；在请求无效或验证失败时记录警告，发生异常时记录错误。</remarks>
        /// <param name="session">用于与客户端通信的会话实例，用以发送响应包。</param>
        /// <param name="request">包含账号、邮箱与临时密码的重置请求，可能为 null。</param>
        /// <returns>表示异步操作的任务。</returns>
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
                    Log.Warning($"邮箱重置密码失败，用户不存在 Account:{request.Account} Email:{request.Email}");
                }
                else if (!string.Equals(user.Email?.Trim(), request.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    response.Success = false;
                    response.Message = "邮箱与账号不匹配";
                    Log.Warning($"邮箱重置密码失败，邮箱与账号不匹配 Account:{request.Account} RequestEmail:{request.Email} StoredEmail:{user.Email}");
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
            if (request == null)
            {
                Log.Warning("收到无效的 AddFriendRequest，数据无法被反序列化。");
                return;
            }
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
            if (request == null)
            {
                Log.Warning("收到无效的 RemoveFriendRequest，数据无法被反序列化。");
                return;
            }
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
            if (request == null)
            {
                Log.Warning("收到无效的 SetFriendRemarkRequest，数据无法被反序列化。");
                return;
            }
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

        /// <summary>
        /// 将响应对象序列化为 UTF-8 JSON，按 msgId 前缀封包并通过会话发送；异常被捕获并记录。
        /// </summary>
        /// <remarks>包格式：4 字节小端 msgId 后跟 JSON 负载（若提供则包含附加的请求 ID 元数据）。使用 Shared.Json.SerializeToUtf8Bytes
        /// 进行序列化并通过会话发送；内部捕获并记录异常。</remarks>
        /// <typeparam name="T">响应类型，可序列化为 JSON；若包含 bool Success 与 Message 属性，在 Success 为 false 时记录警告。</typeparam>
        /// <param name="session">用于发送封装后数据包的会话。</param>
        /// <param name="msgId">消息标识，以 4 字节小端格式写入包头。</param>
        /// <param name="response">要发送的响应对象，序列化为 UTF-8 JSON；可包含 Success (bool) 和 Message 属性用于日志。</param>
        /// <param name="requestId">可选请求标识，会附加到负载元数据用于路由/关联。</param>
        private static void SendDbResponse<T>(ISession session, int msgId, T response, long? requestId = null)
        {
            try
            {
                if (response != null)
                {
                    var successProperty = typeof(T).GetProperty("Success");
                    if (successProperty?.PropertyType == typeof(bool) && successProperty.GetValue(response) is bool success && !success)
                    {
                        string message = typeof(T).GetProperty("Message")?.GetValue(response)?.ToString() ?? string.Empty;
                        Log.Warning($"DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Message:{message}");
                    }
                }

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
            catch (Exception ex)
            {
                Log.Error($"发送 DB 响应失败 MsgId:{msgId} RequestId:{requestId?.ToString() ?? "none"} Exception:{ex}");
            }
        }
    }
}