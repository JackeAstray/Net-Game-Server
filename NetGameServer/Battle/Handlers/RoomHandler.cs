using System;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Battle;

namespace Battle.Handlers
{
    public class RoomHandler
    {
        public Task<BattleJoinResponse> HandleJoinRequestAsync(BattleJoinRequest request)
        {
            // 简单演示直接加入成功并返回
            return Task.FromResult(new BattleJoinResponse
            {
                Success = true,
                Message = $"Joined room {request.RoomId} successfully"
            });
        }
    }
}
