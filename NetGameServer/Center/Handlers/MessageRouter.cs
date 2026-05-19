using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateRoomRequest>(payload.Span);
                    if (req != null)
                    {
                        var res = await matchHandler.HandleCreateRoomRequestAsync(req);
                        SendToGateway(session, clientSessionId, MessageIds.CenterCreateRoomRes, res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCreateRoomReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCreateRoomReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterCreateSceneRes] = async (payload, session, clientSessionId) =>
            {
                try
                {
                    var res = Shared.Json.DeserializeFromUtf8Bytes<CenterCreateSceneResponse>(payload.Span);
                    if (res != null)
                    {
                        matchHandler.HandleCreateSceneResponse(res);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterCreateSceneRes 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterCreateSceneRes 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
            };

            handlers[MessageIds.CenterRegisterNodeReq] = (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterRegisterNodeRequest>(payload.Span);
                    if (req != null)
                    {
                        if (!VerifyRegisterSignature(req))
                        {
                            Shared.Log.Warning($"CenterRegisterNodeReq 签名校验失败，NodeId:{req.NodeId}");
                            return Task.CompletedTask;
                        }

                        NodeManager.Instance.RegisterNode(req.NodeId, req.NodeType, req.Host, req.Port, session);
                        NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterRegisterNodeReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterRegisterNodeReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
                return Task.CompletedTask;
            };

            handlers[MessageIds.CenterNodeStatusReq] = (payload, session, clientSessionId) =>
            {
                try
                {
                    var req = Shared.Json.DeserializeFromUtf8Bytes<CenterNodeStatusRequest>(payload.Span);
                    if (req != null && !string.IsNullOrWhiteSpace(req.NodeId))
                    {
                        if (!VerifyStatusSignature(req))
                        {
                            Shared.Log.Warning($"CenterNodeStatusReq 签名校验失败，NodeId:{req.NodeId}");
                            return Task.CompletedTask;
                        }

                        NodeManager.Instance.UpdateLoad(req.NodeId, req.CurrentLoad);
                    }
                    else
                    {
                        Shared.Log.Warning($"CenterNodeStatusReq 反序列化失败 ClientSessionId:{clientSessionId}");
                    }
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"CenterNodeStatusReq 处理异常 ClientSessionId:{clientSessionId} Exception:{ex}");
                }
                return Task.CompletedTask;
            };

            return handlers;
        }

        private static bool VerifyRegisterSignature(CenterRegisterNodeRequest req)
        {
            if (!IsTimestampValid(req.Timestamp) || string.IsNullOrWhiteSpace(req.Signature))
            {
                return false;
            }

            string source = $"{req.NodeId}|{req.NodeType}|{req.Host}|{req.Port}|{req.CurrentLoad}|{req.Timestamp}";
            string expected = ComputeSignature(source);
            return FixedTimeEquals(expected, req.Signature);
        }

        private static bool VerifyStatusSignature(CenterNodeStatusRequest req)
        {
            if (!IsTimestampValid(req.Timestamp) || string.IsNullOrWhiteSpace(req.Signature))
            {
                return false;
            }

            string source = $"{req.NodeId}|{req.CurrentLoad}|{req.Timestamp}";
            string expected = ComputeSignature(source);
            return FixedTimeEquals(expected, req.Signature);
        }

        private static bool IsTimestampValid(long timestamp)
        {
            if (timestamp <= 0)
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delta = Math.Abs(now - timestamp);
            return delta <= 120;
        }

        private static string ComputeSignature(string source)
        {
            string secret = Shared.ConfigHelper.GetConfig<string>("CenterNodeSharedSecret") ?? "change-this-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);

            using var hmac = new HMACSHA256(key);
            byte[] hash = hmac.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
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
            catch (Exception ex)
            {
                Shared.Log.Error($"SendToGateway 发送失败 MsgId:{msgId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}