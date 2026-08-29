namespace Framework.Core;

/// <summary>
/// Leader 选举（对标 KBE 主备高可用的基础）：
/// - 基于独占文件锁：同一节点目录下只有一个实例能持有锁成为 Leader
/// - 支持 Leader 心跳续约（持有锁的进程定期刷新时间戳）
/// - 备用实例（Standby）定期尝试抢占：Leader 故障（锁释放）后自动接管
/// 用途：Center 主备、跨进程单例服务等需要"同一时刻仅一个活跃实例"的场景。
/// </summary>
public sealed class LeaderElection : IDisposable
{
    private readonly string lockFilePath;
    private readonly string nodeId;
    private FileStream? lockStream;
    private readonly CancellationTokenSource cts = new();
    private Task? heartbeatTask;
    private volatile bool isLeader;
    private readonly object electionGate = new();
    private readonly List<Task> backgroundTasks = new(); // 受 electionGate 保护（V20：追踪全部心跳/抢占任务，Dispose 时统一回收）
    private bool disposed;

    /// <summary>当前实例是否为 Leader。</summary>
    public bool IsLeader => isLeader;

    /// <summary>节点标识（用于日志与健康接口）。</summary>
    public string NodeId => nodeId;

    /// <summary>选举状态变化事件（isLeader）。</summary>
    public event Action<bool>? LeadershipChanged;

    /// <param name="lockFilePath">锁文件路径（Leader 竞选通过独占文件锁实现）</param>
    /// <param name="nodeId">本节点标识</param>
    /// <param name="heartbeatIntervalMs">心跳续约间隔（默认 3000ms）</param>
    public LeaderElection(string lockFilePath, string nodeId, int heartbeatIntervalMs = 3000)
    {
        this.lockFilePath = lockFilePath;
        this.nodeId = nodeId;
        HeartbeatIntervalMs = heartbeatIntervalMs;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockFilePath))!);
        try
        {
            // 以独占写方式打开锁文件：成功即成为 Leader（FileShare.None 阻止其他实例打开）
            lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            isLeader = true;
            WriteLeaderMarker();
            heartbeatTask = Task.Run(HeartbeatLoopAsync);
            backgroundTasks.Add(heartbeatTask);
            Log.Info($"Leader 选举: {nodeId} 成为 Leader (锁: {lockFilePath})");
        }
        catch (IOException)
        {
            isLeader = false;
            Log.Info($"Leader 选举: {nodeId} 处于 Standby（锁被占用: {lockFilePath}）");
            heartbeatTask = Task.Run(StandbyLoopAsync);
            backgroundTasks.Add(heartbeatTask);
        }
    }

    public int HeartbeatIntervalMs { get; }

    private void WriteLeaderMarker()
    {
        if (lockStream == null) return;
        lockStream.SetLength(0);
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{nodeId}|{DateTime.UtcNow:O}");
        lockStream.Write(bytes);
        lockStream.Flush();
    }

    /// <summary>Leader 心跳：定期续写锁文件（保活）。</summary>
    private async Task HeartbeatLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, cts.Token);
                if (!isLeader)
                {
                    // V20 修复：已让出/被替换的心跳自行退出，避免多个心跳任务长期并存
                    break;
                }
                if (lockStream != null)
                {
                    WriteLeaderMarker();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Leader 心跳异常: {ex.Message}");
            }
        }
    }

    /// <summary>Standby 重试：Leader 故障后自动接管。</summary>
    private async Task StandbyLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, cts.Token);
                if (isLeader) continue;

                lock (electionGate)
                {
                    if (isLeader) continue;
                    // 尝试抢占（原 Leader 崩溃后锁自动释放）
                    var fs = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    // 持有锁：把底层句柄保存，替代原 lockStream
                    lockStream?.Dispose();
                    lockStream = fs;
                    isLeader = true;
                    WriteLeaderMarker();
                    Log.Info($"Leader 选举: {nodeId} 接管成为 Leader（原 Leader 已下线）");
                    LeadershipChanged?.Invoke(true);
                    // V20 修复：追踪接管后启动的心跳任务（Dispose 时统一回收）
                    var takeoverHeartbeat = Task.Run(HeartbeatLoopAsync);
                    backgroundTasks.Add(takeoverHeartbeat);
                }
            }
            catch (IOException)
            {
                // 锁仍被占用：继续等待
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Standby 抢占异常: {ex.Message}");
            }
        }
    }

    /// <summary>主动释放 Leader 身份（优雅降级）。</summary>
    public void StepDown()
    {
        if (isLeader)
        {
            lock (electionGate)
            {
                isLeader = false;
                lockStream?.Dispose();
                lockStream = null;
            }
            LeadershipChanged?.Invoke(false);
            Log.Info($"Leader 选举: {nodeId} 主动让出 Leader");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        StepDown();
        // V20 修复：短暂等待后台心跳/抢占任务退出（最多 2s），避免遗留任务持有 FileStream
        Task[] tasks;
        lock (electionGate)
        {
            tasks = backgroundTasks.ToArray();
        }
        try
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 任务取消导致的异常忽略
        }
    }
}
