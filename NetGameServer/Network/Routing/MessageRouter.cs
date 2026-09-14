using System;
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
    /// 根据 msgId 将消息路由到已注册的处理器并执行。
    /// </summary>
    /// <remarks>调用处理器期间发生的异常会被捕获并通过 Shared.Log 记录，不会向上抛出；未找到处理器时记录警告。</remarks>
    /// <param name="session">处理器执行时使用的会话上下文。</param>
    /// <param name="msgId">消息类型的整数标识，用于查找对应的处理器。</param>
    /// <param name="payload">消息负载的只读字节序列。</param>
    /// <returns>如果找到并调用了处理器则返回 true（即使处理器内部抛出异常也视为已处理并返回 true）；若未找到处理器则返回 false。</returns>
    public bool TryRouteMessage(ISession session, int msgId, ReadOnlyMemory<byte> payload)
    {
        if (handlers.TryGetValue(msgId, out var handler))
        {
            try
            {
                handler.Invoke(session, payload);
                return true;
            }
            catch (Exception ex)
            {
                Shared.Log.Error($"[MessageRouter] 处理消息 {msgId} 时抛出异常: {ex}");
                return true;
            }
        }

        Shared.Log.Warning($"[MessageRouter] 未找到消息类型的处理器: {msgId}");
        return false;
    }

    /// <summary>
    /// 注册一个针对特定消息ID的处理逻辑
    /// </summary>
    /// <param name="msgId">消息ID</param>
    /// <param name="handler">处理逻辑</param>
    public void RegisterHandler(int msgId, MessageHandler handler)
    {
        // P3 修复：重复注册会静默覆盖前一个处理器，改为显式告警便于排查
        if (handlers.ContainsKey(msgId))
        {
            Shared.Log.Warning($"[MessageRouter] 重复注册 MsgId {msgId}，将覆盖先前处理器。请检查模块间 MsgId 是否冲突。");
        }
        handlers[msgId] = handler;
    }

    /// <summary>
    /// 注销消息ID的处理逻辑
    /// </summary>
    /// <param name="msgId">消息ID</param>
    /// <param name="handler">处理逻辑</param>
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
}