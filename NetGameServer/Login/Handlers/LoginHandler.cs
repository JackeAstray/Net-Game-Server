using System;
using System.Threading.Tasks;
using Network.Tcp;
using Shared;
using Shared.Messages.Login;
using MailKit.Net.Smtp;
using MimeKit;

namespace Login.Handlers
{
    public class LoginHandler
    {
        private readonly TcpClientWrapper dbClient;

        public LoginHandler(TcpClientWrapper dbClient)
        {
            this.dbClient = dbClient;
        }

        /// <summary>
        /// 处理登录请求。此方法将调用数据库验证帐户和密码。
        /// </summary>
        /// <param name="session"></param>
        /// <param name="request"></param>
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

        public void HandleLoginRequest(TcpSession session, LoginRequest request)
        {
            // Backward compatibility placeholder
            Log.Info($"收到帐户的LoginRequest: {request.Account}");
        }

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
        public void HandleRegisterRequest(TcpSession session, RegisterRequest request)
        {
            Log.Info($"收到帐户的RegisterRequest: {request.Account}");
        }

        /// <summary>
        /// 处理更改密码请求。此方法将调用数据库验证旧密码并更新为新密码。
        /// </summary>
        /// <param name="session"></param>
        /// <param name="request"></param>
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
        /// 处理更改昵称请求。此方法将使用配置中的SMTP设置发送电子邮件。
        /// 如果电子邮件发送成功，则返回一个成功的响应，否则返回一个失败的响应。
        /// </summary>
        /// <param name="session"></param>
        /// </summary>
        /// <param name="session"></param>
        /// <param name="request"></param>
        public void HandleChangeNicknameRequest(TcpSession session, ChangeNicknameRequest request)
        {
            Log.Info($"收到用户的ChangeNicknameRequest: {request.UserId}");
            var response = new ChangeNicknameResponse
            {
                Success = true,
                Message = "更改昵称成功"
            };
        }

        private async Task<T> CallDbAsync<T>(int msgId, object request) where T : class
        {
            var tcs = new TaskCompletionSource<T>();
            byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
            byte[] packet = new byte[data.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), msgId);
            data.CopyTo(packet.AsSpan(4));

            Network.DataReceivedHandler onDataReceived = null;
            onDataReceived = (Network.ISession session, ReadOnlyMemory<byte> responseData) =>
            {
                if (responseData.Length >= 4)
                {
                    int resMsgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(responseData.Span.Slice(0, 4));
                    if (resMsgId == msgId)
                    {
                        dbClient.OnDataReceived -= onDataReceived;
                        try
                        {
                            var result = System.Text.Json.JsonSerializer.Deserialize<T>(responseData.Span.Slice(4));
                            tcs.TrySetResult(result);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"反序列化响应异常: {ex}");
                            tcs.TrySetResult(null);
                        }
                    }
                }
            };

            dbClient.OnDataReceived += onDataReceived;
            dbClient.Send(packet);

            var timeoutTask = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
            {
                dbClient.OnDataReceived -= onDataReceived;
                Log.Warning($"向 DB 请求 MsgId:{msgId} 超时");
                return null;
            }

            return await tcs.Task;
        }

        /// <summary>
        /// 处理找回密码请求。此方法将使用配置中的SMTP设置发送电子邮件。
        /// 如果电子邮件发送成功，则返回一个成功的响应，否则返回一个失败的响应。
        /// </summary>
        /// <param name="session"></param>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task HandleFindPasswordRequestAsync(TcpSession session, FindPasswordRequest request)
        {
            Log.Info($"收到帐户的FindPasswordRequest: {request.Account}, 电子邮件: {request.Email}");

            // Generate a 6-digit random code
            string resetCode = new Random().Next(100000, 999999).ToString();

            // TODO: Save the generated reset code to Cache/Redis or Database along with request.Account and expiry time
            // e.g. CacheManager.Set($"PasswordReset_Code_{request.Account}", resetCode, TimeSpan.FromMinutes(10));

            bool isSuccess = await SendEmailAsync(request.Email, "重置密码", $"您好，\n\n您的密码重置验证码为: {resetCode}\n\n该验证码将在 10 分钟后失效，请勿将验证码泄露给他人。");

            var response = new FindPasswordResponse
            {
                Success = isSuccess,
                Message = isSuccess ? "邮件发送成功" : "邮件发送失败"
            };
            // session.SendAsync(...)
        }

        /// <summary>
        /// 使用配置中的SMTP设置发送电子邮件。如果电子邮件发送成功，则返回true，否则返回false。
        /// </summary>
        /// <param name="toEmail"></param>
        /// <param name="subject"></param>
        /// <param name="body"></param>
        /// <returns></returns>
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