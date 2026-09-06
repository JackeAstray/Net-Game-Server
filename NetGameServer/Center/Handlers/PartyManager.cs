using System;
using System.Collections.Concurrent;
using System.Linq;
using Network;
using Network.Routing;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Center.Handlers
{
    /// <summary>
    /// 队伍管理器（A2）：创建/加入/离开/解散/踢人/就位，经 NodeManager 网关路由广播成员变化。
    /// 一个玩家同时只属于一个队伍；队长离队自动转让给最早加入的成员，队伍空则解散。
    /// 并发约束：单队伍成员变更在 lock(party) 内串行化。
    /// </summary>
    public sealed class PartyManager
    {
        public sealed class PartyMemberState
        {
            public long ClientSessionId { get; set; }
            public int UserId { get; set; }
            public string Nickname { get; set; } = string.Empty;
            public bool Ready { get; set; }
        }

        public sealed class Party
        {
            public string PartyId { get; set; } = string.Empty;
            public long OwnerClientSessionId { get; set; }
            public readonly ConcurrentDictionary<long, PartyMemberState> Members = new();
        }

        private readonly ConcurrentDictionary<string, Party> parties = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, string> memberParty = new();

        /// <summary>当前队伍数（监控/测试）。</summary>
        public int PartyCount => parties.Count;
        /// <summary>当前在队伍中的玩家数（监控/测试）。</summary>
        public int MemberCount => memberParty.Count;

        private static string NewPartyId()
            => Guid.NewGuid().ToString("N")[..8];

        public PartyCreateResponse HandleCreate(long clientSessionId, int userId, string nickname)
        {
            if (clientSessionId <= 0 || userId <= 0)
            {
                return new PartyCreateResponse { Success = false, Message = "会话未登录" };
            }
            if (memberParty.ContainsKey(clientSessionId))
            {
                return new PartyCreateResponse { Success = false, Message = "你已在队伍中" };
            }

            string partyId = NewPartyId();
            var party = new Party
            {
                PartyId = partyId,
                OwnerClientSessionId = clientSessionId
            };
            party.Members[clientSessionId] = new PartyMemberState
            {
                ClientSessionId = clientSessionId,
                UserId = userId,
                Nickname = nickname ?? string.Empty
            };
            parties[partyId] = party;
            memberParty[clientSessionId] = partyId;

            SendNotif(clientSessionId, partyId, "created", "队伍已创建", party, null);
            return new PartyCreateResponse { Success = true, PartyId = partyId, Message = "队伍已创建" };
        }

        public PartyJoinResponse HandleJoin(long clientSessionId, int userId, string nickname, string partyId)
        {
            if (clientSessionId <= 0 || userId <= 0 || string.IsNullOrWhiteSpace(partyId))
            {
                return new PartyJoinResponse { Success = false, Message = "参数无效" };
            }
            if (memberParty.ContainsKey(clientSessionId))
            {
                return new PartyJoinResponse { Success = false, Message = "你已在队伍中" };
            }
            if (!parties.TryGetValue(partyId, out var party))
            {
                return new PartyJoinResponse { Success = false, Message = "队伍不存在" };
            }

            lock (party)
            {
                if (memberParty.ContainsKey(clientSessionId))
                {
                    return new PartyJoinResponse { Success = false, Message = "你已在队伍中" };
                }
                party.Members[clientSessionId] = new PartyMemberState
                {
                    ClientSessionId = clientSessionId,
                    UserId = userId,
                    Nickname = nickname ?? string.Empty
                };
                memberParty[clientSessionId] = partyId;
            }

            SendNotif(clientSessionId, partyId, "member_joined", $"{nickname} 加入队伍", party, null);
            return new PartyJoinResponse { Success = true, PartyId = partyId, Message = "已加入队伍" };
        }

        public PartyLeaveResponse HandleLeave(long clientSessionId)
        {
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return new PartyLeaveResponse { Success = false, Message = "你不在队伍中" };
            }

            return RemoveMember(party, clientSessionId, "member_left", "已离开队伍");
        }

        public PartyDisbandResponse HandleDisband(long clientSessionId)
        {
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return new PartyDisbandResponse { Success = false, Message = "你不在队伍中" };
            }
            if (party.OwnerClientSessionId != clientSessionId)
            {
                return new PartyDisbandResponse { Success = false, Message = "只有队长才能解散队伍" };
            }

            // 通知全部成员后移除
            SendNotif(clientSessionId, partyId, "disbanded", "队伍已解散", party, null);
            foreach (var m in party.Members.Keys)
            {
                memberParty.TryRemove(m, out _);
            }
            parties.TryRemove(partyId, out _);
            return new PartyDisbandResponse { Success = true, Message = "队伍已解散" };
        }

        public PartyMyResponse HandleMy(long clientSessionId)
        {
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return new PartyMyResponse { Success = false, Message = "未加入队伍" };
            }
            return new PartyMyResponse
            {
                Success = true,
                PartyId = partyId,
                OwnerClientSessionId = party.OwnerClientSessionId,
                Members = SnapshotMembers(party)
            };
        }

        public PartyKickResponse HandleKick(long clientSessionId, long targetClientSessionId)
        {
            if (targetClientSessionId <= 0)
            {
                return new PartyKickResponse { Success = false, Message = "参数无效" };
            }
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return new PartyKickResponse { Success = false, Message = "你不在队伍中" };
            }
            if (party.OwnerClientSessionId != clientSessionId)
            {
                return new PartyKickResponse { Success = false, Message = "只有队长才能踢人" };
            }
            if (targetClientSessionId == clientSessionId)
            {
                return new PartyKickResponse { Success = false, Message = "不能踢自己" };
            }
            if (!party.Members.ContainsKey(targetClientSessionId))
            {
                return new PartyKickResponse { Success = false, Message = "目标不在队伍中" };
            }

            var removed = RemoveMember(party, targetClientSessionId, "kicked", "你已被移出队伍");
            if (removed.Success)
            {
                return new PartyKickResponse { Success = true, Message = "已移出成员" };
            }
            return new PartyKickResponse { Success = false, Message = removed.Message };
        }

        public PartyReadyResponse HandleReady(long clientSessionId, bool ready)
        {
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return new PartyReadyResponse { Success = false, Message = "你不在队伍中" };
            }
            lock (party)
            {
                if (party.Members.TryGetValue(clientSessionId, out var member))
                {
                    member.Ready = ready;
                }
            }
            SendNotif(clientSessionId, partyId, "ready_changed", ready ? "已准备" : "取消准备", party, null);
            return new PartyReadyResponse { Success = true, Ready = ready, Message = "ok" };
        }

        /// <summary>客户端断线清理（网关通知）：自动离队；队长断线转让。</summary>
        public void HandleClientDisconnect(long clientSessionId)
        {
            if (!memberParty.TryGetValue(clientSessionId, out var partyId) || !parties.TryGetValue(partyId, out var party))
            {
                return;
            }
            RemoveMember(party, clientSessionId, "member_left", "成员已离线");
        }

        /// <summary>移除成员；队长被移除时自动转让给最早加入的成员；队伍空则解散。</summary>
        private PartyLeaveResponse RemoveMember(Party party, long clientSessionId, string notifType, string message)
        {
            lock (party)
            {
                if (!party.Members.TryRemove(clientSessionId, out _))
                {
                    return new PartyLeaveResponse { Success = false, Message = "你不在队伍中" };
                }
                memberParty.TryRemove(clientSessionId, out _);

                if (party.Members.IsEmpty)
                {
                    parties.TryRemove(party.PartyId, out _);
                    return new PartyLeaveResponse { Success = true, Message = message };
                }

                // 队长离开：转让给最早加入的成员
                if (party.OwnerClientSessionId == clientSessionId)
                {
                    var first = party.Members.Values.OrderBy(m => m.ClientSessionId).First();
                    party.OwnerClientSessionId = first.ClientSessionId;
                }

                SendNotif(clientSessionId, party.PartyId, notifType, message, party, null);
                return new PartyLeaveResponse { Success = true, Message = message };
            }
        }

        private static System.Collections.Generic.List<PartyMemberInfo> SnapshotMembers(Party party)
        {
            return party.Members.Values
                .OrderBy(m => m.ClientSessionId == party.OwnerClientSessionId ? 0 : 1)
                .ThenBy(m => m.ClientSessionId)
                .Select(m => new PartyMemberInfo
                {
                    ClientSessionId = m.ClientSessionId,
                    UserId = m.UserId,
                    Nickname = m.Nickname,
                    Ready = m.Ready
                })
                .ToList();
        }

        /// <summary>把队伍变化通知广播给全部成员（含触发者），经 NodeManager 网关路由逐会话投递。</summary>
        private void SendNotif(long triggerClientSessionId, string partyId, string type, string message, Party party, PartyMemberState? target)
        {
            var notif = new Shared.Messages.Center.PartyMemberNotification
            {
                PartyId = partyId,
                Type = type,
                TargetClientSessionId = target?.ClientSessionId ?? 0,
                Message = message,
                Members = SnapshotMembers(party)
            };
            foreach (var memberSessionId in party.Members.Keys)
            {
                if (!NodeManager.Instance.TryGetGatewaySessionByClientSessionId(memberSessionId, out var gateway))
                {
                    continue;
                }
                byte[] routed = Shared.RouteMetadata.AttachTargetSessionId(Shared.Json.SerializeToUtf8Bytes(notif), memberSessionId);
                byte[] memberPacket = PacketBuilder.BuildPacket(MessageIds.PartyMemberNotif, routed, out int memberLen);
                try
                {
                    gateway.Send(memberPacket.AsSpan(0, memberLen).ToArray());
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"队伍通知发送异常 Party:{partyId} Type:{type} SessionId:{memberSessionId} Exception:{ex.Message}");
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(memberPacket);
                }
            }
        }
    }
}
