using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Shared
{
    public static class RedisHelper
    {
        private static Lazy<ConnectionMultiplexer>? lazyConnection;

        // 初始化 Redis，建议在程序启动时 (如 Main 方法中) 传入连接字符串
        public static void Initialize(string connectionString)
        {
            lazyConnection = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(connectionString));
        }

        public static ConnectionMultiplexer Connection => lazyConnection?.Value 
            ?? throw new InvalidOperationException("Redis 未初始化, 请先调用 RedisHelper.Initialize()");

        public static IDatabase GetDatabase() => Connection.GetDatabase();

        /// <summary>
        /// 写入缓存
        /// </summary>
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
        /// 读取缓存
        /// </summary>
        public static async Task<string?> GetAsync(string key)
        {
            var db = GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }

        /// <summary>
        /// 删除缓存
        /// </summary>
        public static async Task<bool> DeleteAsync(string key)
        {
            var db = GetDatabase();
            return await db.KeyDeleteAsync(key);
        }
    }
}