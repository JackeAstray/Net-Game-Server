using System;
using System.Collections.Generic;

namespace Shared;

/// <summary>
/// 节点启动参数解析（迭代 20 Machine 化）：
/// 解析各节点进程命令行参数，把可被 machine / 运维侧覆盖的字段统一读取出来。
/// 约定（任意顺序、值可省略；未提供则保留 ConfigHelper 默认值）：
///   --port &lt;int&gt;          节点对外监听端口
///   --host &lt;string&gt;        节点对外监听地址
///   --center-host &lt;string&gt; Center 服务器地址
///   --center-port &lt;int&gt;    Center 服务器端口
///   --node-id &lt;string&gt;     节点 ID（默认按 "Type-Host:Port" 生成）
///   --instance-id &lt;string&gt; 节点实例 ID（machine 注入；同类型多实例时由 machine 分配）
///   --machine-id &lt;string&gt;  托管本节点的 Machine 进程 ID
///   --supervised-by &lt;string&gt; 托管方类型（"machine" / "supervisor" / "none"）
///   --config &lt;string&gt;      预留（当前**未被节点消费**：配置文件路径请走 appsettings.json / NG_ 环境变量）。
///                          提供该参数时会记入 Unknown 并告警，避免"以为换了配置文件"的静默误解。
/// 所有参数大小写敏感；不抛异常，未识别参数（含取值非法）被收集到 Unknown 并输出 WARN，便于机器/手工混合启动排查。
/// 取值规则：仅当下一个 token 不是另一个 `--开关` 时才消费它，避免 `--port --host x` 把 `--host` 当作 port 的值吞掉。
/// </summary>
public static class NodeLaunchArgs
{
    public sealed class Parsed
    {
        public int? Port { get; set; }
        public string? Host { get; set; }
        public string? CenterHost { get; set; }
        public int? CenterPort { get; set; }
        public string? NodeId { get; set; }
        public string? InstanceId { get; set; }
        public string? MachineId { get; set; }
        public string? SupervisedBy { get; set; }
        public string? ConfigPath { get; set; }
        public List<string> Unknown { get; } = new();

        public bool HasMachineInjection =>
            !string.IsNullOrEmpty(InstanceId) ||
            !string.IsNullOrEmpty(MachineId) ||
            !string.IsNullOrEmpty(SupervisedBy);
    }

    /// <summary>
    /// 解析命令行参数。参数数组可为空（手工启动走 ConfigHelper）。
    /// </summary>
    /// <param name="args">Main 入参（不含程序名）。</param>
    /// <returns>解析结果，未提供的字段为 null。</returns>
    public static Parsed Parse(string[]? args)
    {
        var result = new Parsed();
        if (args == null) return result;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];

            // P3 修复：取值前先判断"下一个 token 是不是另一个开关"。
            // 原实现无条件 `i++` 消费下一 token，`--port --host 1.2.3.4` 会把 `--host` 当 port 的值吞掉，
            // 同时 `1.2.3.4` 变成未知参数——宿主参数被静默丢弃（机器/手工混合启动且写错时难排查）。
            // 数组元素为 null 视为"值存在但为空"（与原行为一致，仍消费该槽位）。
            bool atEnd = i + 1 >= args.Length;
            string? value = atEnd ? null : args[i + 1];
            bool hasValue = !atEnd && !(value != null && value.StartsWith("--", StringComparison.Ordinal));

            switch (key)
            {
                case "--port":
                    if (TryParseInt(value, out int port)) result.Port = port;
                    else if (hasValue) result.Unknown.Add($"{key}={value}(非法整数)");
                    if (hasValue) i++;
                    break;
                case "--host":
                    if (!string.IsNullOrEmpty(value)) result.Host = value;
                    if (hasValue) i++;
                    break;
                case "--center-host":
                    if (!string.IsNullOrEmpty(value)) result.CenterHost = value;
                    if (hasValue) i++;
                    break;
                case "--center-port":
                    if (TryParseInt(value, out int cp)) result.CenterPort = cp;
                    else if (hasValue) result.Unknown.Add($"{key}={value}(非法整数)");
                    if (hasValue) i++;
                    break;
                case "--node-id":
                    if (!string.IsNullOrEmpty(value)) result.NodeId = value;
                    if (hasValue) i++;
                    break;
                case "--instance-id":
                    if (!string.IsNullOrEmpty(value)) result.InstanceId = value;
                    if (hasValue) i++;
                    break;
                case "--machine-id":
                    if (!string.IsNullOrEmpty(value)) result.MachineId = value;
                    if (hasValue) i++;
                    break;
                case "--supervised-by":
                    if (!string.IsNullOrEmpty(value)) result.SupervisedBy = value;
                    if (hasValue) i++;
                    break;
                case "--config":
                    if (!string.IsNullOrEmpty(value)) result.ConfigPath = value;
                    if (hasValue) i++;
                    // P3 修复：原实现解析后无人消费（Machine/Supervisor 使用各自的 --config 解析），
                    // 调用方传了却毫无效果。这里显式告警，避免"以为换了配置文件"的静默误解。
                    result.Unknown.Add("--config(当前未被节点消费，配置文件路径请走 appsettings.json / NG_ 环境变量)");
                    break;
                default:
                    result.Unknown.Add(key);
                    break;
            }
        }

        return result;
    }

    private static bool TryParseInt(string? s, out int value)
    {
        if (!string.IsNullOrEmpty(s) && int.TryParse(s, out int v))
        {
            value = v;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// 解析并应用：便捷方法，所有节点 Program.cs 一行调用。
    /// 写入运行时配置覆盖（ConfigHelper.SetRuntimeOverride），下游 GetConfig 自动读取。
    /// </summary>
    public static Parsed ParseAndApply(string[]? args)
    {
        var parsed = Parse(args);
        ApplyToConfigHelper(parsed);
        return parsed;
    }

    /// <summary>
    /// 把解析结果通过 ConfigHelper.SetRuntimeOverride 写进运行时配置源。
    /// 比 ApplyToEnvironment 更优：直接覆盖 ConfigHelper 节缓存，无环境变量污染。
    /// </summary>
    public static void ApplyToConfigHelper(Parsed parsed)
    {
        if (parsed == null) return;

        // 通用：节点对外端口/地址
        if (parsed.Port.HasValue) ConfigHelper.SetRuntimeOverride("NodePort", parsed.Port.Value.ToString());
        if (!string.IsNullOrEmpty(parsed.Host)) ConfigHelper.SetRuntimeOverride("NodeHost", parsed.Host);

        // 通用：Center 地址
        if (!string.IsNullOrEmpty(parsed.CenterHost)) ConfigHelper.SetRuntimeOverride("CenterHost", parsed.CenterHost);
        if (parsed.CenterPort.HasValue) ConfigHelper.SetRuntimeOverride("CenterPort", parsed.CenterPort.Value.ToString());

        // 节点身份
        if (!string.IsNullOrEmpty(parsed.NodeId)) ConfigHelper.SetRuntimeOverride("NodeId", parsed.NodeId);
        if (!string.IsNullOrEmpty(parsed.InstanceId)) ConfigHelper.SetRuntimeOverride("InstanceId", parsed.InstanceId);
        if (!string.IsNullOrEmpty(parsed.MachineId)) ConfigHelper.SetRuntimeOverride("MachineId", parsed.MachineId);
        if (!string.IsNullOrEmpty(parsed.SupervisedBy)) ConfigHelper.SetRuntimeOverride("SupervisedBy", parsed.SupervisedBy);

        // 按 NodeType 写一份对应配置键，便于各 ServerApp 既有路径不动直接读取（如 BattlePort）
        // 注：NodeType 由 Program.cs 在调用前显式注入到 Parsed.MachineId 之外的字段
        // 这里只覆盖通用键；类型端口（如 BattlePort）由各节点 Program.cs 在调用 ParseAndApply 后显式写回。
    }
}
