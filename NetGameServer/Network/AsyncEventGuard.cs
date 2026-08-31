using System;
using System.Threading.Tasks;
using Framework.Core;

namespace Network;

/// <summary>
/// OnDataReceived 等 void 事件处理器的"安全 async 包装"：用于把 async lambda
/// 注册到 void 委托时，捕获异常避免进程崩溃。
/// 旧实现: tcpServer.OnDataReceived += async (s, d) => { ... await ... };
///   - async lambda 适配 void 委托变成 async void，await 异常会冒到 AppDomain。
/// 新实现: tcpServer.OnDataReceived += AsyncEventGuard.Wrap(async (s, d) => { ... });
///   - 内部用 Task.Run 启动独立任务，异常进入 UnobservedTaskException 记录日志。
///
/// 安全修复（P2，并发/包序）：
///   原实现对每个数据包 Task.Run 一次，产生两类问题：
///     1) 线程池调度抖动：每包一次任务排队，高吞吐下无谓的上下文切换与分配；
///     2) 同会话包序丢失：前包未完成即可处理后包，登录/读写等操作可乱序交错。
///   现在改为"内联异步派发"：handler 在接收循环线程上同步执行到首个 await 才让出，
///   同步段自然串行（恢复包序），异步段并发（保留吞吐），异常依旧被捕获不冒泡。
///   （若某节点确实需要按客户端/账号严格串行，应在其内部用 OrderedTaskQueue 按键队列，
///    而不是在包级加全局锁，见 DbDispatcher/OrderedTaskQueue 的设计。）
/// </summary>
public static class AsyncEventGuard
{
    /// <summary>
    /// 把 async Func&lt;ISession, ReadOnlyMemory&lt;byte&gt;, Task&gt; 包装为 void 委托。
    /// 同步执行到首个 await；异常被捕获并记录（不冒泡到 AppDomain）。
    /// </summary>
    public static DataReceivedHandler Wrap(Func<ISession, ReadOnlyMemory<byte>, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return (session, data) =>
        {
            _ = HandleAsync(handler, session, data);
        };
    }

    /// <summary>通用 Func&lt;Task&gt; 包装为 Action，异常被记录。</summary>
    public static Action Wrap(Func<Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return () =>
        {
            _ = HandleAsync(handler);
        };
    }

    private static async Task HandleAsync(Func<ISession, ReadOnlyMemory<byte>, Task> handler, ISession session, ReadOnlyMemory<byte> data)
    {
        try
        {
            await handler(session, data).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消属于正常生命周期，不记录
        }
        catch (Exception ex)
        {
            // 记录但不冒泡；防止 async void 异常崩溃进程
            Framework.Core.Log.Error(ex,
                $"[AsyncEventGuard] 异步事件处理异常 SessionId:{session?.SessionId}");
        }
    }

    private static async Task HandleAsync(Func<Task> handler)
    {
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Framework.Core.Log.Error(ex, "[AsyncEventGuard] 异步任务异常");
        }
    }
}
