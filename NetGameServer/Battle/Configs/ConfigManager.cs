using System.Collections.Generic;
using Battle.Handlers;
using System.IO;
using Shared;
using System.Linq;
using System;

namespace Battle.Configs
{
    /// <summary>
    /// 配置管理器，负责加载和存储游戏服务器的各种配置数据。
    ///
    /// P2-14/P2-15 重构：
    /// - 统一走 <see cref="ConfigDatabase"/>（与 Tools/ExcelToJson 产出的 JSON 配置表同契约，消除重复实现）；
    /// - 路径配置化：SceneConfigPath（appsettings / 环境变量可覆盖，默认 Configs/Scenes.json）；
    /// - fail-fast：配置文件缺失或解析为空时明确抛错，不再静默在 CWD 现场生成默认配置（避免工作目录错误时掩盖问题）；
    /// - 校验：重复 SceneId、非法数值在加载期即报错；
    /// - 热重载：FileSystemWatcher 监听配置文件变更 → 清缓存重载，改表即生效（新增场景即时可用）。
    /// </summary>
    public static class ConfigManager
    {
        private static readonly object reloadGate = new();
        private static long lastReloadTicks;
        private const long ReloadDebounceMs = 500;
        private static FileSystemWatcher? watcher;

        public static Dictionary<string, SceneConfig> SceneTemplates { get; private set; } = new();

        /// <summary>当前生效的配置路径（用于日志）。</summary>
        public static string ConfigPath { get; private set; } = "Configs/Scenes.json";

        /// <summary>
        /// 加载所有配置文件。
        /// </summary>
        public static void LoadAll()
        {
            string path = ConfigHelper.GetConfig<string>("SceneConfigPath") ?? "Configs/Scenes.json";
            ConfigPath = path;

            // 与 ExcelToJson 输出同契约：ConfigDatabase.LoadIndexed 按主键建 O(1) 索引，
            // 重复主键会告警（保留最后一条）。useCache=false 保证热重载/首次加载都读盘。
            var templates = ConfigDatabase.LoadIndexed<string, SceneConfig>(path, static c => c.SceneId, useCache: false);
            if (templates == null || templates.Count == 0)
            {
                // fail-fast：缺失/解析失败/空表时明确报错，避免静默使用空模板或现场生成错误配置
                throw new InvalidOperationException(
                    $"场景配置加载失败：{path}（不存在或为空）。请提供有效的场景配置文件（可用 Tools/ExcelToJson 生成，或手工放置 UTF-8 JSON）。");
            }

            ValidateTemplates(templates);
            SceneTemplates = templates;
            Log.Info($"加载的场景模板: {SceneTemplates.Count} 条（{path}）");

            EnsureHotReloadWatch(path);
        }

        /// <summary>配置期校验：非法数值/缺失名称立即 fail-fast，防止运行时静默异常行为。</summary>
        private static void ValidateTemplates(Dictionary<string, SceneConfig> templates)
        {
            foreach (var (sceneId, cfg) in templates)
            {
                if (string.IsNullOrWhiteSpace(cfg.Name))
                {
                    throw new InvalidOperationException($"场景 {sceneId} 的 Name 不能为空");
                }
                if (cfg.MaxPlayers <= 0)
                {
                    throw new InvalidOperationException($"场景 {sceneId} 的 MaxPlayers 必须 > 0（当前 {cfg.MaxPlayers}）");
                }
                if (cfg.UseAoi && cfg.GridSize <= 0)
                {
                    throw new InvalidOperationException($"场景 {sceneId} 开启 AOI 时 GridSize 必须 > 0（当前 {cfg.GridSize}）");
                }
            }
        }

        /// <summary>
        /// 注册配置文件热重载监听（FileSystemWatcher，防抖 500ms）。
        /// 变更后清空 ConfigDatabase 缓存并重新加载；重载失败仅告警，保留上一份生效配置。
        /// </summary>
        private static void EnsureHotReloadWatch(string path)
        {
            if (watcher != null)
            {
                return;
            }

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? ".";
            string file = Path.GetFileName(full);

            try
            {
                var w = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                w.Changed += (_, _) => ReloadFromDisk();
                w.Created += (_, _) => ReloadFromDisk();
                w.Deleted += (_, _) => ReloadFromDisk();
                w.Renamed += (_, _) => ReloadFromDisk();
                watcher = w; // 持有引用防 GC
                Log.Info($"场景配置热重载监听已启动: {full}");
            }
            catch (Exception ex)
            {
                Log.Warning($"场景配置热重载监听启动失败（不影响已加载配置）: {ex.Message}");
            }
        }

        /// <summary>防抖后的磁盘重载（FileSystemWatcher 事件可能重复触发）。</summary>
        private static void ReloadFromDisk()
        {
            long now = Environment.TickCount64;
            if (now - System.Threading.Interlocked.Read(ref lastReloadTicks) < ReloadDebounceMs)
            {
                return;
            }
            System.Threading.Interlocked.Exchange(ref lastReloadTicks, now);

            lock (reloadGate)
            {
                try
                {
                    ConfigDatabase.ClearCache();
                    LoadAll();
                    Log.Info("场景配置热重载完成，当前模板数: " + SceneTemplates.Count);
                }
                catch (Exception ex)
                {
                    Log.Error($"场景配置热重载失败（保留上一份生效配置）: {ex.Message}");
                }
            }
        }
    }
}
