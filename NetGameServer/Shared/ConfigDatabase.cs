using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Shared
{
    /// <summary>
    /// 配置数据库读取端 —— 与 ExcelToJson 工具（NetGameServer/Tools/ExcelToJson）的输出契约严格一致：
    /// - 读取工具生成的 JSON 数组文件（UTF-8，兼容带 BOM；工具产物带 EF BB BF 头）。
    /// - 用 Newtonsoft（与 Unity 客户端参考工具 ReunionMovement Editor/Excel 同一序列化器）反序列化。
    /// - 工具端与读取端共用同一序列化语义（Shared.Json），保证「生成 ↔ 读取」两端一致：
    ///   服务器各节点可加载 ExcelToJson 产出的同一份 JSON，与客户端读取的是同一份数据、同一种格式。
    /// </summary>
    public static class ConfigDatabase
    {
        // 缓存键必须包含**元素类型与索引形状**（P3 修复）：
        // 原实现仅以 path 为键，同一路径按不同类型加载（或 List 与索引混用）会互相覆盖 → 缓存抖动/失效。
        private static readonly ConcurrentDictionary<string, object?> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static string ListCacheKey<T>(string path) => path + "\u0001L\u0001" + typeof(T).FullName;

        /// <summary>
        /// 加载配置表为对象列表。
        /// 注意（契约）：useCache=true 时返回的是**缓存内的同一实例**，属运行时只读数据，
        /// 调用方**不得**排序/增删（会污染全局缓存）；确需修改请用 <see cref="LoadListCopy{T}"/>。
        /// </summary>
        /// <param name="path">ExcelToJson 产出的 .json 文件路径。</param>
        /// <param name="useCache">是否使用路径缓存（配置热更后可 ClearCache 强制重读）。</param>
        public static IReadOnlyList<T>? LoadList<T>(string path, bool useCache = true)
        {
            string key = ListCacheKey<T>(path);
            if (useCache && Cache.TryGetValue(key, out var cached) && cached is List<T> typed)
            {
                return typed;
            }

            var list = LoadCore<T>(path);
            if (useCache && list != null)
            {
                Cache[key] = list;
            }
            return list;
        }

        /// <summary>
        /// 加载配置表并返回**独立副本**（可安全排序/增删的调用方使用）。
        /// 不写缓存（缓存只存放主副本，避免两份数据长期驻留）。
        /// </summary>
        public static List<T>? LoadListCopy<T>(string path)
        {
            var cached = LoadList<T>(path, useCache: true);
            return cached == null ? null : new List<T>(cached);
        }

        /// <summary>加载配置表并按主键建立索引（如按 Id/Number 查），O(1) 访问。</summary>
        /// <param name="path">ExcelToJson 产出的 .json 文件路径。</param>
        /// <param name="keySelector">主键选择器（如 c =&gt; c.Id）。</param>
        /// <param name="useCache">是否使用路径缓存。</param>
        /// <returns>主键 → 配置行 的字典；重复主键后者覆盖并告警（与 Unity 端显式告警一致）。</returns>
        public static Dictionary<TKey, T>? LoadIndexed<TKey, T>(string path, Func<T, TKey> keySelector, bool useCache = true)
            where TKey : notnull
        {
            // P3 修复：索引此前不缓存，每次调用都 O(N) 重建字典（按主键查表若落在每帧/每请求路径上代价可观）。
            // 缓存仅在**无闭包捕获的静态选择器**下启用：此时选择器语义完全由 (声明类型, 方法名) 决定，
            // 可作为稳定缓存键；带捕获的委托（Target != null）行为依赖外部状态，一律不缓存以免读出错行。
            string? indexKey = null;
            if (useCache && keySelector.Target == null &&
                keySelector.Method.DeclaringType != null)
            {
                indexKey = path + "\u0001I\u0001" + typeof(T).FullName + "\u0001" + typeof(TKey).FullName
                          + "\u0001" + keySelector.Method.DeclaringType.FullName + "." + keySelector.Method.Name;
                if (Cache.TryGetValue(indexKey, out var cachedIndex) && cachedIndex is Dictionary<TKey, T> hit)
                {
                    return hit;
                }
            }

            var list = LoadList<T>(path, useCache);
            if (list == null)
            {
                return null;
            }

            var dict = new Dictionary<TKey, T>(list.Count);
            foreach (var item in list)
            {
                if (item == null)
                {
                    continue;
                }
                var key = keySelector(item);
                if (!dict.TryAdd(key, item))
                {
                    Log.Warning($"ConfigDatabase {path} 存在重复主键 {key}，后者已覆盖先者。");
                    dict[key] = item;
                }
            }

            if (indexKey != null)
            {
                Cache[indexKey] = dict;
            }
            return dict;
        }

        /// <summary>清空缓存（配置热更/重载后调用，强制下一次 Load 重新读盘）。</summary>
        public static void ClearCache() => Cache.Clear();

        private static List<T>? LoadCore<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Error($"ConfigDatabase 配置文件不存在: {path}");
                    return null;
                }

                // UTF-8 读取并剥离前导 BOM（工具产物带 EF BB BF；Newtonsoft 不接受前导 BOM 字符）
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8).TrimStart('\uFEFF');
                var list = Json.Deserialize<List<T>>(json);
                if (list == null)
                {
                    Log.Error($"ConfigDatabase 配置文件 {path} 反序列化结果为空。");
                    return null;
                }

                Log.Info($"ConfigDatabase 加载 {path}: {list.Count} 条。");
                return list;
            }
            catch (Exception ex)
            {
                Log.Error($"ConfigDatabase 加载 {path} 异常: {ex.Message}");
                return null;
            }
        }
    }
}
