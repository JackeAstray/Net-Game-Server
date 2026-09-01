using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    /// <summary>
    ///  统一的战斗场景抽象。可用于大世界(World Map)或独立对局房间(Instanced Room)。
    /// </summary>
    public class BattleScene
    {
        public SceneConfig Config { get; }

        public string SceneId => Config.SceneId;
        public bool UseAoi => Config.UseAoi;

        // 每个场景拥有自己独立的实体管理器，数据隔离
        public EntityManager EntityManager { get; }
        public GridAoiManager? AoiManager { get; }

        public BattleScene(SceneConfig config)
        {
            Config = config;
            EntityManager = new EntityManager();

            if (config.UseAoi)
            {
                AoiManager = new GridAoiManager(config.GridSize, config.AoiViewRadius);
            }
        }
    }
}