using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Login;

namespace Login.Handlers
{
    /// <summary>
    /// 消息路由器，负责将来自 Gateway 的消息分发到对应的处理函数。
    /// </summary>
    public static class MessageRouter
    {
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(LoginHandler loginHandler)
        {
            var handlers = new Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>();

            handlers[MessageIds.LoginReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LoginRequest>(payload.Span);
                if (req != null)
                {
                    var res = await loginHandler.HandleLoginRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.LoginRes, res);
                }
            };

            handlers[MessageIds.RegisterReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<RegisterRequest>(payload.Span);
                if (req != null)
                {
                    var res = await loginHandler.HandleRegisterRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.RegisterRes, res);
                }
            };

            handlers[MessageIds.LogoutReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LogoutRequest>(payload.Span);
                if (req != null)
                {
                    var res = new LogoutResponse { Success = true, Message = "登出成功" };
                    SendToGateway(session, clientSessionId, MessageIds.LogoutRes, res);
                }
            };

            handlers[MessageIds.ResetPasswordReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangePasswordRequest>(payload.Span);
                if (req != null)
                {
                    // loginHandler.HandleChangePasswordRequest((Network.Tcp.TcpSession)session, req);
                    var res = new ChangePasswordResponse { Success = true, Message = "更改密码成功" };
                    SendToGateway(session, clientSessionId, MessageIds.ResetPasswordRes, res);
                }
            };

            handlers[MessageIds.UpdateNicknameReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangeNicknameRequest>(payload.Span);
                if (req != null)
                {
                    // loginHandler.HandleChangeNicknameRequest((Network.Tcp.TcpSession)session, req);
                    var res = new ChangeNicknameResponse { Success = true, Message = "更改昵称成功" };
                    SendToGateway(session, clientSessionId, MessageIds.UpdateNicknameRes, res);
                }
            };

            return handlers;
        }

        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] packet = new byte[12 + responsePayload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), clientSessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), msgId);
            responsePayload.CopyTo(packet.AsSpan(12));
            gatewaySession.Send(packet);
        }
    }
}
