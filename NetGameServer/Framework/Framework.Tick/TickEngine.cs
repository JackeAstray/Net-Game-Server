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

    // 性能 Profile（对标 KBE perf 统计）：tick 耗时统计 + 慢 tick 告警
    private long lastTickMs;
    private long maxTickMs;
    private long totalTickMs;
    private long tickCount;
    private long lastSlowWarnTick;
    private int slowTickThresholdMs = 200;

    /// <summary>慢 tick 告警阈值（毫秒，默认 200；启动前设置）。</summary>
    public int SlowTickThresholdMs
    {
        get => slowTickThresholdMs;
        set => slowTickThresholdMs = Math.Max(1, value);
    }

    /// <summary>最近一次 tick 耗时（毫秒）。</summary>
    public long LastTickMs => Interlocked.Read(ref lastTickMs);

    /// <summary>自启动以来最大 tick 耗时（毫秒）。</summary>
    public long MaxTickMs => Interlocked.Read(ref maxTickMs);

    /// <summary>自启动以来平均 tick 耗时（毫秒）。</summary>
    public long AvgTickMs
    {
        get
        {
            long count = Interlocked.Read(ref tickCount);
            return count == 0 ? 0 : Interlocked.Read(ref totalTickMs) / count;
        }
    }

    // 定时器（由主线程独占访问，无需锁）。
    // P2-8 修复：定时器以单调时钟（Environment.TickCount64）毫秒为到期键，不再依赖帧号，
    // 避免过载时帧号快于墙钟导致定时器提前/连发。
    private readonly PriorityQueue<(long dueMs, long seq, TimerHandle handle), long> timers = new();
    private long timerSeq;

    // 跨线程投递队列（锁内入队，tick 线程独占消费——用于把非 tick 线程的实体访问迁移到 tick 线程，
    // 如 FSW 热更新线程的 OnReload 对实体属性/定时器的修改，避免与 tick 逻辑并发竞争）
    private readonly Queue<Action> postedActions = new();
    /// <summary>投递队列上限（D3 修复：防止无界增长）。</summary>
    private const int MaxPostedActions = 16384;

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
        // P2-8 修复：按单调时钟（而非帧号）计算到期时间，过载时定时器真实频率不漂移
        long dueMs = Environment.TickCount64 + Math.Max(1, intervalMs);
        var handle = new TimerHandle(this, intervalMs, callback, repeat);
        lock (timers)
        {
            handle.ConfigureNextDue(dueMs, timerSeq++);
            timers.Enqueue((dueMs, handle.Entry.seq, handle), dueMs);
        }
        return handle;
    }

    internal void Requeue(TimerHandle handle)
    {
        lock (timers)
        {
            handle.ConfigureNextDue(handle.Entry.dueMs, timerSeq++);
            timers.Enqueue((handle.Entry.dueMs, handle.Entry.seq, handle), handle.Entry.dueMs);
        }
    }

    /// <summary>
    /// 跨线程投递：把 action 排入队列，在下一个 tick 开始时于 tick 线程上执行（线程安全）。
    /// 用于把非 tick 线程（FSW/网络线程等）产生的、需要独占 tick 线程的实体操作迁移过来。
    /// 投递动作在定时器回调与 OnTick 之前执行，保证与帧内逻辑严格串行。
    /// </summary>
    public void Post(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        lock (postedActions)
        {
            if (postedActions.Count >= MaxPostedActions)
            {
                Framework.Core.Log.Warn($"TickEngine 跨线程投递队列已满，丢弃动作（上限 {MaxPostedActions}）");
                return;
            }
            postedActions.Enqueue(action);
        }
    }

    private void Loop()
    {
        int intervalMs = Math.Max(1, 1000 / hertz);
        var next = Environment.TickCount64;

        while (running)
        {
            long f = Interlocked.Increment(ref frame);
            long started = Environment.TickCount64;
            TickOnce(f);
            long elapsed = Environment.TickCount64 - started;

            // 耗时统计
            Interlocked.Exchange(ref lastTickMs, elapsed);
            Interlocked.Add(ref totalTickMs, elapsed);
            Interlocked.Increment(ref tickCount);
            long max = Interlocked.Read(ref maxTickMs);
            while (elapsed > max && Interlocked.CompareExchange(ref maxTickMs, elapsed, max) != max)
            {
                max = Interlocked.Read(ref maxTickMs);
            }

            // 慢 tick 告警（节流：5 秒最多一次，避免刷屏）
            if (elapsed > slowTickThresholdMs)
            {
                long now = Environment.TickCount64;
                long last = Volatile.Read(ref lastSlowWarnTick);
                if (now - last > 5000 && Interlocked.CompareExchange(ref lastSlowWarnTick, now, last) == last)
                {
                    Log.Warn($"TickEngine 慢 tick 帧号:{f} 耗时:{elapsed}ms 阈值:{slowTickThresholdMs}ms avg:{AvgTickMs}ms max:{MaxTickMs}ms");
                }
            }

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
        // 先执行跨线程投递的动作（tick 线程独占，与定时器/OnTick 严格串行）
        while (true)
        {
            Action? action = null;
            lock (postedActions)
            {
                if (postedActions.Count == 0)
                {
                    break;
                }
                action = postedActions.Dequeue();
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TickEngine 跨线程投递动作异常");
            }
        }

        // 到期定时器（锁内只出队，锁外执行回调；周期定时器由 handle 重新入队）。
        // P2-8：按单调时钟判定到期，而非帧号。
        while (true)
        {
            TimerHandle? handle = null;
            lock (timers)
            {
                long now = Environment.TickCount64;
                if (timers.Count == 0 || timers.Peek().dueMs > now)
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
    // 下次到期时间：单调时钟毫秒（P2-8 修复，替代原帧号语义）
    private long nextDueMs;
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
            // 周期定时器：下次到期 = 单调时钟 now + interval（不依赖帧号，过载不失真）
            nextDueMs = Environment.TickCount64 + Math.Max(1, intervalMs);
            engine.Requeue(this);
        }
    }

    /// <summary>取消定时器。</summary>
    public void Cancel() => active = false;

    internal void ConfigureNextDue(long dueMs, long seq)
    {
        nextDueMs = dueMs;
        this.seq = seq;
    }

    internal (long dueMs, long seq) Entry => (nextDueMs, seq);
}
