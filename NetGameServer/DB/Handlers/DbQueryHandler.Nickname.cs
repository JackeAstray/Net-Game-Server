using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Shared;
using Shared.Messages.Db;

namespace DB.Handlers
{
    /// <summary>
    /// DB 查询 Handler —— 昵称模块（修改昵称落库）。
    /// 与 <c>DbQueryHandler</c> 同属一个 partial class，按业务模块分文件组织。
    ///
    /// 背景：客户端 `UpdateNicknameReq(10009)` 此前在 Login 侧是**无操作的假成功**（返回"更改昵称成功"
    /// 但从不落库，重启/换端后昵称回退）。本次补齐 DB 侧落库链路：
    ///   Login.HandleUpdateNicknameRequestAsync（会话/Tokenn 归属校验）
    ///     → MessageIds.DbUpdateNicknameReq(1029) → 本处理器写 User.Nickname
    ///     → MessageIds.DbUpdateNicknameRes(1129) → 回 Login → 回客户端。
    /// `User.Nickname` 列已存在（见 Shared.Data.User），故**无需 EF 迁移**。
    /// 昵称不建唯一索引（与注册路径一致，允许重名），因此不做唯一性校验。
    /// </summary>
    public partial class DbQueryHandler
    {
        /// <summary>昵称长度上限（与 Login 侧校验保持一致）。</summary>
        internal const int MaxNicknameLength = 32;

        /// <summary>
        /// 处理昵称修改请求：按用户 ID 定位账户并写入新昵称，返回落库后的最终昵称。
        /// 并发约束：按用户维度经 <see cref="RunPerUser"/> 串行，避免与同用户的其它读改写相互覆盖。
        /// </summary>
        /// <param name="session">用于发送响应的会话（带 RequestId 路由上下文）。</param>
        /// <param name="request">昵称修改请求；为 null 时记录告警并返回。</param>
        public static async Task HandleUpdateNicknameRequest(ISession session, DbUpdateNicknameRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 DbUpdateNicknameRequest，数据无法被反序列化。");
                return;
            }

            // 先在 DB 之外做一次防御性校验：即使上游被绕过，也不把空白/超长/含控制字符的昵称写库。
            string newNickname = (request.NewNickname ?? string.Empty).Trim();
            if (request.UserId <= 0 || !IsValidNickname(newNickname))
            {
                Log.Warning($"修改昵称失败，参数无效 UserId:{request.UserId} NicknameLength:{newNickname.Length}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbUpdateNicknameRes, "昵称无效（1-32 个字符，且不能包含换行等控制字符）");
                return;
            }

            await RunPerUser(UserKey(request.UserId), async () =>
            {
                try
                {
                    var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                    using var scope = factory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
                    if (user == null)
                    {
                        Log.Warning($"修改昵称失败，用户不存在 UserId:{request.UserId}");
                        SendFailureResponse(session, Shared.Messages.MessageIds.DbUpdateNicknameRes, "用户不存在");
                        return;
                    }

                    // 幂等：昵称未变化时直接成功返回，避免无谓写库/触发变更通知
                    if (string.Equals(user.Nickname, newNickname, StringComparison.Ordinal))
                    {
                        SendSuccess(session, newNickname);
                        return;
                    }

                    user.Nickname = newNickname;
                    await dbContext.SaveChangesAsync();

                    Log.Info($"修改昵称成功 UserId:{user.Id} NicknameLength:{newNickname.Length}");
                    SendSuccess(session, newNickname);
                }
                catch (Exception ex)
                {
                    Log.Error($"修改昵称异常 UserId:{request.UserId}: {ex}");
                    SendFailureResponse(session, Shared.Messages.MessageIds.DbUpdateNicknameRes, "修改昵称失败，服务器内部错误");
                }
            });

            static void SendSuccess(ISession session, string nickname)
            {
                var response = new DbUpdateNicknameResponse { Success = true, Message = "更改昵称成功", Nickname = nickname };
                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(
                    Shared.Messages.MessageIds.DbUpdateNicknameRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
        }

        /// <summary>
        /// 昵称合法性（与 Login 侧同规则）：去空白后 1..32 字符，且不含换行/制表等控制字符
        /// （防日志注入与客户端渲染异常）。
        /// </summary>
        internal static bool IsValidNickname(string nickname)
        {
            if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > MaxNicknameLength)
            {
                return false;
            }
            foreach (char c in nickname)
            {
                if (char.IsControl(c))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
