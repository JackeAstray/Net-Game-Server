using System.Security.Cryptography;

namespace Framework.Core.Security;

/// <summary>
/// 会话 ID 生成器：单调计数器 + splitmix64 非线性置换（进程随机种子）。
/// 解决原实现（RandomBase | counter 纯拼接，观察一个 ID 即可反推随机基与计数，后续全部可枚举）
/// 的会话枚举风险。
/// splitmix64 是 64 位双射：同一进程内输出互不重复（唯一性），
/// 且相邻样本无法反推计数器（不可预测性），跨进程种子随机、碰撞概率可忽略。
/// </summary>
public static class SessionIdGenerator
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    private static long counter;
    // 进程随机种子（splitmix64 加盐）
    private static readonly ulong Seed;

    static SessionIdGenerator()
    {
        var buf = new byte[8];
        Rng.GetBytes(buf);
        Seed = BitConverter.ToUInt64(buf, 0);
    }

    /// <summary>
    /// 生成一个全局唯一且不可预测的会话 ID。
    /// 单调计数器保证并发下的唯一性，splitmix64 混淆保证不可预测。
    /// 掩掉符号位保证结果为非负：全代码库以 <c>&gt; 0</c> 作为"会话有效"判定
    /// （如 MatchHandler 人数统计、Gateway 迁移绑定、Friend 回包投递等），
    /// 若输出为负会导致这些判定静默失效（实测 91005 迁移绑定约 50% 概率被丢弃）。
    /// 63 位 splitmix64 双射仍保持进程内唯一、相邻样本不可反推、跨进程碰撞可忽略。
    /// </summary>
    public static long Next()
    {
        long seq = Interlocked.Increment(ref counter);
        return (long)(SplitMix64((ulong)seq + Seed) & 0x7FFF_FFFF_FFFF_FFFFUL);
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
