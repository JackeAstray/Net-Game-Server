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

            // 处理登出请求
            handlers[MessageIds.LogoutReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LogoutRequest>(payload.Span);
                if (req != null)
                {
                    var res = await loginHandler.HandleLogoutRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.LogoutRes, res);

                    // 客户端主动登出了，通知 Gateway 断开它的物理连接。可以通过发一个特殊的命令包去 Gateway
                    // 这里为了简单，LoginServer 只负责业务清理，连接断开在 Gateway 的长连接超时检测。
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
        /// 将处理结果发送回 Gateway。
        /// 统一协议为 [MsgId][Payload]，客户端路由信息通过 payload 元数据 __clientSessionId 传递。
        /// </summary>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(responsePayload, clientSessionId);
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
}
