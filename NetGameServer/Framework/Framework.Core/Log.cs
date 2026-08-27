using Serilog;
using Serilog.Events;

namespace Framework.Core;

/// <summary>
/// 统一日志封装（Serilog 异步写入文件 + 控制台 Error）。
/// 这是底层框架的一部分，与游戏逻辑无关。
/// </summary>
public static class Log
{
    private static readonly object Sync = new();

    /// <summary>
    /// 配置并初始化日志。进程启动时调用一次。
    /// </summary>
    public static void Configure(bool enableConsoleLog = true, string logFilePath = "logs/framework.log")
    {
        lock (Sync)
        {
            var configuration = new LoggerConfiguration()
                .MinimumLevel.Debug()
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

    /// <summary>
    /// 日志事件钩子（level, formattedMessage）：供日志聚合客户端（RemoteLogClient）订阅，
    /// 实现各服务器日志统一上报到 Logger 进程（对标 KBE logger）。
    /// </summary>
    public static event Action<string, string>? LogSink;

    public static void Trace(string template, params object?[] values)
    {
        Serilog.Log.Verbose(template, values);
        LogSink?.Invoke("TRACE", Format(template, values));
    }

    public static void Debug(string template, params object?[] values)
    {
        Serilog.Log.Debug(template, values);
        LogSink?.Invoke("DEBUG", Format(template, values));
    }

    public static void Info(string template, params object?[] values)
    {
        Serilog.Log.Information(template, values);
        LogSink?.Invoke("INFO", Format(template, values));
    }

    public static void Warn(string template, params object?[] values)
    {
        Serilog.Log.Warning(template, values);
        LogSink?.Invoke("WARN", Format(template, values));
    }

    public static void Error(string template, params object?[] values)
    {
        Serilog.Log.Error(template, values);
        LogSink?.Invoke("ERROR", Format(template, values));
    }

    public static void Error(Exception ex, string template, params object?[] values)
    {
        Serilog.Log.Error(ex, template, values);
        LogSink?.Invoke("ERROR", $"{Format(template, values)} Exception:{ex.Message}");
    }

    private static string Format(string template, object?[] values)
    {
        try
        {
            return values.Length == 0 ? template : string.Format(template, values);
        }
        catch
        {
            return template;
        }
    }
}
