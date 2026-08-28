using System;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Network;
using Network.Routing;
using Network.Tcp;
using Shared;
using Shared.Messages;
using Shared.Messages.Center;

namespace Battle
{
    public static class BattleServerApp
    {
        private static Framework.Protocol.MessageDispatcher? dispatcher;
        private static Framework.Scripting.ScriptHost? scriptHost;
        private static Framework.Entity.EntityPersistenceService? persistService;
        private static System.Threading.CancellationTokenSource? centerHeartbeatCts;
        private static Battle.Handlers.SceneManager? sceneManager;
        private static Framework.Tick.TickEngine? tickEngine;
        private static Battle.Handlers.EntitySyncHandler? entitySyncHandler;
        private static TcpClientWrapper? centerClient;
        public static string CurrentNodeId { get; private set; } = string.Empty;

        /// <summary>挂起玩家（断线重连）：clientSessionId -> 挂起截止时间（Ticks）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> suspendedPlayers = new();

        /// <summary>实体迁移中的玩家会话（冻结集合）：迁移期间该会话的入站消息暂缓（对标 KBE 冻结实体迁移）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> migratingSessions = new();

        /// <summary>挂到 tick 线程执行的动作（实体状态访问必须在单线程内进行）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> tickActions = new();

        /// <summary>通知游戏逻辑脚本：实体创建（加入场景）。</summary>
        public static void NotifyEntityCreated(Framework.Entity.Entity entity)
        {
            scriptHost?.NotifyCreate(entity);
        }

        /// <summary>通知游戏逻辑脚本：实体销毁（离开场景）。</summary>
        public static void NotifyEntityDestroyed(Framework.Entity.Entity entity)
        {
            scriptHost?.NotifyDestroy(entity);
        }

        /// <summary>
        /// 崩溃恢复：按持久化数据重建玩家实体并恢复属性（对标 KBE restore_entity_handler）。
        /// 返回恢复的实体列表（加入场景前由调用方绑定）。
        /// 注意：这是全量恢复接口（启动/运维用），玩家加入路径请使用 <see cref="LoadPersistedPlayer"/> 单条加载，
        /// 避免每次加入都扫描全部存档文件。
        /// </summary>
        public static List<Framework.Entity.Entity> RestorePersistedPlayers()
        {
            if (persistService == null)
            {
                return new List<Framework.Entity.Entity>();
            }
            return persistService.RestoreAll("Player");
        }

        /// <summary>
        /// 按玩家会话 ID 单条加载持久化实体（O(1) 文件访问）。
        /// 玩家加入房间时使用，替代全量目录扫描；无存档时返回 null。
        /// </summary>
        public static Framework.Entity.Entity? LoadPersistedPlayer(long clientSessionId)
        {
            try
            {
                return persistService?.LoadEntityById("Player", clientSessionId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"玩家实体持久化加载失败 EntityId:{clientSessionId}");
                return null;
            }
        }

        private static long gameplayEntitySeq;
        private static long gameplayIdNodePrefix = -1;

        /// <summary>
        /// D4 玩法实体 ID 前缀：在 (1L&lt;&lt;40) 高位基址上叠加节点派生段 [32,40)，
        /// 保证不同 Battle 节点生成的玩法实体 ID 互不冲突 → 玩家迁入目标节点后，
        /// 随迁的 Skill/Item 不会与目标节点本地玩法实体撞 ID（v1 节点级计数器会撞）。
        /// </summary>
        private static long GetGameplayIdNodePrefix()
        {
            if (gameplayIdNodePrefix < 0)
            {
                uint hash = 2166136261; // FNV-1a 32
                foreach (char c in CurrentNodeId)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                gameplayIdNodePrefix = (1L << 40) + ((long)(hash & 0xFF) << 32);
            }
            return gameplayIdNodePrefix;
        }

        /// <summary>玩法实体 ID 分配（高位基址 + 节点段 + 序号，避免与网关会话 ID 冲突）。</summary>
        private static long NextGameplayEntityId() => GetGameplayIdNodePrefix() + (System.Threading.Interlocked.Increment(ref gameplayEntitySeq) & 0xFFFFFFFFL);

        /// <summary>
        /// 场景创建时生成场景级玩法实体（Npc 巡逻 / Quest 任务）：
        /// 实体骨架入场景管理器 + AOI 登记 + 通知脚本 OnCreate 初始化属性。
        /// 使 GameLogic/scripts 的玩法脚本在生产运行时真正生效（此前脚本只在测试套件中验证）。
        /// </summary>
        public static void SpawnSceneGameplayEntities(Battle.Handlers.BattleScene scene)
        {
            if (scene == null) return;
            for (int i = 0; i < 3; i++)
            {
                RegisterSceneEntity(scene, Battle.Entities.GameplayEntityDefs.Npc.CreateEntity(NextGameplayEntityId()));
            }
            RegisterSceneEntity(scene, Battle.Entities.GameplayEntityDefs.Quest.CreateEntity(NextGameplayEntityId()));
            Log.Info($"场景玩法实体已生成 SceneId:{scene.SceneId}");
        }

        /// <summary>玩家加入时生成玩家私有玩法实体（Skill / Item），绑定属主（OWN_CLIENT 定向同步）。</summary>
        public static void SpawnPlayerGameplayEntities(Battle.Handlers.BattleScene scene, long clientSessionId)
        {
            if (scene == null) return;
            var skill = Battle.Entities.GameplayEntityDefs.Skill.CreateEntity(NextGameplayEntityId());
            skill.OwnerClientId = clientSessionId;
            RegisterSceneEntity(scene, skill);

            var item = Battle.Entities.GameplayEntityDefs.Item.CreateEntity(NextGameplayEntityId());
            item.OwnerClientId = clientSessionId;
            RegisterSceneEntity(scene, item);
        }

        private static void RegisterSceneEntity(Battle.Handlers.BattleScene scene, Framework.Entity.Entity entity)
        {
            scene.EntityManager.AddOrUpdateEntity(entity.EntityId, entity);
            if (scene.UseAoi && scene.AoiManager != null)
            {
                scene.AoiManager.AddOrUpdateEntity(entity.EntityId, entity, out _, out _);
            }
            NotifyEntityCreated(entity);
        }

        /// <summary>
        /// D4 孤儿回收：移除场景中该玩家属主的玩法实体（Skill/Item，OwnerClientId == clientSessionId）。
        /// 在玩家完整离场（LeaveScene/离房）与迁移出完成（属主实体已随迁）时调用，防止无主玩法实体泄漏。
        /// 须在 tick 线程调用。
        /// </summary>
        /// <returns>回收的玩法实体数量。</returns>
        public static int RecycleOwnedEntities(Battle.Handlers.BattleScene scene, long clientSessionId)
        {
            if (scene == null)
            {
                return 0;
            }
            int recycled = 0;
            foreach (var owned in scene.EntityManager.GetAllEntities())
            {
                if (owned.EntityId == clientSessionId || owned.OwnerClientId != clientSessionId)
                {
                    continue;
                }
                NotifyEntityDestroyed(owned);
                scene.EntityManager.RemoveEntity(owned.EntityId);
                scene.AoiManager?.RemoveEntity(owned.EntityId);
                recycled++;
            }
            if (recycled > 0)
            {
                Log.Info($"玩法实体孤儿回收完成 ClientSessionId:{clientSessionId} 回收数:{recycled}");
            }
            return recycled;
        }

        /// <summary>
        /// D4 迁移序列化：收集该玩家属主的玩法实体（Skill/Item，OwnerClientId == clientSessionId）为迁移负载，
        /// 与玩家主实体同包随迁（属主绑定经 ClientSessionId 表达）。
        /// 须在 tick 线程调用。
        /// </summary>
        public static List<Framework.Protocol.Generated.EntityMigratePayload> SerializeOwnedEntitiesForMigration(long clientSessionId, string sceneId)
        {
            var list = new List<Framework.Protocol.Generated.EntityMigratePayload>();
            var scene = sceneManager?.GetScene(sceneId);
            if (scene == null)
            {
                return list;
            }
            foreach (var owned in scene.EntityManager.GetAllEntities())
            {
                if (owned.EntityId == clientSessionId || owned.OwnerClientId != clientSessionId)
                {
                    continue;
                }
                list.Add(new Framework.Protocol.Generated.EntityMigratePayload
                {
                    EntityId = owned.EntityId,
                    EntityType = owned.TypeName,
                    Props = Framework.Entity.PropertyCodec.SerializeAllValues(owned.CopyValues(), owned.Def, onlySyncToClient: false)
                });
            }
            return list;
        }

        /// <summary>无主世界实体（Npc/Quest 等）允许客户端直接调用的方法白名单（与 SceneManager/脚本 OnMessage 对齐）。</summary>
        private static readonly string[] WorldEntityAllowedMethods = { "TakeDamage", "QueryProgress" };

        /// <summary>分发通用实体脚本动作（客户端 ScriptAction 消息 → 脚本 OnMessage）。</summary>
        /// <remarks>
        /// CRITICAL 修复：脚本动作必须鉴权，否则任意客户端可对任意场景/任意实体的任意方法发起调用（改他人血量、
        /// 触发他人技能、跨房间干扰等）。规则：
        /// 1) 调用者必须已加入目标实体所在场景（杜绝跨场景/跨房间攻击）；
        /// 2) 属主规则：调用者仅可操作自己拥有的实体（Player/Skill/Item 的 OwnerClientId == 会话 ID）；
        /// 3) 无主世界实体（Npc/Quest）：仅放行白名单方法（世界交互如打怪/查询进度）。
        /// </remarks>
        public static void DispatchEntityScriptAction(long callerSessionId, long entityId, string method, object?[] args)
        {
            try
            {
                var scene = sceneManager?.FindSceneByEntityId(entityId);
                var entity = scene?.EntityManager.GetEntity(entityId);
                if (entity == null)
                {
                    Log.Warning($"实体脚本动作未找到目标实体 EntityId:{entityId} Method:{method}");
                    return;
                }

                if (callerSessionId <= 0)
                {
                    Log.Warning($"实体脚本动作被拒绝：调用者会话无效 SessionId:{callerSessionId} EntityId:{entityId} Method:{method}");
                    return;
                }

                // 1) 场景归属：调用者必须已加入目标实体所在场景
                var callerScene = sceneManager?.GetSceneByPlayer(callerSessionId);
                if (callerScene == null || callerScene.SceneId != scene.SceneId)
                {
                    Log.Warning($"实体脚本动作被拒绝：调用者不在目标实体所在场景 SessionId:{callerSessionId} EntityId:{entityId} Method:{method}");
                    return;
                }

                // 2) 属主规则：允许操作自属实体的全部方法
                bool isOwner = entity.OwnerClientId == callerSessionId || entity.EntityId == callerSessionId;

                // 3) 非属主（他人实体或无主世界实体）：仅放行白名单方法
                if (!isOwner && System.Array.IndexOf(WorldEntityAllowedMethods, method) < 0)
                {
                    Log.Warning($"实体脚本动作被拒绝：调用者无权操作该实体 SessionId:{callerSessionId} EntityId:{entityId} Method:{method}");
                    return;
                }

                scriptHost?.DispatchMessage(entity, method, args);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"实体脚本动作分发异常 EntityId:{entityId} Method:{method}");
            }
        }

        /// <summary>
        /// 玩家断线挂起（对标 KBE 断线恢复）：实体保留在场景/AOI（其他玩家看到冻结化身），
        /// 宽限期（ReconnectGraceSeconds，默认 30s）内客户端重连（PlayerSessionResume）即恢复在线；
        /// 超时未恢复则完整离场。返回 true 表示已挂起。
        /// </summary>
        public static bool SuspendPlayerOnDisconnect(Battle.Handlers.BattleScene scene, long clientSessionId, Network.ISession gatewaySession)
        {
            if (scene == null || clientSessionId <= 0) return false;
            var entity = scene.EntityManager.GetEntity(clientSessionId);
            if (entity == null) return false;

            int grace = ConfigHelper.GetConfig<int>("ReconnectGraceSeconds");
            if (grace <= 0) return false; // 配置 <= 0：关闭重连，立即离场

            // 断线即存档（崩溃/重连超时后仍可恢复）
            PersistPlayer(entity);
            suspendedPlayers[clientSessionId] = DateTime.UtcNow.AddSeconds(grace).Ticks;

            tickEngine?.AddTimer(grace * 1000, () =>
            {
                // 宽限期结束仍未重连：完整离场
                if (suspendedPlayers.TryRemove(clientSessionId, out _))
                {
                    var sc = sceneManager?.GetScene(scene.SceneId);
                    if (sc != null)
                    {
                        var gw = GetGatewaySessionByClient(clientSessionId);
                        LeaveScene(sc, clientSessionId, gw ?? gatewaySession);
                        Log.Info($"玩家 {clientSessionId} 重连超时，实体已离场");
                    }
                }
            });
            Log.Info($"玩家 {clientSessionId} 断线，实体挂起 {grace}s 等待重连");
            return true;
        }

        /// <summary>重连恢复：取消挂起，实体恢复在线（实体与场景席位全程保留）。</summary>
        public static void ResumePlayer(long clientSessionId)
        {
            if (suspendedPlayers.TryRemove(clientSessionId, out _))
            {
                Log.Info($"玩家 {clientSessionId} 重连成功，实体恢复在线");
            }
        }

        /// <summary>
        /// 完整离场：持久化 + 销毁脚本实体 + 移除场景/AOI + 通知周边 + 解绑 + 同步 Center。
        /// 断线挂起超时与显式离场共用。
        /// </summary>
        public static void LeaveScene(Battle.Handlers.BattleScene scene, long clientSessionId, Network.ISession? gatewaySession)
        {
            var entity = scene.EntityManager.GetEntity(clientSessionId);
            if (entity != null)
            {
                PersistPlayer(entity);
                NotifyEntityDestroyed(entity);
            }

            // D4 孤儿回收：玩家完整离场，属主玩法实体（Skill/Item）无法随迁，回收防泄漏
            RecycleOwnedEntities(scene, clientSessionId);

            if (gatewaySession != null)
            {
                entitySyncHandler?.OnPlayerLeave(clientSessionId, gatewaySession);
            }
            else
            {
                // 无可用网关会话（连接已断）：直接移除实体与 AOI、解绑
                scene.EntityManager.RemoveEntity(clientSessionId);
                scene.AoiManager?.RemoveEntity(clientSessionId);
                sceneManager?.UnbindPlayer(clientSessionId);
            }

            SyncRoomPlayerCount(scene.SceneId);
            SyncRoomMemberLeave(scene.SceneId, clientSessionId);
        }

        /// <summary>持久化保存玩家实体（离开/下线时调用）。</summary>
        public static void PersistPlayer(Framework.Entity.Entity entity)
        {
            try
            {
                persistService?.SaveEntity(entity);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"玩家实体持久化失败 EntityId:{entity.EntityId}");
            }
        }

        /// <summary>客户端会话 -> 网关会话 映射（帧同步广播用；收包时登记，断开时清除）</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Network.ISession> clientGatewaySessions = new();

        // ===== 单线程消息队列（对标 KBE mailbox）=====
        // 收包线程只入队，TickEngine 主循环串行消费 —— 实体/场景状态只在 tick 线程被读写，
        // 彻底消除"声称单线程、实际并发写 Entity.values"的数据竞争（见 KBE-Gap-Review 三-1）。

        /// <summary>入站消息（payload 已剥离路由元数据）。</summary>
        private readonly struct InboundMessage
        {
            public readonly Network.ISession Session;
            public readonly int MsgId;
            public readonly byte[] Payload;
            public readonly long OriginalSessionId;

            public InboundMessage(Network.ISession session, int msgId, byte[] payload, long originalSessionId)
            {
                Session = session;
                MsgId = msgId;
                Payload = payload;
                OriginalSessionId = originalSessionId;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<InboundMessage> inboundQueue = new();
        private static long queuedInboundCount;

        /// <summary>队列上限：超过则丢弃新消息并告警（防止无界增长；正常流量远低于此）。</summary>
        private const int MaxInboundQueued = 16384;

        /// <summary>入队入站消息（收包线程调用；不阻塞）。</summary>
        private static void EnqueueInbound(Network.ISession session, int msgId, byte[] payload, long originalSessionId)
        {
            if (System.Threading.Interlocked.Read(ref queuedInboundCount) >= MaxInboundQueued)
            {
                Log.Warning($"Battle 入站消息队列已满，丢弃消息 MsgId:{msgId} SessionId:{session.SessionId}");
                return;
            }
            inboundQueue.Enqueue(new InboundMessage(session, msgId, payload, originalSessionId));
            System.Threading.Interlocked.Increment(ref queuedInboundCount);
        }

        /// <summary>tick 线程排空消息队列（每帧开头调用一次，串行处理全部入站消息）。</summary>
        private static void DrainInboundMessages()
        {
            while (inboundQueue.TryDequeue(out var inbound))
            {
                System.Threading.Interlocked.Decrement(ref queuedInboundCount);
                ProcessInboundMessage(inbound);
            }
        }

        /// <summary>待处理入站消息数（压测/监控验证 tick 线程排空用）。</summary>
        public static int PendingInboundCount => (int)System.Threading.Interlocked.Read(ref queuedInboundCount);

        /// <summary>tick 线程排空待执行动作（实体迁移等需在单线程内访问实体状态）。</summary>
        private static void DrainTickActions()
        {
            while (tickActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "tick 动作执行异常");
                }
            }
        }

        /// <summary>
        /// 在 tick 线程内处理一条入站消息：新协议分发优先（强类型 + MemoryPack/JSON 双格式兼容），旧路由回退。
        /// 所有 Battle 处理器均同步完成（Task.FromResult/CompletedTask），此处安全使用 GetResult 串行执行。
        /// </summary>
        private static void ProcessInboundMessage(InboundMessage inbound)
        {
            var session = inbound.Session;
            int msgId = inbound.MsgId;
            byte[] payload = inbound.Payload;
            long originalSessionId = inbound.OriginalSessionId;

            try
            {
                // 冻结实体迁移：迁移中的会话消息暂缓（丢弃），等迁移完成/回滚后恢复
                if (originalSessionId > 0 && migratingSessions.ContainsKey(originalSessionId))
                {
                    Log.Debug($"客户端会话 {originalSessionId} 正在实体迁移，消息暂缓 MsgId:{msgId}");
                    return;
                }

                if (dispatcher != null && dispatcher.TryDispatch(new Battle.Handlers.BattleSessionContext(session, originalSessionId), msgId, payload).GetAwaiter().GetResult())
                {
                    Log.Debug("Battle 新协议分发完成 MsgId:{MsgId} ClientSessionId:{ClientSessionId}", msgId, originalSessionId);
                    return;
                }

                Log.Warning($"Battle 收到未知 MsgId: {msgId}");

                // 旧协议兼容：客户端消息区间内的未知消息回错误响应（仅战斗加入等有明确回包的场景）
                if (originalSessionId > 0 && msgId >= 40000 && msgId < 50000)
                {
                    int responseMsgId = msgId switch
                    {
                        MessageIds.BattleJoinReq => MessageIds.BattleJoinRes,
                        _ => 0
                    };

                    if (responseMsgId > 0)
                    {
                        byte[] unknownPayload = Shared.Json.SerializeToUtf8Bytes(new Shared.Messages.Battle.BattleJoinResponse
                        {
                            Success = false,
                            Message = $"未支持的战斗消息类型: {msgId}"
                        });
                        byte[] routedUnknownPayload = Shared.RouteMetadata.AttachTargetSessionId(unknownPayload, originalSessionId);
                        byte[] unknownPacket = PacketBuilder.BuildPacket(responseMsgId, routedUnknownPayload, out int unknownLength);
                        Network.PacketSender.Send(session, unknownPacket, unknownLength);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 处理消息 ({msgId}) 发生异常: {ex}");
            }
        }

        /// <summary>根据客户端会话 ID 查找其网关会话（用于定向回包）。</summary>
        public static Network.ISession? GetGatewaySessionByClient(long clientSessionId)
        {
            clientGatewaySessions.TryGetValue(clientSessionId, out var session);
            return session;
        }

        /// <summary>登记客户端会话 -> 网关会话 绑定。</summary>
        public static void BindClientGateway(long clientSessionId, Network.ISession gatewaySession)
        {
            if (clientSessionId > 0 && gatewaySession != null)
            {
                clientGatewaySessions[clientSessionId] = gatewaySession;
            }
        }

        /// <summary>解除客户端会话绑定。</summary>
        public static void UnbindClientGateway(long clientSessionId)
        {
            clientGatewaySessions.TryRemove(clientSessionId, out _);
        }

        // ===== 实体在线迁移（C2 第二阶段：冻结-序列化-搬迁-恢复，Center 协调中继，对标 KBE cellapp 实体搬迁） =====

        /// <summary>在 tick 线程上执行动作（实体状态访问必须在单线程内进行）。</summary>
        public static void RunOnTick(Action action)
        {
            if (action != null)
            {
                tickActions.Enqueue(action);
            }
        }

        /// <summary>该客户端会话是否处于实体迁移冻结中。</summary>
        public static bool IsClientMigrating(long clientSessionId) => migratingSessions.ContainsKey(clientSessionId);

        private static void FreezeClientSession(long clientSessionId) => migratingSessions[clientSessionId] = 0;

        private static void UnfreezeClientSession(long clientSessionId) => migratingSessions.TryRemove(clientSessionId, out _);

        /// <summary>序列化玩家实体全部属性（含 CELL_PRIVATE 内部状态）为迁移负载；实体不存在返回 null。</summary>
        public static byte[]? SerializeEntityForMigration(long clientSessionId)
        {
            var scene = sceneManager?.GetSceneByPlayer(clientSessionId);
            var entity = scene?.EntityManager.GetEntity(clientSessionId);
            if (entity == null)
            {
                return null;
            }
            return Framework.Entity.PropertyCodec.SerializeAllValues(entity.CopyValues(), entity.Def, onlySyncToClient: false);
        }

        /// <summary>按实体类型名解析定义（迁移恢复用）。</summary>
        private static Framework.Entity.EntityDef? ResolveEntityDef(string typeName) => typeName switch
        {
            "Player" => Battle.Entities.PlayerEntityDef.Def,
            "Npc" => Battle.Entities.GameplayEntityDefs.Npc,
            "Quest" => Battle.Entities.GameplayEntityDefs.Quest,
            "Skill" => Battle.Entities.GameplayEntityDefs.Skill,
            "Item" => Battle.Entities.GameplayEntityDefs.Item,
            _ => null
        };

        /// <summary>
        /// 在目标场景恢复迁移实体（含场景绑定/AOI/脚本 OnCreate 通知）。返回恢复的实体；失败返回 null。
        /// v1 说明：仅迁移玩家主实体（EntityId = ClientSessionId）；Skill/Item/Npc 等玩法实体暂不跨节点搬迁。
        /// D4 玩法实体迁移 v2：通过 ownerClientId 参数支持随迁玩法实体的属主绑定（非空且非玩家类型时设置）。
        /// </summary>
        public static Framework.Entity.Entity? RestoreMigratedEntity(long entityId, string entityType, string sceneId, byte[] props, long? ownerClientId = null)
        {
            var scene = sceneManager?.GetScene(sceneId);
            if (scene == null)
            {
                Log.Warning($"实体迁移恢复失败：目标场景不存在 SceneId:{sceneId}");
                return null;
            }
            if (scene.EntityManager.GetEntity(entityId) != null)
            {
                Log.Warning($"实体迁移恢复失败：实体已存在 EntityId:{entityId} SceneId:{sceneId}");
                return null;
            }
            var def = ResolveEntityDef(entityType);
            if (def == null)
            {
                Log.Warning($"实体迁移恢复失败：未知实体类型 {entityType}");
                return null;
            }

            var entity = def.CreateEntity(entityId);
            Framework.Entity.PropertyCodec.DeserializeInto(entity, props, applyDirty: false);
            if (string.Equals(entityType, "Player", StringComparison.Ordinal))
            {
                entity.OwnerClientId = entityId; // 玩家实体：属主 = 会话 ID = 实体 ID
                sceneManager.BindPlayerToScene(entityId, sceneId);
            }
            else if (ownerClientId.HasValue && ownerClientId.Value > 0)
            {
                entity.OwnerClientId = ownerClientId.Value; // D4：随迁玩法实体恢复属主绑定
            }
            RegisterSceneEntity(scene, entity);
            Log.Info($"实体迁移恢复完成 EntityId:{entityId} Type:{entityType} Scene:{sceneId}");
            return entity;
        }

        /// <summary>向 Center 回 91004 迁移结果。</summary>
        private static void SendMigrateResult(bool success, long clientSessionId, long entityId, string newNodeId, string message)
        {
            if (centerClient == null)
            {
                return;
            }
            var res = new Framework.Protocol.Generated.EntityMigrateResult
            {
                Success = success,
                ClientSessionId = clientSessionId,
                EntityId = entityId,
                NewNodeId = newNodeId,
                Message = message
            };
            byte[] payload = res.Serialize();
            byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.EntityMigrateResult, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送实体迁移结果失败");
            }
        }

        /// <summary>处理迁移入（目标 Battle）：恢复玩家主实体 + 随迁属主玩法实体 + 回 91004 到 Center。</summary>
        public static void HandleEntityMigrateIn(Framework.Protocol.Generated.EntityMigrateRequest req)
        {
            try
            {
                var entity = RestoreMigratedEntity(
                    req.EntityId,
                    req.EntityType ?? string.Empty,
                    req.SceneId ?? string.Empty,
                    req.Props ?? Array.Empty<byte>());

                // D4：随迁属主玩法实体（Skill/Item）原子恢复 + 属主绑定
                int ownedOk = 0, ownedTotal = req.OwnedEntities?.Count ?? 0;
                if (entity != null && req.OwnedEntities != null)
                {
                    foreach (var owned in req.OwnedEntities)
                    {
                        if (owned == null)
                        {
                            continue;
                        }
                        var restored = RestoreMigratedEntity(
                            owned.EntityId,
                            owned.EntityType ?? string.Empty,
                            req.SceneId ?? string.Empty,
                            owned.Props ?? Array.Empty<byte>(),
                            req.ClientSessionId);
                        if (restored != null)
                        {
                            ownedOk++;
                        }
                    }
                }

                SendMigrateResult(entity != null, req.ClientSessionId, req.EntityId, CurrentNodeId,
                    entity != null ? $"迁移成功（属主实体随迁 {ownedOk}/{ownedTotal}）" : "迁移恢复失败");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"实体迁移入处理异常 EntityId:{req.EntityId}");
                SendMigrateResult(false, req.ClientSessionId, req.EntityId, CurrentNodeId, ex.Message);
            }
        }

        /// <summary>处理迁移出结果（源 Battle）：成功则移除本地实体；失败解除冻结（回滚）。</summary>
        public static void HandleEntityMigrateOutResult(Framework.Protocol.Generated.EntityMigrateResult res)
        {
            if (res.Success)
            {
                CompleteMigrateOut(res.ClientSessionId);
            }
            else
            {
                UnfreezeClientSession(res.ClientSessionId);
                Log.Warning($"实体迁移失败，已回滚解除冻结 ClientSessionId:{res.ClientSessionId} Reason:{res.Message}");
            }
        }

        /// <summary>完成迁移出：移除本地实体/场景绑定/AOI + 通知周边 + 解除冻结（v1：玩家主实体）。</summary>
        private static void CompleteMigrateOut(long clientSessionId)
        {
            UnfreezeClientSession(clientSessionId);
            var scene = sceneManager?.GetSceneByPlayer(clientSessionId);
            if (scene == null)
            {
                sceneManager?.UnbindPlayer(clientSessionId);
                Log.Info($"实体迁移出完成（无场景）ClientSessionId:{clientSessionId}");
                return;
            }

            var entity = scene.EntityManager.GetEntity(clientSessionId);
            if (entity != null)
            {
                NotifyEntityDestroyed(entity);
            }

            // D4：属主玩法实体已随迁至目标节点，源节点回收本地副本（防孤儿泄漏）
            RecycleOwnedEntities(scene, clientSessionId);

            var gatewaySession = GetGatewaySessionByClient(clientSessionId);
            if (gatewaySession != null)
            {
                entitySyncHandler?.OnPlayerLeave(clientSessionId, gatewaySession);
            }
            else
            {
                scene.EntityManager.RemoveEntity(clientSessionId);
                scene.AoiManager?.RemoveEntity(clientSessionId);
                sceneManager.UnbindPlayer(clientSessionId);
            }
            UnbindClientGateway(clientSessionId);
            SyncRoomPlayerCount(scene.SceneId);
            Log.Info($"实体迁移出完成，源节点已移除实体 ClientSessionId:{clientSessionId}");
        }

        /// <summary>
        /// 发起实体迁移（源 Battle）：冻结会话 → 序列化 → 发 91003 到 Center（Center 中继目标节点）。
        /// 结果经 91004 异步回到 tick 线程（HandleEntityMigrateOutResult）：成功移除本地实体，失败回滚解冻。
        /// 注意：须在 tick 线程调用（或经 RunOnTick 排队），因为序列化读取实体状态。
        /// </summary>
        public static void StartEntityMigration(long clientSessionId, string targetNodeId)
        {
            if (centerClient == null)
            {
                Log.Warning($"实体迁移失败：未连接 Center ClientSessionId:{clientSessionId}");
                return;
            }
            if (string.IsNullOrWhiteSpace(targetNodeId) || targetNodeId == CurrentNodeId)
            {
                Log.Warning($"实体迁移目标节点无效 TargetNodeId:{targetNodeId} ClientSessionId:{clientSessionId}");
                return;
            }
            if (migratingSessions.ContainsKey(clientSessionId))
            {
                Log.Warning($"实体迁移已在进行 ClientSessionId:{clientSessionId}");
                return;
            }

            var scene = sceneManager?.GetSceneByPlayer(clientSessionId);
            var entity = scene?.EntityManager.GetEntity(clientSessionId);
            if (scene == null || entity == null)
            {
                Log.Warning($"实体迁移失败：玩家实体不存在 ClientSessionId:{clientSessionId}");
                return;
            }

            FreezeClientSession(clientSessionId);
            var owned = SerializeOwnedEntitiesForMigration(clientSessionId, scene.SceneId);
            var req = new Framework.Protocol.Generated.EntityMigrateRequest
            {
                SourceNodeId = CurrentNodeId,
                TargetNodeId = targetNodeId,
                ClientSessionId = clientSessionId,
                EntityId = clientSessionId,
                EntityType = entity.TypeName,
                SceneId = scene.SceneId,
                Props = Framework.Entity.PropertyCodec.SerializeAllValues(entity.CopyValues(), entity.Def, onlySyncToClient: false),
                OwnedEntities = owned
            };
            byte[] payload = req.Serialize();
            byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.EntityMigrateRequest, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
                Log.Info($"实体迁移发起 ClientSessionId:{clientSessionId} -> {targetNodeId} Scene:{scene.SceneId} 属主实体随迁:{owned.Count}");
            }
            catch (Exception ex)
            {
                UnfreezeClientSession(clientSessionId);
                Log.Error(ex, $"实体迁移发起失败 ClientSessionId:{clientSessionId}");
            }
        }

        /// <summary>
        /// 处理实体远程调用入（目标 Battle）：定位实体并执行方法（对标 KBE 远端实体方法调用）。
        /// CallId=0（fire-and-forget）返回 null（无需回执）；否则返回携带同一 CallId 的回执。
        /// 须在 tick 线程调用（Center 中继消息经入站队列串行消费）。
        /// </summary>
        public static Framework.Protocol.Generated.EntityRemoteCallResult? HandleEntityRemoteCallIn(Framework.Protocol.Generated.EntityRemoteCall call)
        {
            try
            {
                var scene = sceneManager?.FindSceneByEntityId(call.EntityId);
                var entity = scene?.EntityManager.GetEntity(call.EntityId);
                if (entity == null)
                {
                    Log.Warning($"实体远程调用未找到目标实体 EntityId:{call.EntityId} Method:{call.MethodName}");
                    return BuildRemoteCallResult(call, false, null);
                }

                object?[] args = Framework.Entity.ArgCodec.Deserialize(call.Args);
                var (handled, result) = entity.InvokeMethod(call.MethodName, args);
                Log.Info($"实体远程调用执行 EntityId:{call.EntityId} Method:{call.MethodName} handled:{handled}");
                return BuildRemoteCallResult(call, handled, handled ? result : null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"实体远程调用入处理异常 EntityId:{call.EntityId} Method:{call.MethodName}");
                return BuildRemoteCallResult(call, false, null);
            }
        }

        /// <summary>构造远程调用回执（CallId=0 时返回 null，无需回执）。</summary>
        private static Framework.Protocol.Generated.EntityRemoteCallResult? BuildRemoteCallResult(
            Framework.Protocol.Generated.EntityRemoteCall call, bool success, object? result)
        {
            if (call.CallId == 0)
            {
                return null;
            }
            return new Framework.Protocol.Generated.EntityRemoteCallResult
            {
                CallId = call.CallId,
                EntityId = call.EntityId,
                MethodName = call.MethodName,
                Success = success,
                Result = success ? Framework.Entity.ArgCodec.Serialize(new object?[] { result }) : Array.Empty<byte>()
            };
        }

        /// <summary>向 Center 回 91002 实体远程调用回执（Center 中继回源 Battle，调用方完成回执/超时）。</summary>
        public static void SendEntityRemoteCallResult(Framework.Protocol.Generated.EntityRemoteCallResult result)
        {
            if (centerClient == null)
            {
                return;
            }
            byte[] payload = result.Serialize();
            byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.EntityRemoteCallResult, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送实体远程调用回执失败");
            }
        }

        /// <summary>
        /// 创建跨节点实体引用（经 Center 中继 91001，对标 KBE EntityCallAbstract）：
        /// 供业务/脚本调用 targetNodeId 节点上 entityId 实体的方法，可用 CallAsync 等待回执。
        /// </summary>
        public static Framework.Entity.EntityCall CreateRemoteEntityCall(string targetNodeId, long entityId)
        {
            return Framework.Entity.EntityCall.Remote(targetNodeId, entityId, SendEntityRemoteCallToCenter);
        }

        /// <summary>把 EntityRemoteCall 消息发送到 Center（Center 中继目标 Battle）。</summary>
        private static void SendEntityRemoteCallToCenter(Framework.Protocol.Generated.EntityRemoteCall call)
        {
            if (centerClient == null)
            {
                Log.Warn($"实体远程调用发送失败：未连接 Center EntityId:{call.EntityId} Method:{call.MethodName}");
                return;
            }
            byte[] payload = call.Serialize();
            byte[] packet = PacketBuilder.BuildPacket(Framework.Protocol.Generated.MessageIds.EntityRemoteCall, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送实体远程调用失败");
            }
        }

        /// <summary>
        /// 加载配置，构建场景与消息处理器，注册并启动战斗服务器的 TCP 网络，处理会话连接/断开与数据接收并分发内部消息，随后连接到中心服。
        /// </summary>
        /// <remarks>使用 ConfigManager 加载配置；若未配置端口则使用默认端口 31307。初始化
        /// SceneManager、EntitySyncHandler、RoomHandler 和 BattleMainHandler，并通过 MessageRouter 构建处理器集合。创建 NetworkManager 与
        /// TcpServer，订阅连接/断开与数据接收事件；按二进制协议解析 [SessionId(8)][MsgId(4)][Payload] 并根据 MsgId 分发到相应处理器，处理器异常会被记录。注册并启动名为
        /// BattleTcp 的服务器，启动完成后记录监听端口并调用 ConnectToCenter(port)。</remarks>
        /// <returns>表示异步操作的任务。</returns>
        public static async Task StartNetworkAsync()
        {
            Configs.ConfigManager.LoadAll(); // 读取策划配置文件

            int port = ConfigHelper.GetConfig<int>("BattlePort") == 0 ? 31307 : ConfigHelper.GetConfig<int>("BattlePort");

            // 单线程 tick 引擎（对标 KBE gameUpdateHertz，默认 20Hz）：驱动帧同步与定时逻辑
            int tickHertz = ConfigHelper.GetConfig<int>("BattleTickHertz") == 0 ? 20 : ConfigHelper.GetConfig<int>("BattleTickHertz");
            var tickEngine = new Framework.Tick.TickEngine(tickHertz);
            tickEngine.Start();
            BattleServerApp.tickEngine = tickEngine;

            // 实体备份服务（对标 KBE backuper 平滑分摊 + archiver 落盘）
            string backupFile = ConfigHelper.GetConfig<string>("BackupFilePath")
                ?? Path.Combine(AppContext.BaseDirectory, "backups", "entities.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            var backupService = new Framework.Entity.EntityBackupService(
                backupFile,
                periodInTicks: ConfigHelper.GetConfig<int>("BackupPeriodTicks") == 0 ? 100 : ConfigHelper.GetConfig<int>("BackupPeriodTicks"));

            // 游戏逻辑脚本宿主（对标 KBE Python 脚本层）：玩法逻辑与底层框架物理分离，可热更新
            string scriptsDir = ConfigHelper.GetConfig<string>("ScriptsDir") ?? Path.Combine(AppContext.BaseDirectory, "scripts");
            var scriptHost = new Framework.Scripting.ScriptHost(scriptsDir);
            // KBE-Gap-Review S2：把 TickEngine 注入脚本宿主，csx 才能用 AddTimer 代替 tick%N 轮询
            scriptHost.AttachTickEngine(tickEngine);
            scriptHost.Start();
            BattleServerApp.scriptHost = scriptHost;

            // 实体持久化服务（对标 KBE dbmgr entity_table 自动存取 + restore_entity_handler 崩溃恢复）
            string persistDir = ConfigHelper.GetConfig<string>("EntityPersistDir")
                ?? Path.Combine(AppContext.BaseDirectory, "persist", "entities");
            var persistService = new Framework.Entity.EntityPersistenceService(persistDir, id =>
            {
                return Battle.Entities.PlayerEntityDef.Create(id);
            });
            BattleServerApp.persistService = persistService;

            sceneManager = new Battle.Handlers.SceneManager();
            // 场景创建：注册脚本实体管理器（全局数据事件用）+ 备份管理器（迭代 8：改为创建时注册一次，
            // 不再在每 tick 循环内重复 AddManager）+ 生成场景级玩法实体（Npc/Quest）
            sceneManager.SceneCreated += scene =>
            {
                scriptHost.RegisterEntityManager(scene.EntityManager);
                backupService.AddManager(scene.EntityManager);
                SpawnSceneGameplayEntities(scene);
            };
            var entitySyncHandler = new Battle.Handlers.EntitySyncHandler(sceneManager);
            BattleServerApp.entitySyncHandler = entitySyncHandler;

            // tick 引擎驱动（对标 KBE 主循环，单线程串行）：
            // 入站消息排空（mailbox）→ 脚本 OnTick → 备份平滑分摊 → Witness 增量广播
            tickEngine.OnTick += frame =>
            {
                DrainTickActions();
                DrainInboundMessages();

                if (sceneManager != null)
                {
                    foreach (var scene in sceneManager.GetAllScenes())
                    {
                        scriptHost.TickAll(scene.EntityManager, frame);
                    }
                    // 按实体量平滑分摊备份（对标 KBE backuper：每 tick 只备份部分实体）
                    backupService.Tick();
                    // 脚本/AI 驱动的属性变化增量广播（NPC 巡逻、回血、冷却、掉落）
                    entitySyncHandler.TickWitness();
                }

                // EntityCall 超时判定（对标 KBE 远程调用超时回执）：每 10 tick（0.5s @20Hz）清理一次待回执调用
                if (frame % 10 == 0)
                {
                    int expired = Framework.Entity.EntityCallHubRegistry.Default.SweepExpired(DateTime.UtcNow);
                    if (expired > 0)
                    {
                        Log.Warning($"实体远程调用超时清理 {expired} 个（未收到回执）");
                    }
                }

                // 性能 Profile（对标 KBE perf）：每 100 tick（5 秒 @20Hz）输出 tick 统计
                if (frame % 100 == 0)
                {
                    Log.Info($"tick 统计: last={tickEngine.LastTickMs}ms avg={tickEngine.AvgTickMs}ms max={tickEngine.MaxTickMs}ms 阈值={tickEngine.SlowTickThresholdMs}ms 入站队列={System.Threading.Interlocked.Read(ref queuedInboundCount)}");
                }
            };
            var roomHandler = new Battle.Handlers.RoomHandler(sceneManager, entitySyncHandler);
            var battleMainHandler = new Battle.Handlers.BattleMainHandler(sceneManager);

            // 帧同步管理器：客户端输入入队，tick 引擎聚合广播权威帧
            var frameSyncManager = new Battle.Handlers.FrameSyncManager(sceneManager, tickEngine);
            frameSyncManager.SetSendAction((targetSessionId, msgId, payload) =>
            {
                var gatewaySession = GetGatewaySessionByClient(targetSessionId);
                if (gatewaySession != null)
                {
                    byte[] routedPayload = Shared.RouteMetadata.AttachTargetSessionId(payload, targetSessionId);
                    byte[] packet = Network.Routing.PacketBuilder.BuildPacket(msgId, routedPayload, out int totalLength);
                    Network.PacketSender.Send(gatewaySession, packet, totalLength);
                }
            });

            // 新协议分发器：强类型消息 + MemoryPack（JSON 兼容回退），消灭手写 switch
            // KBE-Gap-Review D7：时间同步 manager 注入
            var timeSyncManager = new Battle.Handlers.TimeSyncManager(tickEngine);
            dispatcher = Battle.Handlers.MessageRouter.BuildDispatcher(roomHandler, entitySyncHandler, battleMainHandler, frameSyncManager, timeSyncManager);

            var tcpServer = new TcpServer();

            // 内部连接认证：网关/节点连接必须先通过认证握手（InternalAuth），密钥与 Center 节点注册共用。
            // 安全修复：拒绝占位符密钥。
            string authSecret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            var gatewayAuthFilters = new System.Collections.Concurrent.ConcurrentDictionary<long, Framework.Core.Security.InternalAuthFilter>();

            tcpServer.OnSessionConnected += session =>
            {
                Log.Info($"节点/网关已连接到战斗服: {session.RemoteEndPoint}");
                gatewayAuthFilters[session.SessionId] = new Framework.Core.Security.InternalAuthFilter(authSecret, $"Battle-{ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1"}:{port}");
            };

            tcpServer.OnSessionDisconnected += (session, reason) =>
            {
                gatewayAuthFilters.TryRemove(session.SessionId, out _);
                // 清除该网关会话下绑定的所有客户端会话（玩家断开/网关断开）
                foreach (var pair in clientGatewaySessions)
                {
                    if (ReferenceEquals(pair.Value, session))
                    {
                        clientGatewaySessions.TryRemove(pair.Key, out _);
                    }
                }
                Log.Info($"节点/网关从战斗服断开，原因: {reason}");
            };

            // 统一收包管线（单线程语义）：认证与路由元数据解析在收包线程完成，
            // 业务消息一律入队，由 TickEngine 主循环串行消费（对标 KBE mailbox）。
            tcpServer.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Battle 收到无效数据包，长度不足 4，Session:{session.SessionId} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;

                    // 内部连接认证：未认证连接只接受认证握手消息。
                    if (gatewayAuthFilters.TryGetValue(session.SessionId, out var authFilter))
                    {
                        if (!authFilter.IsAuthenticated)
                        {
                            if (Framework.Core.Security.InternalAuthFilter.IsAuthMessage(msgId))
                            {
                                byte[] authPayload = data.Slice(4).ToArray();
                                if (authFilter.TryAuthenticate(authPayload))
                                {
                                    Log.Info($"Battle <- Gateway/Node 认证成功 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                }
                                else
                                {
                                    Log.Warning($"Battle <- Gateway/Node 认证失败，断开连接 SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                                    session.Close();
                                    return;
                                }
                                return;
                            }

                            Log.Warning($"Battle 拒绝未认证连接的业务消息 MsgId:{msgId} SessionId:{session.SessionId} Remote:{session.RemoteEndPoint}");
                            return;
                        }
                    }

                    Log.Debug("Battle <- Gateway/Node 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);
                    byte[] payload = data.Slice(4).ToArray();

                    long originalSessionId = 0;
                    if (Shared.RouteMetadata.TryExtractClientSessionId(payload, out long clientSessionId, out var cleanPayload))
                    {
                        originalSessionId = clientSessionId;
                        payload = cleanPayload;
                        // 登记客户端会话 -> 网关会话 绑定（帧同步广播用）
                        BindClientGateway(originalSessionId, session);
                        Log.Debug("Battle 路由元数据解析成功 ClientSessionId:{ClientSessionId} MsgId:{MsgId}", originalSessionId, msgId);
                    }

                    // 业务消息入队，tick 线程串行处理（实体/场景状态只在主循环读写）
                    EnqueueInbound(session, msgId, payload, originalSessionId);
                }
                catch (Exception ex)
                {
                    Log.Error($"Battle 处理客户端数据异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };

            await tcpServer.StartAsync(port);
            Log.Info($"Battle 战斗服务器网络已启动，监听端口: {port}");

            ConnectToCenter(port);
        }

        /// <summary>
        /// 连接到 Center 服务器，向其注册本节点并维持心跳与事件处理。
        /// </summary>
        /// <remarks>从配置读取 CenterPort、CenterHost 和 BattleHost（分别默认为 31306、127.0.0.1、127.0.0.1）。建立
        /// TcpClientWrapper，连接成功后发送注册信息、启动每 10 秒一次的心跳上报任务；断开时取消心跳并记录日志，同时处理接收的数据事件。
        /// 协议层（KBE machine 化，迭代 20）：当配置中存在 NodeId/InstanceId/MachineId/SupervisedBy（由 NodeLaunchArgs 注入）时，
        /// 将其填入 CenterRegisterNodeRequest 一起上报，便于管理台按机器聚合。</remarks>
        /// <param name="port">用于对外的端口号，用于在注册和状态上报中标识节点。</param>
        private static void ConnectToCenter(int port)
        {
            int centerPort = ConfigHelper.GetConfig<int>("CenterPort") == 0 ? 31306 : ConfigHelper.GetConfig<int>("CenterPort");
            string centerHost = ConfigHelper.GetConfig<string>("CenterHost") ?? "127.0.0.1";
            string battleHost = ConfigHelper.GetConfig<string>("BattleHost") ?? "127.0.0.1";
            // nodeId 优先级：配置（machine 注入） > 按 host:port 派生（保持后向兼容）
            string nodeId = ConfigHelper.GetConfig<string>("NodeId") ?? $"Battle-{battleHost}:{port}";
            string instanceId = ConfigHelper.GetConfig<string>("InstanceId") ?? string.Empty;
            string machineId = ConfigHelper.GetConfig<string>("MachineId") ?? string.Empty;
            string supervisedBy = ConfigHelper.GetConfig<string>("SupervisedBy") ?? string.Empty;
            CurrentNodeId = nodeId;
            centerClient = new TcpClientWrapper(centerHost, centerPort);

            centerClient.OnConnected += session =>
            {
                Log.Info($"已连接到 Center 服务器 (Host:{centerHost} Port:{centerPort})");
                // 内部连接认证：先发送认证握手，再注册节点
                centerClient.SendInternalAuthHandshake(Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret"), nodeId);
                SendRegisterNode(centerClient, nodeId, "Battle", battleHost, port, GetCurrentLoad(), instanceId, machineId, supervisedBy);

                centerHeartbeatCts?.Cancel();
                centerHeartbeatCts = new System.Threading.CancellationTokenSource();
                var cancellationToken = centerHeartbeatCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                            SendNodeStatus(centerClient, nodeId, GetCurrentLoad());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, cancellationToken);
            };

            centerClient.OnDisconnected += (session, reason) =>
            {
                centerHeartbeatCts?.Cancel();
                Log.Warning($"与 Center 服务器断开连接: {reason}");
            };
            // Center 下发消息同样入队：与客户端消息共用同一 tick 线程串行处理，保证场景/实体状态单线程语义
            centerClient.OnDataReceived += (session, data) =>
            {
                try
                {
                    if (data.Length < 4)
                    {
                        Log.Warning($"Battle 收到 Center 无效数据包，长度不足 4，Session:{session.SessionId} Length:{data.Length}");
                        return;
                    }

                    int msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
                    int payloadLength = data.Length - 4;
                    Log.Debug("Battle <- Center 收到消息 SessionId:{SessionId} Remote:{Remote} MsgId:{MsgId} PacketLength:{PacketLength} PayloadLength:{PayloadLength}", session.SessionId, session.RemoteEndPoint, msgId, data.Length, payloadLength);
                    byte[] payload = data.Slice(4).ToArray();

                    EnqueueInbound(session, msgId, payload, 0);
                }
                catch (Exception ex)
                {
                    Log.Error($"Battle 处理 Center 回包异常 Session:{session.SessionId} Remote:{session.RemoteEndPoint} Exception:{ex}");
                }
            };
            _ = centerClient.ConnectAsync();
        }

        public static void SyncRoomPlayerCount(string roomId)
        {
            if (centerClient == null || sceneManager == null || string.IsNullOrWhiteSpace(roomId))
            {
                return;
            }

            var request = new CenterRoomPlayerCountSyncRequest
            {
                RoomId = roomId,
                CurrentPlayers = sceneManager.GetPlayerCount(roomId)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRoomPlayerCountSyncReq, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 同步房间人数失败 RoomId:{roomId} Exception:{ex}");
            }
        }

        public static void SyncRoomMemberLeave(string roomId, long clientSessionId)
        {
            if (centerClient == null || string.IsNullOrWhiteSpace(roomId) || clientSessionId <= 0)
            {
                return;
            }

            var request = new CenterRoomMemberLeaveSyncRequest
            {
                RoomId = roomId,
                ClientSessionId = clientSessionId
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(request);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRoomMemberLeaveSyncReq, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 同步房间成员退出失败 RoomId:{roomId} ClientSessionId:{clientSessionId} Exception:{ex}");
            }
        }

        /// <summary>
        /// 将本节点的注册请求发送到中心服务器。
        /// </summary>
        /// <remarks>将 CenterRegisterNodeRequest 序列化为 UTF-8 JSON，构建包含 MessageIds.CenterRegisterNodeReq
        /// 的会话包装数据包并通过 centerClient 发送。</remarks>
        /// <param name="centerClient">用于与中心服务器通信并发送数据的客户端包装器。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="nodeType">节点类型标识（例如角色或服务）。</param>
        /// <param name="host">节点的主机名或 IP 地址。</param>
        /// <param name="port">节点的监听端口。</param>
        /// <param name="currentLoad">节点当前的负载值，用于负载均衡或监控。</param>
        /// <param name="instanceId">实例 ID（machine 注入；可空）。</param>
        /// <param name="machineId">托管本节点的 Machine 进程 ID（可空）。</param>
        /// <param name="supervisedBy">托管方类型（"machine" / "supervisor" / "none" / 自定义；可空）。</param>
        private static void SendRegisterNode(TcpClientWrapper centerClient, string nodeId, string nodeType, string host, int port, int currentLoad,
            string instanceId = "", string machineId = "", string supervisedBy = "")
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 协议扩展（迭代 20）：Machine 注入字段参与签名源；空字符串同样参与，Center 侧拼串一致才能通过
            string signatureSource = $"{nodeId}|{nodeType}|{host}|{port}|{currentLoad}|{instanceId}|{machineId}|{supervisedBy}|{timestamp}";
            var registerRequest = new CenterRegisterNodeRequest
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Host = host,
                Port = port,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                InstanceId = instanceId,
                MachineId = machineId,
                SupervisedBy = supervisedBy,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(registerRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterRegisterNodeReq, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 向 Center 注册节点失败 NodeId:{nodeId} Exception:{ex}");
            }
        }

        /// <summary>
        /// 将节点的当前负载序列化为 CenterNodeStatusRequest 并通过中心客户端发送。
        /// </summary>
        /// <remarks>将 CenterNodeStatusRequest 序列化为 UTF-8 字节，使用 MessageIds.CenterNodeStatusReq 构建会话封装报文并通过
        /// centerClient 发送。</remarks>
        /// <param name="centerClient">用于向中心服务器发送封装报文的 TcpClientWrapper 实例。</param>
        /// <param name="nodeId">节点的唯一标识符。</param>
        /// <param name="currentLoad">节点当前的负载值。</param>
        private static void SendNodeStatus(TcpClientWrapper centerClient, string nodeId, int currentLoad)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signatureSource = $"{nodeId}|{currentLoad}|{timestamp}";
            var statusRequest = new CenterNodeStatusRequest
            {
                NodeId = nodeId,
                CurrentLoad = currentLoad,
                Timestamp = timestamp,
                Signature = ComputeCenterSignature(signatureSource)
            };
            byte[] payload = Shared.Json.SerializeToUtf8Bytes(statusRequest);
            byte[] packet = PacketBuilder.BuildPacket(MessageIds.CenterNodeStatusReq, payload, out int totalLength);
            try
            {
                Network.PacketSender.Send(centerClient, packet, totalLength);
            }
            catch (Exception ex)
            {
                Log.Error($"Battle 向 Center 上报节点状态失败 NodeId:{nodeId} Exception:{ex}");
            }
        }

        /// <summary>
        /// 使用共享密钥和 HMAC-SHA256 计算输入字符串的签名，并以 Base64 编码返回。
        /// </summary>
        /// <remarks>从配置键 'CenterNodeSharedSecret' 读取共享密钥；如果未配置，则回退到默认值 'change-this-secret'。</remarks>
        /// <param name="source">要计算签名的输入字符串。</param>
        /// <returns>签名的 Base64 编码字符串，使用 UTF-8 编码的输入和 HMAC-SHA256 生成。</returns>
        private static string ComputeCenterSignature(string source)
        {
            string secret = Framework.Core.Security.SecretConfig.Require("CenterNodeSharedSecret");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(source);
            using var hmac = new HMACSHA256(key);
            return Convert.ToBase64String(hmac.ComputeHash(data));
        }

        /// <summary>
        /// 返回当前负载计数，等于已绑定玩家数与场景数中的较大值。
        /// </summary>
        /// <remarks>通过比较 sceneManager.GetBoundPlayerCount() 与 sceneManager.GetSceneCount()
        /// 的值确定负载。</remarks>
        /// <returns>当前负载计数；sceneManager 为 null 时返回 0。</returns>
        private static int GetCurrentLoad()
        {
            if (sceneManager == null)
            {
                return 0;
            }

            return Math.Max(sceneManager.GetBoundPlayerCount(), sceneManager.GetSceneCount());
        }
    }
}