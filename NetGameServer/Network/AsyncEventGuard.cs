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
/// </summary>
public static class AsyncEventGuard
{
    /// <summary>
    /// 把 async Func&lt;ISession, ReadOnlyMemory&lt;byte&gt;, Task&gt; 包装为 void 委托。
    /// 内部用 Task.Run 调度，异常被捕获并记录（不冒泡到 AppDomain）。
    /// </summary>
    public static DataReceivedHandler Wrap(Func<ISession, ReadOnlyMemory<byte>, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return (session, data) =>
        {
            _ = Task.Run(async () =>
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
            });
        };
    }

    /// <summary>通用 Func&lt;Task&gt; 包装为 Action，异常被记录。</summary>
    public static Action Wrap(Func<Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return () =>
        {
            _ = Task.Run(async () =>
            {
                try { await handler().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Framework.Core.Log.Error(ex, "[AsyncEventGuard] 异步任务异常");
                }
            });
        };
    }
}
