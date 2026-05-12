using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Network.Tcp;
using Shared;
using Shared.Messages;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DB
{
    public static class DbServerApp
    {
        public static ServiceProvider ServiceProvider { get; private set; }

        public static string ComputeMd5Hash(string rawData)
        {
            using (MD5 md5Hash = MD5.Create())
            {
                byte[] bytes = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        public static void InitializeDatabase()
        {
            var services = new ServiceCollection();
            string connectionString = ConfigHelper.GetConfig<string>("ConnectionStrings:MySqlConnection") ?? "Server=127.0.0.1;Port=3306;Database=GameDB;Uid=Ycs;Pwd=Ycs982109683;";
            services.AddDbContext<DefaultDbContext>(options =>
                options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

            ServiceProvider = services.BuildServiceProvider();

            using (var scope = ServiceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();
                try
                {
                    Shared.Log.Info("正在检查并尝试创建数据库(如果不存在)...");
                    dbContext.Database.EnsureCreated();
                    Shared.Log.Info("数据库检查完毕.");

                    if (!dbContext.Users.Any(u => u.Account == "SuperAdmin"))
                    {
                        string adminUniqueId = UIDGenerator.GenerateStringUID();
                        if (adminUniqueId == "0" || adminUniqueId.Length < 9)
                        {
                            adminUniqueId = "100000001";
                        }

                        var adminUser = new Shared.Data.User
                        {
                            Id = 1000,
                            Account = "SuperAdmin",
                            Password = "SuperAdmin",
                            Email = "982109683@qq.com",
                            Nickname = "超级管理员",
                            UniqueId = adminUniqueId,
                            RegistrationTime = DateTime.Now,
                            LastLoginTime = DateTime.Now,
                            LoginIP = "127.0.0.1",
                            IsEnabled = true,
                            IsAdmin = true
                        };
                        dbContext.Users.Add(adminUser);
                        dbContext.SaveChanges();
                        Shared.Log.Info("成功创建默认超级管理员。");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"数据库初始化失败: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Shared.Log.Error($"Detailed Inner Exception: {ex.InnerException.Message}");
                    }
                }
            }
        }

        public static async Task StartNetworkAsync()
        {
            int port = ConfigHelper.GetConfig<int>("DBPort") == 0 ? 30005 : ConfigHelper.GetConfig<int>("DBPort");

            var networkManager = new NetworkManager();
            var tcpServer = new TcpServer();

            var router = new Routing.MessageRouter();
            router.RegisterHandler(MessageIds.DbGetMaxUidReq, async (session, data) => await Handlers.DbQueryHandler.HandleGetMaxUidRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.GetMaxUidRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbLoginVerifyReq, async (session, data) => await Handlers.DbQueryHandler.HandleLoginVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.LoginVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbRegisterVerifyReq, async (session, data) => await Handlers.DbQueryHandler.HandleRegisterVerifyRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.RegisterVerifyRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbAccountQueryReq, async (session, data) => await Handlers.DbQueryHandler.HandleAccountQueryRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.AccountQueryRequest>(data.Span)));
            router.RegisterHandler(MessageIds.DbOnlineStatsReq, async (session, data) => await Handlers.DbQueryHandler.HandleOnlineStatsRequest(session, Shared.Json.DeserializeFromUtf8Bytes<Shared.Messages.Db.OnlineStatsRequest>(data.Span)));

            tcpServer.OnSessionConnected += session => Shared.Log.Info($"客户端已连接: {session.RemoteEndPoint}");
            tcpServer.OnDataReceived += (session, data) =>
            {
                Shared.Log.Info($"接收到数据，长度: {data.Length}");
            };
            router.BindServer(tcpServer);
            tcpServer.OnSessionDisconnected += (session, reason) => Shared.Log.Info($"客户端断开连接，原因: {reason}");

            networkManager.RegisterServer("DBTcp", tcpServer);

            await networkManager.StartServerAsync("DBTcp", port);
            Shared.Log.Info($"DB服务器已启动，监听端口: {port}");
        }
    }
}