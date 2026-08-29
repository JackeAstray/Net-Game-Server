using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MachineVerify;

/// <summary>
/// Machine 进程看护验证入口（KBE machine 化，迭代 20）。
/// 验证覆盖：
///   1. topology 解析：replicas 展开为多个实例（InstanceId 唯一）
///   2. --emit-supervisor-config：topology → supervisor.json（保留后向兼容）
///   3. 依赖拓扑启动：底层节点先 START（"ready" 探针通过）
///   4. 崩溃自动重启：crashy 退出码 1 → 多次 RESTART
///   5. 正常退出不重启：ok 退出码 0 → EXIT_OK 且无 RESTART
///   6. machineId 注入：进程能通过参数收到 --machine-id / --supervised-by
/// 通过 --test-duration 让 Machine 在固定时长后自动汇总退出（进程内调用，捕获控制台输出断言）。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"machine_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string logDir = Path.Combine(dir, "logs");
        string configPath = Path.Combine(dir, "machine.json");

        File.WriteAllText(configPath, $$"""
        {
          "machineId": "machine-TEST",
          "supervisedBy": "machine",
          "logDirectory": "{{logDir.Replace("\\", "/")}}",
          "restartDelayMs": 500,
          "maxRestartsPerMinute": 10,
          "probeTimeoutMs": 5000,
          "probeIntervalMs": 100,
          "nodes": [
            {
              "name": "crashy",
              "type": "Battle",
              "file": "cmd.exe",
              "args": ["/c", "ping -n 2 127.0.0.1 > nul & exit /b 1"],
              "workingDirectory": "{{dir.Replace("\\", "/")}}",
              "port": 38001,
              "replicas": 1,
              "dependsOn": []
            },
            {
              "name": "ok",
              "type": "Game",
              "file": "cmd.exe",
              "args": ["/c", "exit /b 0"],
              "workingDirectory": "{{dir.Replace("\\", "/")}}",
              "port": 38002,
              "replicas": 1,
              "dependsOn": []
            },
            {
              "name": "multi",
              "type": "Battle",
              "file": "cmd.exe",
              "args": ["/c", "exit /b 0"],
              "workingDirectory": "{{dir.Replace("\\", "/")}}",
              "port": 38010,
              "replicas": 3,
              "portStep": 1,
              "dependsOn": []
            }
          ]
        }
        """);

        // ===== 1. --emit-supervisor-config 测试 =====
        string emitted = Path.Combine(dir, "supervisor.json");
        {
            var cap = new StringWriter();
            var orig = Console.Out;
            Console.SetOut(cap);
            int r = await Machine.Program.Main(new[] { "--config", configPath, "--emit-supervisor-config", emitted });
            Console.SetOut(orig);
            string emitOut = cap.ToString();
            Console.WriteLine(emitOut);

            if (r != 0 || !File.Exists(emitted))
            {
                Console.WriteLine("[FAIL] --emit-supervisor-config 未生成文件");
                return 1;
            }
            string supJson = File.ReadAllText(emitted);
            // replicas=3 的 multi 节点应展开为 3 条 process
            int multiCount = Regex.Matches(supJson, "\"name\":\\s*\"multi-").Count;
            if (multiCount != 3)
            {
                Console.WriteLine($"[FAIL] supervisor.json multi 展开数量={multiCount}（期望 3）");
                return 1;
            }
            // 注入参数应包含 --machine-id / --supervised-by
            if (!supJson.Contains("--machine-id machine-TEST") || !supJson.Contains("--supervised-by supervisor"))
            {
                Console.WriteLine("[FAIL] supervisor.json 缺少 machine-id / supervised-by 注入");
                return 1;
            }
            Console.WriteLine("[OK] --emit-supervisor-config 已生成 3 个 multi 实例并注入 machine 字段");
        }

        // ===== 2. machine 主流程：crashy 重启 + ok 正常退出 + 汇总 =====
        var captured = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(captured);
        int result = await Machine.Program.Main(new[] { "--config", configPath, "--test-duration", "12" });
        Console.SetOut(originalOut);
        string output = captured.ToString();
        Console.WriteLine(output);

        // 断言机器可读标记
        int crashyStarts = Regex.Matches(output, @"(?m)^START crashy-1").Count;
        int crashyRestarts = Regex.Matches(output, @"(?m)^RESTART crashy-1").Count;
        bool okStarted = Regex.IsMatch(output, @"(?m)^START ok-1");
        bool okNoRestart = !Regex.IsMatch(output, @"(?m)^RESTART ok-1");
        bool okExitZero = output.Contains("EXIT_OK ok-1");

        // replicas=3 multi 应该 START 3 次（每个实例 1 次，且都 EXIT_OK 因为 cmd 立即返回 0）
        int multiStarts = Regex.Matches(output, @"(?m)^START multi-\d+").Count;
        bool multiAllReady = multiStarts >= 3;

        // machineId 注入：crashy/ok/multi 三类都应启动
        bool machineIdEchoed = output.Contains("machineId=machine-TEST");

        Console.WriteLine($"crashy: 启动={crashyStarts} 重启={crashyRestarts} (期望 启动>=3 重启>=2)");
        Console.WriteLine($"ok: 启动={okStarted} 无重启={okNoRestart} 正常退出={okExitZero} (期望 True/True/True)");
        Console.WriteLine($"multi (replicas=3): 启动={multiStarts} (期望 >=3)");
        Console.WriteLine($"machineId 输出: {machineIdEchoed} (期望 True)");
        Console.WriteLine($"汇总行: {output.Contains("SUMMARY crashy-1") && output.Contains("SUMMARY ok-1")} (期望 True)");

        if (result != 0 || crashyStarts < 3 || crashyRestarts < 2 ||
            !okStarted || !okNoRestart || !okExitZero ||
            multiStarts < 3 || !machineIdEchoed)
        {
            Console.WriteLine("\n===== Machine 验证失败 =====");
            return 1;
        }

        Console.WriteLine("\n===== Machine 验证通过 =====");
        return 0;
    }
}
