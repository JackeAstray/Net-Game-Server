using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Center;

namespace Center.Handlers
{
    public static class MessageRouter
    {
        /// <summary>
        /// 构建并返回一个将消息ID映射到异步处理委托的字典，用于解析消息负载并执行相应的匹配、房间和节点操作。
        /// </summary>
        /// <remarks>各处理程序负责反序列化负载、调用 MatchHandler 的异步或同步方法、向网关发送响应以及在节点注册或状态更新时更新
        /// NodeManager。部分处理程序直接返回已完成的任务。</remarks>
        /// <param name="matchHandler">用于处理匹配及相关请求的 MatchHandler 实例。</param>
        /// <returns>包含消息ID到处理委托映射的字典；委托签名为 Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>。</returns>
        public static Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>> BuildHandlers(MatchHandler matchHandler)
        {
            var handlers = new Dictionary<int, Func<ReadOnlyMemory<byte>, Network.ISession, long, Task>>();

            handlers[MessageIds.CenterMatchReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<CenterMatchRequest>(payload.Span);
                if (req != null)
                {
                    var res = await matchHandler.HandleMatchRequestAsync(clientSessionId, req, session, SendToGateway);
                    if (res != null)
                    {
                        SendToGateway(session, clientSessionId, MessageIds.CenterMatchRes, res);
                    }
                }
            };

            handlers[MessageIds.CenterCreateRoomReq] = async (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateRoomRequest>(payload.Span);
                if (req != null)
                {
                    var res = await matchHandler.HandleCreateRoomRequestAsync(req);
                    SendToGateway(session, clientSessionId, MessageIds.CenterCreateRoomRes, res);
                }
            };

            handlers[MessageIds.CenterCreateSceneRes] = async (payload, session, clientSessionId) =>
            {
                var res = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateSceneResponse>(payload.Span);
                if (res != null)
                {
                    matchHandler.HandleCreateSceneResponse(res);
                }
                await Task.CompletedTask;
            };

            handlers[MessageIds.CenterRegisterNodeReq] = (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRegisterNodeRequest>(payload.Span);
                if (req != null)
                {
                    NodeManager.Instance.RegisterNode(req.NodeId, req.NodeType, req.Host, req.Port, session);
                    NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);

                    // 响应注册成功 (这里假设 0 是保留给内网节点的 ClientSessionId)
                    // var resPayload = Shared.Json.SerializeToUtf8Bytes(new { Success = true });
                    // byte[] packet = Shared.Network.PacketBuilder.BuildInternalPacket(0, MessageIds.CenterRegisterNodeRes, resPayload);
                    // session.Send(packet);
                }
                return Task.CompletedTask;
            };

            handlers[MessageIds.CenterNodeStatusReq] = (payload, session, clientSessionId) =>
            {
                var req = Shared.Json.DeserializeFromUtf8Bytes<CenterNodeStatusRequest>(payload.Span);
                if (req != null && !string.IsNullOrWhiteSpace(req.NodeId))
                {
                    NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);
                }
                return Task.CompletedTask;
            };

            return handlers;
        }

        /// <summary>
        /// 将响应发送回网关服务器，统一协议 [MsgId][Payload]，路由信息通过 payload 元数据传递。
        /// </summary>
        private static void SendToGateway<T>(Network.ISession gatewaySession, long clientSessionId, int msgId, T response)
        {
            byte[] responsePayload = Shared.Json.SerializeToUtf8Bytes(response);
            byte[] routedPayload = Shared.RouteMetadata.AttachClientSessionId(responsePayload, clientSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);

            try
            {
                if (clientSessionId > 0
                    && NodeManager.Instance.TryGetGatewaySessionByClientSessionId(clientSessionId, out var routedGatewaySession)
                    && routedGatewaySession.IsConnected)
                {
                    routedGatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
                    return;
                }

                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}