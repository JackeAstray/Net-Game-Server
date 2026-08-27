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
    public static partial class MessageRouter
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

            handlers[MessageIds.PlayerDisconnectNotif] = async (payload, session, clientSessionId) =>
            {
                Login.Managers.SessionManager.Instance.OnSessionDisconnected(clientSessionId);
                await Task.CompletedTask;
            };

            // 处理登录请求
            handlers[MessageIds.LoginReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LoginRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.LoginRes, new LoginResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误",
                        UserId = 0,
                        Token = string.Empty
                    });
                    return;
                }

                var res = await loginHandler.HandleLoginRequestAsync(req, clientSessionId);
                SendToGateway(session, clientSessionId, MessageIds.LoginRes, res);
            };

            // 处理注册请求
            handlers[MessageIds.RegisterReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<RegisterRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.RegisterRes, new RegisterResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误"
                    });
                    return;
                }

                var res = await loginHandler.HandleRegisterRequestAsync(req);
                SendToGateway(session, clientSessionId, MessageIds.RegisterRes, res);
            };

            // 处理登出请求
            handlers[MessageIds.LogoutReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<LogoutRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.LogoutRes, new LogoutResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误"
                    });
                    return;
                }

                var res = await loginHandler.HandleLogoutRequestAsync(req, clientSessionId);
                SendToGateway(session, clientSessionId, MessageIds.LogoutRes, res);

                // 客户端主动登出了，通知 Gateway 断开它的物理连接。可以通过发一个特殊的命令包去 Gateway
                // 这里为了简单，LoginServer 只负责业务清理，连接断开在 Gateway 的长连接超时检测。
            };

            // 处理重置密码请求
            handlers[MessageIds.ResetPasswordReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangePasswordRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.ResetPasswordRes, new ChangePasswordResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误"
                    });
                    return;
                }

                var res = await loginHandler.HandleChangePasswordRequestAsync(req, clientSessionId);
                SendToGateway(session, clientSessionId, MessageIds.ResetPasswordRes, res);
            };

            // 处理更新昵称请求（示例中未调用具体业务方法，直接返回成功）
            handlers[MessageIds.UpdateNicknameReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<ChangeNicknameRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.UpdateNicknameRes, new ChangeNicknameResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误"
                    });
                    return;
                }

                // 若需调用具体业务逻辑，可通过 loginHandler 调用相应方法
                // loginHandler.HandleChangeNicknameRequest((Network.Tcp.TcpSession)session, req);
                var res = new ChangeNicknameResponse { Success = true, Message = "更改昵称成功" };
                SendToGateway(session, clientSessionId, MessageIds.UpdateNicknameRes, res);
                await Task.CompletedTask;
            };

            // 处理找回密码（发送验证码）请求
            handlers[MessageIds.FindPasswordWithCodeReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<FindPasswordRequest>(payload.Span);
                if (req == null)
                {
                    SendToGateway(session, clientSessionId, MessageIds.FindPasswordWithCodeRes, new FindPasswordResponse
                    {
                        Success = false,
                        Message = "请求数据格式错误"
                    });
                    return;
                }

                var res = await loginHandler.HandleFindPasswordRequestAsync(req);
                SendToGateway(session, clientSessionId, MessageIds.FindPasswordWithCodeRes, res);
            };

            return handlers;
        }

        /// <summary>
        /// 将响应序列化为 UTF-8 JSON、附加客户端会话标识并通过指定的网关会话发送。
        /// </summary>
        /// <remarks>响应先序列化为 UTF-8 字节数组，随后将客户端会话 ID 附加到路由元数据并构建数据包；发送时使用实际总长度的缓冲片段，发送完成后将临时字节数组归还到
        /// ArrayPool<byte>。</remarks>
        /// <typeparam name="T">响应的类型，用于序列化为 JSON 的泛型类型。</typeparam>
        /// <param name="gatewaySession">用于发送构建后数据包的网关会话。</param>
        /// <param name="clientSessionId">目标客户端的会话标识（作为路由元数据附加）。</param>
        /// <param name="msgId">要发送的数据包的消息标识符。</param>
        /// <param name="response">要序列化为负载并发送的响应实例。</param>
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
