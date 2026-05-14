using System;
using System.Threading.Tasks;
using Network.Tcp;
using Shared;
using Shared.Messages.Login;
using MailKit.Net.Smtp;
using MimeKit;

namespace Login.Handlers
{
    /// <summary>
    /// 处理登录模块的业务逻辑。封装了对 DB 服务的请求调用（通过 TcpClientWrapper）
    /// 并提供登录、注册、找回密码、查询账户和在线统计等功能的异步方法。
    /// </summary>
    public class LoginHandler
    {
        private readonly TcpClientWrapper dbClient;

        /// <summary>
        /// 创建 LoginHandler 的实例。
        /// </summary>
        /// <param name="dbClient">用于与 DB 服务通信的 TcpClient 封装器。</param>
        public LoginHandler(TcpClientWrapper dbClient)
        {
            this.dbClient = dbClient;
        }

        /// <summary>
        /// 异步处理登录请求：向 DB 服务发送验证请求并返回登录响应。
        /// 若验证成功，会在响应中生成临时 Token（仅示例用途）。
        /// </summary>
        /// <param name="request">包含账号和密码的登录请求对象。</param>
        /// <returns>包含登录结果、提示信息、用户 Id 及临时 Token 的 LoginResponse。</returns>
        public async Task<LoginResponse> HandleLoginRequestAsync(LoginRequest request)
        {
            Log.Info($"收到帐户的LoginRequest: {request.Account}");

            var verifyReq = new Shared.Messages.Db.LoginVerifyRequest
            {
                Account = request.Account,
                Password = request.Password
            };

            var verifyResp = await CallDbAsync<Shared.Messages.Db.LoginVerifyResponse>(1001, verifyReq);

            var response = new LoginResponse
            {
                Success = verifyResp?.Success ?? false,
                Message = verifyResp?.Message ?? "服务器内部错误",
                UserId = (int)(verifyResp?.UserId ?? 0),
                Token = verifyResp?.Success == true ? Guid.NewGuid().ToString() : string.Empty
            };
            return response;
        }

        /// <summary>
        /// 同步处理登录请求的占位方法（向后兼容）。当前仅记录日志。
        /// </summary>
        /// <param name="session">TCP 会话对象（未使用）。</param>
        /// <param name="request">登录请求数据。</param>
        public void HandleLoginRequest(TcpSession session, LoginRequest request)
        {
            // Backward compatibility placeholder
            Log.Info($"收到帐户的LoginRequest: {request.Account}");
        }

        /// <summary>
        /// 异步处理注册请求：向 DB 服务请求创建新用户并返回结果。
        /// </summary>
        /// <param name="request">包含账号、密码、昵称等注册信息的请求对象。</param>
        /// <returns>RegisterResponse，指示注册是否成功及提示信息。</returns>
        public async Task<RegisterResponse> HandleRegisterRequestAsync(RegisterRequest request)
        {
            Log.Info($"收到帐户的RegisterRequest: {request.Account}");

            long uniqueId = UIDGenerator.GenerateLongUID();

            var verifyReq = new Shared.Messages.Db.RegisterVerifyRequest
            {
                Account = request.Account,
                Password = request.Password,
                Nickname = request.Nickname,
                Uid = uniqueId
            };

            var verifyResp = await CallDbAsync<Shared.Messages.Db.RegisterVerifyResponse>(1002, verifyReq);

            if (verifyResp?.Success != true)
            {
                return new RegisterResponse
                {
                    Success = false,
                    Message = verifyResp?.Message ?? "注册失败"
                };
            }

            return new RegisterResponse
            {
                Success = true,
                Message = "注册成功"
            };
        }

        /// <summary>
        /// 处理注册请求。此方法将调用数据库创建新帐户。
        /// </summary>
        /// <param name="session"></param>
        /// <param name="request"></param>
        /// <summary>
        /// 同步处理注册请求的占位方法（向后兼容）。当前仅记录日志。
        /// </summary>
        /// <param name="session">TCP 会话对象（未使用）。</param>
        /// <param name="request">注册请求数据。</param>
        public void HandleRegisterRequest(TcpSession session, RegisterRequest request)
        {
            Log.Info($"收到帐户的RegisterRequest: {request.Account}");
        }

        /// <summary>
        /// 同步处理更改密码请求的占位方法：当前只记录日志并返回成功响应（示例）。
        /// 实际应校验旧密码并在 DB 中更新为新密码。
        /// </summary>
        /// <param name="session">TCP 会话对象（未使用）。</param>
        /// <param name="request">包含账号、旧密码和新密码的请求对象。</param>
        public void HandleChangePasswordRequest(TcpSession session, ChangePasswordRequest request)
        {
            Log.Info($"收到帐户的ChangePasswordRequest: {request.Account}");
            var response = new ChangePasswordResponse
            {
                Success = true,
                Message = "更改密码成功"
            };
        }

        /// <summary>
        /// 同步处理更改昵称请求的占位方法：当前仅记录日志并返回成功响应。
        /// 备注：注释中提到可以使用 SMTP 发送通知邮件，此处未实现具体邮件逻辑。
        /// </summary>
        /// <param name="session">TCP 会话对象（未使用）。</param>
        /// <param name="request">包含用户 Id 和新昵称的请求对象。</param>
        public void HandleChangeNicknameRequest(TcpSession session, ChangeNicknameRequest request)
        {
            Log.Info($"收到用户的ChangeNicknameRequest: {request.UserId}");
            var response = new ChangeNicknameResponse
            {
                Success = true,
                Message = "更改昵称成功"
            };
        }

        /// <summary>
        /// 异步处理找回密码请求（示例实现）：通常应通过 SMTP 给用户发送包含重置链接或验证码的邮件。
        /// 当前方法返回一个成功的占位响应，实际发送逻辑可通过 SendEmailAsync 实现并根据结果返回成功或失败状态。
        /// </summary>
        /// <param name="request">找回密码请求对象，包含用于定位用户的邮箱或账号信息。</param>
        /// <returns>FindPasswordResponse，指示是否成功触发邮箱发送流程。</returns>
        public async Task<FindPasswordResponse> HandleFindPasswordRequestAsync(FindPasswordRequest request)
        {
            var response = new FindPasswordResponse
            {
                Success = true,
                Message = "找回密码验证发件请求完毕"
            };
            return await Task.FromResult(response);
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

            var verifyResp = await CallDbAsync<Shared.Messages.Db.AccountQueryResponse>(1003, verifyReq);

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
            var verifyResp = await CallDbAsync<Shared.Messages.Db.OnlineStatsResponse>(1004, verifyReq);

            var response = new OnlineStatsResponse
            {
                OnlineCount = verifyResp?.OnlineCount ?? 0,
                OfflineCount = verifyResp?.OfflineCount ?? 0,
                TotalCount = verifyResp?.TotalCount ?? 0
            };

            return response;
        }

        /// <summary>
        /// 异步处理玩家主动登出请求
        /// </summary>
        public async Task<LogoutResponse> HandleLogoutRequestAsync(LogoutRequest request)
        {
            Log.Info($"收到用户的离开请求 userId: {request.UserId}");
            Managers.SessionManager.Instance.ForceLogout(request.UserId);

            return new LogoutResponse { Success = true, Message = "登出成功" };
        }

        /// <summary>
        /// 异步通知 DB 服玩家已下线
        /// </summary>
        public async Task HandleOfflineAsync(int userId)
        {
            Log.Info($"通知 DB 服务用户 {userId} 已下线");
            var req = new Shared.Messages.Db.UpdateOnlineStateRequest
            {
                UserId = userId,
                IsOnline = false
            };
            await CallDbAsync<Shared.Messages.Db.UpdateOnlineStateResponse>(1005, req);
        }

        /// <summary>
        /// 向 DB 服务发送请求并等待响应的通用方法。
        /// 方法将请求序列化为 JSON，并在包头写入消息 ID；通过 dbClient 发送后，监听回包并在
        /// 收到匹配 msgId 的响应时反序列化为目标类型 T 并返回。
        /// </summary>
        /// <typeparam name="T">期望从 DB 返回的响应类型。</typeparam>
        /// <param name="msgId">用于标识请求/响应类型的消息 ID。</param>
        /// <param name="requestData">要发送到 DB 的请求对象（将被序列化）。</param>
        /// <returns>反序列化后的响应对象，或在超时/异常时返回 null。</returns>
        private async Task<T> CallDbAsync<T>(int msgId, object requestData) where T : class
        {
            var tcs = new TaskCompletionSource<byte[]>();
            byte[] data = Shared.Json.SerializeToUtf8Bytes(requestData);
            
            // Generate sequence/request Id
            long requestId = UIDGenerator.GenerateLongUID();
            LoginServerApp.PendingRequests[requestId] = tcs;

            byte[] packet = new byte[data.Length + 12];
            // [MsgId(4)] + [RequestId(8)] + [Data]
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), msgId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(4, 8), requestId);
            data.CopyTo(packet.AsSpan(12));

            dbClient.Send(packet);

            var timeoutTask = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
            {
                LoginServerApp.PendingRequests.TryRemove(requestId, out _);
                Log.Warning($"向 DB 请求 MsgId:{msgId} 超时");
                return null;
            }

            var responseData = await tcs.Task;
            if (responseData == null) return null;

            try
            {
                return Shared.Json.DeserializeFromUtf8Bytes<T>(responseData);
            }
            catch (Exception ex)
            {
                Log.Error($"反序列化响应异常: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 使用配置中的 SMTP 设置发送电子邮件。
        /// 在演示/开发环境中，方法会接受所有 SSL 证书。生产环境应移除不安全的证书回调。
        /// </summary>
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
                    // 出于演示目的，接受所有SSL证书（如果可能，在生产中删除）
                    client.ServerCertificateValidationCallback = (s, c, h, e) => true;

                    await client.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.Auto);
                    await client.AuthenticateAsync(smtpUser, smtpPass);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"发送邮件失败: {ex.Message}");
                return false;
            }
        }
    }
}