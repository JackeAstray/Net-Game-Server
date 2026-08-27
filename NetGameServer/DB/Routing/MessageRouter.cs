using Network;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DB.Routing
{
    public class MessageRouter
    {
        private readonly ConcurrentDictionary<int, Func<ISession, ReadOnlyMemory<byte>, Task>> handlers = new();

        /// <summary>
        /// 按会话保序的任务队列（对标 KBE Buffered_DBTasks）：
        /// 同一调用方（Login/Game 连接）的 DB 请求严格按序执行，不同调用方并发执行。
        /// 避免"先写后读"乱序与同一实体的并发写冲突。
        /// </summary>
        private readonly Framework.Core.OrderedTaskQueue taskQueue = new("DBRouter");

        /// <summary>
        /// 绑定服务器：将消息路由器绑定到网络服务器的事件上，以便在接收到数据时能够正确地处理消息。
        /// </summary>
        /// <param name="server"></param>
        public void BindServer(INetworkServer server)
        {
            server.OnDataReceived -= HandleRawData;
            server.OnDataReceived += HandleRawData;
        }

        /// <summary>
        /// 对外暴露的分发入口（供认证管线在验证通过后调用）。
        /// </summary>
        public void DispatchData(ISession session, ReadOnlyMemory<byte> data) => HandleRawData(session, data);

        /// <summary>
        /// 注册消息处理函数：将消息 ID 与对应的处理函数关联起来。
        /// </summary>
        /// <param name="msgId">消息 ID。</param>
        /// <param name="handler">处理函数。</param>
        public void RegisterHandler(int msgId, Func<ISession, ReadOnlyMemory<byte>, Task> handler)
        {
            handlers[msgId] = handler;
        }

        /// <summary>
        /// 异步解析原始数据库协议数据，读取小端序的消息 ID（前 4 字节）与可选请求 ID，并将剩余负载转发给已注册的消息处理器。
        /// DB 请求通过 OrderedTaskQueue 按会话保序执行（对标 KBE Buffered_DBTasks），
        /// 同一调用方的请求严格串行、不同调用方并发，避免乱序与并发写冲突。
        /// </summary>
        private void HandleRawData(ISession session, ReadOnlyMemory<byte> data)
        {
            try
            {
                if (data.Length < 4)
                {
                    Shared.Log.Error($"[MessageRouter] 收到非法 DB 协议包，长度不足 4，实际: {data.Length}");
                    return;
                }

                int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                ReadOnlyMemory<byte> payload = data.Slice(4);

                long requestId = 0;
                if (Shared.RouteMetadata.TryExtractRequestId(payload, out long extractedRequestId, out var cleanPayload))
                {
                    requestId = extractedRequestId;
                    payload = cleanPayload;
                }

                if (handlers.TryGetValue(msgId, out var handler))
                {
                    // 按会话 ID 保序入队（同一连接先写后读不乱序；不同连接并发执行）
                    _ = taskQueue.EnqueueAsync(session.SessionId, async () =>
                    {
                        try
                        {
                            var targetSession = new RequestContextSession(session, requestId);
                            await handler(targetSession, payload);
                        }
                        catch (Exception ex)
                        {
                            Shared.Log.Error($"[MessageRouter] MsgId {msgId} 处理异常: {ex}");
                        }
                    });
                }
                else
                {
                    Shared.Log.Error($"未知的消息 ID: {msgId}");
                }
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"[MessageRouter] 处理原始 DB 数据时发生异常: {ex}");
            }
        }
    }
}