using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared.Messages;
using Shared.Messages.Center;
namespace Center.Handlers
{
    /// <summary>
    /// 匹配 Handler —— 场景协同与广播模块（场景创建/销毁回包、人数同步、注册房间、广播助手、密码校验）。
    /// 与 MatchHandler.cs 同属一个 partial class，按业务模块分文件组织。
    /// </summary>
    public partial class MatchHandler
    {
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
                return await tcs.Task;
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
                return await tcs.Task;
            }

            pendingSceneDestroys.TryRemove(roomId, out _);
            return null;
        }

        private bool TryGetOwnedRoom(int requesterUserId, string roomId, out RoomRegistryEntry roomEntry, out CenterUpdateRoomSettingsResponse? errorResponse)
        {
            roomEntry = null!;
            errorResponse = null;

            if (string.IsNullOrWhiteSpace(roomId) || !rooms.TryGetValue(roomId.Trim(), out RoomRegistryEntry? found))
            {
                errorResponse = new CenterUpdateRoomSettingsResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                };
                return false;
            }
            roomEntry = found!;

            // 安全修复（P1）：仅已实名房主可修改设置/开赛；Owner=0（匹配房）或未绑定请求者一律拒绝，
            // 防止任意玩家绕过房主校验接管/锁死房间。
            if (roomEntry.Info.OwnerUserId <= 0 || requesterUserId <= 0 || roomEntry.Info.OwnerUserId != requesterUserId)
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

            // 安全修复：房间密码改用加盐 PBKDF2（统一委托 PasswordHasher），替代此前的无盐 SHA-256。
            return Framework.Core.Security.PasswordHasher.HashPassword(password.Trim());
        }

        private static bool IsPasswordValid(string storedHash, string password)
        {
            if (string.IsNullOrWhiteSpace(storedHash))
            {
                return true;
            }

            return Framework.Core.Security.PasswordHasher.VerifyPassword(password, storedHash);
        }
    }
}
