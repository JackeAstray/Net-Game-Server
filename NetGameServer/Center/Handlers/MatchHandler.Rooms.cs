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
    /// <summary>
    /// 匹配 Handler —— 房间生命周期与成员模块（加入/退出/设置/开局/关闭/转让/踢人/准备/聊天/成员列表）。
    /// 与 MatchHandler.cs 同属一个 partial class，按业务模块分文件组织（对标 KBE 按逻辑拆分）。
    /// </summary>
    public partial class MatchHandler
    {
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

            // 安全修复（P1）：仅已实名房主可关房；Owner=0（匹配房）或未绑定请求者一律拒绝，
            // 防止任意玩家关闭整局（DoS）。
            if (room.Info.OwnerUserId <= 0 || requesterUserId <= 0 || room.Info.OwnerUserId != requesterUserId)
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

        public async Task<CenterLeaveRoomResponse> HandleLeaveRoomRequestAsync(long clientSessionId, CenterLeaveRoomRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomClosedNotification> sendClosedToGatewayFunc, Action<Network.ISession, long, int, RoomMemberListChangedNotification> sendMemberListToGatewayFunc, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendOwnerChangedToGatewayFunc)
        {
            if (clientSessionId <= 0)
            {
                return new CenterLeaveRoomResponse
                {
                    Success = false,
                    Message = "无效的会话"
                };
            }

            if (string.IsNullOrWhiteSpace(request.RoomId))
            {
                return new CenterLeaveRoomResponse
                {
                    Success = false,
                    Message = "RoomId 不能为空"
                };
            }

            string roomId = request.RoomId.Trim();
            if (!rooms.TryGetValue(roomId, out var room))
            {
                return new CenterLeaveRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = "房间不存在或已关闭"
                };
            }

            if (!room.MemberStates.TryRemove(clientSessionId, out var leavingMember))
            {
                return new CenterLeaveRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = "当前不在该房间中"
                };
            }

            bool ownerLeft = room.Info.OwnerUserId > 0 && leavingMember.UserId == room.Info.OwnerUserId;
            room.Info.CurrentPlayers = room.MemberStates.Count;

            if (ownerLeft && room.MemberStates.Count > 0)
            {
                TryAutoTransferOwner(gatewaySession, room, sendOwnerChangedToGatewayFunc, "房主已自动转移");
            }

            room.Info.Members = BuildRoomMembers(room);
            var roomSnapshot = CloneRoomInfo(room.Info);

            if (room.MemberStates.Count > 0)
            {
                BroadcastRoomMemberListChanged(gatewaySession, room, sendMemberListToGatewayFunc, "房间成员列表已更新");
                return new CenterLeaveRoomResponse
                {
                    Success = true,
                    RoomId = roomId,
                    Message = "已退出房间",
                    Room = roomSnapshot
                };
            }

            room.Info.RoomStatus = RoomStatuses.Closed;
            var destroyResult = await DestroySceneAsync(room.Info.BattleNodeId, roomId);
            if (destroyResult == null || !destroyResult.Success)
            {
                Shared.Log.Warning($"主动离房后空房自动关闭失败 RoomId:{roomId} Message:{destroyResult?.Message}");
                return new CenterLeaveRoomResponse
                {
                    Success = false,
                    RoomId = roomId,
                    Message = destroyResult?.Message ?? "已退出房间，但空房关闭失败",
                    Room = roomSnapshot
                };
            }

            rooms.TryRemove(roomId, out _);
            BroadcastRoomClosedNotification(gatewaySession, destroyResult.AffectedSessionIds, roomId, sendClosedToGatewayFunc);
            Shared.Log.Info($"玩家主动离房导致空房关闭 RoomId:{roomId} ClientSessionId:{clientSessionId}");

            return new CenterLeaveRoomResponse
            {
                Success = true,
                RoomId = roomId,
                Message = "已退出房间，房间已关闭"
            };
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
                // 安全修复：非成员不允许通过 Ready 消息"后门加入"（此前会自动插入成员，绕过密码/人数上限校验）。
                return Task.FromResult(new RoomReadyResponse
                {
                    Success = false,
                    Message = "当前不在该房间中，请先加入房间"
                });
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

        public async Task<RoomKickMemberResponse> HandleRoomKickMemberRequestAsync(int requesterUserId, RoomKickMemberRequest request, Network.ISession gatewaySession, Action<Network.ISession, long, int, RoomMemberListChangedNotification> sendMemberListToGatewayFunc, Action<Network.ISession, long, int, RoomKickedNotification> sendKickedToGatewayFunc, Action<Network.ISession, long, int, RoomOwnerChangedNotification> sendOwnerChangedToGatewayFunc)
        {
            if (string.IsNullOrWhiteSpace(request.RoomId) || !rooms.TryGetValue(request.RoomId.Trim(), out var room))
            {
                return new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "房间不存在或已关闭"
                };
            }

            if (room.Info.OwnerUserId <= 0 || room.Info.OwnerUserId != requesterUserId)
            {
                return new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "只有当前房主可以踢人"
                };
            }

            if (request.TargetUserId == requesterUserId)
            {
                return new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "不能踢出自己"
                };
            }

            var targetPair = room.MemberStates.FirstOrDefault(pair => pair.Value.UserId == request.TargetUserId);
            if (targetPair.Key <= 0)
            {
                return new RoomKickMemberResponse
                {
                    Success = false,
                    Message = "目标成员不存在"
                };
            }

            var kickedMember = targetPair.Value;
            room.MemberStates.TryRemove(targetPair.Key, out _);
            room.Info.CurrentPlayers = room.MemberStates.Count;

            if (room.MemberStates.Count == 0)
            {
                room.Info.RoomStatus = RoomStatuses.Closed;
                // 安全修复：踢出最后一名成员后空房必须真正销毁场景并从注册表移除
                // （此前只置 Closed，导致 Battle 场景常驻 + 房间永久泄漏在列表中）。
                var destroyResult = await DestroySceneAsync(room.Info.BattleNodeId, room.Info.RoomId);
                if (destroyResult == null || !destroyResult.Success)
                {
                    Shared.Log.Warning($"踢出最后成员后空房关闭失败 RoomId:{room.Info.RoomId} Message:{destroyResult?.Message}");
                }
                rooms.TryRemove(room.Info.RoomId, out _);
                Shared.Log.Info($"踢出最后一名成员导致空房关闭 RoomId:{room.Info.RoomId}");
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

            return new RoomKickMemberResponse
            {
                Success = true,
                Message = $"已将 {kickedMember.DisplayName} 移出房间",
                Room = roomSnapshot
            };
        }
    }
}
