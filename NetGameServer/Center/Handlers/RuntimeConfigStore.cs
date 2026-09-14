using System;
using System.Collections.Generic;
using System.IO;

namespace Center.Handlers
{
    /// <summary>
    /// 运行时配置覆盖的持久化存储（B4 配置中心）：
    /// 管理台写入的覆盖项经 ConfigHelper.SetRuntimeOverride 立即热更，并落盘 data/runtime_config.json，
    /// 重启后由 CenterServerApp 启动时重新加载，保证远程改配不因重启丢失。
    /// </summary>
    public static class RuntimeConfigStore
    {
        private static readonly object WriteGate = new();
        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "runtime_config.json");

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = Shared.Json.DeserializeFromUtf8Bytes<Dictionary<string, string>>(
                        System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(FilePath)));
                    return loaded ?? new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                // P2 修复：解析/读取失败不再"静默按空处理"——那会让全部运行时覆盖无声丢失，
                // 且下一次 Save 会把空字典写回，彻底抹掉原文件。这里把疑似损坏的文件另存为 .corrupt 保留证据，
                // 便于运维人工恢复，同时把日志级别提到 Error。
                Shared.Log.Error(ex, $"运行时配置加载失败（按空处理，原文件已另存 .corrupt 以便恢复）{FilePath}");
                TryQuarantineCorruptFile();
            }
            return new Dictionary<string, string>();
        }

        /// <summary>
        /// 保存覆盖项。返回 false 表示**未成功落盘**（磁盘满/只读/权限），调用方必须回失败，不得假成功。
        /// </summary>
        public static bool Save(Dictionary<string, string> overrides)
        {
            lock (WriteGate)
            {
                return SaveCore(overrides);
            }
        }

        /// <summary>
        /// 原子读-改-写：在写锁内加载当前覆盖项 → 执行 mutate → 原子落盘。
        /// 用于替代调用方"Load → 改字典 → Save"的三段式（原实现无跨请求锁，两个并发管理请求会互相覆盖文件，
        /// 内存 override 还在但重启后少一条）。返回 false 表示落盘失败，调用方必须回失败。
        /// </summary>
        public static bool TryUpdate(Action<Dictionary<string, string>> mutate, out Dictionary<string, string> updated)
        {
            lock (WriteGate)
            {
                updated = Load();
                mutate(updated);
                return SaveCore(updated);
            }
        }

        /// <summary>
        /// 原子落盘：先写临时文件再 Move(overwrite) 替换。
        /// 原实现用 <c>File.WriteAllText</c>（先截断后写），进程在写入途中被杀会留下**残缺 JSON** →
        /// 下次 Load 反序列化失败 → 全部运行时覆盖静默丢失。Move 在同一卷上是原子替换。
        /// </summary>
        private static bool SaveCore(Dictionary<string, string> overrides)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = System.Text.Encoding.UTF8.GetString(Shared.Json.SerializeToUtf8Bytes(overrides));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Shared.Log.Error(ex, $"运行时配置保存失败（未落盘，调用方须回失败）{FilePath}");
                return false;
            }
        }

        /// <summary>把疑似损坏的配置文件另存为 .corrupt（尽力而为），避免被下一次保存直接覆盖。</summary>
        private static void TryQuarantineCorruptFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                string dest = FilePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt";
                File.Move(FilePath, dest, overwrite: true);
                Shared.Log.Warning($"已把损坏的运行时配置文件另存为 {dest}");
            }
            catch (Exception ex)
            {
                Shared.Log.Warning($"损坏的运行时配置文件另存失败（继续按空处理）: {ex.Message}");
            }
        }
    }
}
