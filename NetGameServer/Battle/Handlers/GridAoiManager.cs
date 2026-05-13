using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    /// <summary>
    /// 基于九宫格（Grid）的空间分区 AOI（Area of Interest）管理系统。
    /// 负责将大地图划分成网格，高效查询实体周围可见的网格与实体。
    /// </summary>
    public class GridAoiManager
    {
        // 网格的边长（比如每 50 米划分一个网格）
        public float GridSize { get; }

        // 所有实体的状态存储
        private readonly ConcurrentDictionary<long, EntityState> entities = new();

        // 网格索引: (GridX, GridY) -> 该网格内所有实体的 SessionId 集合
        private readonly ConcurrentDictionary<(int, int), ConcurrentDictionary<long, byte>> grids = new();

        public GridAoiManager(float gridSize = 50.0f)
        {
            GridSize = gridSize;
        }

        /// <summary>
        /// 根据坐标计算属于哪个网格坐标 (X, Y)
        /// 我们主要处理 X 和 Z 平面上的 2D 距离区域
        /// </summary>
        public (int, int) GetGridCoordinate(Vector3 position)
        {
            int gx = (int)Math.Floor(position.X / GridSize);
            int gz = (int)Math.Floor(position.Z / GridSize);
            return (gx, gz);
        }

        /// <summary>
        /// 添加或更新实体及其所在的网格信息。
        /// 若实体跨越了网格，返回 true，以及旧网格坐标和新网格坐标，方便外部计算视野增删。
        /// </summary>
        public bool AddOrUpdateEntity(long sessionId, EntityState state, out (int, int) oldGrid, out (int, int) newGrid)
        {
            oldGrid = (0, 0);
            newGrid = GetGridCoordinate(state.Position);
            bool isGridChanged = false;

            if (entities.TryGetValue(sessionId, out var oldState))
            {
                oldGrid = GetGridCoordinate(oldState.Position);
                if (oldGrid != newGrid)
                {
                    isGridChanged = true;
                    // 从旧网格移除
                    if (grids.TryGetValue(oldGrid, out var oldGridSet))
                    {
                        oldGridSet.TryRemove(sessionId, out _);
                    }
                }
            }
            else
            {
                // 新加入的实体，视为网格发生变化（从无到有）
                isGridChanged = true;
                // 用一个不存在的极大负值代表"无旧网格"
                oldGrid = (int.MinValue, int.MinValue);
            }

            // 更新实体信息
            entities[sessionId] = state;

            // 添加到新网格
            if (isGridChanged)
            {
                var gridSet = grids.GetOrAdd(newGrid, _ => new ConcurrentDictionary<long, byte>());
                gridSet.TryAdd(sessionId, 0);
            }

            return isGridChanged;
        }

        public void RemoveEntity(long sessionId)
        {
            if (entities.TryRemove(sessionId, out var state))
            {
                var gridCoord = GetGridCoordinate(state.Position);
                if (grids.TryGetValue(gridCoord, out var gridSet))
                {
                    gridSet.TryRemove(sessionId, out _);
                }
            }
        }

        public EntityState? GetEntity(long sessionId)
        {
            if (entities.TryGetValue(sessionId, out var state))
            {
                return state;
            }
            return null;
        }

        public IEnumerable<EntityState> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>
        /// 获取给定网格周围一圈（九宫格，共9个网格）所有实体的 SessionId
        /// </summary>
        public List<long> GetSurroundingEntities(int gridX, int gridZ)
        {
            var result = new List<long>();
            for (int x = gridX - 1; x <= gridX + 1; x++)
            {
                for (int z = gridZ - 1; z <= gridZ + 1; z++)
                {
                    if (grids.TryGetValue((x, z), out var gridSet))
                    {
                        result.AddRange(gridSet.Keys);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 给定两个九宫格中心点（旧的和新的），计算出新增的关注网格列表和离开的关注网格列表
        /// 从而找出"进入视野的主体"和"离开视野的主体"
        /// </summary>
        public void CalculateGridDiff((int x, int z) oldGrid, (int x, int z) newGrid, out List<long> enterEntities, out List<long> leaveEntities)
        {
            var oldSurroundings = new HashSet<(int, int)>();
            if (oldGrid.x != int.MinValue)
            {
                for (int x = oldGrid.x - 1; x <= oldGrid.x + 1; x++)
                    for (int z = oldGrid.z - 1; z <= oldGrid.z + 1; z++)
                        oldSurroundings.Add((x, z));
            }

            var newSurroundings = new HashSet<(int, int)>();
            for (int x = newGrid.x - 1; x <= newGrid.x + 1; x++)
                for (int z = newGrid.z - 1; z <= newGrid.z + 1; z++)
                    newSurroundings.Add((x, z));

            enterEntities = new List<long>();
            leaveEntities = new List<long>();

            // 找新视野比老视野多的网格（进入视野）
            foreach (var grid in newSurroundings.Except(oldSurroundings))
            {
                if (grids.TryGetValue(grid, out var gridSet))
                {
                    enterEntities.AddRange(gridSet.Keys);
                }
            }

            // 找老视野比新视野多的网格（离开视野）
            foreach (var grid in oldSurroundings.Except(newSurroundings))
            {
                if (grids.TryGetValue(grid, out var gridSet))
                {
                    leaveEntities.AddRange(gridSet.Keys);
                }
            }
        }
    }
}