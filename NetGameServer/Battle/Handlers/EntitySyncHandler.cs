using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Framework.Entity;
using Framework.Protocol.Generated;
using Shared.Messages;
using Shared.Messages.Battle;
using GenIds = Framework.Protocol.Generated.MessageIds;

namespace Battle.Handlers
{
    /// <summary>
    /// 实体状态同步处理器（对标 KBE Witness）。
    /// 基于实体框架：客户端上报位置/朝向 → 更新实体属性（脏标记）→
    /// 按同步权限分级增量打包脏属性（PropertyCodec）→ 向视野内玩家广播 EntityDeltaSync；
    /// 玩家进入时下发全量 EntitySnapshot。
    /// 每 tick 的 Witness 广播（TickWitness）负责推送脚本/AI 驱动的属性变化
    /// （NPC 巡逻、回血、冷却、掉落），无需客户端上报。
    /// </summary>
    public class EntitySyncHandler
    {
        private readonly SceneManager sceneManager;

        public EntitySyncHandler(SceneManager sceneManager)
        {
            this.sceneManager = sceneManager;
        }

        /// <summary>单次坐标同步的最大位移（世界单位），可由配置 MaxEntityMoveDistancePerSync 覆盖。</summary>
        private const float DefaultMaxMoveDistancePerSync = 20f;

        private static float MaxMoveDistancePerSync
        {
            get
            {
                float cfg = Shared.ConfigHelper.GetConfig<float>("MaxEntityMoveDistancePerSync");
                return cfg > 0 ? cfg : DefaultMaxMoveDistancePerSync;
            }
        }

        /// <summary>拒绝 NaN/Inf，并按单次最大位移钳制移动（服务端权威，防瞬移/加速）。</summary>
        private static Float3 SanitizeAndClampMovement(Float3 from, Float3 to)
        {
            if (float.IsNaN(to.X) || float.IsNaN(to.Y) || float.IsNaN(to.Z) ||
                float.IsInfinity(to.X) || float.IsInfinity(to.Y) || float.IsInfinity(to.Z))
            {
                return from; // 非法输入：保持服务端已知位置
            }

            float maxDist = MaxMoveDistancePerSync;
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float dz = to.Z - from.Z;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (distSq <= maxDist * maxDist)
            {
                return to;
            }

            float dist = MathF.Sqrt(distSq);
            float scale = maxDist / dist;
            return new Float3(from.X + dx * scale, from.Y + dy * scale, from.Z + dz * scale);
        }

        /// <summary>将向量中的 NaN/Inf 分量归零（防止污染客户端渲染/服务器计算）。Float3 字段只读，返回新实例。</summary>
        private static Float3 SanitizeVector(Float3 v)
        {
            float x = (float.IsNaN(v.X) || float.IsInfinity(v.X)) ? 0 : v.X;
            float y = (float.IsNaN(v.Y) || float.IsInfinity(v.Y)) ? 0 : v.Y;
            float z = (float.IsNaN(v.Z) || float.IsInfinity(v.Z)) ? 0 : v.Z;
            return new Float3(x, y, z);
        }

        /// <summary>
        /// 处理来自客户端的坐标/朝向同步请求。
        /// 更新实体属性并广播脏属性增量（仅变化字段，对标 KBE volatile 增量同步）。
        /// </summary>
        public Task HandleEntitySyncRequestAsync(long sessionId, EntitySyncRequest request, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return Task.CompletedTask;

            var entity = scene.EntityManager.GetEntity(sessionId);
            if (entity == null)
            {
                // 玩家尚未创建实体（未走加入流程），忽略
                return Task.CompletedTask;
            }

            // 服务端权威移动校验（防瞬移/加速/坐标注入）：
            // - 拒绝 NaN/Inf 坐标（保持服务端已知位置）；
            // - 按单次同步最大位移钳制（网络抖动可接受范围内），超距移动被拉回。
            var oldPos = entity.Get<Float3>("Position");
            var newPos = SanitizeAndClampMovement(oldPos,
                new Float3(request.Position?.X ?? 0, request.Position?.Y ?? 0, request.Position?.Z ?? 0));
            entity.Set("Position", newPos);
            entity.Set("Rotation", SanitizeVector(
                new Float3(request.Rotation?.X ?? 0, request.Rotation?.Y ?? 0, request.Rotation?.Z ?? 0)));

            if (!entity.IsDirty)
            {
                return Task.CompletedTask;
            }

            // AOI 网格更新（跨格子时补发进出视野）
            if (scene.UseAoi && scene.AoiManager != null)
            {
                UpdateAoiGrid(scene, entity);
            }

            // 按同步权限分级广播脏属性增量（Witness）
            BroadcastDirty(entity, scene);

            return Task.CompletedTask;
        }

        /// <summary>
        /// 每 tick 的 Witness 广播（对标 KBE Witness 主循环）：
        /// 脚本/AI 驱动的属性变化无需客户端上报即可增量广播给视野内玩家。
        /// 由 BattleServerApp 的 TickEngine 驱动。
        /// </summary>
        public void TickWitness()
        {
            foreach (var scene in sceneManager.GetAllScenes())
            {
                foreach (var entity in scene.EntityManager.GetAllEntities())
                {
                    if (!entity.IsDirty) continue;

                    BroadcastDirty(entity, scene);

                    // 脚本移动的实体（NPC 巡逻）需要同步 AOI 网格，保证视野正确
                    if (scene.UseAoi && scene.AoiManager != null)
                    {
                        UpdateAoiGrid(scene, entity);
                    }
                }
            }
        }

        /// <summary>
        /// 按同步权限分级广播脏属性（对标 KBE Witness 的 ALL_CLIENTS / OWN_CLIENT 分级）：
        /// - AllClients / CellPublic → 视野内/场景内所有玩家
        /// - OwnClient → 仅实体属主客户端（Entity.OwnerClientId）
        /// </summary>
        private void BroadcastDirty(Framework.Entity.Entity entity, BattleScene scene)
        {
            var dirty = entity.TakeDirtyProperties();
            if (dirty.Length == 0) return;

            var allScopeNames = new List<string>(dirty.Length);
            var ownScopeNames = new List<string>();

            foreach (var name in dirty)
            {
                if (!entity.Def.TryGetProperty(name, out var prop)) continue;
                if (prop.SyncScope == EntitySyncScope.OwnClient)
                {
                    ownScopeNames.Add(name);
                }
                else
                {
                    allScopeNames.Add(name);
                }
            }

            if (allScopeNames.Count > 0)
            {
                byte[] props = PropertyCodec.SerializeChanges(entity, allScopeNames);
                BroadcastToTargets(entity, props, GetBroadcastTargets(scene, entity));
            }

            if (ownScopeNames.Count > 0 && entity.OwnerClientId > 0)
            {
                byte[] props = PropertyCodec.SerializeChanges(entity, ownScopeNames);
                BroadcastToTargets(entity, props, new List<long> { entity.OwnerClientId });
            }
        }

        /// <summary>
        /// 计算广播目标：AOI 场景取周边九宫格内玩家，小房间取场景内全部玩家。
        /// 属主（Entity.OwnerClientId，含实体自身）始终可见自身状态——受击掉血、冷却、背包等
        /// 变更必须回发属主客户端（对标 KBE：owner 永远在自身 witness 内）。
        /// </summary>
        private List<long> GetBroadcastTargets(BattleScene scene, Framework.Entity.Entity entity)
        {
            var players = GetPlayerSet(scene);
            var result = new List<long>();
            if (entity.OwnerClientId > 0 && players.Contains(entity.OwnerClientId))
            {
                result.Add(entity.OwnerClientId);
            }

            if (scene.UseAoi && scene.AoiManager != null)
            {
                var pos = entity.Get<Framework.Entity.Float3>("Position");
                var (gx, gz) = scene.AoiManager.GetGridCoordinate(pos);
                foreach (var id in scene.AoiManager.GetSurroundingEntities(gx, gz))
                {
                    if (id != entity.EntityId && players.Contains(id) && !result.Contains(id)) result.Add(id);
                }
                return result;
            }

            foreach (var id in players)
            {
                if (id != entity.EntityId && !result.Contains(id)) result.Add(id);
            }
            return result;
        }

        /// <summary>向目标客户端发送增量（每个目标经其网关会话定向投递，网关会话未知则跳过）。</summary>
        private void BroadcastToTargets(Framework.Entity.Entity entity, byte[] props, List<long> targetIds)
        {
            if (targetIds.Count == 0) return;
            var delta = new EntityDeltaSync { EntityId = entity.EntityId, Props = props };
            byte[] payload = delta.Serialize();
            foreach (var targetId in targetIds)
            {
                var gatewaySession = BattleServerApp.GetGatewaySessionByClient(targetId);
                if (gatewaySession == null) continue;
                SendPacket(gatewaySession, targetId, GenIds.EntityDeltaSync, payload);
            }
        }

        /// <summary>场景内玩家会话集合（广播目标只允许玩家；NPC/玩法实体不参与收包）。</summary>
        private HashSet<long> GetPlayerSet(BattleScene scene) => new(sceneManager.GetPlayerSessionIds(scene.SceneId));

        /// <summary>实体 AOI 网格更新：跨格子时向受影响玩家补发进入快照/离开通知（目标仅限玩家）。</summary>
        private void UpdateAoiGrid(BattleScene scene, Framework.Entity.Entity entity)
        {
            var aoi = scene.AoiManager;
            if (aoi == null) return;

            bool moved = aoi.AddOrUpdateEntity(entity.EntityId, entity, out var oldGrid, out var newGrid);
            if (!moved) return;

            var players = GetPlayerSet(scene);
            aoi.CalculateGridDiff(oldGrid, newGrid, out var enterEntities, out var leaveEntities);

            if (leaveEntities.Count > 0)
            {
                var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { entity.EntityId } };
                byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                foreach (var targetId in leaveEntities)
                {
                    if (targetId == entity.EntityId || !players.Contains(targetId)) continue;
                    var gatewaySession = BattleServerApp.GetGatewaySessionByClient(targetId);
                    if (gatewaySession != null)
                    {
                        SendPacket(gatewaySession, targetId, GenIds.EntityLeaveViewNotify, leavePayload);
                    }
                }
            }

            if (enterEntities.Count > 0)
            {
                // 安全修复：全量快照剔除 OWN_CLIENT 私有属性（装备/冷却/背包），避免泄露给视野内其他玩家
                byte[] snapshot = PropertyCodec.SerializeAll(entity, includeOwnClient: false);
                foreach (var targetId in enterEntities)
                {
                    if (targetId == entity.EntityId || !players.Contains(targetId)) continue;
                    var gatewaySession = BattleServerApp.GetGatewaySessionByClient(targetId);
                    if (gatewaySession != null)
                    {
                        SendSnapshot(gatewaySession, targetId, entity.EntityId, snapshot);
                    }
                }
            }
        }

        /// <summary>玩家进入场景：创建实体、加入 AOI、下发全量快照给周边玩家。</summary>
        public void OnPlayerEnter(long sessionId, Framework.Entity.Entity entity, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            scene.EntityManager.AddOrUpdateEntity(sessionId, entity);
            // 全量快照：剔除 OWN_CLIENT 私有属性（该快照会发给视野内其他玩家）
            byte[] snapshot = PropertyCodec.SerializeAll(entity, includeOwnClient: false);

            if (scene.UseAoi && scene.AoiManager != null)
            {
                scene.AoiManager.AddOrUpdateEntity(sessionId, entity, out _, out var newGrid);
                var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);

                // P3 修复：surrounding 含 NPC/玩法实体。分两步——
                // 1) 收集周边实体（含非玩家）快照，回发给新玩家（新玩家应看到视野内 NPC）；
                // 2) 入场通知只发给"玩家"（非玩家 id 无网关会话，原实现对它们也发了新玩家快照，纯浪费投递）。
                var existingSnapshots = new List<(long id, byte[] props)>();
                foreach (var targetId in surrounding)
                {
                    if (targetId == sessionId) continue;
                    var other = scene.EntityManager.GetEntity(targetId);
                    if (other != null)
                    {
                        // 回发给新玩家的他人快照同样剔除 OWN_CLIENT（新玩家看不到他人私有属性）
                        existingSnapshots.Add((targetId, PropertyCodec.SerializeAll(other, includeOwnClient: false)));
                    }
                }

                var players = GetPlayerSet(scene);
                foreach (var targetId in surrounding)
                {
                    if (targetId == sessionId || !players.Contains(targetId)) continue;
                    // 跨网关修复：入场通知需投递给"目标玩家自身所在的网关"（与 BroadcastToTargets/UpdateAoiGrid 一致），
                    // 不能复用进入玩家自己的网关会话——多网关部署下其他玩家在别的网关会导致消息被丢弃。
                    var targetGateway = BattleServerApp.GetGatewaySessionByClient(targetId);
                    if (targetGateway == null) continue;
                    SendSnapshot(targetGateway, targetId, sessionId, snapshot);
                }

                // 回发已有玩家快照给新玩家
                foreach (var (id, props) in existingSnapshots)
                {
                    SendSnapshot(gatewaySession, sessionId, id, props);
                }
            }
            else
            {
                // 小房间：先把已存在实体快照发给进入玩家，再通知其他玩家
                var existingEntities = scene.EntityManager.GetAllEntities().Where(e => e.EntityId != sessionId).ToList();
                foreach (var other in existingEntities)
                {
                    // 全量快照剔除 OWN_CLIENT（对方私有属性不可见）
                    SendSnapshot(gatewaySession, sessionId, other.EntityId, PropertyCodec.SerializeAll(other, includeOwnClient: false));
                }

                foreach (var targetId in scene.EntityManager.GetAllSessionIds())
                {
                    if (targetId == sessionId) continue;
                    // 跨网关修复：入场通知投递到目标玩家自身所在网关（同 AOI 分支）
                    var targetGateway = BattleServerApp.GetGatewaySessionByClient(targetId);
                    if (targetGateway == null) continue;
                    SendSnapshot(targetGateway, targetId, sessionId, snapshot);
                }
            }
        }

        /// <summary>玩家离开场景：移除实体与 AOI，通知周边玩家。</summary>
        public void OnPlayerLeave(long sessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            var players = GetPlayerSet(scene);
            var targetIds = new List<long>();

            if (scene.UseAoi && scene.AoiManager != null)
            {
                var entity = scene.EntityManager.GetEntity(sessionId);
                if (entity != null)
                {
                    var position = entity.Get<Float3>("Position");
                    var grid = scene.AoiManager.GetGridCoordinate(position);
                    foreach (var id in scene.AoiManager.GetSurroundingEntities(grid.Item1, grid.Item2))
                    {
                        if (id != sessionId && players.Contains(id)) targetIds.Add(id);
                    }
                }

                scene.AoiManager.RemoveEntity(sessionId);
                scene.EntityManager.RemoveEntity(sessionId);
            }
            else
            {
                targetIds = players.Where(id => id != sessionId).ToList();
                scene.EntityManager.RemoveEntity(sessionId);
            }

            sceneManager.UnbindPlayer(sessionId);

            if (targetIds.Count > 0)
            {
                var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                foreach (var targetId in targetIds)
                {
                    // 跨网关修复：离开通知投递到目标玩家自身所在网关（同 BroadcastToTargets/UpdateAoiGrid）
                    var targetGateway = BattleServerApp.GetGatewaySessionByClient(targetId);
                    if (targetGateway == null) continue;
                    SendPacket(targetGateway, targetId, GenIds.EntityLeaveViewNotify, leavePayload);
                }
            }
        }

        /// <summary>组装 [MsgId(4)][Payload(带 __targetSessionId 路由元数据)] 并零拷贝发送。</summary>
        private void SendPacket(Network.ISession gatewaySession, long targetSessionId, int msgId, byte[] payload)
        {
            // 性能优化（P-M2）：直接组装 [len][msgId][body][元数据尾部块] 到池化缓冲，
            // 替代 AttachTargetSessionId 的"字典+JSON+中间数组"两步（每目标省 2 次分配 + 1 次 payload 拷贝）。
            byte[] metaJson = BuildTargetMetadataJson(targetSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacketWithMetadata(msgId, payload, metaJson, out int totalLength);
            Network.PacketSender.Send(gatewaySession, packet, totalLength);
        }

        /// <summary>构造单字段路由元数据 JSON（{"__targetSessionId":"&lt;id&gt;"}），避免逐包分配字典/序列化器。</summary>
        private static byte[] BuildTargetMetadataJson(long targetSessionId)
        {
            string json = "{\"__targetSessionId\":\"" + targetSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}";
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        /// <summary>向单个玩家发送全量快照。</summary>
        private void SendSnapshot(Network.ISession gatewaySession, long targetSessionId, long entityId, byte[] props)
        {
            var snapshot = new EntitySnapshot
            {
                EntityId = entityId,
                Props = props
            };
            byte[] payload = snapshot.Serialize();
            SendPacket(gatewaySession, targetSessionId, GenIds.EntitySnapshot, payload);
        }
    }
}
