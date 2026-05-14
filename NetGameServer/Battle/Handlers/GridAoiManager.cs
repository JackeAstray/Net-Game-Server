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
        /// 获取给定世界坐标对应的网格坐标 (GridX, GridY)。使用向下取整确保坐标正确划分到网格中。
        /// </summary>
        /// <param name="position">世界坐标</param>
        /// <returns>网格坐标 (GridX, GridY)</returns>
        public (int, int) GetGridCoordinate(Vector3 position)
        {
            int gx = (int)Math.Floor(position.X / GridSize);
            int gz = (int)Math.Floor(position.Z / GridSize);
            return (gx, gz);
        }

        /// <summary>
        /// 添加或更新实体的状态信息，并根据新旧位置更新网格索引。
        /// </summary>
        /// <param name="sessionId">实体的会话ID</param>
        /// <param name="state">实体的状态信息</param>
        /// <param name="oldGrid">输出参数，表示实体的旧网格坐标</param>
        /// <param name="newGrid">输出参数，表示实体的新网格坐标</param>
        /// <returns>如果实体的网格发生变化，则返回 true；否则返回 false</returns>
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

        /// <summary>
        /// 移除实体及其所在的网格信息。
        /// </summary>
        /// <param name="sessionId">实体的会话ID</param>
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

        /// <summary>
        /// 获取实体的状态信息。
        /// </summary>
        /// <param name="sessionId">实体的会话ID </param>
        /// <returns>实体的状态信息，如果不存在则返回 null</returns>
        public EntityState? GetEntity(long sessionId)
        {
            if (entities.TryGetValue(sessionId, out var state))
            {
                return state;
            }
            return null;
        }

        /// <summary>
        /// 获取所有实体的状态信息列表（用于调试或全局查询）。
        /// 注意：在实际生产环境中，频繁调用可能会有性能问题，需谨慎使用。
        /// </summary>
        /// <returns></returns>
        public IEnumerable<EntityState> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>
        /// 获取指定网格周围九宫格范围内的所有实体 SessionId 列表。
        /// </summary>
        /// <param name="gridX">网格的 X 坐标</param>
        /// <param name="gridZ">网格的 Z 坐标</param>
        /// <returns>指定网格周围九宫格范围内的所有实体 SessionId 列表</returns>
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
        /// 计算从旧网格到新网格的差异，返回进入视野的实体列表和离开视野的实体列表。
        /// </summary>
        /// <param name="oldGrid">旧网格坐标</param>
        /// <param name="newGrid">新网格坐标</param>
        /// <param name="enterEntities">进入视野的实体列表</param>
        /// <param name="leaveEntities">离开视野的实体列表</param>
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