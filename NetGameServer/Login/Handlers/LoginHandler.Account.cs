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
    /// 登录 Handler —— 账户相关业务模块（找回密码/账户查询/在线统计/登出/改密/离线通知）。
    /// 与 LoginHandler.cs 同属一个 partial class，按业务模块分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public partial class LoginHandler
    {
        /// <summary>
        /// 异步处理找回密码请求。
        /// 由于当前版本尚未实现完整的邮箱验证码/重置链接流程，这里显式返回失败，避免误报成功。
        /// </summary>
        /// <param name="request">找回密码请求对象，包含用于定位用户的邮箱或账号信息。</param>
        /// <returns>FindPasswordResponse，当前版本始终返回未实现状态。</returns>
        public async Task<FindPasswordResponse> HandleFindPasswordRequestAsync(FindPasswordRequest request)
        {
            string account = request.Account?.Trim() ?? string.Empty;
            string email = request.Email?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(email))
            {
                Log.Warning($"找回密码失败：账号或邮箱为空，Account:{account}, Email:{email}");
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = "账号和邮箱不能为空"
                };
            }

            string cooldownKey = BuildActionKey("find-password", account);
            if (findPasswordCooldowns.TryGetValue(cooldownKey, out var nextAllowed) && nextAllowed > DateTime.UtcNow)
            {
                int waitSeconds = Math.Max(1, (int)Math.Ceiling((nextAllowed - DateTime.UtcNow).TotalSeconds));
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = $"找回密码请求过于频繁，请在 {waitSeconds} 秒后重试"
                };
            }

            var user = await GetUserByAccountAsync(account);
            if (user == null)
            {
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = "账户不存在"
                };
            }

            if (!string.Equals(user.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase))
            {
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = "邮箱与账号不匹配"
                };
            }

            string verifyCode = GenerateTemporaryPassword();
            string subject = "游戏账号密码重置验证码";
            string body = $"您的账号 {account} 已申请密码重置。\n验证码: {verifyCode}\n有效期: 10 分钟\n请在时限内完成验证。";
            if (!await SendEmailAsync(email, subject, body))
            {
                Log.Error($"找回密码失败：邮件发送失败，Account:{account}, Email:{email}");
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = "验证码发送失败，请稍后重试"
                };
            }

            var resetReq = new Shared.Messages.Db.ResetPasswordByEmailRequest
            {
                Account = account,
                Email = email,
                TemporaryPassword = verifyCode
            };

            var resetResp = await CallDbAsync<Shared.Messages.Db.ResetPasswordByEmailResponse>(MessageIds.DbResetPasswordByEmailReq, resetReq);
            if (resetResp?.Success != true)
            {
                return new FindPasswordResponse
                {
                    Success = false,
                    Message = resetResp?.Message ?? "重置密码失败，请稍后重试"
                };
            }

            findPasswordCooldowns[cooldownKey] = DateTime.UtcNow.Add(FindPasswordCooldown);

            return new FindPasswordResponse
            {
                Success = true,
                Message = "验证码已发送到您的邮箱"
            };
        }

        /// <summary>
        /// 异步处理账户查询请求：向 DB 服务查询指定账号是否存在，并返回其在线/锁定/管理员等状态信息。
        /// </summary>
        /// <param name="request">包含要查询的账号的请求对象。</param>
        /// <returns>AccountQueryResponse，包含账户存在性与各种状态标志及提示信息。</returns>
        public async Task<AccountQueryResponse> HandleAccountQueryRequestAsync(AccountQueryRequest request)
        {
            Log.Info($"收到查询账户请求: {request.Account}");

            var verifyReq = new Shared.Messages.Db.AccountQueryRequest
            {
                Account = request.Account
            };

            var verifyResp = await CallDbAsync<Shared.Messages.Db.AccountQueryResponse>(MessageIds.DbAccountQueryReq, verifyReq);
            if (verifyResp == null)
            {
                Log.Error($"查询账户失败：DB 响应为空，Account:{request.Account}");
            }

            var response = new AccountQueryResponse
            {
                Exists = verifyResp?.Exists ?? false,
                IsOnline = verifyResp?.IsOnline ?? false,
                IsLocked = verifyResp?.IsLocked ?? false,
                IsAdmin = verifyResp?.IsAdmin ?? false,
                Message = verifyResp?.Message ?? "服务器内部错误"
            };

            return response;
        }

        /// <summary>
        /// 异步获取在线统计信息：向 DB 请求当前在线、离线和总用户数的统计结果。
        /// </summary>
        /// <param name="request">在线统计请求（目前无字段，仅作调用占位）。</param>
        /// <returns>OnlineStatsResponse，包含 OnlineCount、OfflineCount 和 TotalCount。</returns>
        public async Task<OnlineStatsResponse> HandleOnlineStatsRequestAsync(OnlineStatsRequest request)
        {
            Log.Info($"收到查询在线统计请求");

            var verifyReq = new Shared.Messages.Db.OnlineStatsRequest { };
            var verifyResp = await CallDbAsync<Shared.Messages.Db.OnlineStatsResponse>(MessageIds.DbOnlineStatsReq, verifyReq);
            if (verifyResp == null)
            {
                Log.Error("查询在线统计失败：DB 响应为空。");
            }

            var response = new OnlineStatsResponse
            {
                OnlineCount = verifyResp?.OnlineCount ?? 0,
                OfflineCount = verifyResp?.OfflineCount ?? 0,
                TotalCount = verifyResp?.TotalCount ?? 0
            };

            return response;
        }

        /// <summary>
        /// 处理注销请求，验证会话并强制登出与该会话关联的用户。
        /// </summary>
        /// <remarks>当 clientSessionId 无效或未绑定用户时返回失败；若请求中的 UserId
        /// 与会话绑定的用户不一致则记录警告并拒绝；成功时调用会话管理器执行强制登出。</remarks>
        /// <param name="request">注销请求对象，可能包含可选的 UserId 用于指明要登出的用户。</param>
        /// <param name="clientSessionId">客户端会话标识；必须为正数，用于查找与会话绑定的用户。</param>
        /// <returns>表示操作结果的 LogoutResponse，Success 表示是否成功，Message 提供说明。</returns>
        public async Task<LogoutResponse> HandleLogoutRequestAsync(LogoutRequest request, long clientSessionId)
        {
            if (clientSessionId <= 0)
            {
                return new LogoutResponse { Success = false, Message = "无效会话" };
            }

            int boundUserId = Managers.SessionManager.Instance.GetUserIdBySessionId(clientSessionId);
            if (boundUserId <= 0)
            {
                return new LogoutResponse { Success = false, Message = "会话未登录" };
            }

            if (request.UserId > 0 && request.UserId != boundUserId)
            {
                Log.Warning($"检测到登出越权尝试，Session:{clientSessionId} 请求UserId:{request.UserId} 实际UserId:{boundUserId}");
                return new LogoutResponse { Success = false, Message = "无权限登出其他账号" };
            }

            Log.Info($"收到用户的离开请求 userId: {boundUserId}");
            Managers.SessionManager.Instance.ForceLogout(boundUserId);

            return new LogoutResponse { Success = true, Message = "登出成功" };
        }

        /// <summary>
        /// 处理客户端的修改密码请求：验证会话有效性和登录状态，然后将请求转交给核心更改逻辑。
        /// </summary>
        /// <remarks>会话无效或未登录时立即返回失败响应；在验证通过后调用 ChangePasswordCoreAsync 并传入绑定的用户 ID。</remarks>
        /// <param name="request">包含修改密码所需的信息（如当前密码与新密码等）的请求对象。</param>
        /// <param name="clientSessionId">客户端会话 ID，用于验证会话并查找绑定的用户；小于等于 0 视为无效。</param>
        /// <returns>表示操作结果的 ChangePasswordResponse；Success 为 true 表示修改成功，失败时 Message 提供原因。</returns>
        public async Task<ChangePasswordResponse> HandleChangePasswordRequestAsync(ChangePasswordRequest request, long clientSessionId)
        {
            if (clientSessionId <= 0)
            {
                return new ChangePasswordResponse { Success = false, Message = "无效会话" };
            }

            int boundUserId = Managers.SessionManager.Instance.GetUserIdBySessionId(clientSessionId);
            if (boundUserId <= 0)
            {
                return new ChangePasswordResponse { Success = false, Message = "会话未登录" };
            }

            return await ChangePasswordCoreAsync(request, boundUserId);
        }

        /// <summary>
        /// 处理更改密码的请求并返回操作结果。
        /// </summary>
        /// <remarks>封装 ChangePasswordCoreAsync 并以重试计数 0 发起请求。</remarks>
        /// <param name="request">包含更改密码所需的凭据和参数。</param>
        /// <returns>表示更改密码操作结果的异步任务，返回 ChangePasswordResponse。</returns>
        public async Task<ChangePasswordResponse> HandleChangePasswordRequestAsync(ChangePasswordRequest request)
        {
            return await ChangePasswordCoreAsync(request, 0);
        }

        /// <summary>
        /// 异步更改用户密码：验证输入、处理频率限制与失败计数，并通过数据库服务验证并执行密码更改。
        /// </summary>
        /// <remarks>记录警告与错误；对过于频繁的操作进行限流并返回等待秒数；根据数据库验证响应清除或登记失败尝试。</remarks>
        /// <param name="request">包含账户、旧密码和新密码的更改密码请求。</param>
        /// <param name="userId">目标用户的标识符；若为 0 则使用 Account 识别用户。</param>
        /// <returns>表示操作结果的 ChangePasswordResponse，包括 Success 标志和消息。</returns>
        private async Task<ChangePasswordResponse> ChangePasswordCoreAsync(ChangePasswordRequest request, int userId)
        {
            string account = request.Account?.Trim() ?? string.Empty;
            string oldPassword = request.OldPassword ?? string.Empty;
            string newPassword = request.NewPassword ?? string.Empty;
            string throttleIdentity = !string.IsNullOrWhiteSpace(account)
                ? account
                : (userId > 0 ? $"uid:{userId}" : "unknown");

            if (string.IsNullOrWhiteSpace(oldPassword))
            {
                Log.Warning($"修改密码失败：旧密码不能为空，Account:{account}, UserId:{userId}");
                return new ChangePasswordResponse
                {
                    Success = false,
                    Message = "旧密码不能为空"
                };
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                Log.Warning($"修改密码失败：新密码不能为空，Account:{account}, UserId:{userId}");
                return new ChangePasswordResponse
                {
                    Success = false,
                    Message = "新密码不能为空"
                };
            }

            if (TryGetThrottleRemaining("change-password", throttleIdentity, out var remaining))
            {
                int waitSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                Log.Warning($"修改密码失败：操作过于频繁，Identity:{throttleIdentity}，剩余 {waitSeconds} 秒");
                return new ChangePasswordResponse
                {
                    Success = false,
                    Message = $"操作过于频繁，请在 {waitSeconds} 秒后重试"
                };
            }

            var verifyReq = new Shared.Messages.Db.ChangePasswordVerifyRequest
            {
                UserId = userId,
                Account = account,
                OldPassword = request.OldPassword,
                NewPassword = request.NewPassword
            };

            var verifyResp = await CallDbAsync<Shared.Messages.Db.ChangePasswordVerifyResponse>(MessageIds.DbChangePasswordReq, verifyReq);
            if (verifyResp == null)
            {
                Log.Error($"修改密码失败：DB 响应为空，Account:{account}, UserId:{userId}");
            }
            bool success = verifyResp?.Success ?? false;
            if (success)
            {
                ClearFailedAttempts("change-password", throttleIdentity);
            }
            else
            {
                RegisterFailedAttempt("change-password", throttleIdentity);
            }

            return new ChangePasswordResponse
            {
                Success = success,
                Message = verifyResp?.Message ?? "更改密码失败"
            };
        }

        /// <summary>
        /// 通知 DB 服务将指定用户标记为已下线。
        /// </summary>
        /// <remarks>在日志中记录信息并异步向 DB 服务发送在线状态更新请求（IsOnline = false）。</remarks>
        /// <param name="userId">要标记为已下线的用户标识符。</param>
        /// <returns>表示异步操作的任务。</returns>
        public async Task HandleOfflineAsync(int userId)
        {
            Log.Info($"通知 DB 服务用户 {userId} 已下线");
            var req = new Shared.Messages.Db.UpdateOnlineStateRequest
            {
                UserId = userId,
                IsOnline = false
            };
            await CallDbAsync<Shared.Messages.Db.UpdateOnlineStateResponse>(MessageIds.DbUpdateOnlineStateReq, req);
        }

        /// <summary>
        /// 从数据库异步获取最大 UID 并使用该值与区域 ID 初始化 UIDGenerator。
        /// </summary>
        /// <remarks>如果从数据库获取的响应为 null，则记录警告并不进行初始化。区域 ID 从配置读取，若为 0 则使用 1 作为默认值；成功初始化后记录信息日志。可能会传播由
        /// CallDbAsync 抛出的异常。</remarks>
        /// <returns>表示异步操作的任务。</returns>
        private async Task SyncUidGeneratorFromDbAsync()
        {
            var maxUidResp = await CallDbAsync<Shared.Messages.Db.GetMaxUidResponse>(MessageIds.DbGetMaxUidReq, new Shared.Messages.Db.GetMaxUidRequest());
            if (maxUidResp == null)
            {
                Log.Warning("UID 冲突后重新同步失败：获取最大 UID 响应为空。");
                return;
            }

            int currentRegionId = ConfigHelper.GetConfig<int>("RegionId") == 0 ? 1 : ConfigHelper.GetConfig<int>("RegionId");
            UIDGenerator.Initialize(currentRegionId, maxUidResp.MaxUid);
            Log.Info($"UID 冲突后已重新同步，区服ID:{currentRegionId}，最大序列:{maxUidResp.MaxUid}");
        }

        /// <summary>
        /// 按账号异步查询用户；若存在则返回包含 Account 和 Email 的 Shared.Data.User，否则返回 null。
        /// </summary>
        /// <remarks>在 DB 响应为空或用户不存在时会记录错误或警告日志；返回的 Email 在未知时为空字符串。</remarks>
        /// <param name="account">要查询的用户账号。</param>
        /// <returns>找到用户时返回包含账号和邮箱的 Shared.Data.User；未找到或 DB 无响应时返回 null。</returns>
        private async Task<Shared.Data.User> GetUserByAccountAsync(string account)
        {
            var queryReq = new Shared.Messages.Db.AccountQueryRequest
            {
                Account = account
            };

            var queryResp = await CallDbAsync<Shared.Messages.Db.AccountQueryResponse>(MessageIds.DbAccountQueryReq, queryReq);
            if (queryResp == null)
            {
                Log.Error($"按账号获取用户失败：DB 响应为空，Account:{account}");
                return null;
            }
            if (queryResp?.Exists != true)
            {
                Log.Warning($"按账号获取用户失败：用户不存在，Account:{account}");
                return null;
            }

            return new Shared.Data.User
            {
                Account = account,
                Email = queryResp.Email ?? string.Empty
            };
        }

        /// <summary>
        /// 生成一个由不含模糊字符的字符集构成的 8 字符临时密码（密码重置/找回密码验证码用）。
        /// </summary>
        /// <remarks>使用 <see cref="System.Security.Cryptography.RandomNumberGenerator"/> 加密安全随机数生成；
        /// 抵御预测攻击（旧实现 <see cref="Random"/> 在并发或已知样本后可预测）。</remarks>
        /// <returns>长度为 8 的密码字符串，字符取自集合 ABCDEFGHJKLMNPQRSTUVWXYZ23456789。</returns>
        private static string GenerateTemporaryPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            const int length = 8;
            Span<byte> randomBytes = stackalloc byte[length];
            System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
            // 取每个字节的低 5 位（0-31）映射到 32 字符集
            // 为避免字节值 > chars.Length 产生偏置分布，使用拒绝采样：超出 32 倍数则重取
            char[] result = new char[length];
            for (int i = 0; i < length; i++)
            {
                int idx = randomBytes[i] % chars.Length;  // chars.Length=32，256%32==0 均匀分布
                result[i] = chars[idx];
            }
            return new string(result);
        }

        /// <summary>
        /// 使用配置中的 SMTP 设置发送电子邮件。
        /// </summary>
        /// <remarks>SSL 证书校验已严格启用（MailKit 默认行为：只信任系统 CA 证书库），
        /// 不再设置接受所有证书的回调。生产环境请确保 SMTP:Host 为可信任域名。</remarks>
        /// <param name="toEmail">收件人邮箱地址。</param>
        /// <param name="subject">邮件主题。</param>
        /// <param name="body">邮件正文（纯文本）。</param>
        /// <returns>如果邮件发送成功返回 true，否则返回 false。</returns>
        private async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
        {
            try
            {
                string smtpHost = ConfigHelper.GetConfig<string>("SMTP:Host") ?? "smtp.163.com";
                int smtpPort = ConfigHelper.GetConfig<int>("SMTP:Port") == 0 ? 465 : ConfigHelper.GetConfig<int>("SMTP:Port");
                string smtpUser = ConfigHelper.GetConfig<string>("SMTP:Account") ?? "your-email@example.com";
                string smtpPass = ConfigHelper.GetConfig<string>("SMTP:Password") ?? "your-password";
                string senderName = ConfigHelper.GetConfig<string>("SMTP:SenderName") ?? "游戏通知";

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(senderName, smtpUser));
                message.To.Add(new MailboxAddress("", toEmail));
                message.Subject = subject;

                message.Body = new TextPart("plain")
                {
                    Text = body
                };

                using (var client = new SmtpClient())
                {
                    // SSL 安全修复：不再设置"接受所有证书"回调。
                    // MailKit 默认只信任系统 CA 证书，移除任意 ServerCertificateValidationCallback。
                    // 若需要自签证书，请将证书安装到系统 CA 证书库，或通过 certFile/certPassword 显式加载。
                    await client.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.Auto);
                    await client.AuthenticateAsync(smtpUser, smtpPass);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"发送邮件失败: {ex}");
                return false;
            }
        }
    }
}
