using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.IO;

namespace Shared
{
    /// <summary>
    /// 配置帮助类（KBE-Gap-Review D9 升级）：
    /// - 启动时一次性构建 IConfigurationRoot
    /// - 提供节缓存（Key → 解析后对象），避免每次 GetConfig 都走配置树查找
    /// - 提供模板校验钩子（注册 IConfigValidator，GetConfig 时自动校验，校验失败抛 ConfigValidationException）
    /// - 提供热重载订阅（OnConfigChanged：节变更时回调并清缓存）
    /// - 默认通过 appsettings.json + 环境变量覆盖（与原版兼容）
    /// </summary>
    public static class ConfigHelper
    {
        public static IConfigurationRoot Configuration { get; }

        // 内存配置源：NodeLaunchArgs / 运行时环境变量覆盖入口（KBE machine 化，迭代 20）
        // 通过 OverrideFromCommandLine 写入，配置读取优先级最高（在 appsettings.json 之后追加，所以会覆盖）
        private static readonly ConcurrentDictionary<string, string?> _runtimeOverrides = new(StringComparer.OrdinalIgnoreCase);
        // 节缓存（Key → 解析后对象）。注意：值类型装箱到 object（与原版行为一致）。
        private static readonly ConcurrentDictionary<string, object?> _sectionCache = new(StringComparer.OrdinalIgnoreCase);
        // 节字符串缓存（避免装箱）
        private static readonly ConcurrentDictionary<string, string?> _stringCache = new(StringComparer.OrdinalIgnoreCase);
        // 已注册的模板校验器
        private static readonly ConcurrentDictionary<string, IConfigValidator> _validators = new(StringComparer.OrdinalIgnoreCase);
        // 热重载事件
        private static event Action<string, string?>? _onConfigChanged;

        static ConfigHelper()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                // 内存源放最后，优先级最高，Machine / NodeLaunchArgs 写入的覆盖生效
                .AddInMemoryCollection(_runtimeOverrides);

            Configuration = builder.Build();

            // 注册变更令牌 → 自动清缓存 + 触发回调
            ChangeToken.OnChange(
                () => Configuration.GetReloadToken(),
                () =>
                {
                    _sectionCache.Clear();
                    _stringCache.Clear();
                    Framework.Core.Log.Info("ConfigHelper 配置热重载，缓存已清空");
                    // 通用回调：把变更广播给所有注册者（按节 path）
                    _onConfigChanged?.Invoke("*", null);
                });
        }

        /// <summary>
        /// 写入单个运行时覆盖项（KBE machine 化，迭代 20）。该值会覆盖 appsettings.json 同名键，
        /// 但保留热重载语义：若 appsettings.json 中该键被改动且 _runtimeOverrides 未再覆盖，仍会被 appsettings 的最新值顶掉（仅在 Override 不存在的字段上）。
        /// 写入后清空缓存并触发 reload token。
        /// </summary>
        public static void SetRuntimeOverride(string key, string? value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (value == null)
            {
                _runtimeOverrides.TryRemove(key, out _);
            }
            else
            {
                _runtimeOverrides[key] = value;
            }
            _sectionCache.Clear();
            _stringCache.Clear();
            Configuration.Reload();
        }

        /// <summary>
        /// 从全局 Configuration 中获取指定配置节并将其绑定为类型 T 的实例。
        /// 结果按节 Key 缓存；节变更（reload）时缓存自动清空。
        /// 若该 Key 已注册 IConfigValidator，校验失败抛 ConfigValidationException。
        /// </summary>
        public static T? GetConfig<T>(string key)
        {
            if (_sectionCache.TryGetValue(key, out var cached))
            {
                return (T?)cached;
            }
            var value = Configuration.GetSection(key).Get<T>();
            if (_validators.TryGetValue(key, out var validator))
            {
                validator.Validate(key, value);
            }
            _sectionCache[key] = value;
            return value;
        }

        /// <summary>
        /// 获取指定配置键的字符串值（带缓存）。
        /// </summary>
        public static string? GetConfig(string key)
        {
            if (_stringCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var value = Configuration[key];
            _stringCache[key] = value;
            return value;
        }

        /// <summary>注册模板校验器（KBE-Gap-Review D9）。重复注册同 key 会覆盖。</summary>
        public static void RegisterValidator(string key, IConfigValidator validator)
        {
            _validators[key] = validator;
            // 注册后立即使缓存失效，强制下一次 GetConfig 走校验
            _sectionCache.TryRemove(key, out _);
        }

        /// <summary>注册热重载回调（KBE-Gap-Review D9：业务层可感知配置变更）。</summary>
        public static void OnConfigChanged(Action<string, string?> callback)
        {
            _onConfigChanged += callback;
        }

        /// <summary>手动清空缓存（测试用）。</summary>
        public static void ClearCache()
        {
            _sectionCache.Clear();
            _stringCache.Clear();
        }
    }

    /// <summary>配置模板校验器接口（KBE-Gap-Review D9）。</summary>
    public interface IConfigValidator
    {
        void Validate(string key, object? value);
    }

    /// <summary>必填字段校验器：value 不能为空引用。</summary>
    public sealed class NotNullValidator : IConfigValidator
    {
        public void Validate(string key, object? value)
        {
            if (value is null)
            {
                throw new ConfigValidationException($"配置 {key} 不能为空");
            }
        }
    }

    /// <summary>非空字符串校验器（专门为 string 配置设计）。</summary>
    public sealed class NotEmptyStringValidator : IConfigValidator
    {
        public void Validate(string key, object? value)
        {
            if (value is not string s || string.IsNullOrWhiteSpace(s))
            {
                throw new ConfigValidationException($"配置 {key} 必须是非空字符串");
            }
        }
    }

    /// <summary>范围校验器：IComparable 数值在 [min, max] 区间内。</summary>
    public sealed class RangeValidator<T> : IConfigValidator where T : IComparable<T>
    {
        private readonly T min;
        private readonly T max;
        public RangeValidator(T min, T max) { this.min = min; this.max = max; }
        public void Validate(string key, object? value)
        {
            if (value is T t)
            {
                if (t.CompareTo(min) < 0 || t.CompareTo(max) > 0)
                {
                    throw new ConfigValidationException($"配置 {key}={t} 超出范围 [{min}, {max}]");
                }
            }
        }
    }

    /// <summary>配置校验失败异常（KBE-Gap-Review D9）。</summary>
    public sealed class ConfigValidationException : System.Exception
    {
        public ConfigValidationException(string message) : base(message) { }
    }
}
