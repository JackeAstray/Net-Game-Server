using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Framework.Entity;

namespace Battle.Handlers
{
    /// <summary>
    /// 战斗回放录制器（A2）：低频采样（默认每 2 秒）对场景内实体做全量属性快照，
    /// 存入每场景环形缓冲（防无界增长）。客户端可请求最近 N 帧用于复盘/观战回放。
    /// 录制在 tick 线程调用（单线程约束），导出可跨线程读（ConcurrentQueue 快照）。
    /// </summary>
    public sealed class BattleReplayRecorder
    {
        /// <summary>每场景最多保留的帧数（~12 分钟 @2s 采样）。</summary>
        private const int MaxFramesPerScene = 360;

        private readonly ConcurrentDictionary<string, ConcurrentQueue<ReplayFrame>> buffers = new(StringComparer.Ordinal);

        public sealed class ReplayEntitySnapshot
        {
            public long EntityId { get; set; }
            public byte[] Props { get; set; } = Array.Empty<byte>();
        }

        public sealed class ReplayFrame
        {
            public long FrameId { get; set; }
            public long TimeMs { get; set; }
            public List<ReplayEntitySnapshot> Snapshots { get; set; } = new();
        }

        /// <summary>录制一次场景快照（tick 线程调用）。</summary>
        public void Record(Battle.Handlers.EntityManager entityManager, string sceneId, long frameId)
        {
            if (entityManager == null || string.IsNullOrEmpty(sceneId))
            {
                return;
            }
            var frame = new ReplayFrame
            {
                FrameId = frameId,
                TimeMs = Environment.TickCount64
            };
            foreach (var entityId in entityManager.GetAllSessionIds())
            {
                var entity = entityManager.GetEntity(entityId);
                if (entity == null)
                {
                    continue;
                }
                // 剔除 OWN_CLIENT 私有属性（回放给观战/复盘用，私有数据不落盘）
                frame.Snapshots.Add(new ReplayEntitySnapshot
                {
                    EntityId = entityId,
                    Props = PropertyCodec.SerializeAll(entity, includeOwnClient: false)
                });
            }

            var q = buffers.GetOrAdd(sceneId, _ => new ConcurrentQueue<ReplayFrame>());
            q.Enqueue(frame);
            while (q.Count > MaxFramesPerScene)
            {
                q.TryDequeue(out _);
            }
        }

        /// <summary>取某场景最近 maxFrames 帧（导出用，可跨线程）。</summary>
        public List<ReplayFrame> GetRecent(string sceneId, int maxFrames)
        {
            if (!buffers.TryGetValue(sceneId, out var q) || q.IsEmpty)
            {
                return new List<ReplayFrame>();
            }
            var all = q.ToArray();
            int take = maxFrames > 0 ? Math.Min(maxFrames, all.Length) : all.Length;
            return all.Skip(all.Length - take).ToList();
        }

        /// <summary>场景销毁时清理录制缓冲。</summary>
        public void RemoveScene(string sceneId)
        {
            if (!string.IsNullOrEmpty(sceneId))
            {
                buffers.TryRemove(sceneId, out _);
            }
        }
    }
}
