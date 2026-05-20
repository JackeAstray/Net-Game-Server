using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Tcp;
using Shared;
using Shared.Messages;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DB
{
    /// <summary>
    /// 数据库服务器应用程序辅助类。
    /// 集中管理与 DB 服务相关的初始化逻辑，包括数据库初始化、服务提供者构建以及网络监听启动等。
    /// 将 Program.cs 中与数据库服务相关的流程抽取到此类，便于维护、测试与复用。
    /// </summary>
    public static class DbServerApp
    {
        /// <summary>
        /// 对外暴露的 ServiceProvider，供其他模块获取 DbContext 或其他注入服务。
        /// 在 InitializeDatabase 中构建并赋值。
        /// </summary>
        public static ServiceProvider ServiceProvider { get; private set; }

        /// <summary>
        /// 使用 PBKDF2 (HMACSHA256) 对明文密码进行加盐哈希，并以包含算法、迭代次数、盐和哈希值的可存储字符串返回。
        /// </summary>
        /// <remarks>返回值包含验证所需的所有组件；验证时应使用相同的算法、迭代次数和盐。为保持长期安全性，可能需要根据当前最佳实践调整迭代次数或算法。</remarks>
        /// <param name="rawPassword">要哈希的明文密码。</param>
        /// <returns>格式为 'PBKDF2$<iterations>$<saltBase64>$<hashBase64>' 的字符串；使用 100000 次迭代、16 字节盐和 32 字节输出（SHA-256）。</returns>
        public static string HashPassword(string rawPassword)
        {
            const int iterations = 100_000;
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(rawPassword, salt, iterations, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// 判断字符串是否为以 "PBKDF2$" 前缀标识且非空白的 PBKDF2 哈希。
        /// </summary>
        /// <remarks>使用 Ordinal 比较前缀（区分大小写）。</remarks>
        /// <param name="storedPassword">要验证的保存密码字符串。</param>
        /// <returns>如果 storedPassword 非空白且以 "PBKDF2$" 开头则为 true，否则为 false。</returns>
        public static bool IsPbkdf2Hash(string storedPassword)
        {
            return !string.IsNullOrWhiteSpace(storedPassword) && storedPassword.StartsWith("PBKDF2$", StringComparison.Ordinal);
        }

        /// <summary>
        /// 验证原始密码是否与使用 PBKDF2（SHA-256）派生并以固定时间比较的存储哈希匹配。
        /// </summary>
        /// <remarks>在存储字符串不是 PBKDF2 格式、解析失败或迭代次数无效时返回 false。使用固定时间比较以减轻时序攻击风险。</remarks>
        /// <param name="rawPassword">要验证的明文密码。</param>
        /// <param name="storedPassword">以 PBKDF2 格式存储的密码，格式为：pbkdf2$iterations$base64Salt$base64Hash。</param>
        /// <returns>若密码匹配则返回 true，否则返回 false。</returns>
        public static bool VerifyPbkdf2Password(string rawPassword, string storedPassword)
        {
            if (!IsPbkdf2Hash(storedPassword))
            {
                return false;
            }

            string[] parts = storedPassword.Split('$');
            if (parts.Length != 4)
            {
                return false;
            }

            if (!int.TryParse(parts[1], out int iterations) || iterations <= 0)
            {
                return false;
            }

            byte[] salt;
            byte[] expectedHash;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expectedHash = Convert.FromBase64String(parts[3]);
            }
            catch
            {
                return false;
            }

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(rawPassword, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }

        /// <summary>
        /// 初始化数据库连接与初始数据：
        /// 1. 根据配置构建 DbContext 并生成 ServiceProvider。
        /// 2. 确保数据库存在（EnsureCreated）。
        /// 3. 若不存在默认超级管理员账号，则插入一个便于首次使用。
        /// </summary>
        public static void InitializeDatabase()
        {
            var services = new ServiceCollection();
            // 从配置读取连接字符串，若未配置则使用默认开发环境的连接字符串作为回退
            string connectionString = ConfigHelper.GetConfig<string>("ConnectionStrings:MySqlConnection") ?? "Server=127.0.0.1;Port=3306;Database=GameDB;Uid=Ycs;Pwd=Ycs982109683;";
            services.AddDbContext<DefaultDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

            ServiceProvider = services.BuildServiceProvider();

            // 通过 scope 获取一次性使用的 DbContext 执行初始化任务
            using (var scope = ServiceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();
                try
                {
                    Shared.Log.Info("正在检查并尝试创建数据库(如果不存在)...");
                    dbContext.Database.EnsureCreated();
                    Shared.Log.Info("数据库检查完毕.");

                    int regionId = Shared.ConfigHelper.GetConfig<int>("RegionId") == 0 ? 1 : Shared.ConfigHelper.GetConfig<int>("RegionId");
                    long currentMaxSequence = 0;
                    foreach (var uniqueId in dbContext.Users.Where(u => !string.IsNullOrWhiteSpace(u.UniqueId)).Select(u => u.UniqueId).ToList())
                    {
                        if (!long.TryParse(uniqueId, out long parsedUid))
                        {
                            continue;
                        }

                        long sequence = parsedUid % 100000000L;
                        if (sequence > currentMaxSequence)
                        {
                            currentMaxSequence = sequence;
                        }
                    }

                    Shared.UIDGenerator.Initialize(regionId, currentMaxSequence);

                    InitializeRedisConnection();

                    // 检查是否存在默认超级管理员账号，如不存在则创建一个（仅在首次初始化时执行）
                    if (!dbContext.Users.Any(u => u.Account == "SuperAdmin"))
                    {
                        long adminUid = Shared.UIDGenerator.GenerateLongUID();
                        var adminUser = new Shared.Data.User
                        {
                            Id = 1000,
                            Account = "SuperAdmin",
                            Password = HashPassword("SuperAdmin"),
                            Email = "982109683@qq.com",
                            Nickname = "超级管理员",
                            UniqueId = adminUid.ToString(),
                            RegistrationTime = DateTime.UtcNow,
                            LastLoginTime = DateTime.UtcNow,
                            LoginIP = "127.0.0.1",
                            IsEnabled = true,
                            IsAdmin = true
                        };
                        dbContext.Users.Add(adminUser);
                        dbContext.SaveChanges();
                        Shared.Log.Warning($"成功创建默认超级管理员，默认密码请在首次部署后立即修改。UID:{adminUid}");
                    }
                }
                catch (Exception ex)
                {
                    // 捕获并记录初始化过程中出现的异常，便于运维与排查
                    Shared.Log.Error($"数据库初始化失败: {ex}");
                    if (ex.InnerException != null)
                    {
                        Shared.Log.Error($"数据库初始化内部异常: {ex.InnerException}");
                    }
                }
            }
        }

        /// <summary>
        /// 初始化 Redis 连接，使用配置中的 RedisConnectionString。
        /// 若配置缺失则回退到本地默认实例。
        /// </summary>
        private static void InitializeRedisConnection()
        {
            string redisConnectionString = ConfigHelper.GetConfig("RedisConnectionString") ?? "127.0.0.1:6379,abortConnect=false";

            try
            {
                RedisHelper.Initialize(redisConnectionString);
                _ = RedisHelper.Connection;
                Shared.Log.Info("Redis 连接已建立。");
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"Redis 连接初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动数据库服务的网络监听（TCP），并绑定消息路由处理器。
        /// 主要职责：
        /// - 创建 NetworkManager 与 TcpServer。
        /// - 注册各类消息处理器（通过 Routing.MessageRouter）。
        /// - 启动监听指定端口以接收来自网关或其他服务的请求。
        /// </summary>
        /// <returns></returns>
        public static async Task StartNetworkAsync()
        {
            // 从配置读取端口，若未配置则使用默认 31305
            int port = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 31305 : ConfigHelper.GetConfig<int>("DBPort");

            var tcpServer = new TcpServer();

            // 创建路由器并注册各类数据库相关的消息处理器
            var router = new Routing.MessageRouter();
            router.RegisterHandler(MessageIds.DbGetMaxUidReq, async (session, data) => await Handlers.DbQueryHandler.HandleGetMaxUidRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.GetMaxUidRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbLoginVerifyReq, async (session, data) => await Handlers.DbQueryHandler.HandleLoginVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.LoginVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbRegisterVerifyReq, async (session, data) => await Handlers.DbQueryHandler.HandleRegisterVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.RegisterVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbAccountQueryReq, async (session, data) => await Handlers.DbQueryHandler.HandleAccountQueryRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.AccountQueryRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbOnlineStatsReq, async (session, data) => await Handlers.DbQueryHandler.HandleOnlineStatsRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.OnlineStatsRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbUpdateOnlineStateReq, async (session, data) => await Handlers.DbQueryHandler.HandleUpdateOnlineStateRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.UpdateOnlineStateRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbAddFriendReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleAddFriendRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddFriendRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbRemoveFriendReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleRemoveFriendRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveFriendRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbSetFriendRemarkReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleSetFriendRemarkRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbSetFriendRemarkRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbGetFriendsReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleGetFriendsRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetFriendsRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbChangePasswordReq, async (session, data) => await Handlers.DbQueryHandler.HandleChangePasswordVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.ChangePasswordVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbResetPasswordByEmailReq, async (session, data) => await Handlers.DbQueryHandler.HandleResetPasswordByEmailRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.ResetPasswordByEmailRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbAddBlacklistReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleAddBlacklistRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddBlacklistRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbRemoveBlacklistReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleRemoveBlacklistRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveBlacklistRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbGetBlacklistReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleGetBlacklistRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetBlacklistRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbResolveUserByUniqueIdReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleResolveUserByUniqueIdRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUniqueIdRequest>(cleanPayload), requestId);
            });
            router.RegisterHandler(MessageIds.DbResolveUserByUserIdReq, async (session, data) =>
            {
                long? requestId = Shared.RouteMetadata.TryExtractRequestId(data, out long extractedRequestId, out var cleanPayload) ? extractedRequestId : null;
                await Handlers.DbQueryHandler.HandleResolveUserByUserIdRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbResolveUserByUserIdRequest>(cleanPayload), requestId);
            });

            // 简单的会话事件日志，用于监控连接与流量，实际部署时可扩展鉴权或限流逻辑
            tcpServer.OnSessionConnected += session => Shared.Log.Info($"DB <- Client 已连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) =>
            {
                if (data.Length < 4)
                {
                    Shared.Log.Warning($"DB 收到无效数据，长度不足4 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Length:{data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                Shared.Log.Info($"DB <- Client 收到消息 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} MsgId:{msgId} PacketLength:{data.Length} PayloadLength:{data.Length - 4}");
            };

            // 将路由器绑定到 TcpServer，使其负责分发收到的消息
            router.BindServer(tcpServer);
            tcpServer.OnSessionDisconnected += (session, reason) => Shared.Log.Info($"DB <- Client 断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint} Reason:{reason}");

            // 启动监听并记录启动信息
            await tcpServer.StartAsync(port);
            Shared.Log.Info($"DB服务器已启动，监听端口: {port}");
        }
    }
}