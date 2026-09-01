using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Framework.Entity;

namespace Battle.Handlers
{
    /// <summary>
    /// 基于九宫格（Grid）的空间分区 AOI（Area of Interest）管理系统。
    /// 负责将大地图划分成网格，高效查询实体周围可见的网格与实体。
    /// 实体使用新实体框架（Framework.Entity.Entity），位置从 "Position" 属性读取。
    /// </summary>
    public class GridAoiManager
    {
        // 网格的边长（比如每 50 米划分一个网格）
        public float GridSize { get; }

        // AOI 视野半径（以网格为单位的邻格半径，1=3x3 九宫格，2=5x5，3=7x7...）
        public int ViewRadius { get; }

        // 所有实体的状态存储
        private readonly ConcurrentDictionary<long, Framework.Entity.Entity> entities = new();

        // 网格索引: (GridX, GridY) -> 该网格内所有实体的 SessionId 集合
        private readonly ConcurrentDictionary<(int, int), ConcurrentDictionary<long, byte>> grids = new();

        // 实体最后一次所在网格（修复：调用方可能先改写实体 Position 再调用 AddOrUpdateEntity，
        // 直接从实体读"旧位置"会得到新位置，导致跨格变化永远检测不到 → 网格索引/AOI 视图过期）
        private readonly ConcurrentDictionary<long, (int, int)> lastGrids = new();

        // 网格坐标钳制范围（防 NaN/Inf/超大坐标导致 float→int 未定义行为与网格索引无界增长）
        private const float MaxGridCoord = 10000f;

        public GridAoiManager(float gridSize = 50.0f, int viewRadius = 1)
        {
            GridSize = gridSize;
            ViewRadius = Math.Max(1, viewRadius);
        }

        /// <summary>当前 AOI 实体总数（统计用）。</summary>
        public int EntityCount => entities.Count;

        /// <summary>当前非空网格数（统计用）。</summary>
        public int GridCount => grids.Count;

        /// <summary>
        /// 获取给定世界坐标对应的网格坐标 (GridX, GridY)。使用向下取整确保坐标正确划分到网格中；
        /// 坐标先按 ±MaxGridCoord 钳制，避免 NaN/Inf/超大值造成 float→int 溢出或网格索引无界增长。
        /// </summary>
        public (int, int) GetGridCoordinate(Float3 position)
        {
            if (float.IsNaN(position.X) || float.IsNaN(position.Z) ||
                float.IsInfinity(position.X) || float.IsInfinity(position.Z))
            {
                return (int.MinValue, int.MinValue); // 非法坐标统一落到哨兵值
            }
            float gx = Math.Clamp(position.X / GridSize, -MaxGridCoord, MaxGridCoord);
            float gz = Math.Clamp(position.Z / GridSize, -MaxGridCoord, MaxGridCoord);
            return ((int)Math.Floor(gx), (int)Math.Floor(gz));
        }

        /// <summary>
        /// 添加或更新实体，并根据新旧位置更新网格索引。
        /// </summary>
        /// <remarks>
        /// 旧网格由内部 lastGrids 索引提供（不读取实体上可能已被调用方改写的 Position），
        /// 因此跨格移动能被可靠检测。
        /// </remarks>
        /// <returns>如果实体的网格发生变化，则返回 true；否则返回 false</returns>
        public bool AddOrUpdateEntity(long sessionId, Framework.Entity.Entity entity, out (int, int) oldGrid, out (int, int) newGrid)
        {
            Float3 position = entity.Get<Float3>("Position");
            newGrid = GetGridCoordinate(position);
            bool isGridChanged = false;

            if (lastGrids.TryGetValue(sessionId, out oldGrid))
            {
                if (oldGrid != newGrid)
                {
                    isGridChanged = true;
                    // 从旧网格移除
                    if (grids.TryGetValue(oldGrid, out var oldGridSet))
                    {
                        oldGridSet.TryRemove(sessionId, out _);
                        if (oldGridSet.Count == 0)
                        {
                            grids.TryRemove(oldGrid, out _); // 空网格回收，防止网格索引无界增长
                        }
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
            entities[sessionId] = entity;
            lastGrids[sessionId] = newGrid;

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
        public void RemoveEntity(long sessionId)
        {
            entities.TryRemove(sessionId, out _);
            if (lastGrids.TryRemove(sessionId, out var gridCoord))
            {
                if (grids.TryGetValue(gridCoord, out var gridSet))
                {
                    gridSet.TryRemove(sessionId, out _);
                    if (gridSet.Count == 0)
                    {
                        grids.TryRemove(gridCoord, out _); // 空网格回收
                    }
                }
            }
        }

        /// <summary>
        /// 获取实体的状态信息。
        /// </summary>
        public Framework.Entity.Entity? GetEntity(long sessionId)
        {
            if (entities.TryGetValue(sessionId, out var entity))
            {
                return entity;
            }
            return null;
        }

        /// <summary>
        /// 获取所有实体的状态信息列表（用于调试或全局查询）。
        /// </summary>
        public IEnumerable<Framework.Entity.Entity> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>
        /// 获取指定网格周围九宫格（半径 <see cref="ViewRadius"/>）范围内的所有实体 SessionId 列表。
        /// </summary>
        public List<long> GetSurroundingEntities(int gridX, int gridZ)
        {
            var result = new List<long>();
            int r = ViewRadius;
            for (int x = gridX - r; x <= gridX + r; x++)
            {
                for (int z = gridZ - r; z <= gridZ + r; z++)
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
        public void CalculateGridDiff((int x, int z) oldGrid, (int x, int z) newGrid, out List<long> enterEntities, out List<long> leaveEntities)
        {
            int r = ViewRadius;
            var oldSurroundings = new HashSet<(int, int)>();
            if (oldGrid.x != int.MinValue)
            {
                for (int x = oldGrid.x - r; x <= oldGrid.x + r; x++)
                    for (int z = oldGrid.z - r; z <= oldGrid.z + r; z++)
                        oldSurroundings.Add((x, z));
            }

            var newSurroundings = new HashSet<(int, int)>();
            for (int x = newGrid.x - r; x <= newGrid.x + r; x++)
                for (int z = newGrid.z - r; z <= newGrid.z + r; z++)
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
