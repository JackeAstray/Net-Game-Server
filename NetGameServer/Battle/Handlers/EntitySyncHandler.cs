using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared.Messages.Battle;
using Shared.Messages;

namespace Battle.Handlers
{
    public class EntitySyncHandler
    {
        private readonly SceneManager _sceneManager;

        public EntitySyncHandler(SceneManager sceneManager)
        {
            _sceneManager = sceneManager;
        }

        /// <summary>
        /// 处理来自客户端的坐标/朝向同步请求。（在网关层通过转发过来，附带 OriginalSessionId）
        /// </summary>
        public Task HandleEntitySyncRequestAsync(long sessionId, EntitySyncRequest request, Network.ISession gatewaySession)
        {
            var scene = _sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return Task.CompletedTask;

            var entity = scene.EntityManager.GetEntity(sessionId);
            if (entity != null)
            {
                // 更新服务器端的状态
                entity.Position = request.Position;
                entity.Rotation = request.Rotation;

                if (scene.UseAoi && scene.AoiManager != null)
                {
                    // 在 AOI 中更新，并计算是否有越界
                    bool moved = scene.AoiManager.AddOrUpdateEntity(sessionId, entity, out var oldGrid, out var newGrid);
                    if (moved)
                    {
                        // 计算新旧视野差异，向需要了解该实体的客户端推送进出视野事件
                        scene.AoiManager.CalculateGridDiff(oldGrid, newGrid, out var enterEntities, out var leaveEntities);

                        // 1. 发送给老视野的玩家我离开了
                        if (leaveEntities.Count > 0)
                        {
                            var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                            byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                            foreach (var targetId in leaveEntities)
                            {
                                if (targetId == sessionId) continue;
                                SendPacket(gatewaySession, targetId, MessageIds.EntityLeaveViewNotif, leavePayload);
                            }

                            // 给我自己发：老地方的人离我而去了
                            var leaveSelf = new EntityLeaveViewNotification { EntityIds = leaveEntities };
                            SendPacket(gatewaySession, sessionId, MessageIds.EntityLeaveViewNotif, Shared.Json.SerializeToUtf8Bytes(leaveSelf));
                        }

                        // 2. 发送给新视野的玩家我进来了
                        if (enterEntities.Count > 0)
                        {
                            var enterNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { entity } };
                            byte[] enterPayload = Shared.Json.SerializeToUtf8Bytes(enterNotif);

                            var newlySeenStates = new List<EntityState>();

                            foreach (var targetId in enterEntities)
                            {
                                if (targetId == sessionId) continue;
                                SendPacket(gatewaySession, targetId, MessageIds.EntityEnterViewNotif, enterPayload);

                                var otherEntity = scene.EntityManager.GetEntity(targetId);
                                if (otherEntity != null)
                                {
                                    newlySeenStates.Add(otherEntity);
                                }
                            }

                            // 给我自己发：新地方的人我看见了
                            if (newlySeenStates.Count > 0)
                            {
                                var enterSelf = new EntityEnterViewNotification { Entities = newlySeenStates };
                                SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelf));
                            }
                        }
                    }

                    // 位移只向当前网格周围的实体的更新
                    var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);
                    BroadcastEntityStateUpdate(entity, surrounding, gatewaySession);
                }
                else
                {
                    // 没有 AOI（小房间开局模式），向所有人广播
                    BroadcastEntityStateUpdate(entity, scene.EntityManager.GetAllSessionIds(), gatewaySession);
                }
            }

            return Task.CompletedTask;
        }

        private void BroadcastEntityStateUpdate(EntityState state, IEnumerable<long> targetSessionIds, Network.ISession gatewaySession)
        {
            var notif = new EntityStateUpdateNotification { State = state };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(notif);

            foreach (var targetSessionId in targetSessionIds)
            {
                SendPacket(gatewaySession, targetSessionId, MessageIds.EntityStateUpdateNotif, payload);
            }
        }

        private void SendPacket(Network.ISession gatewaySession, long targetSessionId, int msgId, byte[] payload)
        {
            byte[] packet = new byte[12 + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), targetSessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), msgId);
            payload.CopyTo(packet.AsSpan(12));
            gatewaySession.Send(packet);
        }

        public void OnPlayerEnter(long sessionId, EntityState newState, Network.ISession gatewaySession)
        {
            var scene = _sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            scene.EntityManager.AddOrUpdateEntity(sessionId, newState);

            if (scene.UseAoi && scene.AoiManager != null)
            {
                scene.AoiManager.AddOrUpdateEntity(sessionId, newState, out _, out var newGrid);

                // 将自己推给新玩家，并获取周围玩家推给自己
                var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);

                var existingEntities = new List<EntityState>();
                var enterOthersNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { newState } };
                byte[] enterOthersPayload = Shared.Json.SerializeToUtf8Bytes(enterOthersNotif);

                foreach (var targetId in surrounding)
                {
                    if (targetId == sessionId) continue;

                    SendPacket(gatewaySession, targetId, MessageIds.EntityEnterViewNotif, enterOthersPayload);

                    var state = scene.EntityManager.GetEntity(targetId);
                    if (state != null) existingEntities.Add(state);
                }

                if (existingEntities.Count > 0)
                {
                    var enterSelfNotif = new EntityEnterViewNotification { Entities = existingEntities };
                    SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelfNotif));
                }
            }
            else
            {
                // 小房间广播
                var existingEntities = scene.EntityManager.GetAllEntities().ToList();
                if (existingEntities.Count > 0)
                {
                    var enterSelfNotif = new EntityEnterViewNotification { Entities = existingEntities };
                    SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelfNotif));
                }

                var enterOthersNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { newState } };
                byte[] enterOthersPayload = Shared.Json.SerializeToUtf8Bytes(enterOthersNotif);

                foreach (var targetId in scene.EntityManager.GetAllSessionIds())
                {
                    if (targetId == sessionId) continue;
                    SendPacket(gatewaySession, targetId, MessageIds.EntityEnterViewNotif, enterOthersPayload);
                }
            }
        }

        public void OnPlayerLeave(long sessionId, Network.ISession gatewaySession)
        {
             var scene = _sceneManager.GetSceneByPlayer(sessionId);
             if (scene == null) return;

             scene.EntityManager.RemoveEntity(sessionId);

             var targetIds = new List<long>();

             if (scene.UseAoi && scene.AoiManager != null)
             {
                 var entity = scene.EntityManager.GetEntity(sessionId);
                 if (entity != null)
                 {
                     var grid = scene.AoiManager.GetGridCoordinate(entity.Position);
                     targetIds = scene.AoiManager.GetSurroundingEntities(grid.Item1, grid.Item2);
                 }
                 scene.AoiManager.RemoveEntity(sessionId);
             }
             else
             {
                 targetIds = scene.EntityManager.GetAllSessionIds().ToList();
             }

             _sceneManager.UnbindPlayer(sessionId);

             if (targetIds.Count > 0)
             {
                 var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                 byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                 foreach (var targetId in targetIds)
                 {
                     if (targetId == sessionId) continue;
                     SendPacket(gatewaySession, targetId, MessageIds.EntityLeaveViewNotif, leavePayload);
                 }
             }
        }
    }
}