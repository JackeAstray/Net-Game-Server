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
    /// 增量打包脏属性（PropertyCodec）→ 向视野内玩家广播 EntityDeltaSync；
    /// 玩家进入时下发全量 EntitySnapshot。
    /// </summary>
    public class EntitySyncHandler
    {
        private readonly SceneManager sceneManager;

        public EntitySyncHandler(SceneManager sceneManager)
        {
            this.sceneManager = sceneManager;
        }

        /// <summary>从玩家所在场景移除实体；若场景使用 AOI 同时移除。</summary>
        public void OnPlayerLeave(long sessionId)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene != null)
            {
                scene.EntityManager.RemoveEntity(sessionId);
                if (scene.UseAoi && scene.AoiManager != null)
                {
                    scene.AoiManager.RemoveEntity(sessionId);
                }
            }
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

            // 更新实体属性（值变化才标记脏）
            entity.Set("Position", new Float3(request.Position?.X ?? 0, request.Position?.Y ?? 0, request.Position?.Z ?? 0));
            entity.Set("Rotation", new Float3(request.Rotation?.X ?? 0, request.Rotation?.Y ?? 0, request.Rotation?.Z ?? 0));

            // 取脏属性并增量打包
            var dirty = entity.TakeDirtyProperties();
            if (dirty.Length > 0)
            {
                byte[] props = PropertyCodec.SerializeChanges(entity, dirty);

                if (scene.UseAoi && scene.AoiManager != null)
                {
                    // AOI 网格更新（跨格子时补发进出视野）
                    bool moved = scene.AoiManager.AddOrUpdateEntity(sessionId, entity, out var oldGrid, out var newGrid);
                    if (moved)
                    {
                        scene.AoiManager.CalculateGridDiff(oldGrid, newGrid, out var enterEntities, out var leaveEntities);

                        if (leaveEntities.Count > 0)
                        {
                            var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                            byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                            foreach (var targetId in leaveEntities)
                            {
                                if (targetId == sessionId) continue;
                                SendPacket(gatewaySession, targetId, GenIds.EntityLeaveViewNotify, leavePayload);
                            }
                        }

                        if (enterEntities.Count > 0)
                        {
                            byte[] snapshot = PropertyCodec.SerializeAll(entity);
                            foreach (var targetId in enterEntities)
                            {
                                if (targetId == sessionId) continue;
                                SendSnapshot(gatewaySession, targetId, sessionId, snapshot);
                            }
                        }
                    }

                    // 向周边玩家广播增量
                    var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);
                    BroadcastDelta(entity, dirty, props, surrounding, gatewaySession);
                }
                else
                {
                    // 小房间：向场景内所有玩家广播增量
                    BroadcastDelta(entity, dirty, props, scene.EntityManager.GetAllSessionIds(), gatewaySession);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>向目标玩家广播脏属性增量（对标 Witness 增量下发）。</summary>
        private void BroadcastDelta(Framework.Entity.Entity entity, string[] dirty, byte[] props, IEnumerable<long> targetSessionIds, Network.ISession gatewaySession)
        {
            var delta = new EntityDeltaSync
            {
                EntityId = entity.EntityId,
                Props = props
            };
            byte[] payload = delta.Serialize();
            foreach (var targetSessionId in targetSessionIds)
            {
                if (targetSessionId == entity.EntityId) continue;
                SendPacket(gatewaySession, targetSessionId, GenIds.EntityDeltaSync, payload);
            }
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

        /// <summary>玩家进入场景：创建实体、加入 AOI、下发全量快照给周边玩家。</summary>
        public void OnPlayerEnter(long sessionId, Framework.Entity.Entity entity, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            scene.EntityManager.AddOrUpdateEntity(sessionId, entity);
            byte[] snapshot = PropertyCodec.SerializeAll(entity);

            if (scene.UseAoi && scene.AoiManager != null)
            {
                scene.AoiManager.AddOrUpdateEntity(sessionId, entity, out _, out var newGrid);
                var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);

                var existingSnapshots = new List<(long id, byte[] props)>();
                foreach (var targetId in surrounding)
                {
                    if (targetId == sessionId) continue;
                    SendSnapshot(gatewaySession, targetId, sessionId, snapshot);

                    var other = scene.EntityManager.GetEntity(targetId);
                    if (other != null)
                    {
                        existingSnapshots.Add((targetId, PropertyCodec.SerializeAll(other)));
                    }
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
                    SendSnapshot(gatewaySession, sessionId, other.EntityId, PropertyCodec.SerializeAll(other));
                }

                foreach (var targetId in scene.EntityManager.GetAllSessionIds())
                {
                    if (targetId == sessionId) continue;
                    SendSnapshot(gatewaySession, targetId, sessionId, snapshot);
                }
            }
        }

        /// <summary>玩家离开场景：移除实体与 AOI，通知周边玩家。</summary>
        public void OnPlayerLeave(long sessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            var targetIds = new List<long>();

            if (scene.UseAoi && scene.AoiManager != null)
            {
                var entity = scene.EntityManager.GetEntity(sessionId);
                if (entity != null)
                {
                    var position = entity.Get<Float3>("Position");
                    var grid = scene.AoiManager.GetGridCoordinate(position);
                    targetIds = scene.AoiManager.GetSurroundingEntities(grid.Item1, grid.Item2);
                }

                scene.AoiManager.RemoveEntity(sessionId);
                scene.EntityManager.RemoveEntity(sessionId);
            }
            else
            {
                targetIds = scene.EntityManager.GetAllSessionIds().Where(id => id != sessionId).ToList();
                scene.EntityManager.RemoveEntity(sessionId);
            }

            sceneManager.UnbindPlayer(sessionId);

            if (targetIds.Count > 0)
            {
                var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                foreach (var targetId in targetIds)
                {
                    if (targetId == sessionId) continue;
                    SendPacket(gatewaySession, targetId, GenIds.EntityLeaveViewNotify, leavePayload);
                }
            }
        }

        /// <summary>组装 [MsgId(4)][Payload(带 __targetSessionId 路由元数据)] 并发送。</summary>
        private void SendPacket(Network.ISession gatewaySession, long targetSessionId, int msgId, byte[] payload)
        {
            byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
            byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
            try
            {
                gatewaySession.Send(packet.AsSpan(0, totalLength).ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }
}

