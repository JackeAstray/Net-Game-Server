using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Framework.Protocol.Generated;
using Framework.Tick;
using Shared.Messages;
using GenIds = Framework.Protocol.Generated.MessageIds;

namespace Battle.Handlers;

/// <summary>
/// 帧同步管理器（对标 KBE 的 gameUpdateHertz 逻辑 tick）：
/// - 客户端通过 BattleFrameSync(40003) 上报输入（入队）
/// - TickEngine 每帧按**场景独立推进帧号**，聚合该场景玩家输入，生成权威帧广播给场景内玩家；
///   无输入也广播空帧（确定性帧同步：客户端可对齐服务端帧节奏与帧号）
/// 协议（客户端视角）：
///   上行：BattleFrameSync { FrameId, Inputs: [PlayerInput] }（客户端预测帧上报）
///   下行：BattleFrameSync { FrameId, Inputs: [PlayerInput { InputId=玩家SessionId, ... }] }（服务端权威帧）
/// </summary>
public sealed class FrameSyncManager
{
    private readonly SceneManager sceneManager;
    private readonly TickEngine tickEngine;

    /// <summary>场景 -> 玩家输入队列（收包线程入队，tick 线程消费）</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(long sessionId, PlayerInput input)>> inputQueues = new();

    /// <summary>场景 -> 服务端帧号（每场景独立推进，多场景互不干扰）</summary>
    private readonly ConcurrentDictionary<string, long> sceneFrames = new();

    /// <summary>单包输入数量上限（防放大广播/大列表分配 DoS）。</summary>
    private const int MaxInputsPerPacket = 64;

    /// <summary>单帧聚合输入数量上限（超出丢弃，防洪泛）。</summary>
    private const int MaxInputsPerFrame = 256;

    /// <summary>单客户端每帧输入配额上限（P3 加固：防单客户端填满整帧导致他人输入被丢弃）。</summary>
    private const int MaxInputsPerClientPerFrame = 8;

    /// <summary>每场景输入队列总长度上限（P3 加固：防输入队列无界增长耗尽内存）。</summary>
    private const int MaxQueuedInputsPerScene = 512;

    /// <summary>每客户端帧状态（FrameId 防重放/乱序）。</summary>
    private sealed class ClientFrameState
    {
        public int LastFrameId;
        public long LastWarnMs;
    }

    /// <summary>场景+客户端 -> 最近接受的 FrameId（用于去重/防重放）。</summary>
    private readonly ConcurrentDictionary<(string SceneId, long ClientId), ClientFrameState> clientFrameStates = new();

    public FrameSyncManager(SceneManager sceneManager, TickEngine tickEngine)
    {
        this.sceneManager = sceneManager;
        this.tickEngine = tickEngine;
        this.tickEngine.OnTick += OnTick;
    }

    /// <summary>
    /// 客户端上报输入（由 MessageRouter 调用；消息队列模式下在 tick 线程执行）。
    /// </summary>
    public void EnqueueInput(long clientSessionId, BattleFrameSync request)
    {
        var scene = sceneManager.GetSceneByPlayer(clientSessionId);
        if (scene == null)
        {
            return;
        }

        // P3 加固：帧输入防重放/乱序 —— 同一客户端的 FrameId 必须严格递增。
        // 此前 FrameId 完全被忽略，作弊客户端可对同一帧反复提交不同输入（能力/开火作弊），
        // 也可乱序回放旧帧。现在 < 上次（乱序/回放）或 == 上次（重复提交）一律丢弃。
        var key = (scene.SceneId, clientSessionId);
        var state = clientFrameStates.GetOrAdd(key, _ => new ClientFrameState { LastFrameId = request.FrameId });
        if (request.FrameId <= state.LastFrameId)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - state.LastWarnMs > 5000)
            {
                state.LastWarnMs = nowMs;
                Shared.Log.Warning($"帧同步 FrameId 非法被丢弃（重放/乱序/重复）SessionId:{clientSessionId} FrameId:{request.FrameId} LastSeen:{state.LastFrameId}");
            }
            return;
        }
        state.LastFrameId = request.FrameId;

        var inputs = request.Inputs;
        if (inputs == null || inputs.Count == 0)
        {
            return;
        }

        // 安全加固：单包输入数量上限（截断），并对浮点输入做 NaN/Inf 清洗与幅度钳制
        int count = Math.Min(inputs.Count, MaxInputsPerPacket);
        if (inputs.Count > MaxInputsPerPacket)
        {
            Shared.Log.Warning($"帧同步输入数量超上限被截断 SessionId:{clientSessionId} Count:{inputs.Count} Cap:{MaxInputsPerPacket}");
        }

        var queue = inputQueues.GetOrAdd(scene.SceneId, _ => new ConcurrentQueue<(long, PlayerInput)>());
        // P3 加固：队列总长度上限（防洪泛填满队列耗尽内存）。
        if (queue.Count >= MaxQueuedInputsPerScene)
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var input = inputs[i];
            SanitizeInput(input);
            queue.Enqueue((clientSessionId, input));
        }
    }

    /// <summary>清洗输入浮点字段：NaN/Inf → 0，幅度钳制到 [-100, 100]（防注入毒化确定性模拟）。</summary>
    private static void SanitizeInput(PlayerInput input)
    {
        input.MoveX = ClampFloat(input.MoveX);
        input.MoveY = ClampFloat(input.MoveY);
    }

    private static float ClampFloat(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0;
        return Math.Clamp(v, -100f, 100f);
    }

    /// <summary>
    /// tick 回调：所有有玩家的场景统一推进帧号并广播权威帧（无输入时广播空帧）。
    /// </summary>
    private void OnTick(long frame)
    {
        foreach (var scene in sceneManager.GetAllScenes())
        {
            var sessionIds = sceneManager.GetPlayerSessionIds(scene.SceneId);

            // 无条件清空输入队列（防离场玩家的过期输入在新玩家加入后被回放；无玩家时直接丢弃）
            var inputs = new List<PlayerInput>();
            if (inputQueues.TryGetValue(scene.SceneId, out var queue))
            {
                // P3 加固：每客户端每帧输入配额（防单客户端洪泛填满整帧导致他人输入被丢弃）。
                var perClient = new Dictionary<long, int>();
                while (queue.TryDequeue(out var entry))
                {
                    if (sessionIds.Length == 0) continue; // 无玩家：丢弃过期输入
                    if (perClient.TryGetValue(entry.sessionId, out var contributed))
                    {
                        if (contributed >= MaxInputsPerClientPerFrame) continue; // 该客户端已达单帧配额
                    }
                    if (inputs.Count >= MaxInputsPerFrame) break; // 帧满
                    var input = entry.input;
                    input.InputId = entry.sessionId; // 用玩家会话ID标识输入来源（long 全量，不截断）
                    inputs.Add(input);
                    perClient[entry.sessionId] = contributed + 1;
                }
            }

            if (sessionIds.Length == 0)
            {
                continue; // 无玩家不广播
            }

            // 场景独立帧号：从 1 开始递增
            long sceneFrame = sceneFrames.AddOrUpdate(scene.SceneId, 1, (_, v) => v + 1);

            var frameMsg = new BattleFrameSync
            {
                FrameId = (int)sceneFrame,
                Inputs = inputs
            };
            byte[] payload = frameMsg.Serialize();

            // 广播给场景内所有玩家（帧同步是全局广播，无需 AOI）
            foreach (var sessionId in sessionIds)
            {
                SendFramePacket(sessionId, payload);
            }
        }
    }

    private void SendFramePacket(long targetSessionId, byte[] payload)
    {
        // 帧同步广播走网关定向投递：通过场景绑定的网关会话发送
        // 注：此处由上层注入发送委托（见 BattleServerApp.FrameSyncSendAction），
        // 因为 FrameSyncManager 不直接持有网关会话。
        _sendAction?.Invoke(targetSessionId, GenIds.BattleFrameSync, payload);
    }

    private Action<long, int, byte[]>? _sendAction;

    /// <summary>注入发送委托（BattleServerApp 在收到玩家消息时按会话绑定网关）。</summary>
    public void SetSendAction(Action<long, int, byte[]> sendAction)
    {
        _sendAction = sendAction;
    }

    /// <summary>停止引擎时的清理。</summary>
    public void Shutdown()
    {
        tickEngine.OnTick -= OnTick;
    }

    /// <summary>
    /// 场景销毁时清理该场景的输入队列与帧号（防字典无界增长 + 防过期输入被新场景回放）。
    /// </summary>
    public void RemoveScene(string sceneId)
    {
        inputQueues.TryRemove(sceneId, out _);
        sceneFrames.TryRemove(sceneId, out _);
        // P3 加固：清理该场景下所有客户端的帧状态（防字典无界增长 + 防换房后旧 FrameId 状态误拒新输入）。
        foreach (var kvp in clientFrameStates)
        {
            if (string.Equals(kvp.Key.SceneId, sceneId, StringComparison.Ordinal))
            {
                clientFrameStates.TryRemove(kvp.Key, out _);
            }
        }
    }
}
