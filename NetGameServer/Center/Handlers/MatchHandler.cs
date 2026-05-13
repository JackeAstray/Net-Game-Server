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
        // 模拟一个极其简单的匹配队列，用于演示匹配成功并分配房间过程
        public async Task<CenterMatchResponse> HandleMatchRequestAsync(CenterMatchRequest request)
        {
            // 根据请求参数区分房间类型
            // 假设客户端如果 CategoryId = "World" 则分配大世界
            // 否则就是一个独立的竞技对战小房间
            bool isWorldMap = request.CategoryId.Equals("World", StringComparison.OrdinalIgnoreCase);

            // 如果是大世界，理论上应该复用已有的 World_01 之类的场景
            // 为了兼顾之前 RoomId 的前缀判断逻辑，如果是世界地图，让 RoomId 包含 "World" 字段
            string newRoomId = isWorldMap ? "World_" + Guid.NewGuid().ToString("N") : "Room_" + Guid.NewGuid().ToString("N");
            string assignedBattleNode = "Battle_01"; // 假设已经调度获取到了合适的 Battle 节点

            await Task.Delay(100);

            // TODO: 在这里未来可以根据 CategoryId 去查数据库或配置文件，返回给客户端这个房间叫什么名，有些什么规则
            string sceneName = isWorldMap ? "艾泽拉斯大版图" : "高级匹配对战房间";

            return new CenterMatchResponse
            {
                Success = true,
                RoomId = newRoomId,
                BattleNodeId = assignedBattleNode,
                Message = $"Match successful. Welcome to {sceneName}"
            };
        }
    }
}
