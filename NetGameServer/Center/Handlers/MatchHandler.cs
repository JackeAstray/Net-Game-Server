using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Center;

namespace Center.Handlers
{
    public class MatchHandler
    {
        // 简易匹配池：按 CategoryId 把玩家的 SessionId 分组排队
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> matchPools = new();

        public async Task<CenterMatchResponse> HandleMatchRequestAsync(long clientSessionId, CenterMatchRequest request)
        {
            // 使用 CategoryId (如 "PVP", "PVE") 作为匹配池的 Key
            string category = string.IsNullOrEmpty(request.CategoryId) ? "PVP" : request.CategoryId;
            bool isWorldMap = category.Equals("World", StringComparison.OrdinalIgnoreCase);

            var pool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
            pool.Enqueue(clientSessionId);

            Shared.Log.Info($"玩家 {clientSessionId} 开始匹配 {category}，当前队列人数: {pool.Count}");

            // 如果是大世界，理论上应该复用已有的 World_01 之类的场景
            if (isWorldMap || pool.Count >= 2) // 设定 2 人即可发车配对成功
            {
                // TODO: 广播给队列里所有的玩家，让他们一起加入同一个 Room。这里演示单向返回给触发满足条件的最后一个玩家。
                // 出队清空
                while (pool.TryDequeue(out var _)) { }

                string newRoomId = isWorldMap ? "World_" + Guid.NewGuid().ToString("N") : "Room_" + Guid.NewGuid().ToString("N");
                string assignedBattleNode = "Battle_01"; // 假设已经调度获取到了合适的 Battle 节点

                await Task.Delay(100);

                string sceneName = isWorldMap ? "大世界" : $"高级 {category} 对战房间";

                return new CenterMatchResponse
                {
                    Success = true,
                    RoomId = newRoomId,
                    BattleNodeId = assignedBattleNode,
                    Message = $"Match successful. Welcome to {sceneName}"
                };
            }

            return new CenterMatchResponse
            {
                Success = false,
                RoomId = "",
                BattleNodeId = "",
                Message = "正在排队中，等待其他玩家加入..."
            };
        }

        public async Task<CenterCreateRoomResponse> HandleCreateRoomRequestAsync(CenterCreateRoomRequest request)
        {
            string newRoomId = "Room_" + Guid.NewGuid().ToString("N");
            string assignedBattleNode = "Battle_01"; // 调度分配逻辑

            await Task.Delay(50); // 假装请求节点分配耗时

            return new CenterCreateRoomResponse
            {
                Success = true,
                RoomId = newRoomId,
                BattleNodeId = assignedBattleNode,
                Message = $"房间已创建 ({(request.IsPrivate ? "私密" : "公开")}): {request.SceneType}"
            };
        }
    }
}
