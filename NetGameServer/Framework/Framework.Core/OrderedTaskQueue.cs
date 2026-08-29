using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Framework.Core;

/// <summary>
/// 按键保序的任务队列（对标 KBE Buffered_DBTasks）：
/// - 同一 key（如实体 ID / DBID）上的任务严格按提交顺序串行执行（先写后读不乱序）
/// - 不同 key 之间并发执行（互不阻塞）
/// - DB 线程池模型：任务在后台线程池执行，不阻塞主循环
///
/// 迭代 8（三-16 修正）：由"每任务 Task.Run + 链式 prev 引用"改为
/// 每 key FIFO 队列 + 固定 worker 池（Channel 派发令牌）：
/// - 同 key 严格 FIFO 由队列本身保证（信号量方案不保证等待者 FIFO，已弃用）；
/// - 仅当 key 从空闲转忙碌时才派发一个令牌，worker 一次性串行清空该 key 队列，
///   消除每任务 Task.Run 的线程池调度开销与长队列持有的整条任务链引用；
/// - 固定 worker 数避免高吞吐下线程池膨胀。
/// </summary>
public sealed class OrderedTaskQueue
{
    private readonly Channel<KeyState> dispatchChannel;
    private readonly ConcurrentDictionary<object, KeyState> keyStates = new();
    private readonly Task[] workers;
    private readonly string name;
    private int pendingCount;

    /// <summary>提交任务后可选的回调（任务完成时触发）。</summary>
    public event Action<object, Exception?>? TaskCompleted;

    private sealed class WorkItem
    {
        public required object Key;
        public required Func<Task> Runner;
        public required TaskCompletionSource Completion;
    }

    /// <summary>单个 key 的 FIFO 队列 + 运行状态（全部访问在 Gate 锁内，保证严格串行）。</summary>
    private sealed class KeyState
    {
        public readonly object Gate = new();
        public readonly Queue<WorkItem> Queue = new();
        public bool Running;
        public long LastActivityTicks = Environment.TickCount64;
    }

    public OrderedTaskQueue(string name = "OrderedTaskQueue", int maxConcurrency = 0)
    {
        this.name = name;
        int workerCount = maxConcurrency > 0
            ? maxConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 8);
        dispatchChannel = Channel.CreateUnbounded<KeyState>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(WorkerLoop);
        }
    }

    /// <summary>
    /// 提交一个任务：同一 key 串行执行，不同 key 并发执行。
    /// </summary>
    /// <param name="key">保序键（实体 ID、DBID、账号等）</param>
    /// <param name="action">任务体（在后台线程执行，不应阻塞主线程太久）</param>
    /// <returns>任务完成后的 Task（异常会被捕获并通过 TaskCompleted 上报，不向上抛出）</returns>
    public Task Enqueue(object key, Action action)
    {
        return EnqueueCore(key, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>异步版本。</summary>
    public Task EnqueueAsync(object key, Func<Task> action)
    {
        return EnqueueCore(key, action);
    }

    private Task EnqueueCore(object key, Func<Task> runner)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new WorkItem { Key = key, Runner = runner, Completion = completion };
        var state = keyStates.GetOrAdd(key, _ => new KeyState());

        Interlocked.Increment(ref pendingCount);

        bool dispatch = false;
        lock (state.Gate)
        {
            state.LastActivityTicks = Environment.TickCount64;
            state.Queue.Enqueue(work);
            if (!state.Running)
            {
                state.Running = true; // 空闲 → 忙碌：派发一个令牌让 worker 清空本 key 队列
                dispatch = true;
            }
        }

        if (dispatch)
        {
            dispatchChannel.Writer.TryWrite(state);
        }
        return completion.Task;
    }

    /// <summary>当前排队/执行中的任务数（调试用）。</summary>
    public int Count => Volatile.Read(ref pendingCount);

    /// <summary>清理空闲 key（长时间无任务时释放队列与字典条目）。</summary>
    public void SweepIdle(TimeSpan idleThreshold)
    {
        long thresholdMs = (long)idleThreshold.TotalMilliseconds;
        long now = Environment.TickCount64;
        foreach (var (key, state) in keyStates)
        {
            lock (state.Gate)
            {
                // 无排队且无执行中任务才可安全移除（不会破坏保序）
                if (state.Running || state.Queue.Count > 0)
                {
                    continue;
                }
                if (now - state.LastActivityTicks <= thresholdMs)
                {
                    continue;
                }
            }
            keyStates.TryRemove(new KeyValuePair<object, KeyState>(key, state));
        }
    }

    /// <summary>
    /// 工作线程主循环（V18 修复：线程异常后自动重启，防止并发能力因一次异常永久降级）。
    /// 通道被正常 Complete 时退出；其余异常记录日志后延时重启。
    /// </summary>
    private async Task WorkerLoop()
    {
        while (true)
        {
            try
            {
                await DrainDispatchLoop();
                return; // 通道结束：正常退出
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{name}] 工作线程异常，重启中");
                try
                {
                    await Task.Delay(100);
                }
                catch
                {
                    return;
                }
            }
        }
    }

    private async Task DrainDispatchLoop()
    {
        await foreach (var state in dispatchChannel.Reader.ReadAllAsync())
        {
            // 串行清空该 key 当前排队的全部任务（FIFO），队列空时交还 Running=false
            while (true)
            {
                WorkItem? item;
                lock (state.Gate)
                {
                    if (state.Queue.Count == 0)
                    {
                        state.Running = false;
                        break;
                    }
                    item = state.Queue.Dequeue();
                }
                await RunItem(state, item);
            }
        }
    }

    private async Task RunItem(KeyState state, WorkItem item)
    {
        try
        {
            try
            {
                await item.Runner();
                TaskCompleted?.Invoke(item.Key, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{name}] 任务执行异常 key={item.Key}");
                TaskCompleted?.Invoke(item.Key, ex);
            }
        }
        finally
        {
            Interlocked.Decrement(ref pendingCount);
            state.LastActivityTicks = Environment.TickCount64;
            item.Completion.TrySetResult();
        }
    }
}
