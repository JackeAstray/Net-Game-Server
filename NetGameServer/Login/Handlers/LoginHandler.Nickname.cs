using System;
using System.Threading.Tasks;
using Shared;
using Shared.Messages;
using Shared.Messages.Db;
using Shared.Messages.Login;

namespace Login.Handlers
{
    /// <summary>
    /// 昵称模块（改昵称落库）。
    /// 与 <c>LoginHandler</c> 同属一个 partial class（拆分文件：LoginHandler.cs/.Account.cs/.Security.cs/.Nickname.cs）。
    ///
    /// 背景：`UpdateNicknameReq(10009)` 此前是无操作的**假成功**（HTTP/TCP 三条路径均返回"更改昵称成功"但从不落库，
    /// 重启或换端后昵称回退，用户以为已修改）。本文件补齐 Login 侧链路：
    ///   会话/Tokna 归属校验 → DB 落库（MessageIds.DbUpdateNicknameReq=1029）
    ///   → 回包 → ChangeNicknameResponse 回客户端。
    ///
    /// 安全要点（**归属校验是本模块的核心**）：绝不允许凭请求体里的 UserId 修改他人昵称。
    /// TCP 路径取会话绑定的 UserId；HTTP 路径取 Token 中的 UserId，并且要求请求体 UserId（若给出）与之一致。
    /// </summary>
    public partial class LoginHandler
    {
        /// <summary>昵称长度上限（与 DB 侧 DbQueryHandler.MaxNicknameLength 保持一致）。</summary>
        internal const int MaxNicknameLength = 32;

        /// <summary>
        /// TCP 入口：按当前会话绑定的用户修改昵称。
        /// </summary>
        /// <param name="request">含新昵称的请求；<c>UserId</c> 仅用于一致性校验，不会被用作操作目标。</param>
        /// <param name="clientSessionId">客户端会话 ID（由 Gateway 路由元数据提供）。</param>
        public async Task<ChangeNicknameResponse> HandleUpdateNicknameRequestAsync(ChangeNicknameRequest request, long clientSessionId)
        {
            if (clientSessionId <= 0)
            {
                return new ChangeNicknameResponse { Success = false, Message = "无效会话" };
            }

            int boundUserId = Managers.SessionManager.Instance.GetUserIdBySessionId(clientSessionId);
            if (boundUserId <= 0)
            {
                return new ChangeNicknameResponse { Success = false, Message = "会话未登录" };
            }

            // 越权防护：请求体声明了 UserId 且与会话绑定用户不一致 → 直接拒绝（不静默改用会话用户）
            if (request.UserId > 0 && request.UserId != boundUserId)
            {
                Log.Warning($"修改昵称越权被拒：SessionUserId:{boundUserId} RequestUserId:{request.UserId}");
                return new ChangeNicknameResponse { Success = false, Message = "无权限修改其他账户昵称" };
            }

            return await UpdateNicknameCoreAsync(request.NewNickname, boundUserId);
        }

        /// <summary>
        /// HTTP 入口：Token 必须有效，且只能修改 Token 持有人自己的昵称。
        /// </summary>
        public async Task<(bool Allowed, string? Reason, ChangeNicknameResponse? Response)> HandleUpdateNicknameWithTokenAsync(ChangeNicknameRequest request, string? token)
        {
            var verified = VerifyToken(token);
            if (verified == null)
            {
                return (false, "登录凭证无效或已过期，请重新登录", null);
            }

            if (request.UserId > 0 && request.UserId != verified.Value.UserId)
            {
                Log.Warning($"修改昵称越权被拒：TokenUserId:{verified.Value.UserId} 请求 UserId:{request.UserId}");
                return (false, "无权限修改其他账户昵称", null);
            }

            var response = await UpdateNicknameCoreAsync(request.NewNickname, verified.Value.UserId);
            return (true, null, response);
        }

        /// <summary>
        /// 昵称修改核心：校验新昵称 → 频率限制 → 调用 DB 落库 → 返回结果。
        /// </summary>
        /// <param name="newNickname">客户端提交的新昵称（服务端做 trim + 合法性校验）。</param>
        /// <param name="userId">目标用户（必须已由调用方完成归属校验）。</param>
        private async Task<ChangeNicknameResponse> UpdateNicknameCoreAsync(string? newNickname, int userId)
        {
            string nickname = (newNickname ?? string.Empty).Trim();

            if (userId <= 0)
            {
                return new ChangeNicknameResponse { Success = false, Message = "会话未登录" };
            }

            if (string.IsNullOrWhiteSpace(nickname))
            {
                return new ChangeNicknameResponse { Success = false, Message = "昵称不能为空" };
            }

            if (nickname.Length > MaxNicknameLength)
            {
                return new ChangeNicknameResponse { Success = false, Message = $"昵称长度不能超过 {MaxNicknameLength} 个字符" };
            }

            // 控制字符会破坏日志与客户端渲染（换行/制表等），一律拒绝（与 DB 侧 IsValidNickname 同规则）
            foreach (char c in nickname)
            {
                if (char.IsControl(c))
                {
                    return new ChangeNicknameResponse { Success = false, Message = "昵称不能包含换行等控制字符" };
                }
            }

            // 频率限制：昵称修改是低频操作（防刷），身份用 userId（TCP 与 HTTP 两条路径统一）
            string throttleIdentity = $"uid:{userId}";
            if (TryGetThrottleRemaining("update-nickname", throttleIdentity, out var remaining))
            {
                int waitSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                Log.Warning($"修改昵称失败：操作过于频繁，UserId:{userId}，剩余 {waitSeconds} 秒");
                return new ChangeNicknameResponse
                {
                    Success = false,
                    Message = $"操作过于频繁，请在 {waitSeconds} 秒后重试"
                };
            }

            var dbReq = new DbUpdateNicknameRequest
            {
                UserId = userId,
                NewNickname = nickname
            };

            var dbResp = await CallDbAsync<DbUpdateNicknameResponse>(MessageIds.DbUpdateNicknameReq, dbReq);
            if (dbResp == null)
            {
                Log.Error($"修改昵称失败：DB 响应为空，UserId:{userId}");
                return new ChangeNicknameResponse { Success = false, Message = "服务器内部错误，请稍后重试" };
            }

            if (dbResp.Success)
            {
                ClearFailedAttempts("update-nickname", throttleIdentity);
                Log.Info($"修改昵称成功 UserId:{userId} NicknameLength:{dbResp.Nickname.Length}");
            }
            else
            {
                // 仅业务性失败（如用户不存在/昵称非法）才计入失败次数，避免把服务端故障放大成锁号
                // （与登录路径一致：DB 无响应不计失败次数，见 HandleLoginRequestAsync）
                RegisterFailedAttempt("update-nickname", throttleIdentity);
                Log.Warning($"修改昵称失败 UserId:{userId} Reason:{dbResp.Message}");
            }

            return new ChangeNicknameResponse
            {
                Success = dbResp.Success,
                Message = string.IsNullOrWhiteSpace(dbResp.Message) ? "更改昵称失败" : dbResp.Message
            };
        }
    }
}
