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
    public class LoginHandler
    {
        private readonly TcpClientWrapper dbClient;

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
        }

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
                Token = verifyResp?.Success == true ? Guid.NewGuid().ToString() : string.Empty,
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
        /// 异步处理玩家主动登出请求
        /// </summary>
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

        public async Task<ChangePasswordResponse> HandleChangePasswordRequestAsync(ChangePasswordRequest request)
        {
            return await ChangePasswordCoreAsync(request, 0);
        }

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
            await CallDbAsync<Shared.Messages.Db.UpdateOnlineStateResponse>(MessageIds.DbUpdateOnlineStateReq, req);
        }

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
            long requestId = System.Threading.Interlocked.Increment(ref sequenceId);
            LoginServerApp.PendingRequests[requestId] = tcs;

            byte[] payloadWithRequestId = Shared.RouteMetadata.AttachRequestId(data, requestId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, payloadWithRequestId, out int totalLength);
            dbClient.Send(packet.AsSpan(0, totalLength).ToArray());
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);

            int timeoutMs = ConfigHelper.GetConfig<int>("DbRequestTimeoutMs");
            if (timeoutMs <= 0)
            {
                timeoutMs = 5000;
            }

            using var cts = new System.Threading.CancellationTokenSource();
            var timeoutTask = Task.Delay(timeoutMs, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                LoginServerApp.PendingRequests.TryRemove(requestId, out _);
                Shared.Log.Warning($"向 DB 请求 MsgId:{msgId} 超时，TimeoutMs:{timeoutMs}, RequestId:{requestId}");
                return null;
            }

            cts.Cancel(); // 取消 Task.Delay 防止资源泄露
            var responseData = await tcs.Task;
            if (responseData == null)
            {
                Shared.Log.Error($"DB 回包为空，MsgId:{msgId}, RequestId:{requestId}");
                return null;
            }

            try
            {
                return Shared.Json.DeserializeFromUtf8Bytes<T>(responseData);
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"反序列化响应异常，MsgId:{msgId}, RequestId:{requestId}, Exception:{ex}");
                return null;
            }
        }

        private static bool TryGetThrottleRemaining(string action, string identity, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            string key = BuildActionKey(action, identity);
            if (!actionAttemptTrackers.TryGetValue(key, out var tracker))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (tracker.LockedUntilUtc > now)
            {
                remaining = tracker.LockedUntilUtc - now;
                return true;
            }

            if (tracker.FailedCount <= 0)
            {
                actionAttemptTrackers.TryRemove(key, out _);
            }

            return false;
        }

        private static void RegisterFailedAttempt(string action, string identity)
        {
            DateTime now = DateTime.UtcNow;
            string key = BuildActionKey(action, identity);
            actionAttemptTrackers.AddOrUpdate(
                key,
                _ => new ActionAttemptTracker { FailedCount = 1, LockedUntilUtc = DateTime.MinValue },
                (_, existing) =>
                {
                    if (existing.LockedUntilUtc > now)
                    {
                        return existing;
                    }

                    int failedCount = existing.FailedCount + 1;
                    if (failedCount >= MaxFailedAttempts)
                    {
                        Log.Warning($"{action}:{identity} 连续失败达到阈值，已锁定 {ThrottleLockDuration.TotalMinutes} 分钟");
                        return new ActionAttemptTracker
                        {
                            FailedCount = 0,
                            LockedUntilUtc = now.Add(ThrottleLockDuration)
                        };
                    }

                    return new ActionAttemptTracker
                    {
                        FailedCount = failedCount,
                        LockedUntilUtc = DateTime.MinValue
                    };
                });
        }

        private static void ClearFailedAttempts(string action, string identity)
        {
            string key = BuildActionKey(action, identity);
            actionAttemptTrackers.TryRemove(key, out _);
        }

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

        private static string GenerateTemporaryPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var random = new Random();
            char[] result = new char[8];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = chars[random.Next(chars.Length)];
            }
            return new string(result);
        }

        private static string BuildActionKey(string action, string identity)
        {
            string normalizedIdentity = (identity ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedIdentity))
            {
                normalizedIdentity = "unknown";
            }

            return $"{action}:{normalizedIdentity}";
        }

        private sealed class ActionAttemptTracker
        {
            public int FailedCount { get; set; }
            public DateTime LockedUntilUtc { get; set; }
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