using Framework.Protocol.Generated;
using MemoryPack;

namespace Framework.Protocol;

/// <summary>
/// 消息处理器委托：payload 为去掉帧头后的纯消息体（已剥离路由元数据）。
/// </summary>
public delegate Task MessageHandlerDelegate(ISessionContext session, ReadOnlyMemory<byte> payload);

/// <summary>会话上下文（发送回包/广播的最小抽象，由服务器适配）。</summary>
public interface ISessionContext
{
    /// <summary>目标客户端会话 ID（0 表示内部消息）。</summary>
    long ClientSessionId { get; }

    /// <summary>向目标客户端发送 [MsgId][Payload] 包（自动附加路由元数据）。</summary>
    void Send(int msgId, ReadOnlyMemory<byte> payload);

    /// <summary>向目标客户端发送消息对象（自动序列化）。</summary>
    void Send(IGameMessage message);

    /// <summary>向指定客户端会话发送（帧同步等场景用）。</summary>
    void SendTo(long clientSessionId, int msgId, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// 配置化消息分发器（对标 KBE 自动生成的消息处理器注册表）：
/// - 由 RouterTable（Protocol/defs/*.def 生成）驱动，注册处不需要手写 MsgId 分支
/// - 每个消息类型注册一个强类型处理器
/// - 分发时按消息类型静态抽象成员反序列化（MemoryPack，零反射）
/// </summary>
public sealed class MessageDispatcher
{
    private readonly Dictionary<int, HandlerEntry> handlers = new();
    private readonly object gate = new();

    private sealed record HandlerEntry(Func<ReadOnlyMemory<byte>, IGameMessage> Deserializer, Func<ISessionContext, IGameMessage, Task> Handler);

    /// <summary>
    /// 注册强类型消息处理器。处理器签名：Task OnXxx(ISessionContext ctx, TMessage msg)
    /// </summary>
    public MessageDispatcher Register<TMessage>(Func<ISessionContext, TMessage, Task> handler) where TMessage : class, IGameMessage, new()
    {
        return Register<TMessage>(handler, jsonFallback: false);
    }

    /// <summary>
    /// 注册强类型消息处理器，并可选启用 JSON 兼容反序列化。
    /// jsonFallback=true 时（协议迁移过渡期），payload 自动探测：
    /// - 二进制（MemoryPack）优先
    /// - 非二进制（旧客户端 JSON）回退 JSON 解析
    /// </summary>
    public MessageDispatcher Register<TMessage>(Func<ISessionContext, TMessage, Task> handler, bool jsonFallback) where TMessage : class, IGameMessage, new()
    {
        int msgId = new TMessage().MessageId;
        Func<ReadOnlyMemory<byte>, IGameMessage> deserializer = jsonFallback
            ? payload => DeserializeCompatible<TMessage>(payload)
            : payload => MemoryPackSerializer.Deserialize<TMessage>(payload.Span)
                ?? throw new InvalidOperationException($"消息 {typeof(TMessage).Name} 反序列化为 null");
        Func<ISessionContext, IGameMessage, Task> wrapped = (ctx, msg) => handler(ctx, (TMessage)msg);

        lock (gate)
        {
            handlers[msgId] = new HandlerEntry(deserializer, wrapped);
        }
        return this;
    }

    /// <summary>
    /// 双格式反序列化：先按 MemoryPack 二进制解析；失败则按 JSON 解析（协议迁移过渡期兼容）。
    /// </summary>
    private static TMessage DeserializeCompatible<TMessage>(ReadOnlyMemory<byte> payload) where TMessage : class, IGameMessage, new()
    {
        // 探测：JSON 文本以 '{' 开头
        if (payload.Length > 0 && payload.Span[0] == (byte)'{')
        {
            var json = System.Text.Encoding.UTF8.GetString(payload.Span);
            var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<TMessage>(json);
            if (msg != null) return msg;
        }
        else
        {
            var bin = MemoryPackSerializer.Deserialize<TMessage>(payload.Span);
            if (bin != null) return bin;
        }

        throw new InvalidOperationException($"消息 {typeof(TMessage).Name} 兼容反序列化失败");
    }

    /// <summary>注册同步消息处理器。</summary>
    public MessageDispatcher RegisterSync<TMessage>(Action<ISessionContext, TMessage> handler) where TMessage : class, IGameMessage, new()
    {
        return RegisterSync<TMessage>(handler, jsonFallback: false);
    }

    /// <summary>注册同步消息处理器（可选 JSON 兼容）。</summary>
    public MessageDispatcher RegisterSync<TMessage>(Action<ISessionContext, TMessage> handler, bool jsonFallback) where TMessage : class, IGameMessage, new()
    {
        return Register<TMessage>((ctx, msg) =>
        {
            handler(ctx, msg);
            return Task.CompletedTask;
        }, jsonFallback);
    }

    /// <summary>是否已注册某 MsgId。</summary>
    public bool IsRegistered(int msgId)
    {
        lock (gate)
        {
            return handlers.ContainsKey(msgId);
        }
    }

    /// <summary>已注册的消息数。</summary>
    public int RegisteredCount
    {
        get
        {
            lock (gate)
            {
                return handlers.Count;
            }
        }
    }

    /// <summary>
    /// 分发一条消息：按 MsgId 查处理器，按类型反序列化并执行。
    /// 未注册返回 false（调用方可回退旧逻辑或返回错误响应）。
    /// </summary>
    public async Task<bool> TryDispatch(ISessionContext session, int msgId, ReadOnlyMemory<byte> payload)
    {
        HandlerEntry? entry;
        lock (gate)
        {
            handlers.TryGetValue(msgId, out entry);
        }

        if (entry == null)
        {
            return false;
        }

        try
        {
            IGameMessage msg = entry.Deserializer(payload);
            await entry.Handler(session, msg);
            return true;
        }
        catch (Exception ex)
        {
            Framework.Core.Log.Error(ex, $"MessageDispatcher 处理 MsgId={msgId} 异常");
            return true;
        }
    }
}
