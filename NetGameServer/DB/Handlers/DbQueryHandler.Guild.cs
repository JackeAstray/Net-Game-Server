using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Shared;
using Shared.Messages;
using Shared.Messages.Db;
using Shared.Data.Social;

namespace DB.Handlers
{
    /// <summary>
    /// DB 查询 Handler —— 公会模块（创建/查询/加入/退出/解散/踢人/转让/宣言）。
    /// 与 DbQueryHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// 并发约束：同一公会的读写经 RunPerUser(guildId) 串行化；名称唯一性由 DB 唯一索引兜底。
    /// </summary>
    public partial class DbQueryHandler
    {
        private const int MaxGuildNameLength = 24;
        private const int MaxGuildDeclLength = 128;

        private static async Task<IServiceScope> CreateScopeAsync()
        {
            var factory = Program.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            return factory.CreateScope();
        }

        public static async Task HandleGuildCreateRequest(ISession session, DbGuildCreateRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0 || string.IsNullOrWhiteSpace(request.Name))
            {
                SendFailureResponse(session, MessageIds.DbGuildCreateRes, "公会名称不能为空");
                return;
            }
            string name = request.Name.Trim();
            if (name.Length > MaxGuildNameLength)
            {
                SendFailureResponse(session, MessageIds.DbGuildCreateRes, $"公会名称过长（上限 {MaxGuildNameLength} 字）");
                return;
            }
            string declaration = (request.Declaration ?? string.Empty).Trim();
            if (declaration.Length > MaxGuildDeclLength)
            {
                SendFailureResponse(session, MessageIds.DbGuildCreateRes, $"公会宣言过长（上限 {MaxGuildDeclLength} 字）");
                return;
            }

            await RunPerUser("guild-create:" + name, async () =>
            {
                using var scope = await CreateScopeAsync();
                var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();
                try
                {
                    // 用户是否已加入公会
                    bool alreadyInGuild = await context.GuildMembers.AnyAsync(m => m.UserId == request.UserId);
                    if (alreadyInGuild)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildCreateRes, "你已加入公会，不能重复创建");
                        return;
                    }
                    bool nameTaken = await context.Guilds.AnyAsync(g => g.Name == name);
                    if (nameTaken)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildCreateRes, "公会名称已存在");
                        return;
                    }

                    var guild = new Guild
                    {
                        Name = name,
                        OwnerUserId = request.UserId,
                        Declaration = declaration,
                        CreatedAt = DateTime.UtcNow
                    };
                    context.Guilds.Add(guild);
                    await context.SaveChangesAsync();

                    context.GuildMembers.Add(new GuildMember
                    {
                        GuildId = guild.Id,
                        UserId = request.UserId,
                        Role = "Owner",
                        JoinedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildCreateRes, new DbGuildCreateResponse
                    {
                        Success = true,
                        Message = "公会创建成功",
                        GuildId = guild.Id
                    }, requestId);
                }
                catch (DbUpdateException)
                {
                    SendFailureResponse(session, MessageIds.DbGuildCreateRes, "公会名称已存在（并发创建被拒）");
                }
                catch (Exception ex)
                {
                    Log.Error($"公会创建异常 UserId:{request.UserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildCreateRes, "公会创建失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildMyRequest(ISession session, DbGuildMyRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildMyRes, "用户ID无效");
                return;
            }

            await RunPerUser("guild-member:" + request.UserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var member = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.UserId);
                    if (member == null)
                    {
                        SendDbResponse(session, MessageIds.DbGuildMyRes, new DbGuildMyResponse
                        {
                            Success = true,
                            Message = "未加入公会",
                            GuildId = 0
                        }, requestId);
                        return;
                    }

                    var guild = await context.Guilds.FirstOrDefaultAsync(g => g.Id == member.GuildId);
                    if (guild == null)
                    {
                        SendDbResponse(session, MessageIds.DbGuildMyRes, new DbGuildMyResponse
                        {
                            Success = true,
                            Message = "公会不存在",
                            GuildId = 0
                        }, requestId);
                        return;
                    }

                    var members = await (from m in context.GuildMembers
                                         join u in context.Users on m.UserId equals u.Id into joined
                                         from u in joined.DefaultIfEmpty()
                                         where m.GuildId == guild.Id
                                         orderby (m.Role == "Owner" ? 0 : 1), m.JoinedAt
                                         select new DbGuildMemberItem
                                         {
                                             UserId = m.UserId,
                                             Nickname = u != null ? u.Nickname : string.Empty,
                                             Role = m.Role
                                         }).ToListAsync();

                    SendDbResponse(session, MessageIds.DbGuildMyRes, new DbGuildMyResponse
                    {
                        Success = true,
                        Message = "ok",
                        GuildId = guild.Id,
                        Name = guild.Name,
                        OwnerUserId = guild.OwnerUserId,
                        Declaration = guild.Declaration,
                        Members = members
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"查询公会异常 UserId:{request.UserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildMyRes, "查询公会失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildJoinRequest(ISession session, DbGuildJoinRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0 || request.GuildId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildJoinRes, "参数无效");
                return;
            }

            await RunPerUser("guild:" + request.GuildId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var guild = await context.Guilds.FirstOrDefaultAsync(g => g.Id == request.GuildId);
                    if (guild == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildJoinRes, "公会不存在");
                        return;
                    }
                    bool alreadyIn = await context.GuildMembers.AnyAsync(m => m.UserId == request.UserId);
                    if (alreadyIn)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildJoinRes, "你已加入公会");
                        return;
                    }

                    context.GuildMembers.Add(new GuildMember
                    {
                        GuildId = guild.Id,
                        UserId = request.UserId,
                        Role = "Member",
                        JoinedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildJoinRes, new DbGuildJoinResponse
                    {
                        Success = true,
                        Message = "加入成功"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"加入公会异常 UserId:{request.UserId} GuildId:{request.GuildId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildJoinRes, "加入公会失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildLeaveRequest(ISession session, DbGuildLeaveRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildLeaveRes, "用户ID无效");
                return;
            }

            await RunPerUser("guild-member:" + request.UserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var member = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.UserId);
                    if (member == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildLeaveRes, "你未加入公会");
                        return;
                    }
                    if (member.Role == "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildLeaveRes, "会长需先转让或解散公会才能退出");
                        return;
                    }

                    context.GuildMembers.Remove(member);
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildLeaveRes, new DbGuildLeaveResponse
                    {
                        Success = true,
                        Message = "已退出公会"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"退出公会异常 UserId:{request.UserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildLeaveRes, "退出公会失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildDisbandRequest(ISession session, DbGuildDisbandRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildDisbandRes, "用户ID无效");
                return;
            }

            await RunPerUser("guild-owner:" + request.UserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var member = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.UserId);
                    if (member == null || member.Role != "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildDisbandRes, "只有会长才能解散公会");
                        return;
                    }

                    var members = await context.GuildMembers.Where(m => m.GuildId == member.GuildId).ToListAsync();
                    context.GuildMembers.RemoveRange(members);
                    var guild = await context.Guilds.FirstOrDefaultAsync(g => g.Id == member.GuildId);
                    if (guild != null)
                    {
                        context.Guilds.Remove(guild);
                    }
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildDisbandRes, new DbGuildDisbandResponse
                    {
                        Success = true,
                        Message = "公会已解散"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"解散公会异常 UserId:{request.UserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildDisbandRes, "解散公会失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildKickRequest(ISession session, DbGuildKickRequest? request, long? requestId = null)
        {
            if (request == null || request.OperatorUserId <= 0 || request.TargetUserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildKickRes, "参数无效");
                return;
            }

            await RunPerUser("guild-owner:" + request.OperatorUserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var operatorMember = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.OperatorUserId);
                    if (operatorMember == null || operatorMember.Role != "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildKickRes, "只有会长才能踢人");
                        return;
                    }
                    var target = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.TargetUserId && m.GuildId == operatorMember.GuildId);
                    if (target == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildKickRes, "目标成员不在公会中");
                        return;
                    }
                    if (target.Role == "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildKickRes, "不能踢出会长");
                        return;
                    }

                    context.GuildMembers.Remove(target);
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildKickRes, new DbGuildKickResponse
                    {
                        Success = true,
                        Message = "已踢出成员"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"踢出公会成员异常 Operator:{request.OperatorUserId} Target:{request.TargetUserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildKickRes, "踢人失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildTransferRequest(ISession session, DbGuildTransferRequest? request, long? requestId = null)
        {
            if (request == null || request.OperatorUserId <= 0 || request.TargetUserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildTransferRes, "参数无效");
                return;
            }

            await RunPerUser("guild-owner:" + request.OperatorUserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var operatorMember = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.OperatorUserId);
                    if (operatorMember == null || operatorMember.Role != "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildTransferRes, "只有会长才能转让");
                        return;
                    }
                    var target = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.TargetUserId && m.GuildId == operatorMember.GuildId);
                    if (target == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildTransferRes, "目标成员不在公会中");
                        return;
                    }

                    var guild = await context.Guilds.FirstOrDefaultAsync(g => g.Id == operatorMember.GuildId);
                    if (guild == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildTransferRes, "公会不存在");
                        return;
                    }

                    guild.OwnerUserId = request.TargetUserId;
                    target.Role = "Owner";
                    operatorMember.Role = "Member";
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildTransferRes, new DbGuildTransferResponse
                    {
                        Success = true,
                        Message = "会长已转让"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"转让会长异常 Operator:{request.OperatorUserId} Target:{request.TargetUserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildTransferRes, "转让失败，服务器内部错误");
                }
            });
        }

        public static async Task HandleGuildUpdateDeclRequest(ISession session, DbGuildUpdateDeclRequest? request, long? requestId = null)
        {
            if (request == null || request.UserId <= 0)
            {
                SendFailureResponse(session, MessageIds.DbGuildUpdateDeclRes, "用户ID无效");
                return;
            }
            string declaration = (request.Declaration ?? string.Empty).Trim();
            if (declaration.Length > MaxGuildDeclLength)
            {
                SendFailureResponse(session, MessageIds.DbGuildUpdateDeclRes, $"公会宣言过长（上限 {MaxGuildDeclLength} 字）");
                return;
            }

            await RunPerUser("guild-owner:" + request.UserId, async () =>
            {
                try
                {
                    using var scope = await CreateScopeAsync();
                    var context = scope.ServiceProvider.GetRequiredService<DefaultDbContext>();

                    var member = await context.GuildMembers.FirstOrDefaultAsync(m => m.UserId == request.UserId);
                    if (member == null || member.Role != "Owner")
                    {
                        SendFailureResponse(session, MessageIds.DbGuildUpdateDeclRes, "只有会长才能修改宣言");
                        return;
                    }
                    var guild = await context.Guilds.FirstOrDefaultAsync(g => g.Id == member.GuildId);
                    if (guild == null)
                    {
                        SendFailureResponse(session, MessageIds.DbGuildUpdateDeclRes, "公会不存在");
                        return;
                    }

                    guild.Declaration = declaration;
                    await context.SaveChangesAsync();

                    SendDbResponse(session, MessageIds.DbGuildUpdateDeclRes, new DbGuildUpdateDeclResponse
                    {
                        Success = true,
                        Message = "宣言已更新"
                    }, requestId);
                }
                catch (Exception ex)
                {
                    Log.Error($"修改宣言异常 UserId:{request.UserId} Err:{ex}");
                    SendFailureResponse(session, MessageIds.DbGuildUpdateDeclRes, "修改宣言失败，服务器内部错误");
                }
            });
        }
    }
}
