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

        public static string HashPassword(string rawPassword)
        {
            const int iterations = 100_000;
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(rawPassword, salt, iterations, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool IsPbkdf2Hash(string storedPassword)
        {
            return !string.IsNullOrWhiteSpace(storedPassword) && storedPassword.StartsWith("PBKDF2$", StringComparison.Ordinal);
        }

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
                    Shared.Log.Error($"数据库初始化失败: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Shared.Log.Error($"Detailed Inner Exception: {ex.InnerException.Message}");
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
            router.RegisterHandler(MessageIds.DbAddFriendReq, async (session, data) => await Handlers.DbQueryHandler.HandleAddFriendRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbAddFriendRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbRemoveFriendReq, async (session, data) => await Handlers.DbQueryHandler.HandleRemoveFriendRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbRemoveFriendRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbSetFriendRemarkReq, async (session, data) => await Handlers.DbQueryHandler.HandleSetFriendRemarkRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbSetFriendRemarkRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbGetFriendsReq, async (session, data) => await Handlers.DbQueryHandler.HandleGetFriendsRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.DbGetFriendsRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbChangePasswordReq, async (session, data) => await Handlers.DbQueryHandler.HandleChangePasswordVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.ChangePasswordVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbResetPasswordByEmailReq, async (session, data) => await Handlers.DbQueryHandler.HandleResetPasswordByEmailRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.ResetPasswordByEmailRequest>(data.Span)));

            // 简单的会话事件日志，用于监控连接与流量，实际部署时可扩展鉴权或限流逻辑
            tcpServer.OnSessionConnected += session => Shared.Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) =>
            {
                Shared.Log.Info($"接收到数据，长度: {data.Length}");
            };

            // 将路由器绑定到 TcpServer，使其负责分发收到的消息
            router.BindServer(tcpServer);
            tcpServer.OnSessionDisconnected += (session, reason) => Shared.Log.Info($"客户端断开连接，原因: {reason}");

            // 启动监听并记录启动信息
            await tcpServer.StartAsync(port);
            Shared.Log.Info($"DB服务器已启动，监听端口: {port}");
        }
    }
}