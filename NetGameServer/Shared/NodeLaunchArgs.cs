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
///   --config &lt;string&gt;      备用：自定义配置文件路径（留给未来扩展）
/// 所有参数大小写敏感；不抛异常，未识别参数被忽略并输出 WARN，便于机器/手工混合启动。
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
            string? value = i + 1 < args.Length ? args[i + 1] : null;

            switch (key)
            {
                case "--port":
                    if (TryParseInt(value, out int port)) result.Port = port;
                    i++;
                    break;
                case "--host":
                    if (!string.IsNullOrEmpty(value)) result.Host = value;
                    i++;
                    break;
                case "--center-host":
                    if (!string.IsNullOrEmpty(value)) result.CenterHost = value;
                    i++;
                    break;
                case "--center-port":
                    if (TryParseInt(value, out int cp)) result.CenterPort = cp;
                    i++;
                    break;
                case "--node-id":
                    if (!string.IsNullOrEmpty(value)) result.NodeId = value;
                    i++;
                    break;
                case "--instance-id":
                    if (!string.IsNullOrEmpty(value)) result.InstanceId = value;
                    i++;
                    break;
                case "--machine-id":
                    if (!string.IsNullOrEmpty(value)) result.MachineId = value;
                    i++;
                    break;
                case "--supervised-by":
                    if (!string.IsNullOrEmpty(value)) result.SupervisedBy = value;
                    i++;
                    break;
                case "--config":
                    if (!string.IsNullOrEmpty(value)) result.ConfigPath = value;
                    i++;
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
    /// 把解析结果应用到环境变量（NETGAME_*）—— 这样下游代码（BattleServerApp/GameServerApp 等）
    /// 仍可通过 ConfigHelper.GetConfig 读出，无需改动现有初始化路径，零侵入。
    /// 注意：仅当命令行有显式提供时写入环境变量；未提供的字段保持 ConfigHelper 默认值。
    /// </summary>
    public static void ApplyToEnvironment(Parsed parsed)
    {
        if (parsed == null) return;
        if (parsed.Port.HasValue) Environment.SetEnvironmentVariable("NETGAME_NODE_PORT", parsed.Port.Value.ToString());
        if (!string.IsNullOrEmpty(parsed.Host)) Environment.SetEnvironmentVariable("NETGAME_NODE_HOST", parsed.Host);
        if (!string.IsNullOrEmpty(parsed.CenterHost)) Environment.SetEnvironmentVariable("NETGAME_CENTER_HOST", parsed.CenterHost);
        if (parsed.CenterPort.HasValue) Environment.SetEnvironmentVariable("NETGAME_CENTER_PORT", parsed.CenterPort.Value.ToString());
        if (!string.IsNullOrEmpty(parsed.NodeId)) Environment.SetEnvironmentVariable("NETGAME_NODE_ID", parsed.NodeId);
        if (!string.IsNullOrEmpty(parsed.InstanceId)) Environment.SetEnvironmentVariable("NETGAME_INSTANCE_ID", parsed.InstanceId);
        if (!string.IsNullOrEmpty(parsed.MachineId)) Environment.SetEnvironmentVariable("NETGAME_MACHINE_ID", parsed.MachineId);
        if (!string.IsNullOrEmpty(parsed.SupervisedBy)) Environment.SetEnvironmentVariable("NETGAME_SUPERVISED_BY", parsed.SupervisedBy);
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
