using Serilog;
using Serilog.Events;

namespace Framework.Core;

/// <summary>
/// 统一日志封装（Serilog 异步写入文件 + 控制台 Error）。
/// 这是底层框架的一部分，与游戏逻辑无关。
/// 性能约定：所有方法在日志级别被禁用时零成本返回（不格式化、不触发 LogSink），
/// 热路径调用请使用模板形式（Log.Debug("... {Field}", value)）而非字符串插值。
/// </summary>
public static class Log
{
    private static readonly object Sync = new();

    /// <summary>Verbose 级别是否启用（热路径日志守卫用）。</summary>
    public static bool IsVerboseEnabled => Serilog.Log.IsEnabled(LogEventLevel.Verbose);

    /// <summary>Debug 级别是否启用（热路径日志守卫用）。</summary>
    public static bool IsDebugEnabled => Serilog.Log.IsEnabled(LogEventLevel.Debug);

    /// <summary>Information 级别是否启用。</summary>
    public static bool IsInfoEnabled => Serilog.Log.IsEnabled(LogEventLevel.Information);

    /// <summary>
    /// 配置并初始化日志。进程启动时调用一次。
    /// </summary>
    /// <param name="enableConsoleLog">是否启用控制台输出（仅 Error 及以上）。</param>
    /// <param name="logFilePath">日志文件路径。</param>
    /// <param name="minimumLevel">最低日志级别（"Verbose"/"Debug"/"Information"/"Warning"/"Error"，默认 Information）。</param>
    public static void Configure(bool enableConsoleLog = true, string logFilePath = "logs/framework.log", string minimumLevel = "Information")
    {
        lock (Sync)
        {
            var level = ParseLevel(minimumLevel);
            var configuration = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .WriteTo.Async(a => a.File(logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true));

            if (enableConsoleLog)
            {
                configuration.WriteTo.Async(a => a.Console(restrictedToMinimumLevel: LogEventLevel.Error));
            }

            Serilog.Log.CloseAndFlush();
            Serilog.Log.Logger = configuration.CreateLogger();
        }
    }

    private static LogEventLevel ParseLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    /// <summary>
    /// 日志事件钩子（level, formattedMessage）：供日志聚合客户端（RemoteLogClient）订阅，
    /// 实现各服务器日志统一上报到 Logger 进程（对标 KBE logger）。
    /// </summary>
    public static event Action<string, string>? LogSink;

    public static void Trace(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Verbose)) return;
        Serilog.Log.Verbose(template, values);
        var sink = LogSink;
        if (sink != null) sink("TRACE", Format(template, values));
    }

    public static void Debug(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Debug)) return;
        Serilog.Log.Debug(template, values);
        var sink = LogSink;
        if (sink != null) sink("DEBUG", Format(template, values));
    }

    public static void Info(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Information)) return;
        Serilog.Log.Information(template, values);
        var sink = LogSink;
        if (sink != null) sink("INFO", Format(template, values));
    }

    public static void Warn(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Warning)) return;
        Serilog.Log.Warning(template, values);
        var sink = LogSink;
        if (sink != null) sink("WARN", Format(template, values));
    }

    /// <summary>Warn 的别名（业务层惯例使用 Warning）。</summary>
    public static void Warning(string template, params object?[] values) => Warn(template, values);

    public static void Error(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Error)) return;
        Serilog.Log.Error(template, values);
        var sink = LogSink;
        if (sink != null) sink("ERROR", Format(template, values));
    }

    public static void Error(Exception ex, string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Error)) return;
        Serilog.Log.Error(ex, template, values);
        var sink = LogSink;
        if (sink != null) sink("ERROR", $"{Format(template, values)} Exception:{ex.Message}");
    }

    public static void Fatal(string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Fatal)) return;
        Serilog.Log.Fatal(template, values);
        var sink = LogSink;
        if (sink != null) sink("FATAL", Format(template, values));
    }

    public static void Fatal(Exception ex, string template, params object?[] values)
    {
        if (!Serilog.Log.IsEnabled(LogEventLevel.Fatal)) return;
        Serilog.Log.Fatal(ex, template, values);
        var sink = LogSink;
        if (sink != null) sink("FATAL", $"{Format(template, values)} Exception:{ex.Message}");
    }

    /// <summary>关闭并刷新所有日志接收器（进程退出时调用）。</summary>
    public static void CloseAndFlush() => Serilog.Log.CloseAndFlush();

    private static readonly Serilog.Parsing.MessageTemplateParser TemplateParser = new();

    /// <summary>
    /// 把 Serilog 模板渲染为普通文本（供 LogSink/日志聚合）。
    /// P3 修复：模板是命名占位符风格（如 "{Field}"），原实现用 string.Format 无法解析命名占位符，
    /// 每次抛 FormatException 后回退原模板，导致聚合日志全是未替换的 {Field}。改用 Serilog 自身渲染，
    /// 支持命名占位符、{{ 转义与 :format 格式符。
    /// </summary>
    private static string Format(string template, object?[] values)
    {
        try
        {
            if (values.Length == 0)
            {
                return template;
            }

            var parsed = TemplateParser.Parse(template);
            var properties = new Dictionary<string, Serilog.Events.LogEventPropertyValue>();
            int index = 0;
            foreach (var token in parsed.Tokens)
            {
                if (token is Serilog.Parsing.PropertyToken pt && index < values.Length)
                {
                    properties[pt.PropertyName] = new Serilog.Events.ScalarValue(values[index]);
                    index++;
                }
            }
            return parsed.Render(properties);
        }
        catch
        {
            return template;
        }
    }
}
