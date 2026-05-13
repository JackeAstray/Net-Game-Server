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
            // 简单演示直接分配一个临时房间
            string newRoomId = Guid.NewGuid().ToString("N");
            string assignedBattleNode = "Battle_01"; // 假设已经调度获取到了合适的 Battle 节点

            await Task.Delay(100); 

            return new CenterMatchResponse
            {
                Success = true,
                RoomId = newRoomId,
                BattleNodeId = assignedBattleNode,
                Message = "Match successful"
            };
        }
    }
}
