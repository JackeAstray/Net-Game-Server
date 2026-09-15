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
        // 防重放状态：跨请求共享的单调 SessionSeq（D6 修复）。此前 token 只签发不验证、防重放从未生效；
        // 现在签发走 IssueNextSeq（同用户新登录 seq 单调递增），验证走 TryAcceptSeq（旧 token 重放被拒）。
        private readonly Framework.Core.Security.SessionGuard.AntiReplayState tokenAntiReplay = new();

        /// <summary>
        /// DB 请求序列号（<see cref="CallDbAsync{T}"/> 生成 RequestId 用，Interlocked 递增）。
        /// 注意：它被 <c>LoginHandler.Security.cs</c> 使用，勿删。
        /// </summary>
        private static long sequenceId = 0;

        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan ThrottleLockDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FindPasswordCooldown = TimeSpan.FromMinutes(10);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActionAttemptTracker> actionAttemptTrackers =
            new System.Collections.Concurrent.ConcurrentDictionary<string, ActionAttemptTracker>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 账号脱敏（PII 治理）：日志中不再输出明文账号（此前每次登录/注册都会以 Info 级把账号写入
        /// 本地日志文件并随 RemoteLog 聚合上报）。仅保留首尾少量字符用于人工比对，
        /// 例如 <c>alice@example.com</c> → <c>al***om(len=17)</c>。
        /// 失败/告警路径（低频、排障必需）仍保留完整账号。
        /// </summary>
        internal static string RedactAccount(string? account)
        {
            if (string.IsNullOrEmpty(account))
            {
                return "(empty)";
            }
            if (account.Length <= 2)
            {
                return new string('*', account.Length) + $"(len={account.Length})";
            }
            string head = account.Substring(0, 2);
            int tailLen = Math.Min(2, account.Length - 2);
            string tail = tailLen > 0 ? account.Substring(account.Length - tailLen) : string.Empty;
            return $"{head}***{tail}(len={account.Length})";
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> findPasswordCooldowns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 安全修复（P0）：找回密码一次性验证码登记表（内存态，带过期时间）。
        // 阶段一仅登记，阶段二校验通过后才真正重置密码，杜绝"请求即锁号"的 DoS。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingPasswordReset> pendingPasswordResets =
            new System.Collections.Concurrent.ConcurrentDictionary<string, PendingPasswordReset>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 创建 LoginHandler 的实例。
        /// </summary>
        /// <param name="dbClient">用于与 DB 服务通信的 TcpClient 封装器。</param>
        public LoginHandler(TcpClientWrapper dbClient)
        {
            this.dbClient = dbClient;
            // 无状态签名 Token 服务：密钥从配置读取，缺省时使用随机密钥（重启后旧 Token 失效，保证安全性）。
            string secret = Shared.ConfigHelper.GetConfig<string>("TokenSecret") ?? Guid.NewGuid().ToString("N");
            // 安全修复：占位符/示例密钥（含 .env.example 的 CHANGE_ME_* 模板值）拒绝上线，避免公开常量作为
            // 令牌签名密钥导致任意伪造 token；缺失时仍随机回退（快速启动可用），但配置了占位符/过短密钥
            // 直接启动失败（fail-closed），杜绝"部署即静默失守"。
            Framework.Core.Security.SecretConfig.RejectPlaceholder(secret, "TokenSecret");
            if (secret.Length < 16)
            {
                throw new InvalidOperationException("TokenSecret 长度过短（<16 字符）：请配置强随机 TokenSecret（建议 ≥32 字符），禁止使用弱密钥。");
            }
            tokenService = new Framework.Core.Security.TokenService(secret);
        }

        /// <summary>
        /// 生成登录 Token（HMAC-SHA256 签名，含用户身份、SessionSeq（防重放）、过期时间，无状态可验证）。
        /// D6 修复：seq 由共享 AntiReplayState.IssueNextSeq 单调递增，同用户新登录不互相覆盖、
        /// 旧 Token 无法重放（此前固定 seq:1 导致第二个 Token 一定被防重放拒绝或可被重放）。
        /// </summary>
        public string IssueToken(int userId, string uid) => tokenService.Issue(userId, uid, tokenAntiReplay.IssueNextSeq(userId));

        /// <summary>
        /// 验证 Token。成功返回 (userId, uid, seq, expires)；失败或重放旧 seq 返回 null。
        /// D6 修复：接入共享 AntiReplayState，旧 Token/重放 seq 会被 TryAcceptSeq 拒绝。
        /// </summary>
        public (int UserId, string Uid, long Seq, long Expires)? VerifyToken(string? token)
            => tokenService.Verify(token, tokenAntiReplay);

        /// <summary>
        /// 周期清理防重放状态中长期不活跃用户的条目（P2 修复：防字典随累计登录用户数无界增长）。
        /// 由 Login 心跳循环周期调用。
        /// </summary>
        public void SweepTokenReplay(TimeSpan idle) => tokenAntiReplay.Sweep(idle);

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
            Log.Info($"收到帐户的LoginRequest: {RedactAccount(account)}");

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

            // P3 修复：输入长度上限（超长账号/密码会放大 PBKDF2 哈希与 Redis 键成本，匿名可达）。
            if (account.Length > 64 || request.Password.Length > 128)
            {
                Log.Warning($"登录失败：账号或密码超长，账号长度:{account.Length}");
                return new LoginResponse
                {
                    Success = false,
                    Message = "账号或密码格式不正确",
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
            else if (verifyResp != null)
            {
                // P3 修复：仅在"凭据确实错误"时累计失败次数。DB 超时/不可用（verifyResp == null）属服务端故障，
                // 原实现同样计数 → DB 抖动 5 次即把账号锁定 300 秒（自伤放大；攻击者也可借制造 DB 超时批量锁号）。
                RegisterFailedAttempt("login", account);
            }
            else
            {
                Log.Warning($"登录未计入失败次数（DB 无响应，按服务端故障处理）账号:{account}");
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
            Log.Info($"收到帐户的RegisterRequest: {RedactAccount(account)}");

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

            // P3 修复：输入长度上限（超长账号/密码放大 PBKDF2 哈希与存储成本，匿名可达）。
            if (account.Length > 64 || request.Password.Length > 128 || request.Nickname.Length > 32)
            {
                Log.Warning($"注册失败：账号/密码/昵称超长，账号长度:{account.Length} 密码长度:{request.Password.Length} 昵称长度:{request.Nickname.Length}");
                return new RegisterResponse
                {
                    Success = false,
                    Message = "账号/密码/昵称格式不正确"
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
                    if (!UIDGenerator.IsInitialized)
                    {
                        return new RegisterResponse
                        {
                            Success = false,
                            Message = "服务器正在初始化UID，请稍后重试"
                        };
                    }
                    // 预留段耗尽 / 序列越界：重新向 DB 申请发号段后重试（防多实例发号碰撞）
                    await SyncUidGeneratorFromDbAsync();
                    continue;
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
                    // 与登录路径对齐：DB 无响应属服务端故障，不计入失败次数（防 DB 抖动批量锁号自伤放大）
                    Log.Error($"注册失败：DB 响应为空，账号:{account}, Attempt:{attempt + 1}");
                    return new RegisterResponse
                    {
                        Success = false,
                        Message = "注册失败，请稍后重试"
                    };
                }

                if (verifyResp.Success)
                {
                    ClearFailedAttempts("register", account);
                    return new RegisterResponse
                    {
                        Success = true,
                        Message = "注册成功"
                    };
                }

                string message = verifyResp.Message;
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
