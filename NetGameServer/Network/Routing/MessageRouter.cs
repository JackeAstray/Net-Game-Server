using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Network.Routing;

/// <summary>
/// 全局网络消息路由中心，负责将接收到的原始二进制数据根据 MsgId 分发到各业务注册的处理器中。
/// 支持零GC的反序列化集成和中间件管线体系。
/// </summary>
public class MessageRouter
{
    // 定义消息回调委托。允许接收和读取二进制数据，可以方便对接 Protobuf, MessagePack 或原生 Span 解析
    public delegate void MessageHandler(ISession session, ReadOnlyMemory<byte> payload);

    private readonly ConcurrentDictionary<int, MessageHandler> handlers = new();

    /// <summary>
    /// 将此路由器附加到网络服务器上的所有连接接收事件。
    /// 可以统一截获 INetworkServer 的 OnDataReceived。
    /// </summary>
    public void BindServer(INetworkServer server)
    {
        server.OnDataReceived -= HandleRawData;
        server.OnDataReceived += HandleRawData;
    }

    public void UnbindServer(INetworkServer server)
    {
        server.OnDataReceived -= HandleRawData;
    }

    /// <summary>
    /// 手动分发消息
    /// </summary>
    public void RouteMessage(ISession session, int msgId, ReadOnlyMemory<byte> payload)
    {
        if (handlers.TryGetValue(msgId, out var handler))
        {
            try
            {
                handler.Invoke(session, payload);
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"[MessageRouter] 处理消息 {msgId} 时抛出异常: {ex}");
            }
        }
        else
        {
            Shared.Log.Warning($"[MessageRouter] 未找到消息类型的处理器: {msgId}");
        }
    }

    /// <summary>
    /// 注册一个针对特定消息ID的处理逻辑
    /// </summary>
    public void RegisterHandler(int msgId, MessageHandler handler)
    {
        handlers.AddOrUpdate(msgId, handler, (_, existing) => existing + handler);
    }

    /// <summary>
    /// 注销消息ID的处理逻辑
    /// </summary>
    public void UnregisterHandler(int msgId, MessageHandler handler)
    {
        if (handlers.TryGetValue(msgId, out var existing))
        {
            var updated = (MessageHandler?)Delegate.Remove(existing, handler);
            if (updated == null)
            {
                handlers.TryRemove(msgId, out _);
            }
            else
            {
                handlers[msgId] = updated;
            }
        }
    }

    /// <summary>
    /// 解码接收到的数据：前4个字节视为 MsgId（小端序），后续为具体的协议包体（Payload）
    /// </summary>
    private void HandleRawData(ISession session, ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4)
        {
            Shared.Log.Error($"[MessageRouter] 会话 {session.SessionId} 发送的最小包长度无效，已忽略。");
            return;
        }

        int msgId = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
        var payload = data.Slice(4); // 零拷贝切片出数据体

        if (handlers.TryGetValue(msgId, out var handler))
        {
            try
            {
                handler.Invoke(session, payload);
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"[MessageRouter] MsgId {msgId} 处理发生异常: {ex}");
            }
        }
        else
        {
            Shared.Log.Warning($"[MessageRouter] 收到未注册的 MsgId {msgId}，已丢弃");
        }
    }
}