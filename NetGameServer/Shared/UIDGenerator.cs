using System;
using System.Threading;

namespace Shared
{
    /// <summary>
    /// 原神风格纯数字发号器 (区服前缀 + 自增序列)
    ///
    /// 多实例并发安全问题（P2 修复）：
    /// - 进程内 Interlocked 只保证单进程不重复；多个 Login/DB 实例各自从 DB 最大序号初始化后
    ///   独立自增，会跨实例碰撞。调用方应通过 reserveBatch 预留一段（如 1000），
    ///   本进程只在该段内发号，段耗尽时抛异常促使调用方重新向 DB 申请（见 GenerateLongUID）。
    /// - 越界保护：序列号超过 9 位上限（99,999,999）时直接抛异常，避免静默侵入下一区服前缀空间。
    /// </summary>
    public static class UIDGenerator
    {
        // 计数器，保存当前的序号
        private static long currentCounter = 0;

        // 预留段上限（0 表示不限制）：本进程可安全发号到该值；超出需重新向 DB 申请（防多实例碰撞）
        private static long reservedThrough = 0;

        // 当前大区的前缀。例如：1代表1区，那么最终生成的就是 100000000 + 序号
        private static long regionPrefix = 100000000;

        // 是否已完成初始化（从 DB 同步最大序号）
        private static int initialized = 0;

        public static bool IsInitialized => Volatile.Read(ref initialized) == 1;

        /// <summary>
        /// 服务器启动时，从数据库获取当前最大 UID 以初始化发号器。
        /// 如果数据库为空，则传入 0。
        /// </summary>
        /// <param name="regionId">区服ID (1-9)</param>
        /// <param name="currentMaxSequenceID">当前数据库最大的序号（不包含区服前缀）。如果最大UID是100005，这里传入5</param>
        /// <param name="reserveBatch">预留发号段大小（如 1000）：本进程只发 [max+1, max+batch]；
        /// 大于 0 时，段耗尽 GenerateLongUID 抛异常以触发调用方重新申请；0 表示不预留（仅做越界保护）。</param>
        public static void Initialize(int regionId, long currentMaxSequenceID, long reserveBatch = 0)
        {
            if (regionId < 1 || regionId > 9)
            {
                Log.Error($"无效的区服ID: {regionId}. 区服ID必须在 1 到 9 之间");
                return;
            }

            // 例如 regionId = 1，regionPrefix = 100000000
            // 例如 regionId = 8，regionPrefix = 800000000 (亚服风格)
            regionPrefix = regionId * 100000000L;

            currentCounter = Math.Max(0, currentMaxSequenceID);
            reservedThrough = reserveBatch > 0 ? currentCounter + reserveBatch : 0;
            Volatile.Write(ref initialized, 1);
        }

        /// <summary>
        /// 生成原神风格的9位纯数字 UID。
        /// 序列号越界（>99,999,999）或预留段耗尽时抛 InvalidOperationException，
        /// 调用方应重新向 DB 申请发号段后重试（Login 已实现该重试路径）。
        /// </summary>
        /// <returns>例如: 100000001</returns>
        public static long GenerateLongUID()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("UID 生成器尚未初始化完成");
            }

            // Interlocked 保证在多线程/高并发下的原子自增，单进程内绝对不会重复
            long sequence = Interlocked.Increment(ref currentCounter);
            if (sequence > 99999999L)
            {
                throw new InvalidOperationException(
                    $"UID 序列号越界（>99,999,999）：当前序列 {sequence}，请扩容区服或调整发号策略");
            }
            if (reservedThrough > 0 && sequence > reservedThrough)
            {
                throw new InvalidOperationException(
                    $"UID 预留发号段已耗尽（上限 {reservedThrough}）：请重新向 DB 申请发号段");
            }
            return regionPrefix + sequence;
        }

        /// <summary>
        /// 生成字符串格式的 UID
        /// </summary>
        public static string GenerateStringUID()
        {
            return GenerateLongUID().ToString();
        }
    }
}