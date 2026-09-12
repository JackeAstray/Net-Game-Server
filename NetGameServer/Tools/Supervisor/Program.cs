using System.Diagnostics;

namespace Supervisor;

/// <summary>
/// 轻量进程看护（对标 KBE machine/watchdog）：
/// - 按 JSON 配置启动/托管服务器进程，进程异常退出（code != 0）自动重启（指数退避，上限 30s）
/// - 正常退出（code == 0）不重启（视为主动停机）
/// - 输出带机器可读标记（START/RESTART/EXIT_OK/SUMMARY），便于验证套件断言
/// 用法：Supervisor --config supervisor.json [--test-duration 秒]（测试模式：到时自动停机关闭子进程）
/// 注意（T2）：Supervisor 为旧版看护，与 Machine 互斥——同一批 game 进程只能由二者之一托管，
/// 禁止同时运行；请优先使用 Tools/Machine（topology 拓扑 + 依赖 + replicas + 探针就绪）。
/// </summary>
public static class Program
{
    public sealed class SupervisorConfig
    {
        public string? LogDirectory { get; set; }
        public int RestartDelayMs { get; set; } = 2000;
        public int MaxRestartsPerMinute { get; set; } = 10;
        public List<ProcessConfig> Processes { get; set; } = new();
    }

    public sealed class ProcessConfig
    {
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string? Args { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool Enabled { get; set; } = true;
        public int? RestartDelayMs { get; set; }
    }

    /// <summary>internal 供 HttpConsole 读取状态与控制指令。</summary>
    internal sealed class ManagedProcess
    {
        public required ProcessConfig Config { get; init; }
        public Process? Process;
        public int StartCount;
        public int RestartCount;
        public volatile bool Stopping;
        public string LogFile = string.Empty;
        /// <summary>持久日志 writer（进程生命周期内复用，避免每行日志开关文件）。</summary>
        public StreamWriter? LogWriter;
        public List<long> RestartTimestampsUtc = new(); // T2：分钟级重启限流时间戳（UTC Ticks）
    }

    public static async Task<int> Main(string[] args)
    {
        string configPath = "supervisor.json";
        int testDurationSeconds = 0;
        int httpPort = 31322;
        string? httpToken = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--test-duration" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedDuration)) testDurationSeconds = parsedDuration;
            else if (args[i] == "--http-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedHttpPort)) httpPort = parsedHttpPort;
            else if (args[i] == "--http-token" && i + 1 < args.Length) httpToken = args[++i];
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[Supervisor] 配置不存在: {configPath}（可用 --config 指定；样例见 supervisor.sample.json）");
            return 2;
        }

        SupervisorConfig config;
        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<SupervisorConfig>(File.ReadAllText(configPath)) ?? new SupervisorConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supervisor] 配置解析失败: {ex.Message}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(config.LogDirectory))
        {
            Directory.CreateDirectory(config.LogDirectory);
        }

        var managed = config.Processes.Where(p => p.Enabled).Select(p => new ManagedProcess { Config = p }).ToList();
        Console.WriteLine($"[Supervisor] 启动，托管进程数: {managed.Count}");

        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        foreach (var m in managed)
        {
            StartProcess(m, config);
        }

        // HTTP 控制台：本机可视化托管进程状态与控制（--http-port 0 禁用；测试模式不启动）
        if (testDurationSeconds <= 0 && httpPort > 0)
        {
            _ = Task.Run(async () => await HttpConsole.RunAsync(managed, config, httpPort, httpToken, stopping.Token));
        }

        if (testDurationSeconds > 0)
        {
            // 测试/CI 冒烟模式：到时自动汇总并关闭全部子进程
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(testDurationSeconds), stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
            Console.WriteLine("[Supervisor] 测试模式结束，汇总:");
            foreach (var m in managed)
            {
                Console.WriteLine($"SUMMARY {m.Config.Name} starts={m.StartCount} restarts={m.RestartCount}");
            }
            await StopAllAsync(managed);
            return 0;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("[Supervisor] 收到停止信号，正在关闭托管进程...");
        await StopAllAsync(managed);
        return 0;
    }

    /// <summary>internal 供 HttpConsole 手动启动/重启指令调用。</summary>
    internal static void StartProcess(ManagedProcess managed, SupervisorConfig config)
    {
        // P1 修复：实例级互斥——与 OnProcessExited/HttpConsole 控制指令共用锁，
        // 防并发启动双进程（孤儿进程）与计数错乱。
        lock (managed)
        {
            managed.StartCount++;
            // P1 修复：释放旧 Process 句柄（崩溃重启场景防句柄累积）
            var oldProc = managed.Process;
            if (oldProc != null)
            {
                try { oldProc.Dispose(); } catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = managed.Config.File,
                Arguments = managed.Config.Args ?? string.Empty,
                WorkingDirectory = managed.Config.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            bool captureOutput = !string.IsNullOrWhiteSpace(config.LogDirectory);
            if (captureOutput)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                managed.LogFile = Path.Combine(config.LogDirectory!, $"{managed.Config.Name}.log");
                // 重启时旧 writer 可能未关闭（防句柄泄漏），先关再开
                managed.LogWriter?.Dispose();
                Directory.CreateDirectory(config.LogDirectory!);
                managed.LogWriter = new StreamWriter(managed.LogFile, append: true) { AutoFlush = true };
            }

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (captureOutput)
                {
                    p.OutputDataReceived += (_, e) => AppendLog(managed, e.Data);
                    p.ErrorDataReceived += (_, e) => AppendLog(managed, e.Data);
                }
                // P1 修复：Exited 回调携带事件源进程，防止锁内读到已被新进程替换的 managed.Process
                p.Exited += (s, _) => OnProcessExited(s as Process, managed, config);
                p.Start();
                if (captureOutput)
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                managed.Process = p;
                Console.WriteLine($"START {managed.Config.Name} pid={p.Id} count={managed.StartCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Supervisor] {managed.Config.Name} 启动失败: {ex.Message}");
            }
        }
    }

    private static void AppendLog(ManagedProcess managed, string? line)
    {
        // P3 修复：未配置 LogFile（LogDirectory 为空）时直接跳过，避免每条日志都走 File.AppendAllText("") 抛异常再被静默吞掉。
        if (line == null || managed.LogWriter == null) return;
        try
        {
            managed.LogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        }
        catch
        {
        }
    }

    /// <summary>进程退出回调（P1 修复：与 StartProcess/控制指令共用实例锁；<paramref name="exited"/> 为事件源进程）。</summary>
    private static void OnProcessExited(Process? exited, ManagedProcess managed, SupervisorConfig config)
    {
        lock (managed)
        {
            int exitCode = -1;
            try { exitCode = exited?.ExitCode ?? -1; } catch { /* 事件源进程已被释放 */ }
            if (managed.Stopping)
            {
                Console.WriteLine($"[Supervisor] {managed.Config.Name} 已退出（停机中，不重启）");
                return;
            }

            if (exitCode == 0)
            {
                Console.WriteLine($"EXIT_OK {managed.Config.Name} code=0（正常退出，不重启）");
                return;
            }

            // 崩溃：指数退避重启（基础延迟 * 2^min(重启次数,5)，上限 30s）
            managed.RestartCount++;

            // T2 修复：分钟级重启限流——1 分钟内重启次数达到上限则放弃自动重启（防崩溃-重启循环打满 CPU/日志）。
            int maxRestartsPerMinute = config.MaxRestartsPerMinute > 0 ? config.MaxRestartsPerMinute : 10;
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            managed.RestartTimestampsUtc.RemoveAll(t => nowUtcTicks - t > TimeSpan.FromMinutes(1).Ticks);
            if (managed.RestartTimestampsUtc.Count >= maxRestartsPerMinute)
            {
                managed.Stopping = true;
                Console.WriteLine($"[Supervisor] {managed.Config.Name} 1 分钟内重启次数超限（≥{maxRestartsPerMinute}），放弃自动重启");
                return;
            }
            managed.RestartTimestampsUtc.Add(nowUtcTicks);

            int baseDelay = managed.Config.RestartDelayMs ?? config.RestartDelayMs;
            int delay = Math.Min(baseDelay * (1 << Math.Min(managed.RestartCount, 5)), 30000);
            Console.WriteLine($"RESTART {managed.Config.Name} #{managed.RestartCount} code={exitCode} delay={delay}ms");

            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                // 锁内启动：与 HTTP 控制指令互斥，防双进程
                lock (managed)
                {
                    if (!managed.Stopping)
                    {
                        StartProcess(managed, config);
                    }
                }
            });
        }
    }

    private static async Task StopAllAsync(List<ManagedProcess> managed)
    {
        foreach (var m in managed)
        {
            m.Stopping = true;
            var p = m.Process;
            if (p == null || p.HasExited) continue;
            try
            {
                if (!p.CloseMainWindow())
                {
                    p.Kill(entireProcessTree: true);
                }
                else if (!p.WaitForExit(3000))
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
        foreach (var m in managed)
        {
            m.LogWriter?.Flush();
            m.LogWriter?.Dispose();
            m.LogWriter = null;
        }
        await Task.CompletedTask;
    }
}
