using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Shared
{
    public static class RedisHelper
    {
    private static readonly object InitGate = new();
    private static Lazy<ConnectionMultiplexer>? lazyConnection;

    /// <summary>
    /// 使用提供的连接字符串配置一个延迟初始化的 ConnectionMultiplexer 实例，用于后续按需建立与 Redis 的连接。
    /// </summary>
    /// <remarks>
    /// 连接在首次访问 lazyConnection.Value 时建立；建立过程中可能抛出异常。
    /// 修复：再次调用 Initialize 时先安全释放旧连接（避免 ConnectionMultiplexer 句柄泄漏），
    /// 适用于配置热重载场景。
    /// </remarks>
    /// <param name="connectionString">Redis 连接字符串，用于在首次访问时通过 ConnectionMultiplexer.Connect 建立连接。</param>
    public static void Initialize(string connectionString)
    {
        lock (InitGate)
        {
            // 安全修复：先释放旧连接（如果已初始化），避免连接句柄泄漏
            if (lazyConnection is { IsValueCreated: true } oldLazy)
            {
                try
                {
                    oldLazy.Value.Close(allowCommandsToComplete: true);
                    oldLazy.Value.Dispose();
                }
                catch
                {
                    // 释放失败不阻塞重新初始化
                }
            }
            lazyConnection = new Lazy<ConnectionMultiplexer>(
                () => ConnectionMultiplexer.Connect(connectionString),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

        public static ConnectionMultiplexer Connection => lazyConnection?.Value
            ?? throw new InvalidOperationException("Redis 未初始化, 请先调用 RedisHelper.Initialize()");

        public static IDatabase GetDatabase() => Connection.GetDatabase();

        /// <summary>
        /// 异步将指定键的字符串值写入数据库，可选择设置过期时间。
        /// </summary>
        /// <remarks>如果 expiry 为 null，则不设置过期时间；内部通过 GetDatabase().StringSetAsync 执行写入。</remarks>
        /// <param name="key">要写入的键。</param>
        /// <param name="value">要写入的字符串值。</param>
        /// <param name="expiry">可选的过期时间；为 null 表示不设置过期。</param>
        /// <returns>表示写入操作完成的异步任务。</returns>
        public static async Task SetAsync(string key, string value, TimeSpan? expiry = null)
        {
            var db = GetDatabase();
            if (expiry.HasValue)
            {
                await db.StringSetAsync(key, value, expiry.Value);
            }
            else
            {
                await db.StringSetAsync(key, value);
            }
        }

        /// <summary>
        /// 异步从默认数据库检索指定键对应的字符串值；若键不存在或无值则返回 null。
        /// </summary>
        /// <param name="key">要检索的键。</param>
        /// <returns>表示异步操作的任务，结果为键对应的字符串值；若不存在则为 null。</returns>
        public static async Task<string?> GetAsync(string key)
        {
            var db = GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }

        /// <summary>
        /// 异步删除指定键并返回删除是否成功。
        /// </summary>
        /// <remarks>通过 GetDatabase 获取数据库实例并调用 KeyDeleteAsync 执行异步删除。</remarks>
        /// <param name="key">要删除的键。</param>
        /// <returns>如果键已被删除则返回 true；否则返回 false。</returns>
        public static async Task<bool> DeleteAsync(string key)
        {
            var db = GetDatabase();
            return await db.KeyDeleteAsync(key);
        }
    }
}