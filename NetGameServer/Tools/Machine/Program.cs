using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Machine;

/// <summary>
/// Machine 进程（对标 KBE machine/kbengine.xml，迭代 20）：
/// 读 topology.json 描述的节点拓扑，按依赖顺序拉起各节点进程，
/// 给每个实例注入 --node-id / --port / --instance-id / --machine-id / --supervised-by。
/// 进程崩溃按指数退避重启（与 Supervisor 一致策略）；启动时按依赖 + TCP 探针等待前置节点就绪。
/// 状态输出机器可读标记（START/RESTART/EXIT_OK/PROBE_OK/SUMMARY）便于 MachineVerify 断言。
/// 用法：Machine --config machine.json [--test-duration 秒]（测试模式到时自动停机汇总）。
/// 注意（T2）：Machine 与旧版 Supervisor 互斥——同一批 game 进程只能由二者之一托管，
/// 禁止同时运行；--emit-supervisor-config 仅用于显式切换到老 Supervisor 路径时生成其配置。
/// </summary>
public static class Program
{
    public sealed class Topology
    {
        [JsonPropertyName("machineId")]
        public string MachineId { get; set; } = "machine-A";

        [JsonPropertyName("supervisedBy")]
        public string SupervisedBy { get; set; } = "machine";

        [JsonPropertyName("logDirectory")]
        public string? LogDirectory { get; set; }

        [JsonPropertyName("restartDelayMs")]
        public int RestartDelayMs { get; set; } = 2000;

        [JsonPropertyName("maxRestartsPerMinute")]
        public int MaxRestartsPerMinute { get; set; } = 10;

        [JsonPropertyName("probeTimeoutMs")]
        public int ProbeTimeoutMs { get; set; } = 30000;

        [JsonPropertyName("probeIntervalMs")]
        public int ProbeIntervalMs { get; set; } = 200;

        [JsonPropertyName("nodes")]
        public List<NodeSpec> Nodes { get; set; } = new();
    }

    public sealed class NodeSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("args")]
        public List<string> Args { get; set; } = new();

        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("replicas")]
        public int Replicas { get; set; } = 1;

        [JsonPropertyName("portStep")]
        public int PortStep { get; set; } = 1;

        [JsonPropertyName("dependsOn")]
        public List<string> DependsOn { get; set; } = new();

        [JsonPropertyName("probe")]
        public ProbeSpec? Probe { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("restartDelayMs")]
        public int? RestartDelayMs { get; set; }
    }

    public sealed class ProbeSpec
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = "127.0.0.1";

        [JsonPropertyName("port")]
        public int Port { get; set; }
    }

    /// <summary>展开后的实例（replicas 展开成多条），含唯一 InstanceId / NodeId / Port。</summary>
    public sealed class InstanceSpec
    {
        public required NodeSpec Template { get; init; }
        public required string InstanceId { get; init; }   // "Battle-1#2"
        public required int InstanceIndex { get; init; }   // 0-based
        public required int EffectivePort { get; init; }
        public required string EffectiveHost { get; init; }
        public string GeneratedNodeId =>
            $"{Template.Type}-{EffectiveHost}:{EffectivePort}";
    }

    public sealed class ManagedInstance
    {
        public required InstanceSpec Spec { get; init; }
        public Process? Process;
        public int StartCount;
        public int RestartCount;
        public volatile bool Stopping;
        public string LogFile = string.Empty;
        public DateTime? LastStartedAtUtc;
        public DateTime? LastExitedAtUtc;
        public int? LastExitCode;
        public List<long> RestartTimestampsUtc = new(); // T2：分钟级重启限流时间戳（UTC Ticks）
        // 启动后是否完成过就绪探针（仅当 spec 含 probe 时有意义）
        public bool ProbeOk;
        // 是否对外可见"已就绪"（probe 通过或无需 probe + 进程已 START 过）
        public bool Ready;
    }

    public static async Task<int> Main(string[] args)
    {
        string configPath = "machine.json";
        int testDurationSeconds = 0;
        string? emitSupervisorConfigPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] == "--test-duration" && i + 1 < args.Length) testDurationSeconds = int.Parse(args[++i]);
            else if (args[i] == "--emit-supervisor-config" && i + 1 < args.Length) emitSupervisorConfigPath = args[++i];
        }

        if (emitSupervisorConfigPath != null)
        {
            return EmitSupervisorConfig(configPath, emitSupervisorConfigPath);
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[Machine] 配置不存在: {configPath}（可用 --config 指定；样例见 machine.sample.json）");
            return 2;
        }

        Topology topology;
        try
        {
            topology = System.Text.Json.JsonSerializer.Deserialize<Topology>(File.ReadAllText(configPath)) ?? new Topology();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Machine] 配置解析失败: {ex.Message}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(topology.LogDirectory))
        {
            Directory.CreateDirectory(topology.LogDirectory);
        }

        // 展开 replicas → 实例列表
        var instances = ExpandInstances(topology);
        var managed = instances.Select(spec => new ManagedInstance
        {
            Spec = spec,
            LogFile = string.IsNullOrWhiteSpace(topology.LogDirectory) ? string.Empty
                : Path.Combine(topology.LogDirectory, $"{spec.InstanceId}.log")
        }).ToList();

        Console.WriteLine($"[Machine] 启动 machineId={topology.MachineId} 托管实例数: {managed.Count} (supervisedBy={topology.SupervisedBy})");

        // 拓扑排序：按 dependsOn 拓扑排序生成启动顺序；同层并行启动
        var startOrder = TopoSortInstances(managed, topology);
        if (startOrder == null)
        {
            Console.WriteLine("[Machine] 拓扑存在循环依赖或引用未知节点，请检查 dependsOn");
            return 2;
        }

        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        // 启动 worker：每个 ready 信号触发后启动后续层
        _ = Task.Run(async () => await StartLoopAsync(managed, startOrder, topology, stopping.Token));

        if (testDurationSeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(testDurationSeconds), stopping.Token);
            }
            catch (OperationCanceledException) { }
            Console.WriteLine("[Machine] 测试模式结束，汇总:");
            foreach (var m in managed)
            {
                Console.WriteLine($"SUMMARY {m.Spec.InstanceId} starts={m.StartCount} restarts={m.RestartCount} ready={m.Ready}");
            }
            await StopAllAsync(managed);
            return 0;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("[Machine] 收到停止信号，正在关闭托管进程...");
        await StopAllAsync(managed);
        return 0;
    }

    private static List<InstanceSpec> ExpandInstances(Topology topology)
    {
        var list = new List<InstanceSpec>();
        foreach (var node in topology.Nodes.Where(n => n.Enabled))
        {
            int replicas = Math.Max(1, node.Replicas);
            for (int i = 0; i < replicas; i++)
            {
                int port = node.Port + i * node.PortStep;
                string host = string.IsNullOrEmpty(node.Host) ? "127.0.0.1" : node.Host;
                // 探针地址：spec 显式给定 > 默认按 effective host:port
                var probe = node.Probe;
                if (probe == null)
                {
                    probe = new ProbeSpec { Host = host, Port = port };
                }
                list.Add(new InstanceSpec
                {
                    Template = new NodeSpec
                    {
                        Name = node.Name,
                        Type = node.Type,
                        File = node.File,
                        Args = new List<string>(node.Args),
                        WorkingDirectory = node.WorkingDirectory,
                        Port = port,
                        Host = host,
                        Replicas = 1,
                        PortStep = node.PortStep,
                        DependsOn = new List<string>(node.DependsOn),
                        Probe = probe,
                        Enabled = true,
                        RestartDelayMs = node.RestartDelayMs
                    },
                    InstanceId = $"{node.Name}-{i + 1}",
                    InstanceIndex = i,
                    EffectivePort = port,
                    EffectiveHost = host
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 拓扑排序：返回按层（layer）组织的实例启动列表。
    /// 第 0 层 = 无依赖的实例（同时启动）；后续层 = 其依赖全部就绪的实例（ready 时启动）。
    /// 返回 null 表示拓扑存在循环依赖或引用未知节点。
    /// </summary>
    private static List<List<ManagedInstance>>? TopoSortInstances(
        List<ManagedInstance> managed, Topology topology)
    {
        // 实例名（依赖按 NodeSpec.Name 写）→ 实例组（同一 NodeSpec 可能多个 replicas）
        var byName = managed.GroupBy(m => m.Spec.Template.Name).ToDictionary(g => g.Key, g => g.ToList());

        // 检测未知节点引用
        var allNames = new HashSet<string>(byName.Keys);
        foreach (var node in topology.Nodes)
        {
            foreach (var dep in node.DependsOn)
            {
                if (!allNames.Contains(dep))
                {
                    Console.WriteLine($"[Machine] 节点 {node.Name} 引用未知依赖 {dep}");
                    return null;
                }
            }
        }

        // 按依赖深度分层
        var layers = new List<List<ManagedInstance>>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int maxIterations = byName.Count + 1;
        while (placed.Count < byName.Count && maxIterations-- > 0)
        {
            var layer = new List<ManagedInstance>();
            foreach (var (name, instances) in byName)
            {
                if (placed.Contains(name)) continue;
                var deps = topology.Nodes.First(n => n.Name == name).DependsOn;
                if (deps.All(d => placed.Contains(d)))
                {
                    layer.AddRange(instances);
                }
            }
            if (layer.Count == 0) return null; // 循环依赖
            foreach (var inst in layer) placed.Add(inst.Spec.Template.Name);
            layers.Add(layer);
        }
        return layers;
    }

    private static async Task StartLoopAsync(
        List<ManagedInstance> managed,
        List<List<ManagedInstance>> layers,
        Topology topology,
        CancellationToken stoppingToken)
    {
        // 把全部实例按层依次启动；层与层之间等待上一层所有实例 ready
        foreach (var layer in layers)
        {
            // 同层并行启动
            var startTasks = layer.Select(m => Task.Run(() => StartProcess(m, topology), stoppingToken));
            await Task.WhenAll(startTasks);

            // 等待同层 ready（带超时）
            await WaitForLayerReadyAsync(layer, topology, stoppingToken);
        }
    }

    private static void StartProcess(ManagedInstance managed, Topology topology)
    {
        managed.StartCount++;
        managed.LastStartedAtUtc = DateTime.UtcNow;

        var spec = managed.Spec.Template;
        // 注入命令行参数（顺序：用户 args → machine 注入）
        var argList = new List<string>(spec.Args);
        argList.Add("--port"); argList.Add(managed.Spec.EffectivePort.ToString());
        argList.Add("--host"); argList.Add(managed.Spec.EffectiveHost);
        argList.Add("--node-id"); argList.Add(managed.Spec.GeneratedNodeId);
        argList.Add("--instance-id"); argList.Add(managed.Spec.InstanceId);
        argList.Add("--machine-id"); argList.Add(topology.MachineId);
        argList.Add("--supervised-by"); argList.Add(topology.SupervisedBy);

        var psi = new ProcessStartInfo
        {
            FileName = spec.File,
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);

        bool captureOutput = !string.IsNullOrWhiteSpace(topology.LogDirectory);
        if (captureOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (captureOutput)
            {
                p.OutputDataReceived += (_, e) => AppendLog(managed, e.Data);
                p.ErrorDataReceived += (_, e) => AppendLog(managed, e.Data);
            }
            p.Exited += (_, _) => OnProcessExited(managed, topology);
            p.Start();
            // 安全修复（P1）：stderr 与 stdout 都必须启用异步读取，否则子进程写满 stderr 管道缓冲（64KB）会阻塞挂起，
            // 而进程仍存活、看护不会重启它 —— 恰好是报错最多的节点最容易踩中。
            if (captureOutput)
            {
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            managed.Process = p;
            Console.WriteLine($"START {managed.Spec.InstanceId} type={spec.Type} pid={p.Id} port={managed.Spec.EffectivePort} count={managed.StartCount}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Machine] {managed.Spec.InstanceId} 启动失败: {ex.Message}");
        }
    }

    private static void AppendLog(ManagedInstance managed, string? line)
    {
        if (line == null || string.IsNullOrEmpty(managed.LogFile)) return;
        try
        {
            File.AppendAllText(managed.LogFile, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static void OnProcessExited(ManagedInstance managed, Topology topology)
    {
        int exitCode = managed.Process?.ExitCode ?? -1;
        managed.LastExitCode = exitCode;
        managed.LastExitedAtUtc = DateTime.UtcNow;
        managed.Ready = false; // 退出后失活

        if (managed.Stopping)
        {
            Console.WriteLine($"[Machine] {managed.Spec.InstanceId} 已退出（停机中，不重启）");
            return;
        }

        if (exitCode == 0)
        {
            Console.WriteLine($"EXIT_OK {managed.Spec.InstanceId} code=0（正常退出，不重启）");
            return;
        }

        // 崩溃：指数退避重启
        managed.RestartCount++;

        // T2 修复：分钟级重启限流——1 分钟内重启次数达到上限则放弃自动重启（防崩溃-重启循环打满 CPU/日志）。
        int maxRestartsPerMinute = topology.MaxRestartsPerMinute > 0 ? topology.MaxRestartsPerMinute : 10;
        long nowUtcTicks = DateTime.UtcNow.Ticks;
        managed.RestartTimestampsUtc.RemoveAll(t => nowUtcTicks - t > TimeSpan.FromMinutes(1).Ticks);
        if (managed.RestartTimestampsUtc.Count >= maxRestartsPerMinute)
        {
            managed.Stopping = true;
            Console.WriteLine($"[Machine] {managed.Spec.InstanceId} 1 分钟内重启次数超限（≥{maxRestartsPerMinute}），放弃自动重启");
            return;
        }
        managed.RestartTimestampsUtc.Add(nowUtcTicks);

        int baseDelay = managed.Spec.Template.RestartDelayMs ?? topology.RestartDelayMs;
        int delay = Math.Min(baseDelay * (1 << Math.Min(managed.RestartCount, 5)), 30000);
        Console.WriteLine($"RESTART {managed.Spec.InstanceId} #{managed.RestartCount} code={exitCode} delay={delay}ms");

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            if (!managed.Stopping)
            {
                StartProcess(managed, topology);
            }
        });
    }

    private static async Task WaitForLayerReadyAsync(
        List<ManagedInstance> layer, Topology topology, CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(topology.ProbeTimeoutMs);
        while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            foreach (var m in layer)
            {
                if (m.Ready) continue;
                if (m.Process == null || m.Process.HasExited) continue;

                // 是否需要探针
                var probe = m.Spec.Template.Probe;
                if (probe == null)
                {
                    // 无探针：进程已 START 就算 ready
                    m.Ready = true;
                    m.ProbeOk = true;
                    Console.WriteLine($"READY {m.Spec.InstanceId} (no-probe)");
                    continue;
                }

                if (await TryProbeAsync(probe.Host, probe.Port, stoppingToken))
                {
                    m.Ready = true;
                    m.ProbeOk = true;
                    Console.WriteLine($"PROBE_OK {m.Spec.InstanceId} {probe.Host}:{probe.Port}");
                }
            }
            if (layer.All(m => m.Ready)) return;
            await Task.Delay(topology.ProbeIntervalMs, stoppingToken);
        }

        // 超时：仍未 ready 的实例输出 WARN（不致命，下游可能仍能工作）
        foreach (var m in layer.Where(x => !x.Ready))
        {
            Console.WriteLine($"[Machine] WARN 等待 {m.Spec.InstanceId} 就绪超时");
        }
    }

    private static async Task<bool> TryProbeAsync(string host, int port, CancellationToken stoppingToken)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(500, stoppingToken));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopAllAsync(List<ManagedInstance> managed)
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
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 把 machine topology 渲染成 supervisor.json（兼容老版 Supervisor 静态看护）：
    /// - replicas 展开成多条 process（Name = "{Type}-{i+1}"）
    /// - Args 仅透传 topology 中显式给的 args（机器不注入 --machine-id 等老 Supervisor 不识别的参数）
    /// - WorkingDirectory/LogDirectory/RestartDelayMs/MaxRestartsPerMinute 复用同一份配置
    /// 用于：暂时不想跑 Machine 但想用 topology 一份配置 → 自动生成 supervisor.json。
    /// </summary>
    private static int EmitSupervisorConfig(string machineConfigPath, string outPath)
    {
        if (!File.Exists(machineConfigPath))
        {
            Console.WriteLine($"[Machine] 配置不存在: {machineConfigPath}");
            return 2;
        }
        Topology topology;
        try
        {
            topology = System.Text.Json.JsonSerializer.Deserialize<Topology>(File.ReadAllText(machineConfigPath)) ?? new Topology();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Machine] 配置解析失败: {ex.Message}");
            return 2;
        }

        var instances = ExpandInstances(topology);
        var processes = instances.Select(inst => new
        {
            name = inst.InstanceId,
            file = inst.Template.File,
            args = string.Join(" ", inst.Template.Args
                .Concat(new[] {
                    $"--port {inst.EffectivePort}",
                    $"--host {inst.EffectiveHost}",
                    $"--node-id {inst.GeneratedNodeId}",
                    $"--instance-id {inst.InstanceId}",
                    $"--machine-id {topology.MachineId}",
                    $"--supervised-by supervisor"
                })
                .Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            workingDirectory = inst.Template.WorkingDirectory,
            enabled = true,
            restartDelayMs = inst.Template.RestartDelayMs
        });

        var supervisorConfig = new
        {
            logDirectory = topology.LogDirectory,
            restartDelayMs = topology.RestartDelayMs,
            maxRestartsPerMinute = topology.MaxRestartsPerMinute,
            processes
        };

        var json = System.Text.Json.JsonSerializer.Serialize(supervisorConfig, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(outPath, json);
        Console.WriteLine($"[Machine] 已生成 supervisor 兼容配置 → {outPath}（进程数: {instances.Count}）");
        return 0;
    }
}
