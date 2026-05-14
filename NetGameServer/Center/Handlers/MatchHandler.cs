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

        // 用于等待真实的 Battle 节点返回创建房间结果
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CenterCreateSceneResponse>> pendingSceneCreations = new();

        /// <summary>
        /// 处理匹配请求。根据 CategoryId 将玩家加入对应的匹配池。
        /// 当满足条件（如人数达到阈值）时，寻找真实的BattleNode请求创建场景并返回。
        /// </summary>
        /// <param name="clientSessionId">客户端会话 ID。</param>
        /// <param name="request">匹配请求对象。</param>
        /// <returns>匹配响应对象。</returns>
        public async Task<CenterMatchResponse?> HandleMatchRequestAsync(long clientSessionId, CenterMatchRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, CenterMatchResponse> sendToGatewayFunc)
        {
            string category = string.IsNullOrEmpty(request.CategoryId) ? "PVP" : request.CategoryId;
            bool isWorldMap = category.Equals("World", StringComparison.OrdinalIgnoreCase);

            var pool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
            pool.Enqueue(clientSessionId);

            Shared.Log.Info($"玩家 {clientSessionId} 开始匹配 {category}，当前队列人数: {pool.Count}");

            if (isWorldMap || pool.Count >= 2)
            {
                // 获取一个最空闲的 Battle 节点
                string? assignedBattleNode = NodeManager.Instance.GetBestBattleNode();
                if (string.IsNullOrEmpty(assignedBattleNode))
                {
                    return new CenterMatchResponse
                    {
                        Success = false,
                        Message = "当前没有可用的战斗节点(BattleServer)"
                    };
                }

                var matchedPlayers = new List<long>();
                while (pool.TryDequeue(out var pid)) 
                { 
                    matchedPlayers.Add(pid);
                }

                string roomId = isWorldMap ? "World_" + Guid.NewGuid().ToString("N") : "Room_" + Guid.NewGuid().ToString("N");

                // 1. 发送 RPC 请求到 BattleNode 建房
                var battleNodeInfo = NodeManager.Instance.GetNode(assignedBattleNode);
                var tcs = new TaskCompletionSource<CenterCreateSceneResponse>();
                pendingSceneCreations[roomId] = tcs;

                var req = new CenterCreateSceneRequest { RoomId = roomId, SceneType = category, IsPrivate = false };
                var payload = Shared.Json.SerializeToUtf8Bytes(req);
                byte[] packet = Network.Routing.PacketBuilder.BuildSessionWrapperPacket(0, Shared.Messages.MessageIds.CenterCreateSceneReq, payload); // 0 表示服务器之间的调用
                battleNodeInfo?.Session.Send(packet);

                // 等待真正的 BattleNode 返回创建结果，设定一个超时时间
                var delayTask = Task.Delay(5000);
                if (await Task.WhenAny(tcs.Task, delayTask) == tcs.Task)
                {
                    var sceneResult = tcs.Task.Result;
                    pendingSceneCreations.TryRemove(roomId, out _);

                    if (sceneResult.Success)
                    {
                        string sceneName = isWorldMap ? "大世界" : $"高级 {category} 对战房间";
                        var successResponse = new CenterMatchResponse
                        {
                            Success = true,
                            RoomId = roomId,
                            BattleNodeId = assignedBattleNode,
                            SceneId = sceneResult.SceneId,
                            SceneType = category,
                            Message = $"Match successful. Welcome to {sceneName}"
                        };

                        // Notify all players except the current one triggering the match threshold
                        foreach (var pid in matchedPlayers)
                        {
                            if (pid != clientSessionId)
                            {
                                sendToGatewayFunc(gatewaySession, pid, Shared.Messages.MessageIds.CenterMatchRes, successResponse);
                            }
                        }

                        return successResponse;
                    }
                }

                pendingSceneCreations.TryRemove(roomId, out _);
                var failedResponse = new CenterMatchResponse
                {
                    Success = false,
                    Message = "Battle 节点创建房间失败或超时"
                };

                foreach (var pid in matchedPlayers)
                {
                    if (pid != clientSessionId)
                    {
                        sendToGatewayFunc(gatewaySession, pid, Shared.Messages.MessageIds.CenterMatchRes, failedResponse);
                    }
                }

                return failedResponse;
            }

            return new CenterMatchResponse
            {
                Success = false,
                Message = "正在排队中，等待其他玩家加入..."
            };
        }

        /// <summary>
        /// 处理创建房间请求。这个方法可以被外部调用（如管理后台）来直接创建一个房间，而不是通过匹配池。
        /// </summary>
        /// <param name="request">创建房间请求对象。</param>
        /// <returns>创建房间响应对象。</returns>
        public async Task<CenterCreateRoomResponse> HandleCreateRoomRequestAsync(CenterCreateRoomRequest request)
        {
            string assignedBattleNode = NodeManager.Instance.GetBestBattleNode() ?? string.Empty;
            if (string.IsNullOrEmpty(assignedBattleNode))
            {
                return new CenterCreateRoomResponse
                {
                    Success = false,
                    Message = "当前没有可用的战斗节点"
                };
            }

            string roomId = "Room_" + Guid.NewGuid().ToString("N");

            var battleNodeInfo = NodeManager.Instance.GetNode(assignedBattleNode);
            var tcs = new TaskCompletionSource<CenterCreateSceneResponse>();
            pendingSceneCreations[roomId] = tcs;

            var req = new CenterCreateSceneRequest { RoomId = roomId, SceneType = request.SceneType, IsPrivate = request.IsPrivate };
            var payload = Shared.Json.SerializeToUtf8Bytes(req);
            byte[] packet = Network.Routing.PacketBuilder.BuildSessionWrapperPacket(0, MessageIds.CenterCreateSceneReq, payload);
            battleNodeInfo?.Session.Send(packet);

            var delayTask = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, delayTask) == tcs.Task)
            {
                var sceneResult = tcs.Task.Result;
                pendingSceneCreations.TryRemove(roomId, out _);
                if (sceneResult.Success)
                {
                    return new CenterCreateRoomResponse
                    {
                        Success = true,
                        RoomId = roomId,
                        BattleNodeId = assignedBattleNode,
                        SceneId = sceneResult.SceneId,
                        SceneType = request.SceneType,
                        Message = $"房间已创建 ({(request.IsPrivate ? "私密" : "公开")}): {request.SceneType}"
                    };
                }
            }

            pendingSceneCreations.TryRemove(roomId, out _);
            return new CenterCreateRoomResponse
            {
                Success = false,
                Message = "房间创建请求超时或失败"
            };
        }

        /// <summary>
        /// 处理来自 BattleNode 的创建场景响应。当 BattleNode 完成房间创建后，会调用这个方法来通知结果。
        /// </summary>
        /// <param name="response"></param>
        public void HandleCreateSceneResponse(CenterCreateSceneResponse response)
        {
            if (pendingSceneCreations.TryGetValue(response.RoomId, out var tcs))
            {
                tcs.TrySetResult(response);
            }
        }
    }
}