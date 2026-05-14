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
    /// </summary>
    public static class ConfigManager
    {
        public static Dictionary<string, SceneConfig> SceneTemplates { get; private set; } = new();

        /// <summary>
        /// 加载所有配置文件
        /// </summary>
        public static void LoadAll()
        {
            Log.Info("正在加载场景配置...");

            // 在一个真实的项目中，你会把它和其他设置放在一起
            string configPath = "Configs/Scenes.json";

            if (!File.Exists(configPath))
            {
                // 如果不存在，则创建目录
                Directory.CreateDirectory("Configs");

                // 如果它不存在，我们现在将创建一个默认的引导
                var defaultTemplates = new List<SceneConfig>
                {
                    new SceneConfig
                    {
                        SceneId = "World",
                        Name = "大世界主城",
                        SceneType = "World",
                        UseAoi = true,
                        GridSize = 20.0f,
                        MaxPlayers = 1000
                    },
                    new SceneConfig
                    {
                        SceneId = "PVP",
                        Name = "5v5 竞技场",
                        SceneType = "PVP",
                        UseAoi = false,
                        GridSize = 0,
                        MaxPlayers = 10
                    }
                };

                byte[] jsonData = Shared.Json.SerializeToUtf8Bytes(defaultTemplates);
                File.WriteAllBytes(configPath, jsonData);

                SceneTemplates = defaultTemplates.ToDictionary(k => k.SceneId, v => v);
                Log.Info($"创建并加载默认活动场景模板: {SceneTemplates.Count}");
            }
            else
            {
                try
                {
                    byte[] fileData = File.ReadAllBytes(configPath);
                    var configs = Shared.Json.DeserializeFromUtf8Bytes<List<SceneConfig>>(fileData);
                    if (configs != null)
                    {
                        SceneTemplates = configs.ToDictionary(k => k.SceneId, v => v);
                        Log.Info($"加载的场景模板: {SceneTemplates.Count}");
                    }
                    else
                    {
                        Log.Error($"配置文件 {configPath} 反序列化结果为空。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"加载场景配置文件 {configPath} 时发生异常: {ex.Message}");
                }
            }
        }
    }
}