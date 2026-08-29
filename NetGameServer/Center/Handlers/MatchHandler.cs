using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Center;
namespace Center.Handlers
{
    public partial class MatchHandler
    {
        private sealed class RoomRegistryEntry
        {
            public required RoomInfo Info { get; init; }
            public string PasswordHash { get; set; } = string.Empty;
            public ConcurrentDictionary<long, RoomMemberState> MemberStates { get; } = new();
        }

        private sealed class RoomMemberState
        {
            public long ClientSessionId { get; init; }
            public int UserId { get; set; }
            public bool IsReady { get; set; }
            public string DisplayName { get; set; } = string.Empty;
            public string UniqueId { get; set; } = string.Empty;
        }

        // 简易匹配池：按 CategoryId 把玩家的 SessionId 分组排队
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> matchPools = new();
        // 防重复入队：CategoryId -> 已排队玩家集合（修复重复匹配/自我匹配）
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, byte>> queuedPlayers = new();
        // 排队时间戳：SessionId -> 入队时间（超时剔除断线遗留的死队列）
        private readonly ConcurrentDictionary<long, DateTime> queuedAt = new();
        private static readonly TimeSpan MatchQueueTimeout = TimeSpan.FromSeconds(60);

        // 用于等待真实的 Battle 节点返回创建房间结果
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CenterCreateSceneResponse>> pendingSceneCreations = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CenterDestroySceneResponse>> pendingSceneDestroys = new();
        private readonly ConcurrentDictionary<string, RoomRegistryEntry> rooms = new();

        /// <summary>
        /// 房间快照（管理台监控用）：返回当前注册的全部房间信息。
        /// </summary>
        public IReadOnlyList<RoomInfo> GetRoomsSnapshot()
        {
            return rooms.Values.Select(e => e.Info).ToList();
        }

        /// <summary>
        /// 处理匹配请求。根据 CategoryId 将玩家加入对应的匹配池。
        /// 当满足条件（如人数达到阈值）时，寻找真实的BattleNode请求创建场景并返回。
        /// </summary>
        public async Task<CenterMatchResponse?> HandleMatchRequestAsync(long clientSessionId, CenterMatchRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, CenterMatchResponse> sendToGatewayFunc)
        {
            string category = string.IsNullOrWhiteSpace(request.CategoryId) ? "PVP" : request.CategoryId.Trim();
            bool isWorldMap = category.Equals("World", StringComparison.OrdinalIgnoreCase);

            var pool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
            var queued = queuedPlayers.GetOrAdd(category, _ => new ConcurrentDictionary<long, byte>());

            // 防重复入队：同一玩家在同一个分类只能排队一次（此前无条件 Enqueue，双击即重复匹配/自我匹配）。
            if (!queued.TryAdd(clientSessionId, 0))
            {
                return new CenterMatchResponse
                {
                    Success = false,
                    Message = "你已在匹配队列中，请勿重复匹配"
                };
            }
            queuedAt[clientSessionId] = DateTime.UtcNow;
            pool.Enqueue(clientSessionId);

            Shared.Log.Info($"玩家 {clientSessionId} 开始匹配 {category}，当前队列人数: {pool.Count}");

            if (!isWorldMap && pool.Count < 2)
            {
                return new CenterMatchResponse
                {
                    Success = false,
                    Message = "正在排队中，等待其他玩家加入..."
                };
            }

            var matchedPlayers = new List<long>();
            while (pool.TryDequeue(out var pid))
            {
                // 跳过已失效排队者：不在 queued（重复/已移除）或排队超时（断线遗留），避免死会话被匹配。
                if (!queued.ContainsKey(pid))
                {
                    continue;
                }
                if (queuedAt.TryGetValue(pid, out var enqueuedAt) && DateTime.UtcNow - enqueuedAt > MatchQueueTimeout)
                {
                    queued.TryRemove(pid, out _);
                    queuedAt.TryRemove(pid, out _);
                    continue;
                }
                matchedPlayers.Add(pid);
            }

            // 本轮匹配到的玩家从排队集合移除（若后续创建场景失败，RestoreMatchedPlayersToPool 会重新登记）。
            foreach (var pid in matchedPlayers)
            {
                queued.TryRemove(pid, out _);
                queuedAt.TryRemove(pid, out _);
            }

            void RestoreMatchedPlayersToPool()
            {
                var restorePool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
                var restoreQueued = queuedPlayers.GetOrAdd(category, _ => new ConcurrentDictionary<long, byte>());
                foreach (var matchedPlayer in matchedPlayers)
                {
                    restoreQueued.TryAdd(matchedPlayer, 0);
                    queuedAt[matchedPlayer] = DateTime.UtcNow;
                    restorePool.Enqueue(matchedPlayer);
                }
            }

            string? assignedBattleNode = NodeManager.Instance.GetBestBattleNode();
            if (string.IsNullOrEmpty(assignedBattleNode))
            {
                Shared.Log.Warning($"匹配失败：没有可用的战斗节点 Category:{category} ClientSessionId:{clientSessionId}");
                RestoreMatchedPlayersToPool();
                return new CenterMatchResponse
                {
                    Success = false,
                    Message = "当前没有可用的战斗节点(BattleServer)"
                };
            }

            string roomId = isWorldMap ? "World_" + Guid.NewGuid().ToString("N") : "Room_" + Guid.NewGuid().ToString("N");
            string roomName = isWorldMap ? "大世界" : $"高级 {category} 对战房间";
            int maxPlayers = isWorldMap ? 100 : Math.Max(2, matchedPlayers.Count);

            var sceneResult = await CreateSceneAsync(assignedBattleNode, new CenterCreateSceneRequest
            {
                RoomId = roomId,
                SceneType = category,
                IsPrivate = false,
                RoomName = roomName,
                MaxPlayers = maxPlayers
            });

            if (sceneResult == null || !sceneResult.Success)
            {
                RestoreMatchedPlayersToPool();
                var failedResponse = new CenterMatchResponse
                {
                    Success = false,
                    Message = "Battle 节点创建房间失败或超时"
                };
                Shared.Log.Warning($"匹配创建场景失败或超时 RoomId:{roomId} Category:{category} ClientSessionId:{clientSessionId}");

                foreach (var pid in matchedPlayers)
                {
                    if (pid != clientSessionId)
                    {
                        sendToGatewayFunc(gatewaySession, pid, MessageIds.CenterMatchRes, failedResponse);
                    }
                }

                return failedResponse;
            }

            RegisterRoom(new RoomInfo
            {
                RoomId = roomId,
                RoomName = roomName,
                SceneId = sceneResult.SceneId,
                SceneType = category,
                BattleNodeId = assignedBattleNode,
                OwnerUserId = 0,
                IsPrivate = false,
                HasPassword = false,
                MaxPlayers = maxPlayers,
                CurrentPlayers = matchedPlayers.Count,
                RoomStatus = RoomStatuses.Waiting,
                CustomRules = new Dictionary<string, string>(),
                CreatedAtUtc = DateTime.UtcNow
            }, string.Empty, matchedPlayers);

            var successResponse = new CenterMatchResponse
            {
                Success = true,
                RoomId = roomId,
                BattleNodeId = assignedBattleNode,
                SceneId = sceneResult.SceneId,
                SceneType = category,
                Message = $"Match successful. Welcome to {roomName}"
            };

            foreach (var pid in matchedPlayers)
            {
                if (pid != clientSessionId)
                {
                    sendToGatewayFunc(gatewaySession, pid, MessageIds.CenterMatchRes, successResponse);
                }
            }

            return successResponse;
        }

        public async Task<CenterCreateRoomResponse> HandleCreateRoomRequestAsync(long clientSessionId, int ownerUserId, string ownerUid, string ownerNickname, CenterCreateRoomRequest request)
        {
            string assignedBattleNode = NodeManager.Instance.GetBestBattleNode() ?? string.Empty;
            if (string.IsNullOrEmpty(assignedBattleNode))
            {
                Shared.Log.Warning($"创建房间失败：当前没有可用的战斗节点 SceneType:{request.SceneType}");
                return new CenterCreateRoomResponse
                {
                    Success = false,
                    Message = "当前没有可用的战斗节点"
                };
            }

            string roomId = "Room_" + Guid.NewGuid().ToString("N");
            string roomName = string.IsNullOrWhiteSpace(request.RoomName) ? $"{request.SceneType}_Room" : request.RoomName.Trim();
            int maxPlayers = request.MaxPlayers <= 0 ? 4 : request.MaxPlayers;
            bool hasPassword = !string.IsNullOrWhiteSpace(request.Password);
            bool isPrivate = request.IsPrivate || hasPassword;

            var sceneResult = await CreateSceneAsync(assignedBattleNode, new CenterCreateSceneRequest
            {
                RoomId = roomId,
                SceneType = string.IsNullOrWhiteSpace(request.SceneType) ? "PVP" : request.SceneType.Trim(),
                IsPrivate = isPrivate,
                RoomName = roomName,
                MaxPlayers = maxPlayers
            });

            if (sceneResult == null || !sceneResult.Success)
            {
                return new CenterCreateRoomResponse
                {
                    Success = false,
                    Message = "房间创建请求超时或失败"
                };
            }

            RegisterRoom(new RoomInfo
            {
                RoomId = roomId,
                RoomName = roomName,
                SceneId = sceneResult.SceneId,
                SceneType = string.IsNullOrWhiteSpace(request.SceneType) ? "PVP" : request.SceneType.Trim(),
                BattleNodeId = assignedBattleNode,
                OwnerUserId = ownerUserId,
                IsPrivate = isPrivate,
                HasPassword = hasPassword,
                MaxPlayers = maxPlayers,
                CurrentPlayers = clientSessionId > 0 ? 1 : 0,
                RoomStatus = RoomStatuses.Waiting,
                CustomRules = new Dictionary<string, string>(),
                CreatedAtUtc = DateTime.UtcNow
            }, request.Password, clientSessionId > 0 ? new[] { clientSessionId } : Array.Empty<long>(), ownerUserId, ownerUid, ownerNickname);

            return new CenterCreateRoomResponse
            {
                Success = true,
                RoomId = roomId,
                RoomName = roomName,
                BattleNodeId = assignedBattleNode,
                SceneId = sceneResult.SceneId,
                SceneType = string.IsNullOrWhiteSpace(request.SceneType) ? "PVP" : request.SceneType.Trim(),
                HasPassword = hasPassword,
                MaxPlayers = maxPlayers,
                CurrentPlayers = clientSessionId > 0 ? 1 : 0,
                Message = $"房间已创建 ({(isPrivate ? "私密" : "公开")}): {roomName}"
            };
        }

        public Task<CenterListRoomsResponse> HandleListRoomsRequestAsync(CenterListRoomsRequest request)
        {
            IEnumerable<RoomInfo> query = rooms.Values.Select(static entry => CloneRoomInfo(entry.Info));
            if (!string.IsNullOrWhiteSpace(request.SceneType))
            {
                query = query.Where(room => room.SceneType.Equals(request.SceneType.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!request.IncludePrivate)
            {
                query = query.Where(room => !room.IsPrivate);
            }

            RoomInfo[] roomList = query
                .OrderByDescending(room => room.CreatedAtUtc)
                .ThenBy(room => room.RoomId)
                .ToArray();

            return Task.FromResult(new CenterListRoomsResponse
            {
                Success = true,
                Message = $"已找到 {roomList.Length} 个房间",
                Rooms = roomList
            });
        }
    }
}
