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
        private static readonly ConcurrentDictionary<string, object?> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>加载配置表为对象列表（缓存按路径复用；配置属运行时只读数据）。</summary>
        /// <param name="path">ExcelToJson 产出的 .json 文件路径。</param>
        /// <param name="useCache">是否使用路径缓存（配置热更后可 ClearCache 强制重读）。</param>
        public static List<T>? LoadList<T>(string path, bool useCache = true)
        {
            if (useCache && Cache.TryGetValue(path, out var cached) && cached is List<T> typed)
            {
                return typed;
            }

            var list = LoadCore<T>(path);
            if (useCache && list != null)
            {
                Cache[path] = list;
            }
            return list;
        }

        /// <summary>加载配置表并按主键建立索引（如按 Id/Number 查），O(1) 访问。</summary>
        /// <param name="path">ExcelToJson 产出的 .json 文件路径。</param>
        /// <param name="keySelector">主键选择器（如 c =&gt; c.Id）。</param>
        /// <param name="useCache">是否使用路径缓存。</param>
        /// <returns>主键 → 配置行 的字典；重复主键后者覆盖并告警（与 Unity 端显式告警一致）。</returns>
        public static Dictionary<TKey, T>? LoadIndexed<TKey, T>(string path, Func<T, TKey> keySelector, bool useCache = true)
            where TKey : notnull
        {
            var list = LoadList<T>(path, useCache);
            if (list == null)
            {
                return null;
            }

            var dict = new Dictionary<TKey, T>();
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
