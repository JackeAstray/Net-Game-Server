using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Network.Tcp;
using Shared;
using Shared.Messages.Login;
using MailKit.Net.Smtp;
using MimeKit;
using Shared.Data;
using Shared.Messages;
namespace Login.Handlers
{
    /// <summary>
    /// 处理登录模块的业务逻辑。封装了对 DB 服务的请求调用（通过 TcpClientWrapper）
    /// 并提供登录、注册、找回密码、查询账户和在线统计等功能的异步方法。
    /// </summary>
    public partial class LoginHandler
    {
        private readonly TcpClientWrapper dbClient;
        private readonly Framework.Core.Security.TokenService tokenService;

        private static long sequenceId = 0;
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan ThrottleLockDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FindPasswordCooldown = TimeSpan.FromMinutes(10);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActionAttemptTracker> actionAttemptTrackers =
            new System.Collections.Concurrent.ConcurrentDictionary<string, ActionAttemptTracker>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> findPasswordCooldowns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 创建 LoginHandler 的实例。
        /// </summary>
        /// <param name="dbClient">用于与 DB 服务通信的 TcpClient 封装器。</param>
        public LoginHandler(TcpClientWrapper dbClient)
        {
            this.dbClient = dbClient;
            // 无状态签名 Token 服务：密钥从配置读取，缺省时使用随机密钥（重启后旧 Token 失效，保证安全性）。
            string secret = Shared.ConfigHelper.GetConfig<string>("TokenSecret") ?? Guid.NewGuid().ToString("N");
            tokenService = new Framework.Core.Security.TokenService(secret);
        }

        /// <summary>
        /// 生成登录 Token（HMAC-SHA256 签名，含用户身份、SessionSeq（防重放）、过期时间，无状态可验证）。
        /// D6：登录发放 seq=1；续签/重连由调用方传入递增 seq。
        /// </summary>
        public string IssueToken(int userId, string uid) => tokenService.Issue(userId, uid, seq: 1);

        /// <summary>
        /// 验证 Token。成功返回 (userId, uid, seq, expires)；失败或重放旧 seq 返回 null。
        /// </summary>
        public (int UserId, string Uid, long Seq, long Expires)? VerifyToken(string? token) => tokenService.Verify(token);

        /// <summary>
        /// 异步处理登录请求：向 DB 服务发送验证请求并返回登录响应。
        /// 若验证成功，会在响应中生成临时 Token（仅示例用途）。
        /// </summary>
        /// <param name="request">包含账号和密码的登录请求对象。</param>
        /// <param name="clientSessionId">来自网关的客户端会话ID；HTTP场景可为0。</param>
        /// <returns>包含登录结果、提示信息、用户 Id 及临时 Token 的 LoginResponse。</returns>
        public async Task<LoginResponse> HandleLoginRequestAsync(LoginRequest request, long clientSessionId = 0)
        {
            string account = request.Account?.Trim() ?? string.Empty;
            Log.Info($"收到帐户的LoginRequest: {account}");

            if (string.IsNullOrWhiteSpace(account))
            {
                Log.Warning("登录失败：账号不能为空。");
                return new LoginResponse
                {
                    Success = false,
                    Message = "账号不能为空",
                    UserId = 0,
                    Token = string.Empty
                };
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                Log.Warning($"登录失败：密码不能为空，账号:{account}");
                return new LoginResponse
                {
                    Success = false,
                    Message = "密码不能为空",
                    UserId = 0,
                    Token = string.Empty
                };
            }

            if (TryGetThrottleRemaining("login", account, out var remaining))
            {
                int waitSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                Log.Warning($"账号 {account} 触发登录锁定，剩余 {waitSeconds} 秒");
                return new LoginResponse
                {
                    Success = false,
                    Message = $"登录失败次数过多，请在 {waitSeconds} 秒后重试",
                    UserId = 0,
                    Token = string.Empty
                };
            }

            var verifyReq = new Shared.Messages.Db.LoginVerifyRequest
            {
                Account = account,
                Password = request.Password
            };

            var verifyResp = await CallDbAsync<Shared.Messages.Db.LoginVerifyResponse>(MessageIds.DbLoginVerifyReq, verifyReq);
            if (verifyResp == null)
            {
                Log.Error($"登录失败：DB 响应为空，账号:{account}, Session:{clientSessionId}");
            }

            var response = new LoginResponse
            {
                Success = verifyResp?.Success ?? false,
                Message = verifyResp?.Message ?? "服务器内部错误",
                UserId = (int)(verifyResp?.UserId ?? 0),
                // 真实签名 Token：HMAC-SHA256 无状态签发，替代原 Guid 占位符
                Token = verifyResp?.Success == true ? IssueToken((int)verifyResp.UserId, verifyResp.UniqueId ?? string.Empty) : string.Empty,
                UniqueId = verifyResp?.Success == true ? verifyResp.UniqueId ?? string.Empty : string.Empty,
                Nickname = verifyResp?.Success == true ? verifyResp.Nickname ?? string.Empty : string.Empty,
                Email = verifyResp?.Success == true ? verifyResp.Email ?? string.Empty : string.Empty,
                LastLoginTime = verifyResp?.Success == true ? verifyResp.LastLoginTime : default,
                LoginCount = verifyResp?.Success == true ? verifyResp.LoginCount : 0,
                IsAdmin = verifyResp?.Success == true && verifyResp.IsAdmin
            };

            if (response.Success)
            {
                ClearFailedAttempts("login", account);
            }
            else
            {
                RegisterFailedAttempt("login", account);
            }

            if (response.Success && response.UserId > 0 && clientSessionId > 0)
            {
                await Managers.SessionManager.Instance.OnUserLoginAsync(new User { Id = response.UserId }, clientSessionId);
            }

            return response;
        }


        /// <summary>
        /// 异步处理注册请求：向 DB 服务请求创建新用户并返回结果。
        /// </summary>
        /// <param name="request">包含账号、密码、昵称等注册信息的请求对象。</param>
        /// <returns>RegisterResponse，指示注册是否成功及提示信息。</returns>
        public async Task<RegisterResponse> HandleRegisterRequestAsync(RegisterRequest request)
        {
            string account = request.Account?.Trim() ?? string.Empty;
            Log.Info($"收到帐户的RegisterRequest: {account}");

            if (string.IsNullOrWhiteSpace(account))
            {
                Log.Warning("注册失败：账号不能为空。");
                return new RegisterResponse
                {
                    Success = false,
                    Message = "账号不能为空"
                };
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                Log.Warning($"注册失败：密码不能为空，账号:{account}");
                return new RegisterResponse
                {
                    Success = false,
                    Message = "密码不能为空"
                };
            }

            if (string.IsNullOrWhiteSpace(request.Nickname))
            {
                Log.Warning($"注册失败：昵称不能为空，账号:{account}");
                return new RegisterResponse
                {
                    Success = false,
                    Message = "昵称不能为空"
                };
            }

            if (TryGetThrottleRemaining("register", account, out var remaining))
            {
                int waitSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                Log.Warning($"注册失败：操作过于频繁，账号:{account}，剩余 {waitSeconds} 秒");
                return new RegisterResponse
                {
                    Success = false,
                    Message = $"操作过于频繁，请在 {waitSeconds} 秒后重试"
                };
            }

            if (!UIDGenerator.IsInitialized)
            {
                Log.Warning($"注册失败：UID 生成器未初始化，账号:{account}");
                return new RegisterResponse
                {
                    Success = false,
                    Message = "服务器正在初始化UID，请稍后重试"
                };
            }

            const int maxUidRetry = 3;
            for (int attempt = 0; attempt < maxUidRetry; attempt++)
            {
                long uniqueId;
                try
                {
                    uniqueId = UIDGenerator.GenerateLongUID();
                }
                catch (InvalidOperationException ex)
                {
                    Log.Warning($"注册失败：UID 生成异常，账号:{account}，异常:{ex.Message}");
                    return new RegisterResponse
                    {
                        Success = false,
                        Message = "服务器正在初始化UID，请稍后重试"
                    };
                }

                var verifyReq = new Shared.Messages.Db.RegisterVerifyRequest
                {
                    Account = account,
                    Password = request.Password,
                    Nickname = request.Nickname,
                    Uid = uniqueId
                };

                var verifyResp = await CallDbAsync<Shared.Messages.Db.RegisterVerifyResponse>(MessageIds.DbRegisterVerifyReq, verifyReq);
                if (verifyResp == null)
                {
                    Log.Error($"注册失败：DB 响应为空，账号:{account}, Attempt:{attempt + 1}");
                }

                if (verifyResp?.Success == true)
                {
                    ClearFailedAttempts("register", account);
                    return new RegisterResponse
                    {
                        Success = true,
                        Message = "注册成功"
                    };
                }

                string message = verifyResp?.Message ?? "注册失败";
                if (!string.Equals(message, "UID已存在", StringComparison.Ordinal))
                {
                    Log.Warning($"注册失败：账号:{account}，原因:{message}");
                    RegisterFailedAttempt("register", account);
                    return new RegisterResponse
                    {
                        Success = false,
                        Message = message
                    };
                }

                Log.Warning($"注册遇到 UID 冲突，账号:{account}，第 {attempt + 1} 次重试。");
                await SyncUidGeneratorFromDbAsync();
            }

            RegisterFailedAttempt("register", account);
            return new RegisterResponse
            {
                Success = false,
                Message = "UID生成冲突，请重试"
            };
        }
    }
}
