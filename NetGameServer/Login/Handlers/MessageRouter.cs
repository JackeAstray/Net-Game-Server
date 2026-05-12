using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Login;

namespace Login.Handlers
{
    /// <summary>
    /// 消息路由器，负责将来自 Gateway 的消息分发到对应的处理函数。
    /// 包含构建消息处理器字典的逻辑，以及将处理结果回写给 Gateway 的辅助方法。
    /// </summary>
    public static class MessageRouter
    {
        /// <summary>
        /// 构建消息处理器映射（MsgId -> 处理函数）。
        /// 处理函数签名为: Func{payload, gatewaySession, clientSessionId, Task}。
        /// </summary>
        /// <param name="loginHandler">用于执行业务逻辑的 LoginHandler 实例（依赖注入或外部创建并传入）。</param>
        /// <returns>返回一个以消息 Id 为键、处理委托为值的字典。</returns>
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(LoginHandler loginHandler)
        {
            var handlers = new Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>();

            // 处理登录请求
            handlers[MessageIds.LoginReq] = async (payload, session, clientSessionId) =>
            {
                // 反序列化负载为 LoginRequest
                var req = Shared.Json.DeserializeFromUtf8Bytes<LoginRequest>(payload.Span);
                if (req != null)
                {
                    // 调用业务处理器并将结果发送回 Gateway
                    var res = await loginHandler.HandleLoginRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.LoginRes, res);
                }
            };

            // 处理注册请求
            handlers[MessageIds.RegisterReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<RegisterRequest>(payload.Span);
                if (req != null)
                {
                    var res = await loginHandler.HandleRegisterRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.RegisterRes, res);
                }
            };

            // 处理登出请求（此处为示例直接返回成功）
            handlers[MessageIds.LogoutReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LogoutRequest>(payload.Span);
                if (req != null)
                {
                    var res = new LogoutResponse { Success = true, Message = "登出成功" };
                    SendToGateway(session, clientSessionId, MessageIds.LogoutRes, res);
                }
            };

            // 处理重置密码请求（示例中未调用具体业务方法，直接返回成功）
            handlers[MessageIds.ResetPasswordReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangePasswordRequest>(payload.Span);
                if (req != null)
                {
                    // 若需调用具体业务逻辑，可通过 loginHandler 调用相应方法
                    // loginHandler.HandleChangePasswordRequest((Network.Tcp.TcpSession)session, req);
                    var res = new ChangePasswordResponse { Success = true, Message = "更改密码成功" };
                    SendToGateway(session, clientSessionId, MessageIds.ResetPasswordRes, res);
                }
            };

            // 处理更新昵称请求（示例中未调用具体业务方法，直接返回成功）
            handlers[MessageIds.UpdateNicknameReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangeNicknameRequest>(payload.Span);
                if (req != null)
                {
                    // 若需调用具体业务逻辑，可通过 loginHandler 调用相应方法
                    // loginHandler.HandleChangeNicknameRequest((Network.Tcp.TcpSession)session, req);
                    var res = new ChangeNicknameResponse { Success = true, Message = "更改昵称成功" };
                    SendToGateway(session, clientSessionId, MessageIds.UpdateNicknameRes, res);
                }
            };

            return handlers;
        }

        /// <summary>
        /// 将处理结果打包并发送回 Gateway。
        /// 包格式: [ClientSessionId(8 bytes little endian)][MsgId(4 bytes little endian)][Payload]
        /// </summary>
        /// <typeparam name="T">响应对象的类型（将会序列化为 JSON 字节数组）。</typeparam>
        /// <param name="gatewaySession">网关会话对象，用于发送数据给网关。</param>
        /// <param name="clientSessionId">原始客户端会话 ID（由网关转发过来，需原样返回以便网关转发给正确客户端）。</param>
        /// <param name="msgId">响应消息的 MsgId。</param>
        /// <param name="response">要发送的响应对象，会被序列化为 JSON。</param>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            // 将响应对象序列化为 UTF8 JSON 字节数组
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            // 构建最终包: 8 字节 ClientSessionId + 4 字节 MsgId + Payload
            byte[] packet = new byte[12 + responsePayload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), clientSessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), msgId);
            responsePayload.CopyTo(packet.AsSpan(12));
            // 发送到网关，由网关负责根据 clientSessionId 转发到具体客户端
            gatewaySession.Send(packet);
        }
    }
}
