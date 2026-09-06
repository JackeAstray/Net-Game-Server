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
                Shared.Log.Warning($"运行时配置加载失败，按空处理 {FilePath} Exception:{ex.Message}");
            }
            return new Dictionary<string, string>();
        }

        public static void Save(Dictionary<string, string> overrides)
        {
            lock (WriteGate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string json = Shared.Json.SerializeToUtf8Bytes(overrides) is byte[] bytes
                        ? System.Text.Encoding.UTF8.GetString(bytes)
                        : "{}";
                    File.WriteAllText(FilePath, json);
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"运行时配置保存失败 {FilePath} Exception:{ex.Message}");
                }
            }
        }
    }
}
