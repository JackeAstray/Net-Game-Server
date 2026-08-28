using Framework.Core;
using Framework.Protocol.Generated;

namespace Framework.Entity;

/// <summary>
/// 实体 Mailbox 封装（对标 KBE entityMailboxComponent / cellMailbox）：
/// 脚本层友好的远端实体方法调用入口。自动判断目标实体的位置：
/// - Local（同进程）：直接经 <see cref="EntityManager.DispatchRemoteCall"/> 同步分发，零网络开销
/// - Remote（跨节点）：走 <see cref="EntityCall"/> + <see cref="EntityCallHub"/> 异步回执链路
/// 通过 <see cref="Entity.Mailbox"/> 在 csx 脚本中调用，宿主在 <see cref="EntityManager.AddOrUpdateEntity"/>
/// 时自动挂载 Local Mailbox；跨节点场景可显式 <see cref="AttachMailbox"/> 替换为 Remote Mailbox。
/// </summary>
public sealed class EntityMailbox
{
    /// <summary>无操作回调（异步调用未提供回调时的默认占位）。</summary>
    private static readonly Action<bool, object?> NoopCallback = static (_, _) => { };

    /// <summary>目标实体 ID。</summary>
    public long EntityId { get; }

    private readonly EntityManager? localManager;
    private readonly string? targetNodeId;
    private readonly Action<EntityRemoteCall>? remoteSendAction;

    /// <summary>是否为同进程 mailbox（同步分发，零网络开销）。</summary>
    public bool IsLocal => localManager != null && remoteSendAction == null;

    /// <summary>是否为跨节点 mailbox（走 EntityCallHub 异步回执）。</summary>
    public bool IsRemote => remoteSendAction != null;

    public EntityMailbox(long entityId, EntityManager? localManager, string? targetNodeId, Action<EntityRemoteCall>? remoteSendAction)
    {
        EntityId = entityId;
        this.localManager = localManager;
        this.targetNodeId = targetNodeId;
        this.remoteSendAction = remoteSendAction;
    }

    /// <summary>构造同进程 Mailbox：目标实体在当前 EntityManager 管理范围内时直接同步分发。</summary>
    public static EntityMailbox Local(long entityId, EntityManager manager) =>
        new(entityId, manager, targetNodeId: null, remoteSendAction: null);

    /// <summary>构造跨节点 Mailbox：调用经节点路由（Center 中继）到达目标节点执行。</summary>
    public static EntityMailbox Remote(long entityId, string targetNodeId, Action<EntityRemoteCall> sendAction) =>
        new(entityId, localManager: null, targetNodeId, sendAction);

    /// <summary>
    /// 调用目标实体方法（fire-and-forget，无回执/超时）。
    /// Local 路径：同步执行；Remote 路径：CallId=0 经 sendAction 发出。
    /// </summary>
    public void Call(string methodName, params object?[] args)
    {
        if (IsLocal)
        {
            DispatchLocal(methodName, args ?? Array.Empty<object?>());
            return;
        }
        SendRemote(methodName, args ?? Array.Empty<object?>(), callId: 0);
    }

    /// <summary>
    /// 异步调用目标实体方法并等待回执（对标 KBE mailbox 带回调调用）：
    /// - Local 路径：同步执行，结果直接通过 onComplete(Success, Result) 回调（无 CallId/超时）
    /// - Remote 路径：分配 CallId 并注册到 <see cref="EntityCallHub"/>（含超时截止），
    ///   远端回执到达时由 Hub 自动关联回调；超时未回执由 <see cref="EntityCallHub.SweepExpired"/> 触发
    /// </summary>
    /// <returns>Local 路径返回 -1（无 CallId）；Remote 路径返回分配的 CallId（0 表示发送失败）。</returns>
    public long CallAsync(string methodName, object?[]? args, Action<bool, object?>? onComplete = null, int timeoutMs = 5000)
    {
        object?[] a = args ?? Array.Empty<object?>();
        if (IsLocal)
        {
            var (handled, result) = DispatchLocal(methodName, a);
            try { onComplete?.Invoke(handled, result); }
            catch (Exception ex) { Log.Error(ex, $"Mailbox 本地回调异常 EntityId:{EntityId} Method:{methodName}"); }
            return -1L;
        }

        if (remoteSendAction == null)
        {
            Log.Warn($"Mailbox 远程路径未配置 sendAction，异步调用被忽略 EntityId:{EntityId} Method:{methodName}");
            return 0L;
        }

        long callId = EntityCallHubRegistry.Default.NextCallId();
        EntityCallHubRegistry.Default.Register(callId, new EntityCallHub.PendingCall
        {
            CallId = callId,
            TargetNodeId = targetNodeId,
            MethodName = methodName,
            DeadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs)),
            Callback = onComplete ?? NoopCallback
        });
        SendRemote(methodName, a, callId);
        return callId;
    }

    private (bool Handled, object? Result) DispatchLocal(string methodName, object?[] args)
    {
        if (localManager == null)
        {
            Log.Warn($"Mailbox 本地路径未配置 EntityManager EntityId:{EntityId} Method:{methodName}");
            return (false, null);
        }
        var call = new EntityRemoteCall
        {
            EntityId = EntityId,
            MethodName = methodName,
            Args = ArgCodec.Serialize(args),
            CallId = 0
        };
        return localManager.DispatchRemoteCall(call);
    }

    private void SendRemote(string methodName, object?[] args, long callId)
    {
        if (remoteSendAction == null)
        {
            Log.Warn($"Mailbox 远程路径未配置 sendAction，调用被忽略 EntityId:{EntityId} Method:{methodName}");
            return;
        }
        try
        {
            remoteSendAction(new EntityRemoteCall
            {
                TargetNodeId = targetNodeId ?? string.Empty,
                EntityId = EntityId,
                MethodName = methodName,
                Args = ArgCodec.Serialize(args),
                CallId = callId
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Mailbox 远程 sendAction 异常 EntityId:{EntityId} Method:{methodName} CallId:{callId}");
        }
    }
}
