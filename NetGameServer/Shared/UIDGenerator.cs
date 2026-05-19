using System;
using System.Threading;

namespace Shared
{
    /// <summary>
    /// 原神风格纯数字发号器 (区服前缀 + 自增序列)
    /// </summary>
    public static class UIDGenerator
    {
        // 计数器，保存当前的序号
        private static long currentCounter = 0;

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
        public static void Initialize(int regionId, long currentMaxSequenceID)
        {
            if (regionId < 1 || regionId > 9)
            {
                Log.Error($"无效的区服ID: {regionId}. 区服ID必须在 1 到 9 之间");
                return;
            }

            // 例如 regionId = 1，regionPrefix = 100000000
            // 例如 regionId = 8，regionPrefix = 800000000 (亚服风格)
            regionPrefix = regionId * 100000000L;

            currentCounter = currentMaxSequenceID;
            Volatile.Write(ref initialized, 1);
        }

        /// <summary>
        /// 生成原神风格的9位纯数字 UID
        /// </summary>
        /// <returns>例如: 100000001</returns>
        public static long GenerateLongUID()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("UID 生成器尚未初始化完成");
            }

            // Interlocked 保证在多线程/高并发下的原子自增，绝对不会重复
            long sequence = Interlocked.Increment(ref currentCounter);
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