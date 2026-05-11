using Serilog;
using Serilog.Events;

namespace Shared
{
    /// <summary>
    /// 日志帮助类，提供对 Serilog 的封装
    /// </summary>
    public static class Log
    {
        private static bool enableConsoleLog = true;
        private static string logFilePath = "logs/log.txt";

        static Log()
        {
            ConfigureLogger();
        }

        /// <summary>
        /// 重新配置日志，允许设置是否输出到控制台以及日志文件路径
        /// </summary>
        /// <param name="enableConsoleLog">是否启用控制台输出</param>
        /// <param name="logFilePath">日志文件路径，默认为 logs/log.txt</param>
        public static void Configure(bool enableConsoleLog = true, string logFilePath = "logs/log.txt")
        {
            Log.enableConsoleLog = enableConsoleLog;
            Log.logFilePath = logFilePath;
            ConfigureLogger();
        }

        /// <summary>
        /// 为应用程序配置Serilog日志记录器，包括文件和可选的控制台日志记录。
        /// </summary>
        /// <remarks>
        /// 将最低日志级别设置为Debug，并将日志写入具有每日滚动功能的文件间隔。
        /// 如果启用了控制台日志记录，日志也会写入控制台。必须调用此方法在记录之前，确保正确的记录器初始化
        /// </remarks>
        private static void ConfigureLogger()
        {
            var configuration = new LoggerConfiguration()
                .MinimumLevel.Debug();

            // 将文件 sink 包装为异步写入，减少同步 I/O 阻塞的风险
            configuration.WriteTo.Async(a => a.File(logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true));

            // 控制台 sink 仅输出 Error 及以上级别，并使用异步写入以降低阻塞风险
            if (Log.enableConsoleLog)
            {
                configuration.WriteTo.Async(a => a.Console(restrictedToMinimumLevel: LogEventLevel.Error));
            }

            // 如果已存在活动的Logger，先刷新并关闭释放资源（避免重复配置导致文件被占用或线程泄漏）
            Serilog.Log.CloseAndFlush();
            Serilog.Log.Logger = configuration.CreateLogger();
        }

        /// <summary>
        /// 记录一条信息级别的日志
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        public static void Info(string messageTemplate) => Serilog.Log.Information(messageTemplate);

        /// <summary>
        /// 记录一条信息级别的日志，带有格式化的属性值
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        /// <param name="propertyValues">属性值数组</param>
        public static void Info(string messageTemplate, params object[] propertyValues) => Serilog.Log.Information(messageTemplate, propertyValues);

        /// <summary>
        /// 记录一条调试级别的日志
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        public static void Debug(string messageTemplate) => Serilog.Log.Debug(messageTemplate);

        /// <summary>
        /// 记录一条调试级别的日志，带有格式化的属性值
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        /// <param name="propertyValues">属性值数组</param>
        public static void Debug(string messageTemplate, params object[] propertyValues) => Serilog.Log.Debug(messageTemplate, propertyValues);

        /// <summary>
        /// 记录一条警告级别的日志
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        public static void Warning(string messageTemplate) => Serilog.Log.Warning(messageTemplate);

        /// <summary>
        /// 记录一条警告级别的日志，带有格式化的属性值
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        /// <param name="propertyValues">属性值数组</param>
        public static void Warning(string messageTemplate, params object[] propertyValues) => Serilog.Log.Warning(messageTemplate, propertyValues);

        /// <summary>
        /// 记录一条错误级别的日志
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        public static void Error(string messageTemplate) => Serilog.Log.Error(messageTemplate);

        /// <summary>
        /// 记录一条错误级别的日志，带有格式化的属性值
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        /// <param name="propertyValues">属性值数组</param>
        public static void Error(string messageTemplate, params object[] propertyValues) => Serilog.Log.Error(messageTemplate, propertyValues);

        /// <summary>
        /// 记录一条带有异常信息的错误级别的日志
        /// </summary>
        /// <param name="exception">异常对象</param>
        /// <param name="messageTemplate">消息模板</param>
        public static void Error(System.Exception exception, string messageTemplate) => Serilog.Log.Error(exception, messageTemplate);

        /// <summary>
        /// 记录一条致命级别的日志
        /// </summary>
        /// <param name="messageTemplate">消息模板</param>
        public static void Fatal(string messageTemplate) => Serilog.Log.Fatal(messageTemplate);

        /// <summary>
        /// 记录一条带有异常信息的致命级别的日志
        /// </summary>
        /// <param name="exception">异常对象</param>
        /// <param name="messageTemplate">消息模板</param>
        public static void Fatal(System.Exception exception, string messageTemplate) => Serilog.Log.Fatal(exception, messageTemplate);

        /// <summary>
        /// 关闭并刷新所有日志接收器
        /// </summary>
        public static void CloseAndFlush() => Serilog.Log.CloseAndFlush();
    }
}