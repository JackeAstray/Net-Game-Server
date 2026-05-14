using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shared.Messages.Battle;
using Shared.Messages;

namespace Battle.Handlers
{
    /// <summary>
    /// 实体状态同步处理器
    /// 负责处理来自客户端（经由网关转发）的位置信息、朝向信息等同步请求，
    /// 并根据场景是否使用 AOI（视野剔除）决定广播策略：
    /// - 若使用 AOI，则只向视野内/周边玩家广播，并在实体跨格子时发送进出视野通知；
    /// - 若不使用 AOI，则向场景内所有玩家广播。
    /// </summary>
    public class EntitySyncHandler
    {
        // 场景管理器，用于查找玩家所在的 Scene 实例
        private readonly SceneManager sceneManager;

        // 构造函数，注入场景管理器
        public EntitySyncHandler(SceneManager sceneManager)
        {
            this.sceneManager = sceneManager;
        }

        /// <summary>
        /// 玩家离开或者断线时清理数据
        /// </summary>
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
        /// 说明：客户端发送的同步包由 Gateway 转发到 Battle 节点，
        /// 转发时会在包头中附带 OriginalSessionId（即真实玩家会话 id），
        /// 该方法使用该 sessionId 查找玩家所在场景并分发更新。
        /// </summary>
        /// <param name="sessionId">玩家会话ID</param>
        /// <param name="request">实体同步请求</param>
        /// <param name="gatewaySession">网关会话</param>
        /// <returns></returns>
        public Task HandleEntitySyncRequestAsync(long sessionId, EntitySyncRequest request, Network.ISession gatewaySession)
        {
            // 根据 sessionId 查找玩家所在的场景
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return Task.CompletedTask;

            // 查找场景内对应实体的状态对象
            var entity = scene.EntityManager.GetEntity(sessionId);
            if (entity != null)
            {
                // 更新服务器端存储的位置信息与朝向
                entity.Position = request.Position;
                entity.Rotation = request.Rotation;

                // 如果场景开启了 AOI（基于网格的视野剔除）逻辑，使用更精细的广播策略
                if (scene.UseAoi && scene.AoiManager != null)
                {
                    // 将实体添加或更新到 AOI 管理器，获取旧网格与新网格坐标
                    bool moved = scene.AoiManager.AddOrUpdateEntity(sessionId, entity, out var oldGrid, out var newGrid);
                    if (moved)
                    {
                        // 计算新旧网格之间的差集，得到进入与离开的观察者列表
                        scene.AoiManager.CalculateGridDiff(oldGrid, newGrid, out var enterEntities, out var leaveEntities);

                        // 1) 通知离开视野的玩家：当前实体已离开
                        if (leaveEntities.Count > 0)
                        {
                            var leaveNotif = new EntityLeaveViewNotification { EntityIds = new List<long> { sessionId } };
                            byte[] leavePayload = Shared.Json.SerializeToUtf8Bytes(leaveNotif);
                            foreach (var targetId in leaveEntities)
                            {
                                if (targetId == sessionId) continue;
                                SendPacket(gatewaySession, targetId, MessageIds.EntityLeaveViewNotif, leavePayload);
                            }

                            // 同时给自己发送一条：老视野中有哪些实体离开了
                            var leaveSelf = new EntityLeaveViewNotification { EntityIds = leaveEntities };
                            SendPacket(gatewaySession, sessionId, MessageIds.EntityLeaveViewNotif, Shared.Json.SerializeToUtf8Bytes(leaveSelf));
                        }

                        // 2) 通知进入视野的玩家：当前实体已出现
                        if (enterEntities.Count > 0)
                        {
                            var enterNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { entity } };
                            byte[] enterPayload = Shared.Json.SerializeToUtf8Bytes(enterNotif);

                            var newlySeenStates = new List<EntityState>();

                            // 向新视野内的玩家广播我的出现，同时收集这些玩家的状态以便回发给自己
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

                            // 给自己发送新视野中已有的实体状态（方便客户端一次性构建视野内实体列表）
                            if (newlySeenStates.Count > 0)
                            {
                                var enterSelf = new EntityEnterViewNotification { Entities = newlySeenStates };
                                SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelf));
                            }
                        }
                    }

                    // 无论是否跨格子，位置更新都需要通知当前网格周围的玩家
                    var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);
                    BroadcastEntityStateUpdate(entity, surrounding, gatewaySession);
                }
                else
                {
                    // 未使用 AOI 的小房间场景，直接向场景内所有玩家广播位置更新
                    BroadcastEntityStateUpdate(entity, scene.EntityManager.GetAllSessionIds(), gatewaySession);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 将实体状态封装为通知并发送到指定玩家列表
        /// </summary>
        /// <param name="state">实体状态</param>
        /// <param name="targetSessionIds">目标玩家的会话ID列表</param>
        /// <param name="gatewaySession">网关会话</param>
        private void BroadcastEntityStateUpdate(EntityState state, IEnumerable<long> targetSessionIds, Network.ISession gatewaySession)
        {
            var notif = new EntityStateUpdateNotification { State = state };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(notif);

            foreach (var targetSessionId in targetSessionIds)
            {
                SendPacket(gatewaySession, targetSessionId, MessageIds.EntityStateUpdateNotif, payload);
            }
        }

        /// <summary>
        /// 将消息组装成网关约定的包格式并发送：
        /// [8 bytes OriginalSessionId][4 bytes MsgId][payload]
        /// </summary>
        /// <param name="gatewaySession">网关会话</param>
        /// <param name="targetSessionId">目标玩家的会话ID</param>
        /// <param name="msgId">消息ID</param>
        /// <param name="payload">消息负载</param>
        private void SendPacket(Network.ISession gatewaySession, long targetSessionId, int msgId, byte[] payload)
        {
            byte[] packet = new byte[12 + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(0, 8), targetSessionId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), msgId);
            payload.CopyTo(packet.AsSpan(12));
            gatewaySession.Send(packet);
        }

        /// <summary>
        /// 当玩家进入场景时的处理逻辑：
        /// - 将实体加入 EntityManager
        /// - 如果开启 AOI，则计算并通知周围玩家该实体进入；并把周围玩家的状态回发给该玩家；
        /// - 否则在小房间内把已存在实体列表回发给该玩家，并通知其他玩家他进入。
        /// </summary>
        /// <param name="sessionId">玩家的会话ID</param>
        /// <param name="newState">玩家的实体状态</param>
        /// <param name="gatewaySession">网关会话</param>
        public void OnPlayerEnter(long sessionId, EntityState newState, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            // 把玩家实体加入或更新到场景实体管理器
            scene.EntityManager.AddOrUpdateEntity(sessionId, newState);

            if (scene.UseAoi && scene.AoiManager != null)
            {
                // 将实体加入 AOI 并获取所在网格坐标
                scene.AoiManager.AddOrUpdateEntity(sessionId, newState, out _, out var newGrid);

                // 获取周边视野内的玩家列表
                var surrounding = scene.AoiManager.GetSurroundingEntities(newGrid.Item1, newGrid.Item2);

                var existingEntities = new List<EntityState>();
                var enterOthersNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { newState } };
                byte[] enterOthersPayload = Shared.Json.SerializeToUtf8Bytes(enterOthersNotif);

                // 通知周边玩家：有新玩家进入视野
                foreach (var targetId in surrounding)
                {
                    if (targetId == sessionId) continue;

                    SendPacket(gatewaySession, targetId, MessageIds.EntityEnterViewNotif, enterOthersPayload);

                    var state = scene.EntityManager.GetEntity(targetId);
                    if (state != null) existingEntities.Add(state);
                }

                // 将周边玩家的状态回发给进入的玩家，方便客户端构建本地视野中的实体列表
                if (existingEntities.Count > 0)
                {
                    var enterSelfNotif = new EntityEnterViewNotification { Entities = existingEntities };
                    SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelfNotif));
                }
            }
            else
            {
                // 小房间场景：先把已存在的实体列表发给进入的玩家
                var existingEntities = scene.EntityManager.GetAllEntities().ToList();
                if (existingEntities.Count > 0)
                {
                    var enterSelfNotif = new EntityEnterViewNotification { Entities = existingEntities };
                    SendPacket(gatewaySession, sessionId, MessageIds.EntityEnterViewNotif, Shared.Json.SerializeToUtf8Bytes(enterSelfNotif));
                }

                // 再通知房间内其他玩家：有玩家进入
                var enterOthersNotif = new EntityEnterViewNotification { Entities = new List<EntityState> { newState } };
                byte[] enterOthersPayload = Shared.Json.SerializeToUtf8Bytes(enterOthersNotif);

                foreach (var targetId in scene.EntityManager.GetAllSessionIds())
                {
                    if (targetId == sessionId) continue;
                    SendPacket(gatewaySession, targetId, MessageIds.EntityEnterViewNotif, enterOthersPayload);
                }
            }
        }

        /// <summary>
        /// 当玩家离开场景时的处理逻辑：
        /// - 从 EntityManager 中移除实体；
        /// - 如果使用 AOI，则从 AOI 中移除并通知周边玩家；否则通知场景内所有玩家；
        /// - 解绑玩家与场景的映射。
        /// </summary>
        /// <param name="sessionId">玩家的会话ID</param>
        /// <param name="gatewaySession">网关会话</param>
        public void OnPlayerLeave(long sessionId, Network.ISession gatewaySession)
        {
            var scene = sceneManager.GetSceneByPlayer(sessionId);
            if (scene == null) return;

            // 从实体管理器中移除
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
                // 从 AOI 中移除此实体
                scene.AoiManager.RemoveEntity(sessionId);
            }
            else
            {
                // 小房间：通知场景内所有玩家
                targetIds = scene.EntityManager.GetAllSessionIds().ToList();
            }

            // 解除玩家与场景的绑定关系
            sceneManager.UnbindPlayer(sessionId);

            // 向目标玩家发送离开视野通知
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