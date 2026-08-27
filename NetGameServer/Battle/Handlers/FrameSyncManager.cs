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
/// - TickEngine 每帧聚合每个场景的玩家输入，生成统一的帧数据广播给场景内玩家
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

    /// <summary>服务端当前帧号</summary>
    private long serverFrame;

    public long CurrentFrame => Volatile.Read(ref serverFrame);

    public FrameSyncManager(SceneManager sceneManager, TickEngine tickEngine)
    {
        this.sceneManager = sceneManager;
        this.tickEngine = tickEngine;
        this.tickEngine.OnTick += OnTick;
    }

    /// <summary>
    /// 客户端上报输入（由 MessageRouter 调用）。
    /// </summary>
    public void EnqueueInput(long clientSessionId, BattleFrameSync request)
    {
        var scene = sceneManager.GetSceneByPlayer(clientSessionId);
        if (scene == null)
        {
            return;
        }

        var queue = inputQueues.GetOrAdd(scene.SceneId, _ => new ConcurrentQueue<(long, PlayerInput)>());
        foreach (var input in request.Inputs ?? new List<PlayerInput>())
        {
            queue.Enqueue((clientSessionId, input));
        }
    }

    /// <summary>
    /// tick 回调：聚合各场景输入并广播权威帧。
    /// </summary>
    private void OnTick(long frame)
    {
        foreach (var pair in inputQueues)
        {
            string sceneId = pair.Key;
            var queue = pair.Value;
            var scene = sceneManager.GetScene(sceneId);
            if (scene == null)
            {
                continue;
            }

            // 聚合本帧所有玩家输入
            var inputs = new List<PlayerInput>();
            while (queue.TryDequeue(out var entry))
            {
                var input = entry.input;
                input.InputId = (int)entry.sessionId; // 用玩家会话ID标识输入来源
                inputs.Add(input);
            }

            if (inputs.Count == 0)
            {
                continue; // 无输入不广播（可改为心跳帧）
            }

            long currentFrame = Interlocked.Increment(ref serverFrame);
            var frameMsg = new BattleFrameSync
            {
                FrameId = (int)currentFrame,
                Inputs = inputs
            };
            byte[] payload = frameMsg.Serialize();

            // 广播给场景内所有玩家（帧同步是全局广播，无需 AOI）
            foreach (var sessionId in scene.EntityManager.GetAllSessionIds())
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
}
