using Framework.Core;

namespace Framework.Tick;

/// <summary>
/// 单线程 tick 引擎（对标 KBE 主循环：所有游戏逻辑在 gameUpdateHertz 驱动的
/// handleGameTick 内串行执行，无锁）。
/// 用法：
///   var engine = new TickEngine(20);               // 20Hz
///   engine.OnTick += frame => {...};               // 每帧回调
///   engine.AddTimer(500, () => {...});             // 500ms 定时器
///   engine.Start();  ...  engine.Stop();
/// </summary>
public sealed class TickEngine
{
    private readonly int hertz;
    private readonly object gate = new();
    private Thread? thread;
    private volatile bool running;
    private long frame;

    // 定时器（由主线程独占访问，无需锁）
    private readonly PriorityQueue<(long dueTick, long seq, TimerHandle handle), long> timers = new();
    private long timerSeq;

    /// <summary>每 tick 回调（frame 为帧号，从 1 开始）。</summary>
    public event Action<long>? OnTick;

    /// <summary>当前帧号。</summary>
    public long CurrentFrame => Volatile.Read(ref frame);

    public int Hertz => hertz;

    public TickEngine(int hertz = 20)
    {
        this.hertz = hertz > 0 ? hertz : 20;
    }

    /// <summary>启动引擎（后台线程）。</summary>
    public void Start()
    {
        lock (gate)
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"TickEngine-{hertz}Hz"
            };
            thread.Start();
            Log.Info($"TickEngine 启动 ({hertz}Hz)");
        }
    }

    /// <summary>停止引擎并等待线程退出。</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
        }
        thread?.Join(TimeSpan.FromSeconds(2));
        thread = null;
        Log.Info("TickEngine 已停止");
    }

    /// <summary>
    /// 添加一次性/周期定时器（在 tick 线程内回调）。
    /// </summary>
    /// <param name="intervalMs">间隔毫秒</param>
    /// <param name="callback">回调</param>
    /// <param name="repeat">true=周期执行，false=一次执行</param>
    /// <returns>定时器句柄（可用 Cancel 取消）</returns>
    public TimerHandle AddTimer(int intervalMs, Action callback, bool repeat = false)
    {
        long due = CurrentFrame + (long)Math.Ceiling(intervalMs / 1000.0 * hertz);
        var handle = new TimerHandle(this, intervalMs, callback, repeat);
        lock (timers)
        {
            handle.ConfigureNextDue(due, timerSeq++);
            timers.Enqueue((due, handle.Entry.seq, handle), due);
        }
        return handle;
    }

    internal void Requeue(TimerHandle handle)
    {
        lock (timers)
        {
            handle.ConfigureNextDue(handle.Entry.dueTick, timerSeq++);
            timers.Enqueue((handle.Entry.dueTick, handle.Entry.seq, handle), handle.Entry.dueTick);
        }
    }

    private void Loop()
    {
        int intervalMs = Math.Max(1, 1000 / hertz);
        var next = Environment.TickCount64;

        while (running)
        {
            long f = Interlocked.Increment(ref frame);
            TickOnce(f);

            next += intervalMs;
            long delay = next - Environment.TickCount64;
            if (delay > 0)
            {
                Thread.Sleep((int)delay);
            }
            else
            {
                next = Environment.TickCount64; // 落后时重新对齐，避免忙等
            }
        }
    }

    internal void TickOnce(long currentFrame)
    {
        // 到期定时器（锁内只出队，锁外执行回调；周期定时器由 handle 重新入队）
        while (true)
        {
            TimerHandle? handle = null;
            lock (timers)
            {
                if (timers.Count == 0 || timers.Peek().dueTick > currentFrame)
                {
                    break;
                }
                handle = timers.Dequeue().handle;
            }
            try
            {
                handle?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TickEngine 定时器回调异常");
            }
        }

        try
        {
            OnTick?.Invoke(currentFrame);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TickEngine OnTick 异常");
        }
    }
}

/// <summary>定时器句柄：取消后回调不再执行。</summary>
public sealed class TimerHandle
{
    private readonly TickEngine engine;
    private readonly int intervalMs;
    private readonly Action callback;
    private readonly bool repeat;
    private long nextDueTick;
    private long seq;
    private volatile bool active = true;

    internal TimerHandle(TickEngine engine, int intervalMs, Action callback, bool repeat)
    {
        this.engine = engine;
        this.intervalMs = intervalMs;
        this.callback = callback;
        this.repeat = repeat;
    }

    public bool IsActive => active;

    internal void Invoke()
    {
        if (!active)
        {
            return;
        }
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            Framework.Core.Log.Error(ex, "TimerHandle 回调异常");
        }

        if (repeat && active)
        {
            nextDueTick = engine.CurrentFrame + (long)Math.Ceiling(intervalMs / 1000.0 * engine.Hertz);
            engine.Requeue(this);
        }
    }

    /// <summary>取消定时器。</summary>
    public void Cancel() => active = false;

    internal void ConfigureNextDue(long dueTick, long seq)
    {
        nextDueTick = dueTick;
        this.seq = seq;
    }

    internal (long dueTick, long seq) Entry => (nextDueTick, seq);
}
