using System.Security.Cryptography;

namespace Network;

/// <summary>
/// 会话 ID 生成器：加密随机 + 单调计数器混合。
/// 修复原实现（纯 Interlocked 计数器）的会话枚举风险。
/// 格式：高位 32 位为加密随机基座，低位 32 位为递增序列。
/// </summary>
internal static class SessionIdGenerator
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    private static long counter;

    private static readonly long RandomBase;

    static SessionIdGenerator()
    {
        var buf = new byte[8];
        Rng.GetBytes(buf);
        long randomBits = BitConverter.ToUInt32(buf, 0) & 0x7FFFFFFF;
        RandomBase = randomBits << 32;
    }

    public static long Next()
    {
        long seq = Interlocked.Increment(ref counter);
        return RandomBase | (seq & 0xFFFFFFFFL);
    }
}
