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
        public void HandleLoginRequest(TcpSession session, LoginRequest request)
        {
            Log.Info($"Received LoginRequest for account: {request.Account}");
            // TODO: Call DB to verify
            // For now, return success
            var response = new LoginResponse
            {
                Success = true,
                Message = "Login successful",
                UserId = 1,
                Token = Guid.NewGuid().ToString()
            };
            // session.SendAsync(...)
        }

        public void HandleRegisterRequest(TcpSession session, RegisterRequest request)
        {
            Log.Info($"Received RegisterRequest for account: {request.Account}");
            var response = new RegisterResponse
            {
                Success = true,
                Message = "Register successful"
            };
            // session.SendAsync(...)
        }

        public void HandleChangePasswordRequest(TcpSession session, ChangePasswordRequest request)
        {
            Log.Info($"Received ChangePasswordRequest for account: {request.Account}");
            var response = new ChangePasswordResponse
            {
                Success = true,
                Message = "Change password successful"
            };
        }

        public void HandleChangeNicknameRequest(TcpSession session, ChangeNicknameRequest request)
        {
            Log.Info($"Received ChangeNicknameRequest for UserId: {request.UserId}");
            var response = new ChangeNicknameResponse
            {
                Success = true,
                Message = "Change nickname successful"
            };
        }

        public async Task HandleFindPasswordRequestAsync(TcpSession session, FindPasswordRequest request)
        {
            Log.Info($"Received FindPasswordRequest for account: {request.Account}, email: {request.Email}");

            bool isSuccess = await SendEmailAsync(request.Email, "Reset Password", "Your password reset code is: 123456");

            var response = new FindPasswordResponse
            {
                Success = isSuccess,
                Message = isSuccess ? "Email sent successfully" : "Failed to send email"
            };
            // session.SendAsync(...)
        }

        private async Task<bool> SendEmailAsync(string toEmail, string subject, string body)
        {
            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Game Server", "your-email@example.com"));
                message.To.Add(new MailboxAddress("", toEmail));
                message.Subject = subject;

                message.Body = new TextPart("plain")
                {
                    Text = body
                };

                using (var client = new SmtpClient())
                {
                    // For demo-purposes, accept all SSL certificates
                    client.ServerCertificateValidationCallback = (s, c, h, e) => true;

                    string smtpHost = ConfigHelper.GetConfig<string>("SmtpHost") ?? "smtp.example.com";
                    int smtpPort = ConfigHelper.GetConfig<int>("SmtpPort") == 0 ? 587 : ConfigHelper.GetConfig<int>("SmtpPort");
                    string smtpUser = ConfigHelper.GetConfig<string>("SmtpUser") ?? "your-email@example.com";
                    string smtpPass = ConfigHelper.GetConfig<string>("SmtpPass") ?? "your-password";

                    await client.ConnectAsync(smtpHost, smtpPort, false);
                    await client.AuthenticateAsync(smtpUser, smtpPass);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to send email: {ex.Message}");
                return false;
            }
        }
    }
}
