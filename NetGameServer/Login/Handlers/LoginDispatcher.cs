using Framework.Protocol;
using Framework.Protocol.Generated;
using Shared.Messages;
using Shared.Messages.Login;
using ISession = Network.ISession;
using LoginMsg = Framework.Protocol.Generated.Login;

namespace Login.Handlers;

/// <summary>
/// Login 服务器的会话上下文适配（ISessionContext 实现）：
/// 将 MessageDispatcher 的抽象发送接口适配到 Login 的网关会话 + __clientSessionId 路由元数据。
/// </summary>
public sealed class LoginSessionContext : ISessionContext
{
    private readonly ISession gatewaySession;
    private readonly long clientSessionId;

    public LoginSessionContext(ISession gatewaySession, long clientSessionId)
    {
        this.gatewaySession = gatewaySession;
        this.clientSessionId = clientSessionId;
    }

    public long ClientSessionId => clientSessionId;

    public void Send(int msgId, ReadOnlyMemory<byte> payload)
    {
        byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, clientSessionId);
        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
        try
        {
            gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public void Send(IGameMessage message)
    {
        Send(message.MessageId, message.Serialize());
    }

    public void SendTo(long targetSessionId, int msgId, ReadOnlyMemory<byte> payload)
    {
        byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(payload, targetSessionId);
        byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
        try
        {
            gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(packet);
        }
    }
}

/// <summary>
/// 基于 MessageDispatcher 的强类型处理器（对标 KBE 自动生成的处理器注册表）。
/// 使用生成的消息类 + MemoryPack 二进制序列化（JSON 兼容回退），消灭手写 MsgId 分支。
/// </summary>
public static partial class MessageRouter
{
    /// <summary>
    /// 构建登录链路的配置化分发器。未注册的 MsgId 由调用方回退旧字典。
    /// </summary>
    public static Framework.Protocol.MessageDispatcher BuildDispatcher(LoginHandler loginHandler)
    {
        var dispatcher = new Framework.Protocol.MessageDispatcher();

        // 登录（旧客户端 JSON / 新客户端 MemoryPack 双格式）
        // async 处理：避免 GetAwaiter().GetResult() 阻塞网关收包线程（DB 校验可能耗时数百毫秒）
        dispatcher.Register<LoginMsg>(async (ctx, msg) =>
        {
            var req = new LoginRequest { Account = msg.Account, Password = msg.Password };
            var res = await loginHandler.HandleLoginRequestAsync(req, ctx.ClientSessionId);
            ctx.Send(new LoginResult
            {
                Success = res.Success,
                Message = res.Message ?? string.Empty,
                UserId = res.UserId,
                Token = res.Token ?? string.Empty,
                UniqueId = res.UniqueId ?? string.Empty,
                Nickname = res.Nickname ?? string.Empty,
                Email = res.Email ?? string.Empty,
                LastLoginTime = res.LastLoginTime.Kind == DateTimeKind.Utc
                    ? new DateTimeOffset(res.LastLoginTime).ToUnixTimeSeconds()
                    : new DateTimeOffset(res.LastLoginTime, TimeSpan.Zero).ToUnixTimeSeconds(),
                LoginCount = res.LoginCount,
                IsAdmin = res.IsAdmin
            });
        }, jsonFallback: true);

        // 注册
        dispatcher.Register<Register>(async (ctx, msg) =>
        {
            var req = new RegisterRequest { Account = msg.Account, Password = msg.Password, Nickname = msg.Nickname };
            var res = await loginHandler.HandleRegisterRequestAsync(req);
            ctx.Send(new RegisterResult { Success = res.Success, Message = res.Message ?? string.Empty });
        }, jsonFallback: true);

        // 登出
        dispatcher.Register<Logout>(async (ctx, msg) =>
        {
            var req = new LogoutRequest { UserId = msg.UserId };
            var res = await loginHandler.HandleLogoutRequestAsync(req, ctx.ClientSessionId);
            ctx.Send(new LogoutResult { Success = res.Success, Message = res.Message ?? string.Empty });
        }, jsonFallback: true);

        // 玩家断线通知（网关内部消息）
        dispatcher.RegisterSync<PlayerDisconnect>((ctx, msg) =>
        {
            Login.Managers.SessionManager.Instance.OnSessionDisconnected(ctx.ClientSessionId);
        }, jsonFallback: true);

        // 重置密码（对标旧 ResetPasswordReq：会话 + 账户改密）
        dispatcher.Register<ResetPassword>(async (ctx, msg) =>
        {
            var req = new ChangePasswordRequest
            {
                Account = msg.Account,
                OldPassword = msg.OldPassword,
                NewPassword = msg.NewPassword
            };
            var res = await loginHandler.HandleChangePasswordRequestAsync(req, ctx.ClientSessionId);
            ctx.Send(new ResetPasswordResult { Success = res.Success, Message = res.Message ?? string.Empty });
        }, jsonFallback: true);

        // 更新昵称（与旧 UpdateNicknameReq 一致：直接返回成功）
        dispatcher.RegisterSync<UpdateNickname>((ctx, msg) =>
        {
            ctx.Send(new UpdateNicknameResult { Success = true, Message = "更改昵称成功" });
        }, jsonFallback: true);

        // 找回密码（发送验证码；对标旧 FindPasswordWithCodeReq）
        dispatcher.Register<FindPasswordWithCode>(async (ctx, msg) =>
        {
            var req = new FindPasswordRequest { Account = msg.Account, Email = msg.Email };
            var res = await loginHandler.HandleFindPasswordRequestAsync(req);
            ctx.Send(new FindPasswordWithCodeResult { Success = res.Success, Message = res.Message ?? string.Empty });
        }, jsonFallback: true);

        return dispatcher;
    }
}
