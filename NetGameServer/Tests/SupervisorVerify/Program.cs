using System.Text.RegularExpressions;

namespace SupervisorVerify;

/// <summary>
/// Supervisor 进程看护验证入口。
/// 覆盖：crashy 进程被自动重启（RESTART 标记）+ ok 进程正常退出不重启（EXIT_OK 标记）。
/// 通过 --test-duration 让 Supervisor 在固定时长后自动汇总退出（进程内调用，捕获控制台输出断言）。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        // ===== Supervisor 进程看护验证 =====
        // 1. crashy：cmd 延迟约 1 秒后退出码 1 → 应被反复自动重启（RESTART 标记）
        // 2. ok：立即以退出码 0 结束 → 应正常退出且不重启（EXIT_OK 标记，无 RESTART）

        string dir = Path.Combine(Path.GetTempPath(), $"supervisor_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string configPath = Path.Combine(dir, "supervisor.json");
        File.WriteAllText(configPath, $$"""
        {
          "LogDirectory": "{{dir.Replace("\\", "/")}}/logs",
          "RestartDelayMs": 500,
          "Processes": [
            { "Name": "crashy", "File": "cmd.exe", "Args": "/c ping -n 2 127.0.0.1 > nul & exit /b 1", "WorkingDirectory": "{{dir.Replace("\\", "/")}}" },
            { "Name": "ok", "File": "cmd.exe", "Args": "/c exit /b 0", "WorkingDirectory": "{{dir.Replace("\\", "/")}}" }
          ]
        }
        """);

        var captured = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(captured);
        int result = await Supervisor.Program.Main(new[] { "--config", configPath, "--test-duration", "12" });
        Console.SetOut(originalOut);
        string output = captured.ToString();
        Console.WriteLine(output);

        // 断言（机器可读标记；^ 锚定行首避免 RESTART 行误计入 START）
        int crashyStarts = Regex.Matches(output, @"(?m)^START crashy").Count;
        int crashyRestarts = Regex.Matches(output, @"(?m)^RESTART crashy").Count;
        bool okStarted = Regex.IsMatch(output, @"(?m)^START ok");
        bool okNoRestart = !Regex.IsMatch(output, @"(?m)^RESTART ok");
        bool okExitZero = output.Contains("EXIT_OK ok");
        bool summaryPresent = output.Contains("SUMMARY crashy") && output.Contains("SUMMARY ok");

        Console.WriteLine($"crashy: 启动={crashyStarts} 重启={crashyRestarts} (期望 启动>=3 重启>=2)");
        Console.WriteLine($"ok: 启动={okStarted} 无重启={okNoRestart} 正常退出标记={okExitZero} (期望 True/True/True)");
        Console.WriteLine($"汇总行: {summaryPresent} (期望 True)");

        if (result != 0 || crashyStarts < 3 || crashyRestarts < 2 || !okStarted || !okNoRestart || !okExitZero || !summaryPresent)
        {
            Console.WriteLine("\n===== Supervisor 验证失败 =====");
            return 1;
        }

        Console.WriteLine("\n===== Supervisor 验证通过 =====");
        return 0;
    }
}
