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
    /// DB 查询 Handler —— 账户模块（最大 UID/登录验证/注册验证/账户查询/在线统计/在线状态）。
    /// 与 DbQueryHandler.cs 同属一个 partial class，按业务模块分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public partial class DbQueryHandler
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

                // P2 修复：原实现全表拉取 UniqueId 到内存找最大序号；改为单条 SQL 聚合。
                // 修复（P3）：不能对字符串列取字典序 MAX——一旦出现位数不同的 UID（例如 8 位脏数据 "12345679" 与 9 位 "112345680"），
                // 字符串序 "12345679" > "112345680"，会把序号同步到偏小的值，从而生成已存在的 UID，引发注册"UID 生成冲突"。
                // 改为"先按长度降序、再按字典序降序"得到数值最大（纯数字、无前导零的定宽/变宽 UID 均正确）。
                // P2 修复（空表）：Users 表为空或全无 UniqueId 时，MaxAsync 会抛 "Sequence contains no elements"，
                // 产生异常日志噪音并回错包；先 Any 判定，空表返回 MaxUid=0（正是正确的初始序号，调用方默认值一致）。
                long maxSequence = 0;
                if (await dbContext.Users.AnyAsync(u => !string.IsNullOrWhiteSpace(u.UniqueId)))
                {
                    string? maxUniqueId = await dbContext.Users
                        .Where(u => !string.IsNullOrWhiteSpace(u.UniqueId))
                        .OrderByDescending(u => u.UniqueId.Length)
                        .ThenByDescending(u => u.UniqueId)
                        .Select(u => u.UniqueId)
                        .FirstOrDefaultAsync();
                    if (maxUniqueId != null && long.TryParse(maxUniqueId, out long maxUid))
                    {
                        maxSequence = maxUid % 100000000L;
                    }
                }

                // 构造响应消息格式
                var response = new GetMaxUidResponse
                {
                    MaxUid = maxSequence
                };

                // 将响应模型序列化为JSON UTF-8字节数组
                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);

                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbGetMaxUidRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"获取最大UID异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbGetMaxUidRes, "获取最大UID失败，服务器内部错误");
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

            // 账号级串行（P1-2）：同一账号的登录读改写按序执行，防止并发写丢更新
            await RunPerUser(AccountKey(request.Account), async () =>
            {
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
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbLoginVerifyRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"验证登录异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbLoginVerifyRes, "登录验证失败，服务器内部错误");
            }
            });
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

            // 账号级串行（P1-2）：同一账号的注册读改写按序执行（与登录同 key，注册后立即可见）
            await RunPerUser(AccountKey(request.Account), async () =>
            {
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
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbRegisterVerifyRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"注册账号异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbRegisterVerifyRes, "注册失败，服务器内部错误");
            }
            });
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
                    response.UserId = user.Id;
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
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbAccountQueryRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"查询账户异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbAccountQueryRes, "账户查询失败，服务器内部错误");
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
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbOnlineStatsRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"查询在线统计异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbOnlineStatsRes, "在线统计失败，服务器内部错误");
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
            // 账号级串行（P1-2）：同一用户的在线状态读改写按序执行，防止并发在线/离线更新相互覆盖
            await RunPerUser(UserKey(request.UserId), async () =>
            {
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
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbUpdateOnlineStateRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"更新在线状态异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbUpdateOnlineStateRes, "更新在线状态失败，服务器内部错误");
            }
            });
        }

    }
}
