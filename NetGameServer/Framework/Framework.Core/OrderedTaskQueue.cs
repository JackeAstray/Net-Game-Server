using System.Collections.Concurrent;

namespace Framework.Core;

/// <summary>
/// 按键保序的任务队列（对标 KBE Buffered_DBTasks）：
/// - 同一 key（如实体 ID / DBID）上的任务严格按提交顺序串行执行（先写后读不乱序）
/// - 不同 key 之间并发执行（互不阻塞）
/// - DB 线程池模型：任务在后台线程池执行，不阻塞主循环
/// </summary>
public sealed class OrderedTaskQueue
{
    private readonly ConcurrentDictionary<object, KeyQueue> keyQueues = new();
    private readonly TaskFactory factory;
    private readonly string name;

    /// <summary>提交任务后可选的回调（任务完成时触发）。</summary>
    public event Action<object, Exception?>? TaskCompleted;

    public OrderedTaskQueue(string name = "OrderedTaskQueue", int maxConcurrency = 0)
    {
        this.name = name;
        var options = maxConcurrency > 0
            ? new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }
            : new ParallelOptions();
        factory = new TaskFactory(TaskScheduler.Default);
    }

    /// <summary>
    /// 提交一个任务：同一 key 串行执行，不同 key 并发执行。
    /// </summary>
    /// <param name="key">保序键（实体 ID、DBID、账号等）</param>
    /// <param name="action">任务体（在后台线程执行，不应阻塞主线程太久）</param>
    /// <returns>任务完成后的 Task（异常会被捕获并通过 TaskCompleted 上报，不向上抛出）</returns>
    public Task Enqueue(object key, Action action)
    {
        var queue = keyQueues.GetOrAdd(key, _ => new KeyQueue());
        return queue.Enqueue(() => RunSafely(key, action));
    }

    /// <summary>异步版本。</summary>
    public Task EnqueueAsync(object key, Func<Task> action)
    {
        var queue = keyQueues.GetOrAdd(key, _ => new KeyQueue());
        return queue.Enqueue(() => RunSafelyAsync(key, action));
    }

    /// <summary>当前队列数（调试用）。</summary>
    public int Count => keyQueues.Count;

    /// <summary>清理空闲队列（长时间无任务时释放内存）。</summary>
    public void SweepIdle(TimeSpan idleThreshold)
    {
        foreach (var (key, queue) in keyQueues)
        {
            if (queue.IsIdle(idleThreshold) && keyQueues.TryRemove(key, out var q) && q.PendingCount > 0)
            {
                // 移除时仍有任务在排队的键，重新放回（保守做法：保留）
                if (q.PendingCount > 0)
                {
                    keyQueues.TryAdd(key, q);
                }
            }
        }
    }

    private Task RunSafely(object key, Action action)
    {
        try
        {
            action();
            TaskCompleted?.Invoke(key, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[{name}] 任务执行异常 key={key}");
            TaskCompleted?.Invoke(key, ex);
        }
        return Task.CompletedTask;
    }

    private async Task RunSafelyAsync(object key, Func<Task> action)
    {
        try
        {
            await action();
            TaskCompleted?.Invoke(key, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[{name}] 异步任务执行异常 key={key}");
            TaskCompleted?.Invoke(key, ex);
        }
    }

    /// <summary>单个 key 的任务链：前一个完成后启动下一个。</summary>
    private sealed class KeyQueue
    {
        private readonly object gate = new();
        private Task? tail = Task.CompletedTask;
        private int pending;
        private DateTime lastActivity = DateTime.UtcNow;

        public int PendingCount => pending;

        public bool IsIdle(TimeSpan threshold) =>
            pending == 0 && DateTime.UtcNow - lastActivity > threshold;

        public Task Enqueue(Func<Task> runner)
        {
            lock (gate)
            {
                pending++;
                lastActivity = DateTime.UtcNow;
                // 链式追加：tail 完成后执行 runner（保证同一 key 严格串行）
                var prev = tail;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await prev;
                    }
                    catch
                    {
                        // 前序异常不应阻断后续任务（已在上层记录）
                    }
                    await runner();
                    lock (gate)
                    {
                        pending--;
                        lastActivity = DateTime.UtcNow;
                    }
                });
                tail = task;
                return task;
            }
        }
    }
}
