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
        public string SceneId { get; }
        public bool UseAoi { get; }

        // 每个场景拥有自己独立的实体管理器，数据隔离
        public EntityManager EntityManager { get; }
        public GridAoiManager? AoiManager { get; }

        public BattleScene(string sceneId, bool useAoi = true, float gridSize = 50.0f)
        {
            SceneId = sceneId;
            UseAoi = useAoi;
            EntityManager = new EntityManager();

            if (useAoi)
            {
                AoiManager = new GridAoiManager(gridSize);
            }
        }
    }
}
