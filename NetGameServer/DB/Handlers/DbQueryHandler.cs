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

                int maxUid = 0;
                // 判断如果存在用户数据，则获取所有记录中最大的UID
                if (await dbContext.Users.AnyAsync())
                {
                    maxUid = await dbContext.Users.MaxAsync(u => u.Id);
                }

                // 构造响应消息格式
                var response = new GetMaxUidResponse
                {
                    MaxUid = maxUid
                };

                // 将响应模型序列化为JSON UTF-8字节数组
                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);

                // 创建一个足够容纳协议头(4字节)和数据长度的字节数组
                byte[] packet = new byte[data.Length + 4];

                // 写入消息ID（此处1000为模拟消息ID，使用小端序列化封装在封包前4个字节）
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1000);

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

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account && u.Password == request.Password);

                //string hashedPassword = Program.ComputeMd5Hash(request.Password);
                //var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account && u.Password == hashedPassword);

                var response = new LoginVerifyResponse
                {
                    Success = user != null,
                    Message = user != null ? "登录成功" : "账号或密码错误",
                    UserId = user?.Id ?? 0
                };

                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1001);
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
                        Password = request.Password,
                        Nickname = request.Nickname,
                        UniqueId = request.Uid.ToString(),
                        RegistrationTime = DateTime.UtcNow,
                        LastLoginTime = DateTime.UtcNow
                    };
                    dbContext.Users.Add(user);
                    await dbContext.SaveChangesAsync();

                    response.Success = true;
                    response.Message = "注册成功";
                }

                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1002);
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
                    response.Message = "查询成功";
                }
                else
                {
                    response.Exists = false;
                    response.Message = "账户不存在";
                }

                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1003);
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

                byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), 1004);
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
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbUpdateOnlineStateReq);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"更新在线状态异常: {ex}");
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
        public static async Task HandleAddFriendRequest(ISession session, DbAddFriendRequest? request)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbAddFriendResponse();

                // Check if already friends
                bool exists = await dbContext.Friends.AnyAsync(f => f.UserId == request.UserId && f.FriendUserId == request.FriendUserId);
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
                        FriendUserId = request.FriendUserId,
                        Remark = request.Remark ?? string.Empty,
                        AddTime = DateTime.UtcNow
                    };
                    dbContext.Friends.Add(newFriend);
                    await dbContext.SaveChangesAsync();

                    response.Success = true;
                    response.Message = "添加成功";
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbAddFriendReq); // NOTE: Ideally use something like DbAddFriendRes, but using Req ID for simplicity based on previous pattern
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
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
        public static async Task HandleRemoveFriendRequest(ISession session, DbRemoveFriendRequest? request)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbRemoveFriendResponse();

                var friend = await dbContext.Friends.FirstOrDefaultAsync(f => f.UserId == request.UserId && f.FriendUserId == request.FriendUserId);
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

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbRemoveFriendReq);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
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
        public static async Task HandleSetFriendRemarkRequest(ISession session, DbSetFriendRemarkRequest? request)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbSetFriendRemarkResponse();

                var friend = await dbContext.Friends.FirstOrDefaultAsync(f => f.UserId == request.UserId && f.FriendUserId == request.FriendUserId);
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

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbSetFriendRemarkReq);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
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
        public static async Task HandleGetFriendsRequest(ISession session, DbGetFriendsRequest? request)
        {
            if (request == null) return;
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new DbGetFriendsResponse();

                var friendsList = await dbContext.Friends.Where(f => f.UserId == request.UserId).ToListAsync();

                response.Success = true;
                response.Message = "获取成功";
                response.Friends = friendsList;

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = new byte[data.Length + 4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), Shared.Messages.MessageIds.DbGetFriendsReq);
                data.CopyTo(packet.AsSpan(4));
                session.Send(packet);
            }
            catch (Exception ex)
            {
                Log.Error($"获取好友列表异常: {ex}");
            }
        }
    }
}