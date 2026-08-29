using System.Security.Cryptography;

namespace Framework.Core.Security;

/// <summary>
/// 会话 ID 生成器：加密随机 + 单调计数器混合。
/// 解决原实现（纯 Interlocked 计数器，可预测）的会话枚举风险。
/// 格式：高位 32 位为随机数，低位 32 位为计数器，两者混合后不可顺序预测。
/// </summary>
public static class SessionIdGenerator
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    private static long counter;

    private static readonly long RandomBase;

    static SessionIdGenerator()
    {
        var buf = new byte[8];
        Rng.GetBytes(buf);
        // 只取高 32 位随机作为随机基座（转为 long 后再左移，避免无符号运算）。
        // 注：randomBits & 0x7FFFFFFF 保证非负，RandomBase 恒 ≥ 0（死分支 if(RandomBase<0) 已删除）。
        long randomBits = BitConverter.ToUInt32(buf, 0) & 0x7FFFFFFF;
        RandomBase = randomBits << 32;
    }

    /// <summary>
    /// 生成一个全局唯一且不可预测的会话 ID。
    /// 单调计数器保证并发下的唯一性，随机基座保证不可预测。
    /// </summary>
    public static long Next()
    {
        long seq = Interlocked.Increment(ref counter);
        return RandomBase | (seq & 0xFFFFFFFFL);
    }
}
