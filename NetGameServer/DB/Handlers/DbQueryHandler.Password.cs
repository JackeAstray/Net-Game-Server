using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Shared.Messages.Db;
using Shared;
using DB;
using Shared.Data.Social;
namespace DB.Handlers
{
    /// <summary>
    /// DB 查询 Handler —— 密码模块（修改密码验证/邮箱重置密码）。
    /// 与 DbQueryHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// </summary>
    public partial class DbQueryHandler
    {
        /// <summary>
        /// 验证并处理更改密码请求：校验请求与用户信息、验证旧密码的 PBKDF2 哈希，成功时更新并持久化新密码，然后向会话发送结果响应。
        /// </summary>
        /// <remarks>方法访问数据库查找用户、验证账号匹配与密码格式，使用 PBKDF2
        /// 验证旧密码并在通过时哈希并保存新密码；在任何失败或异常情况下记录相应日志并发送失败响应。</remarks>
        /// <param name="session">用于发送响应的会话连接。</param>
        /// <param name="request">包含要验证的用户标识、账号、旧密码与新密码；为 null 时记录警告并返回。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleChangePasswordVerifyRequest(ISession session, ChangePasswordVerifyRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ChangePasswordVerifyRequest，数据无法被反序列化。");
                return;
            }

            // 账号级串行（P1-2）：同一账号的改密读改写按序执行（与登录同 key）
            await RunPerUser(AccountKey(request.Account), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
                var response = new ChangePasswordVerifyResponse();

                if (user == null)
                {
                    user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);
                }

                if (user == null)
                {
                    response.Success = false;
                    response.Message = "用户不存在";
                    Log.Warning($"更改密码失败，用户不存在 UserId:{request.UserId} Account:{request.Account}");
                }
                else if (!string.Equals(user.Account, request.Account, StringComparison.Ordinal))
                {
                    response.Success = false;
                    response.Message = "账号不匹配";
                    Log.Warning($"更改密码失败，账号不匹配 UserId:{request.UserId} RequestAccount:{request.Account} StoredAccount:{user.Account}");
                }
                else
                {
                    if (!DB.DbServerApp.IsPbkdf2Hash(user.Password))
                    {
                        response.Success = false;
                        response.Message = "当前账号密码格式不受支持，请先由管理员重置为PBKDF2密码";
                        Log.Warning($"更改密码失败，密码格式不受支持 UserId:{request.UserId} Account:{request.Account}");
                    }
                    else
                    {
                        bool oldPasswordMatched = DB.DbServerApp.VerifyPbkdf2Password(request.OldPassword, user.Password);

                        if (!oldPasswordMatched)
                        {
                            response.Success = false;
                            response.Message = "旧密码错误";
                            Log.Warning($"更改密码失败，旧密码错误 UserId:{request.UserId} Account:{request.Account}");
                        }
                        else
                        {
                            user.Password = DB.DbServerApp.HashPassword(request.NewPassword);
                            await dbContext.SaveChangesAsync();
                            response.Success = true;
                            response.Message = "更改密码成功";
                        }
                    }
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbChangePasswordRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"更改密码异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbChangePasswordRes, "更改密码失败，服务器内部错误");
            }
            });
        }

        /// <summary>
        /// 处理通过邮箱的重置密码请求：校验请求和用户邮箱，匹配后将临时密码哈希写入数据库，发送响应并记录日志。
        /// </summary>
        /// <remarks>通过依赖注入获取 DbContext；在请求无效或验证失败时记录警告，发生异常时记录错误。</remarks>
        /// <param name="session">用于与客户端通信的会话实例，用以发送响应包。</param>
        /// <param name="request">包含账号、邮箱与临时密码的重置请求，可能为 null。</param>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task HandleResetPasswordByEmailRequest(ISession session, ResetPasswordByEmailRequest? request)
        {
            if (request == null)
            {
                Log.Warning("收到无效的 ResetPasswordByEmailRequest，数据无法被反序列化。");
                return;
            }

            // 账号级串行（P1-2）：同一账号的邮箱重置读改写按序执行（与登录/改密同 key）
            await RunPerUser(AccountKey(request.Account), async () =>
            {
            try
            {
                var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
                using var scope = factory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                var response = new ResetPasswordByEmailResponse();
                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Account == request.Account);
                if (user == null)
                {
                    response.Success = false;
                    response.Message = "用户不存在";
                    Log.Warning($"邮箱重置密码失败，用户不存在 Account:{request.Account} Email:{request.Email}");
                }
                else if (!string.Equals(user.Email?.Trim(), request.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    response.Success = false;
                    response.Message = "邮箱与账号不匹配";
                    Log.Warning($"邮箱重置密码失败，邮箱与账号不匹配 Account:{request.Account} RequestEmail:{request.Email} StoredEmail:{user.Email}");
                }
                else
                {
                    user.Password = DB.DbServerApp.HashPassword(request.TemporaryPassword);
                    await dbContext.SaveChangesAsync();
                    response.Success = true;
                    response.Message = "验证码校验通过，密码重置成功";
                }

                byte[] data = Shared.Json.SerializeToUtf8Bytes(response);
                // 帧长度修复（P1）：统一 BuildPacket 加长度头 + PacketSender 免启发式发送
                byte[] packet = Network.Routing.PacketBuilder.BuildPacket(Shared.Messages.MessageIds.DbResetPasswordByEmailRes, data, out int totalLength);
                Network.PacketSender.Send(session, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"邮箱重置密码异常: {ex}");
                SendFailureResponse(session, Shared.Messages.MessageIds.DbResetPasswordByEmailRes, "邮箱重置密码失败，服务器内部错误");
            }
            });
        }

        // --- Friend系统处理程序 ---

    }
}
