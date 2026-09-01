using System.Diagnostics;
using System.Runtime.InteropServices;
using Framework.Core;

namespace Shared;

/// <summary>
/// 节点生命周期管理（对标 GeekServer/KBE 的优雅关服经验，迭代 21）：
/// - 安装 Ctrl+C / SIGTERM 信号处理，进程收到终止信号后进入"排空（draining）"状态；
/// - 按注册顺序执行关闭钩子（各自节点负责 flush 持久化 / 摘注册 / 关连接）；
/// - WaitForShutdownAsync 替代裸 Task.Delay(-1)，主流程在钩子完成后自然返回退出。
/// 排空期间 HealthServer 的 /readyz 返回 503（负载均衡/编排器据此摘流量），/healthz 保持 200（存活探针）。
/// </summary>
public sealed class NodeLifecycle
{
    private static readonly NodeLifecycle _instance = new();
    public static NodeLifecycle Default => _instance;

    private readonly object sync = new();
    private readonly List<Func<Task>> hooks = new();
    private readonly TaskCompletionSource<bool> shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int shutdownStarted;

    /// <summary>是否已进入排空/关闭流程（只读；健康检查据此返回 503）。</summary>
    public volatile bool IsDraining;

    /// <summary>排空超时（单钩子默认 10s）。</summary>
    public TimeSpan HookTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private NodeLifecycle()
    {
    }

    /// <summary>注册关闭钩子（幂等无要求；按注册顺序执行）。</summary>
    public void RegisterShutdownHook(Func<Task> hook)
    {
        lock (sync)
        {
            hooks.Add(hook);
        }
    }

    /// <summary>
    /// 安装终止信号处理（Ctrl+C、SIGTERM、SIGINT、进程退出兜底）。
    /// 触发后异步执行关闭流程（不阻塞信号线程）。
    /// </summary>
    public void InstallSignalHandlers()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 交给关闭流程处理
            Log.Info("收到 Ctrl+C（Console.CancelKeyPress），进入优雅关闭流程...");
            _ = RunShutdownAsync();
        };

        // SIGTERM / SIGINT（Docker/K8s/orchestrator 停止容器时发送）
        try
        {
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                Log.Info("收到 SIGTERM，进入优雅关闭流程...");
                _ = RunShutdownAsync();
            });
            PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
            {
                ctx.Cancel = true;
                Log.Info("收到 SIGINT，进入优雅关闭流程...");
                _ = RunShutdownAsync();
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"PosixSignalRegistration 注册失败（非 POSIX 平台可忽略）: {ex.Message}");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // 进程退出兜底：尽力同步触发关闭（无法 await，只标记排空 + 启动钩子）
            if (Interlocked.Exchange(ref shutdownStarted, 1) == 0)
            {
                IsDraining = true;
                Log.Info("进程退出兜底：标记排空并触发关闭钩子...");
            }
            shutdownSignal.TrySetResult(true);
        };
    }

    /// <summary>
    /// 等待关闭信号（替代裸 Task.Delay(-1)）。信号到达后返回，调用方随后执行 RunShutdownAsync。
    /// </summary>
    public Task WaitForShutdownAsync()
    {
        InstallSignalHandlers();
        return shutdownSignal.Task;
    }

    /// <summary>
    /// 执行优雅关闭：标记排空 → 依次运行钩子（带超时）→ 记录完成。
    /// 幂等：只执行一次。钩子抛异常不影响后续钩子与退出。
    /// </summary>
    public async Task RunShutdownAsync()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
        {
            return;
        }
        IsDraining = true;
        Log.Info("节点进入排空：执行优雅关闭钩子...");

        List<Func<Task>> snapshot;
        lock (sync)
        {
            snapshot = new List<Func<Task>>(hooks);
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var task = snapshot[i]();
                var completed = await Task.WhenAny(task, Task.Delay(HookTimeout));
                if (completed != task)
                {
                    Log.Warning($"关闭钩子 [{i}] 超时（>{HookTimeout.TotalSeconds}s），继续下一个");
                }
                else
                {
                    await task; // 传播异常
                    sw.Stop();
                    Log.Info($"关闭钩子 [{i}] 完成，耗时 {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"关闭钩子 [{i}] 执行异常，继续下一个");
            }
        }

        Log.Info("优雅关闭完成，进程退出。");
        shutdownSignal.TrySetResult(true);
    }
}
