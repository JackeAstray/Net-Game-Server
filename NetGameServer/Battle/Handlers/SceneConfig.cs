using System.Collections.Generic;

namespace Battle.Handlers
{
    /// <summary>
    /// 统一的场景/房间配置文件
    /// </summary>
    public class SceneConfig
    {
        public string SceneId { get; set; } = string.Empty;

        // 场景名称，例如 "艾泽拉斯"、"召唤师峡谷"、"四川麻将-高级房"
        public string Name { get; set; } = string.Empty;

        // 场景大类，如 "World", "PVP", "PVE", "Mahjong"
        public string SceneType { get; set; } = "Room";

        public int MaxPlayers { get; set; } = 100;

        // 是否为私人房间
        public bool IsPrivate { get; set; } = false;

        // 是否开启九宫格 AOI 视野剔除
        public bool UseAoi { get; set; } = false;

        // 网格尺寸（开启 AOI 时有效）
        public float GridSize { get; set; } = 50.0f;

        // AOI 视野半径（以网格为单位的九宫格半径，1=3x3 九宫格，2=5x5，3=7x7...）
        public int AoiViewRadius { get; set; } = 1;

        // 扩展规则字典：存储局内特有规则
        // 例如：{"WinCondition" : "DestroyBase", "TimeLimit" : "3600", "CanRespawn" : "true"}
        public Dictionary<string, string> CustomRules { get; set; } = new();
    }
}