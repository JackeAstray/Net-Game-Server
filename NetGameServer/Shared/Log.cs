namespace Shared
{
    /// <summary>
    /// 日志门面（业务层入口）——统一转发到 Framework.Core.Log 单一配置源。
    ///
    /// 设计说明（对标迭代 8 三-7 修正）：
    /// 历史上 Shared.Log 与 Framework.Core.Log 各自 CloseAndFlush + 重设全局 Serilog Logger，
    /// 谁后配置谁生效（文件路径还不同：logs/log.txt vs logs/framework.log），输出不稳定；
    /// 且 Shared.Log 的静态构造函数会在任意代码首次触碰时把全局 Logger 重置回默认路径，
    /// 静默覆盖进程启动时已配置好的 Logger。
    /// 本类现在是纯转发门面：不持有配置、不重设 Logger，所有调用转发到 Framework.Core.Log。
    /// 每个进程只需在启动时调用一次 Configure（业务层入口 Program 均调用本类的 Configure）。
    /// 转发同时修复了业务层日志不触发 LogSink（远程聚合）的缺口——Framework.Core.Log 的
    /// LogSink 事件现在对业务日志同样生效。
    /// </summary>
    public static class Log
    {
        /// <summary>Debug 级别是否启用（热路径日志守卫用）。</summary>
        public static bool IsDebugEnabled => Framework.Core.Log.IsDebugEnabled;

        /// <summary>Verbose 级别是否启用（热路径日志守卫用）。</summary>
        public static bool IsVerboseEnabled => Framework.Core.Log.IsVerboseEnabled;

        /// <summary>Information 级别是否启用。</summary>
        public static bool IsInfoEnabled => Framework.Core.Log.IsInfoEnabled;

        /// <summary>
        /// 配置并初始化日志（转发到单一配置源）。进程启动时调用一次。
        /// </summary>
        /// <param name="enableConsoleLog">是否启用控制台输出（仅 Error 及以上）。</param>
        /// <param name="logFilePath">日志文件路径。</param>
        /// <param name="minimumLevel">最低日志级别（"Verbose"/"Debug"/"Information"/"Warning"/"Error"，默认 Information）。</param>
        public static void Configure(bool enableConsoleLog = true, string logFilePath = "logs/log.txt", string minimumLevel = "Information")
            => Framework.Core.Log.Configure(enableConsoleLog, logFilePath, minimumLevel);

        /// <summary>记录一条信息级别的日志。</summary>
        public static void Info(string messageTemplate)
            => Framework.Core.Log.Info(messageTemplate);

        /// <summary>记录一条信息级别的日志，带有格式化属性值。</summary>
        public static void Info(string messageTemplate, params object[] propertyValues)
            => Framework.Core.Log.Info(messageTemplate, propertyValues);

        /// <summary>记录一条调试级别的日志。</summary>
        public static void Debug(string messageTemplate)
            => Framework.Core.Log.Debug(messageTemplate);

        /// <summary>记录一条调试级别的日志，带有格式化属性值。</summary>
        public static void Debug(string messageTemplate, params object[] propertyValues)
            => Framework.Core.Log.Debug(messageTemplate, propertyValues);

        /// <summary>记录一条警告级别的日志。</summary>
        public static void Warning(string messageTemplate)
            => Framework.Core.Log.Warning(messageTemplate);

        /// <summary>记录一条警告级别的日志，带有格式化属性值。</summary>
        public static void Warning(string messageTemplate, params object[] propertyValues)
            => Framework.Core.Log.Warning(messageTemplate, propertyValues);

        /// <summary>Warn 别名。</summary>
        public static void Warn(string messageTemplate, params object[] propertyValues)
            => Framework.Core.Log.Warn(messageTemplate, propertyValues);

        /// <summary>记录一条错误级别的日志。</summary>
        public static void Error(string messageTemplate)
            => Framework.Core.Log.Error(messageTemplate);

        /// <summary>记录一条错误级别的日志，带有格式化属性值。</summary>
        public static void Error(string messageTemplate, params object[] propertyValues)
            => Framework.Core.Log.Error(messageTemplate, propertyValues);

        /// <summary>记录一条带异常的错误级别日志。</summary>
        public static void Error(System.Exception exception, string messageTemplate)
            => Framework.Core.Log.Error(exception, messageTemplate);

        /// <summary>记录一条致命级别的日志。</summary>
        public static void Fatal(string messageTemplate)
            => Framework.Core.Log.Fatal(messageTemplate);

        /// <summary>记录一条带异常的致命级别日志。</summary>
        public static void Fatal(System.Exception exception, string messageTemplate)
            => Framework.Core.Log.Fatal(exception, messageTemplate);

        /// <summary>关闭并刷新所有日志接收器。</summary>
        public static void CloseAndFlush() => Framework.Core.Log.CloseAndFlush();
    }
}
