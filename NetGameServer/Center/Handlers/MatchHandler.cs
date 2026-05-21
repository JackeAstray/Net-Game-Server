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
    public class MatchHandler
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

        // 用于等待真实的 Battle 节点返回创建房间结果
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CenterCreateSceneResponse>> pendingSceneCreations = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CenterDestroySceneResponse>> pendingSceneDestroys = new();
        private readonly ConcurrentDictionary<string, RoomRegistryEntry> rooms = new();

        /// <summary>
        /// 处理匹配请求。根据 CategoryId 将玩家加入对应的匹配池。
        /// 当满足条件（如人数达到阈值）时，寻找真实的BattleNode请求创建场景并返回。
        /// </summary>
        public async Task<CenterMatchResponse?> HandleMatchRequestAsync(long clientSessionId, CenterMatchRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, CenterMatchResponse> sendToGatewayFunc)
        {
            string category = string.IsNullOrWhiteSpace(request.CategoryId) ? "PVP" : request.CategoryId.Trim();
            bool isWorldMap = category.Equals("World", StringComparison.OrdinalIgnoreCase);

            var pool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
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
                matchedPlayers.Add(pid);
            }

            void RestoreMatchedPlayersToPool()
            {
                var restorePool = matchPools.GetOrAdd(category, _ => new ConcurrentQueue<long>());
                foreach (var matchedPlayer in matchedPlayers)
                {
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

        public Task<CenterJoinRoomResponse> HandleJoinRoomRequestAsync(long clientSessionId, int requesterUserId, string requesterUid, string requesterNickname, CenterJoinRoomRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId))
            {
                return Task.FromResult(new CenterJoinRoomResponse
                {
                    Success = false,
                    Message = "RoomId 不能为空"
                });
            }

            if (!rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new CenterJoinRoomResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            if (room.Info.RoomStatus == RoomStatuses.Closed)
            {
                return Task.FromResult(new CenterJoinRoomResponse
                {
                    Success = false,
                    Message = "房间已关闭"
                });
            }

            if (room.Info.HasPassword && !IsPasswordValid(room.PasswordHash, request.Password))
            {
                return Task.FromResult(new CenterJoinRoomResponse
                {
                    Success = false,
                    RoomId = room.Info.RoomId,
                    RoomName = room.Info.RoomName,
                    HasPassword = true,
                    Message = "房间密码错误"
                });
            }

            if (room.Info.MaxPlayers > 0 && room.Info.CurrentPlayers >= room.Info.MaxPlayers && !room.MemberStates.ContainsKey(clientSessionId))
            {
                return Task.FromResult(new CenterJoinRoomResponse
                {
                    Success = false,
                    RoomId = room.Info.RoomId,
                    RoomName = room.Info.RoomName,
                    HasPassword = room.Info.HasPassword,
                    MaxPlayers = room.Info.MaxPlayers,
                    CurrentPlayers = room.Info.CurrentPlayers,
                    Message = "房间人数已满"
                });
            }

            if (clientSessionId > 0)
            {
                if (!room.MemberStates.TryGetValue(clientSessionId, out var memberState))
                {
                    memberState = new RoomMemberState
                    {
                        ClientSessionId = clientSessionId
                    };
                    room.MemberStates[clientSessionId] = memberState;
                }

                memberState.UserId = requesterUserId;
                memberState.UniqueId = requesterUid ?? string.Empty;
                memberState.IsReady = requesterUserId > 0 && requesterUserId == room.Info.OwnerUserId;
                memberState.DisplayName = !string.IsNullOrWhiteSpace(requesterNickname)
                    ? requesterNickname
                    : !string.IsNullOrWhiteSpace(requesterUid)
                        ? requesterUid
                        : requesterUserId > 0 ? $"Player_{requesterUserId}" : $"Player_{clientSessionId}";
                room.Info.CurrentPlayers = room.MemberStates.Count;
                room.Info.Members = BuildRoomMembers(room);
            }

            return Task.FromResult(new CenterJoinRoomResponse
            {
                Success = true,
                RoomId = room.Info.RoomId,
                RoomName = room.Info.RoomName,
                BattleNodeId = room.Info.BattleNodeId,
                SceneId = room.Info.SceneId,
                SceneType = room.Info.SceneType,
                HasPassword = room.Info.HasPassword,
                MaxPlayers = room.Info.MaxPlayers,
                CurrentPlayers = room.Info.CurrentPlayers,
                Message = room.Info.RoomStatus == RoomStatuses.Playing ? "房间已开局，可重新进入" : "允许加入房间"
            });
        }

        public Task<CenterUpdateRoomSettingsResponse> HandleUpdateRoomSettingsRequestAsync(int requesterUserId, CenterUpdateRoomSettingsRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomSettingsChangedNotification> sendToGatewayFunc)
        {
            if (!TryGetOwnedRoom(requesterUserId, request.RoomId, out var roomEntry, out var errorResponse))
            {
                return Task.FromResult(errorResponse!);
            }

            string sceneType = string.IsNullOrWhiteSpace(request.SceneType) ? roomEntry.Info.SceneType : request.SceneType.Trim();
            string roomName = string.IsNullOrWhiteSpace(request.RoomName) ? roomEntry.Info.RoomName : request.RoomName.Trim();
            int maxPlayers = request.MaxPlayers <= 0 ? roomEntry.Info.MaxPlayers : request.MaxPlayers;
            bool hasPassword = !string.IsNullOrWhiteSpace(request.Password);
            bool isPrivate = request.IsPrivate || hasPassword;

            roomEntry.Info.SceneType = sceneType;
            roomEntry.Info.RoomName = roomName;
            roomEntry.Info.MaxPlayers = maxPlayers;
            roomEntry.Info.IsPrivate = isPrivate;
            roomEntry.Info.HasPassword = hasPassword;
            roomEntry.Info.CustomRules = request.CustomRules ?? new Dictionary<string, string>();
            roomEntry.PasswordHash = ComputePasswordHash(request.Password);
            roomEntry.Info.Members = BuildRoomMembers(roomEntry);

            BroadcastRoomSettingsChanged(gatewaySession, roomEntry, sendToGatewayFunc, $"房间设置已更新：{roomName}");

            return Task.FromResult(new CenterUpdateRoomSettingsResponse
            {
                Success = true,
                Message = "房间设置更新成功",
                Room = CloneRoomInfo(roomEntry.Info)
            });
        }

        public Task<CenterStartRoomGameResponse> HandleStartRoomGameRequestAsync(int requesterUserId, CenterStartRoomGameRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomGameStartedNotification> sendToGatewayFunc)
        {
            if (!TryGetOwnedRoom(requesterUserId, request.RoomId, out var roomEntry, out _))
            {
                return Task.FromResult(new CenterStartRoomGameResponse
                {
                    Success = false,
                    Message = "只有房主可以开始游戏"
                });
            }

            if (roomEntry.MemberStates.Count <= 0)
            {
                return Task.FromResult(new CenterStartRoomGameResponse
                {
                    Success = false,
                    RoomId = roomEntry.Info.RoomId,
                    Message = "房间内暂无玩家，无法开始"
                });
            }

            bool hasUnreadyNonOwner = roomEntry.MemberStates.Values.Any(member => member.UserId != roomEntry.Info.OwnerUserId && !member.IsReady);
            if (hasUnreadyNonOwner)
            {
                return Task.FromResult(new CenterStartRoomGameResponse
                {
                    Success = false,
                    RoomId = roomEntry.Info.RoomId,
                    Message = "存在未准备成员，无法开始游戏"
                });
            }

            roomEntry.Info.RoomStatus = RoomStatuses.Playing;
            roomEntry.Info.CurrentPlayers = roomEntry.MemberStates.Count;
            roomEntry.Info.Members = BuildRoomMembers(roomEntry);
            var roomSnapshot = CloneRoomInfo(roomEntry.Info);
            BroadcastRoomGameStarted(gatewaySession, roomEntry, sendToGatewayFunc, roomSnapshot);

            return Task.FromResult(new CenterStartRoomGameResponse
            {
                Success = true,
                RoomId = roomSnapshot.RoomId,
                BattleNodeId = roomSnapshot.BattleNodeId,
                SceneId = roomSnapshot.SceneId,
                SceneType = roomSnapshot.SceneType,
                Message = "游戏开始",
                Room = roomSnapshot
            });
        }

        public async Task<CenterCloseRoomResponse> HandleCloseRoomRequestAsync(int requesterUserId, CenterCloseRoomRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomClosedNotification> sendToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId))
            {
                return new CenterCloseRoomResponse
                {
                    Success = false,
                    Message = "RoomId 不能为空"
                };
            }

            string roomId = request.RoomId.Trim();
            if (!rooms.TryGetValue(roomId, out var room))
            {
                return new CenterCloseRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = "房间不存在或已关闭"
                };
            }

            if (room.Info.OwnerUserId > 0 && requesterUserId > 0 && room.Info.OwnerUserId != requesterUserId)
            {
                return new CenterCloseRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = "只有房主可以关闭房间"
                };
            }

            var destroyResult = await DestroySceneAsync(room.Info.BattleNodeId, roomId);
            if (destroyResult == null || !destroyResult.Success)
            {
                return new CenterCloseRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = destroyResult?.Message ?? "关闭房间超时或失败"
                };
            }

            room.Info.RoomStatus = RoomStatuses.Closed;
            rooms.TryRemove(roomId, out _);
            BroadcastRoomClosedNotification(gatewaySession, destroyResult.AffectedSessionIds.Length > 0 ? destroyResult.AffectedSessionIds : room.MemberStates.Keys, roomId, sendToGatewayFunc);
            return new CenterCloseRoomResponse
            {
                Success = true,
                RoomId = roomId,
                Message = "房间已关闭"
            };
        }

        public void HandleCreateSceneResponse(CenterCreateSceneResponse response)
        {
            if (pendingSceneCreations.TryGetValue(response.RoomId, out var tcs))
            {
                tcs.TrySetResult(response);
            }
            else
            {
                Shared.Log.Warning($"收到未知的创建场景响应 RoomId:{response.RoomId} SceneId:{response.SceneId}");
            }
        }

        public void HandleDestroySceneResponse(CenterDestroySceneResponse response)
        {
            if (pendingSceneDestroys.TryGetValue(response.RoomId, out var tcs))
            {
                tcs.TrySetResult(response);
            }
            else
            {
                Shared.Log.Warning($"收到未知的销毁场景响应 RoomId:{response.RoomId}");
            }
        }

        public void HandleRoomPlayerCountSync(CenterRoomPlayerCountSyncRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId))
            {
                return;
            }

            string roomId = request.RoomId.Trim();
            if (rooms.TryGetValue(roomId, out var room))
            {
                room.Info.CurrentPlayers = Math.Max(0, request.CurrentPlayers);
                Shared.Log.Debug($"房间人数同步 RoomId:{roomId} CurrentPlayers:{room.Info.CurrentPlayers}");
            }
        }

        public async Task HandleRoomMemberLeaveSyncAsync(CenterRoomMemberLeaveSyncRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomClosedNotification> sendClosedToGatewayFunc, Action<Network.ISession, long, int, RoomMemberListChangedNotification> sendMemberListToGatewayFunc, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendOwnerChangedToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || request.ClientSessionId <= 0)
            {
                return;
            }

            string roomId = request.RoomId.Trim();
            if (!rooms.TryGetValue(roomId, out var room))
            {
                return;
            }

            bool ownerLeft = room.MemberStates.TryGetValue(request.ClientSessionId, out var leavingMember) && leavingMember.UserId == room.Info.OwnerUserId;
            room.MemberStates.TryRemove(request.ClientSessionId, out _);
            room.Info.CurrentPlayers = room.MemberStates.Count;

            if (ownerLeft && room.MemberStates.Count > 0)
            {
                TryAutoTransferOwner(gatewaySession, room, sendOwnerChangedToGatewayFunc, "房主已自动转移");
            }

            room.Info.Members = BuildRoomMembers(room);
            Shared.Log.Info($"房间成员离开同步 RoomId:{roomId} ClientSessionId:{request.ClientSessionId} CurrentPlayers:{room.Info.CurrentPlayers}");

            if (room.MemberStates.Count > 0)
            {
                BroadcastRoomMemberListChanged(gatewaySession, room, sendMemberListToGatewayFunc, "房间成员列表已更新");
                return;
            }

            room.Info.RoomStatus = RoomStatuses.Closed;
            var destroyResult = await DestroySceneAsync(room.Info.BattleNodeId, roomId);
            if (destroyResult == null || !destroyResult.Success)
            {
                Shared.Log.Warning($"空房自动关闭失败 RoomId:{roomId} Message:{destroyResult?.Message}");
                return;
            }

            rooms.TryRemove(roomId, out _);
            BroadcastRoomClosedNotification(gatewaySession, destroyResult.AffectedSessionIds, roomId, sendClosedToGatewayFunc);
            Shared.Log.Info($"空房已自动关闭 RoomId:{roomId}");
        }

        private void RegisterRoom(RoomInfo roomInfo, string password = "", IEnumerable<long>? memberSessionIds = null, int ownerUserId = 0, string ownerUid = "", string ownerNickname = "")
        {
            var entry = new RoomRegistryEntry
            {
                Info = roomInfo,
                PasswordHash = ComputePasswordHash(password)
            };

            if (memberSessionIds != null)
            {
                foreach (var sessionId in memberSessionIds.Where(static id => id > 0))
                {
                    entry.MemberStates[sessionId] = new RoomMemberState
                    {
                        ClientSessionId = sessionId,
                        UserId = ownerUserId,
                        IsReady = ownerUserId > 0 && ownerUserId == roomInfo.OwnerUserId,
                        DisplayName = !string.IsNullOrWhiteSpace(ownerNickname) ? ownerNickname : ownerUserId > 0 ? $"Owner_{ownerUserId}" : $"Player_{sessionId}",
                        UniqueId = ownerUid ?? string.Empty
                    };
                }
            }

            entry.Info.CurrentPlayers = entry.MemberStates.Count > 0 ? entry.MemberStates.Count : entry.Info.CurrentPlayers;
            entry.Info.Members = BuildRoomMembers(entry);
            rooms[roomInfo.RoomId] = entry;
        }

        private static void BroadcastRoomClosedNotification(Network.ISession gatewaySession, IEnumerable<long> affectedSessionIds, string roomId, Action<Network.ISession, long, int, RoomClosedNotification> sendToGatewayFunc)
        {
            foreach (long sessionId in affectedSessionIds.Distinct())
            {
                if (sessionId <= 0)
                {
                    continue;
                }

                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomClosedNotif, new RoomClosedNotification
                {
                    RoomId = roomId,
                    Message = "当前房间已被关闭，请退出并返回大厅。"
                });
            }
        }

        private async Task<CenterCreateSceneResponse?> CreateSceneAsync(string battleNodeId, CenterCreateSceneRequest request)
        {
            var battleNodeInfo = NodeManager.Instance.GetNode(battleNodeId);
            if (battleNodeInfo == null)
            {
                Shared.Log.Warning($"创建场景失败：Battle 节点信息不存在 NodeId:{battleNodeId}");
                return null;
            }

            var tcs = new TaskCompletionSource<CenterCreateSceneResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingSceneCreations[request.RoomId] = tcs;

            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(MessageIds.CenterCreateSceneReq, payload, out int totalLength);
            try
            {
                battleNodeInfo.Session.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                pendingSceneCreations.TryRemove(request.RoomId, out _);
                Shared.Log.Error($"创建房间请求发送失败 RoomId:{request.RoomId} BattleNode:{battleNodeId} Exception:{ex}");
                return null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var delayTask = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, delayTask) == tcs.Task)
            {
                pendingSceneCreations.TryRemove(request.RoomId, out _);
                return tcs.Task.Result;
            }

            pendingSceneCreations.TryRemove(request.RoomId, out _);
            return null;
        }

        private async Task<CenterDestroySceneResponse?> DestroySceneAsync(string battleNodeId, string roomId)
        {
            var battleNodeInfo = NodeManager.Instance.GetNode(battleNodeId);
            if (battleNodeInfo == null)
            {
                Shared.Log.Warning($"销毁场景失败：Battle 节点信息不存在 NodeId:{battleNodeId} RoomId:{roomId}");
                return null;
            }

            var tcs = new TaskCompletionSource<CenterDestroySceneResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingSceneDestroys[roomId] = tcs;

            byte[] payload = Shared.Json.SerializeToUtf8Bytes(new CenterDestroySceneRequest
            {
                RoomId = roomId
            });
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(MessageIds.CenterDestroySceneReq, payload, out int totalLength);
            try
            {
                battleNodeInfo.Session.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            catch (Exception ex)
            {
                pendingSceneDestroys.TryRemove(roomId, out _);
                Shared.Log.Error($"关闭房间请求发送失败 RoomId:{roomId} BattleNode:{battleNodeId} Exception:{ex}");
                return null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }

            var delayTask = Task.Delay(5000);
            if (await Task.WhenAny(tcs.Task, delayTask) == tcs.Task)
            {
                pendingSceneDestroys.TryRemove(roomId, out _);
                return tcs.Task.Result;
            }

            pendingSceneDestroys.TryRemove(roomId, out _);
            return null;
        }

        private bool TryGetOwnedRoom(int requesterUserId, string roomId, out RoomRegistryEntry roomEntry, out CenterUpdateRoomSettingsResponse? errorResponse)
        {
            roomEntry = null!;
            errorResponse = null;

            if (string.IsNullOrWhiteSpace(roomId) || !rooms.TryGetValue(roomId.Trim(), out roomEntry))
            {
                errorResponse = new CenterUpdateRoomSettingsResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                };
                return false;
            }

            if (roomEntry.Info.OwnerUserId > 0 && requesterUserId > 0 && roomEntry.Info.OwnerUserId != requesterUserId)
            {
                errorResponse = new CenterUpdateRoomSettingsResponse
                {
                    Success = false,
                    Message = "只有房主可以修改房间设置"
                };
                return false;
            }

            return true;
        }

        public Task<RoomMemberListResponse> HandleRoomMemberListRequestAsync(RoomMemberListRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new RoomMemberListResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            room.Info.Members = BuildRoomMembers(room);
            return Task.FromResult(new RoomMemberListResponse
            {
                Success = true,
                Message = "获取房间成员成功",
                Room = CloneRoomInfo(room.Info)
            });
        }

        public Task<RoomReadyResponse> HandleRoomReadyRequestAsync(long clientSessionId, int requesterUserId, string requesterUid, string requesterNickname, RoomReadyRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomReadyChangedNotification> sendToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new RoomReadyResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            if (!room.MemberStates.TryGetValue(clientSessionId, out var memberState))
            {
                memberState = new RoomMemberState
                {
                    ClientSessionId = clientSessionId,
                    UserId = requesterUserId,
                    UniqueId = requesterUid ?? string.Empty,
                    DisplayName = !string.IsNullOrWhiteSpace(requesterNickname) ? requesterNickname : requesterUserId > 0 ? $"Player_{requesterUserId}" : $"Player_{clientSessionId}"
                };
                room.MemberStates[clientSessionId] = memberState;
            }

            memberState.UserId = requesterUserId;
            memberState.UniqueId = requesterUid ?? string.Empty;
            if (string.IsNullOrWhiteSpace(memberState.DisplayName))
            {
                memberState.DisplayName = !string.IsNullOrWhiteSpace(requesterNickname) ? requesterNickname : requesterUserId > 0 ? $"Player_{requesterUserId}" : $"Player_{clientSessionId}";
            }

            memberState.IsReady = request.IsReady;
            room.Info.Members = BuildRoomMembers(room);
            BroadcastRoomReadyChanged(gatewaySession, room, sendToGatewayFunc, request.IsReady ? "成员已准备" : "成员取消准备");

            return Task.FromResult(new RoomReadyResponse
            {
                Success = true,
                Message = request.IsReady ? "准备成功" : "已取消准备",
                Room = CloneRoomInfo(room.Info)
            });
        }

        public Task<CenterRoomChatResponse> HandleRoomChatRequestAsync(long clientSessionId, int requesterUserId, string requesterUid, string requesterNickname, CenterRoomChatRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, CenterRoomChatNotification> sendToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new CenterRoomChatResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Task.FromResult(new CenterRoomChatResponse
                {
                    Success = false,
                    Message = "聊天内容不能为空"
                });
            }

            if (!room.MemberStates.TryGetValue(clientSessionId, out var memberState))
            {
                return Task.FromResult(new CenterRoomChatResponse
                {
                    Success = false,
                    Message = "当前不在该房间中"
                });
            }

            memberState.UserId = requesterUserId > 0 ? requesterUserId : memberState.UserId;
            if (!string.IsNullOrWhiteSpace(requesterUid))
            {
                memberState.UniqueId = requesterUid;
            }
            if (!string.IsNullOrWhiteSpace(requesterNickname))
            {
                memberState.DisplayName = requesterNickname;
            }

            var notification = new CenterRoomChatNotification
            {
                RoomId = room.Info.RoomId,
                SenderUserId = memberState.UserId,
                SenderUniqueId = memberState.UniqueId,
                SenderName = string.IsNullOrWhiteSpace(memberState.DisplayName) ? $"Player_{clientSessionId}" : memberState.DisplayName,
                Content = request.Content.Trim(),
                SendTimeUtc = DateTime.UtcNow
            };

            foreach (long sessionId in room.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.CenterRoomChatNotif, notification);
            }

            return Task.FromResult(new CenterRoomChatResponse
            {
                Success = true,
                Message = "房间消息发送成功"
            });
        }

        public Task<RoomTransferOwnerResponse> HandleRoomTransferOwnerRequestAsync(int requesterUserId, RoomTransferOwnerRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new RoomTransferOwnerResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            if (room.Info.OwnerUserId <= 0 || room.Info.OwnerUserId != requesterUserId)
            {
                return Task.FromResult(new RoomTransferOwnerResponse
                {
                    Success = false,
                    Message = "只有当前房主可以转移房主"
                });
            }

            var targetMember = room.MemberStates.Values.FirstOrDefault(member => member.UserId == request.TargetUserId);
            if (targetMember == null)
            {
                return Task.FromResult(new RoomTransferOwnerResponse
                {
                    Success = false,
                    Message = "目标成员不存在"
                });
            }

            room.Info.OwnerUserId = request.TargetUserId;
            targetMember.IsReady = true;
            room.Info.Members = BuildRoomMembers(room);
            var roomSnapshot = CloneRoomInfo(room.Info);
            BroadcastRoomOwnerChanged(gatewaySession, room, sendToGatewayFunc, $"房主已转移给 {targetMember.DisplayName}", roomSnapshot);

            return Task.FromResult(new RoomTransferOwnerResponse
            {
                Success = true,
                Message = "房主转移成功",
                Room = roomSnapshot
            });
        }

        public Task<RoomKickMemberResponse> HandleRoomKickMemberRequestAsync(int requesterUserId, RoomKickMemberRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomMemberListChangedNotification> sendMemberListToGatewayFunc, Action<Network.ISession, long, int, RoomKickedNotification> sendKickedToGatewayFunc, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendOwnerChangedToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return Task.FromResult(new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                });
            }

            if (room.Info.OwnerUserId <= 0 || room.Info.OwnerUserId != requesterUserId)
            {
                return Task.FromResult(new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "只有当前房主可以踢人"
                });
            }

            if (request.TargetUserId == requesterUserId)
            {
                return Task.FromResult(new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "不能踢出自己"
                });
            }

            var targetPair = room.MemberStates.FirstOrDefault(pair => pair.Value.UserId == request.TargetUserId);
            if (targetPair.Key <= 0)
            {
                return Task.FromResult(new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "目标成员不存在"
                });
            }

            var kickedMember = targetPair.Value;
            room.MemberStates.TryRemove(targetPair.Key, out _);
            room.Info.CurrentPlayers = room.MemberStates.Count;

            if (room.MemberStates.Count == 0)
            {
                room.Info.RoomStatus = RoomStatuses.Closed;
            }
            else if (room.Info.OwnerUserId == request.TargetUserId)
            {
                TryAutoTransferOwner(gatewaySession, room, sendOwnerChangedToGatewayFunc, "房主已自动转移");
            }

            room.Info.Members = BuildRoomMembers(room);
            var roomSnapshot = CloneRoomInfo(room.Info);

            sendKickedToGatewayFunc(gatewaySession, kickedMember.ClientSessionId, MessageIds.RoomKickedNotif, new RoomKickedNotification
            {
                RoomId = roomSnapshot.RoomId,
                Message = "你已被房主移出房间。"
            });

            if (room.MemberStates.Count > 0)
            {
                BroadcastRoomMemberListChanged(gatewaySession, room, sendMemberListToGatewayFunc, "房间成员列表已更新");
            }

            return Task.FromResult(new RoomKickMemberResponse
            {
                Success = true,
                Message = $"已将 {kickedMember.DisplayName} 移出房间",
                Room = roomSnapshot
            });
        }

        private static void BroadcastRoomSettingsChanged(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomSettingsChangedNotification> sendToGatewayFunc, string message)
        {
            var roomSnapshot = CloneRoomInfo(roomEntry.Info);
            foreach (long sessionId in roomEntry.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomSettingsChangedNotif, new RoomSettingsChangedNotification
                {
                    Room = roomSnapshot,
                    Message = message
                });
            }
        }

        private static void BroadcastRoomMemberListChanged(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomMemberListChangedNotification> sendToGatewayFunc, string message)
        {
            var roomSnapshot = CloneRoomInfo(roomEntry.Info);
            foreach (long sessionId in roomEntry.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomMemberListChangedNotif, new RoomMemberListChangedNotification
                {
                    Room = roomSnapshot,
                    Message = message
                });
            }
        }

        private static void BroadcastRoomReadyChanged(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomReadyChangedNotification> sendToGatewayFunc, string message)
        {
            var roomSnapshot = CloneRoomInfo(roomEntry.Info);
            foreach (long sessionId in roomEntry.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomReadyChangedNotif, new RoomReadyChangedNotification
                {
                    Room = roomSnapshot,
                    Message = message
                });
            }
        }

        private static void BroadcastRoomGameStarted(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomGameStartedNotification> sendToGatewayFunc, RoomInfo roomSnapshot)
        {
            foreach (long sessionId in roomEntry.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomGameStartedNotif, new RoomGameStartedNotification
                {
                    Success = true,
                    RoomId = roomSnapshot.RoomId,
                    BattleNodeId = roomSnapshot.BattleNodeId,
                    SceneId = roomSnapshot.SceneId,
                    SceneType = roomSnapshot.SceneType,
                    Message = "房主已开始游戏",
                    Room = roomSnapshot
                });
            }
        }

        private static bool TryAutoTransferOwner(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendToGatewayFunc, string message)
        {
            var nextOwner = roomEntry.MemberStates.Values.OrderBy(member => member.ClientSessionId).FirstOrDefault();
            if (nextOwner == null || nextOwner.UserId <= 0)
            {
                return false;
            }

            roomEntry.Info.OwnerUserId = nextOwner.UserId;
            roomEntry.Info.Members = BuildRoomMembers(roomEntry);
            BroadcastRoomOwnerChanged(gatewaySession, roomEntry, sendToGatewayFunc, message, CloneRoomInfo(roomEntry.Info));
            return true;
        }

        private static void BroadcastRoomOwnerChanged(Network.ISession gatewaySession, RoomRegistryEntry roomEntry, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendToGatewayFunc, string message, RoomInfo roomSnapshot)
        {
            foreach (long sessionId in roomEntry.MemberStates.Keys)
            {
                sendToGatewayFunc(gatewaySession, sessionId, MessageIds.RoomOwnerChangedNotif, new RoomOwnerChangedNotification
                {
                    Room = roomSnapshot,
                    Message = message
                });
            }
        }

        private static RoomMemberInfo[] BuildRoomMembers(RoomRegistryEntry roomEntry)
        {
            return roomEntry.MemberStates.Values
                .Select(member => new RoomMemberInfo
                {
                    UserId = member.UserId,
                    ClientSessionId = member.ClientSessionId,
                    IsOwner = roomEntry.Info.OwnerUserId > 0 && member.UserId == roomEntry.Info.OwnerUserId,
                    IsReady = member.IsReady,
                    DisplayName = !string.IsNullOrWhiteSpace(member.DisplayName)
                        ? member.DisplayName
                        : !string.IsNullOrWhiteSpace(member.UniqueId)
                            ? member.UniqueId
                            : member.UserId > 0 ? $"Player_{member.UserId}" : $"Player_{member.ClientSessionId}"
                })
                .OrderByDescending(member => member.IsOwner)
                .ThenBy(member => member.DisplayName)
                .ToArray();
        }

        private static RoomInfo CloneRoomInfo(RoomInfo source)
        {
            return new RoomInfo
            {
                RoomId = source.RoomId,
                RoomName = source.RoomName,
                SceneId = source.SceneId,
                SceneType = source.SceneType,
                BattleNodeId = source.BattleNodeId,
                OwnerUserId = source.OwnerUserId,
                IsPrivate = source.IsPrivate,
                HasPassword = source.HasPassword,
                MaxPlayers = source.MaxPlayers,
                CurrentPlayers = source.CurrentPlayers,
                RoomStatus = source.RoomStatus,
                CustomRules = source.CustomRules == null ? new Dictionary<string, string>() : new Dictionary<string, string>(source.CustomRules),
                Members = source.Members == null ? Array.Empty<RoomMemberInfo>() : source.Members.Select(member => new RoomMemberInfo
                {
                    UserId = member.UserId,
                    ClientSessionId = member.ClientSessionId,
                    IsOwner = member.IsOwner,
                    IsReady = member.IsReady,
                    DisplayName = member.DisplayName
                }).ToArray(),
                CreatedAtUtc = source.CreatedAtUtc
            };
        }

        private static string ComputePasswordHash(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                return string.Empty;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(password.Trim());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToBase64String(hash);
        }

        private static bool IsPasswordValid(string storedHash, string password)
        {
            if (string.IsNullOrWhiteSpace(storedHash))
            {
                return true;
            }

            return string.Equals(storedHash, ComputePasswordHash(password), StringComparison.Ordinal);
        }
    }
}
