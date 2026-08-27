using System.Diagnostics;

namespace Supervisor;

/// <summary>
/// 轻量进程看护（对标 KBE machine/watchdog）：
/// - 按 JSON 配置启动/托管服务器进程，进程异常退出（code != 0）自动重启（指数退避，上限 30s）
/// - 正常退出（code == 0）不重启（视为主动停机）
/// - 输出带机器可读标记（START/RESTART/EXIT_OK/SUMMARY），便于验证套件断言
/// 用法：Supervisor --config supervisor.json [--test-duration 秒]（测试模式：到时自动停机关闭子进程）
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

    private sealed class ManagedProcess
    {
        public required ProcessConfig Config { get; init; }
        public Process? Process;
        public int StartCount;
        public int RestartCount;
        public volatile bool Stopping;
        public string LogFile = string.Empty;
    }

    public static async Task<int> Main(string[] args)
    {
        string configPath = "supervisor.json";
        int testDurationSeconds = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--test-duration" && i + 1 < args.Length) testDurationSeconds = int.Parse(args[++i]);
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

    private static void StartProcess(ManagedProcess managed, SupervisorConfig config)
    {
        managed.StartCount++;
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
            managed.LogFile = Path.Combine(config.LogDirectory, $"{managed.Config.Name}.log");
        }

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (captureOutput)
            {
                p.OutputDataReceived += (_, e) => AppendLog(managed, e.Data);
                p.ErrorDataReceived += (_, e) => AppendLog(managed, e.Data);
            }
            p.Exited += (_, _) => OnProcessExited(managed, config);
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

    private static void AppendLog(ManagedProcess managed, string? line)
    {
        if (line == null) return;
        try
        {
            File.AppendAllText(managed.LogFile, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void OnProcessExited(ManagedProcess managed, SupervisorConfig config)
    {
        int exitCode = managed.Process?.ExitCode ?? -1;
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
        int baseDelay = managed.Config.RestartDelayMs ?? config.RestartDelayMs;
        int delay = Math.Min(baseDelay * (1 << Math.Min(managed.RestartCount, 5)), 30000);
        Console.WriteLine($"RESTART {managed.Config.Name} #{managed.RestartCount} code={exitCode} delay={delay}ms");

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            if (!managed.Stopping)
            {
                StartProcess(managed, config);
            }
        });
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
        await Task.CompletedTask;
    }
}
