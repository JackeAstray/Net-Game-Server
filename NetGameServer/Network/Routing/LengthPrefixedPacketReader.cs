using System.Buffers.Binary;
using System.IO;

namespace Network.Routing;

public sealed class LengthPrefixedPacketReader
{
    /// <summary>
    /// 单个网络包的最大允许字节数（不含 4 字节长度前缀）。
    /// 防止攻击者声明超大长度触发 OOM（DoS 防护）。默认 64KB，覆盖正常游戏包；
    /// 调用方如需更大的请使用 <see cref="LengthPrefixedPacketReader(int, int)"/> 显式指定。
    /// </summary>
    public const int DefaultMaxPacketLength = 64 * 1024;

    /// <summary>惰性压缩阈值：已消费前缀达到该字节数且不小于剩余数据时，才一次性搬到缓冲开头（摊薄 O(n²)）。</summary>
    private const int CompactThreshold = 16 * 1024;

    private readonly int maxPacketLength;
    private byte[] buffer;
    private int bufferedCount;   // 缓冲内有效字节总数（含已消费前缀）
    private int startOffset;     // 第一个未消费字节的下标

    public LengthPrefixedPacketReader(int initialCapacity = 8192, int maxPacketLength = DefaultMaxPacketLength)
    {
        if (initialCapacity < 4)
        {
            initialCapacity = 4;
        }
        if (maxPacketLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPacketLength), "必须 > 0");
        }
        this.maxPacketLength = maxPacketLength;
        buffer = new byte[initialCapacity];
    }

    /// <summary>
    /// 将指定的 ReadOnlySpan<byte> 追加到内部缓冲区的末尾。
    /// </summary>
    /// <remarks>必要时会扩展内部缓冲区以容纳附加的数据；随后将数据复制到缓冲区并更新缓冲计数。</remarks>
    /// <param name="data">要追加到缓冲区末尾的只读字节序列。</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(bufferedCount + data.Length);
        data.CopyTo(buffer.AsSpan(bufferedCount));
        bufferedCount += data.Length;
    }

    /// <summary>
    /// 尝试从内部缓冲区读取下一个以 4 字节小端整型为长度前缀的包；成功时将有效负载作为 ReadOnlyMemory<byte> 返回。
    /// </summary>
    /// <remarks>
    /// P2 修复：采用"已消费偏移 + 惰性压缩"取代原先每个包都做一次 Buffer.BlockCopy 前移——
    /// 原先一次 TCP 段携带 N 个小包时共搬运 O(N²) 字节；现仅当已消费前缀足够大且不小于剩余数据时
    /// 才做一次整段搬移，摊薄为摊还 O(1)/包。
    /// </remarks>
    /// <param name="packet">输出参数；成功时包含包的有效负载（已复制到新的字节数组），失败时为 default(ReadOnlyMemory<byte>)。</param>
    /// <returns>成功读取并移除一个完整包时返回 true；缓冲区数据不足以构成完整包时返回 false。</returns>
    /// <exception cref="InvalidDataException">当长度前缀小于等于 0 或被视为无效的长度值时抛出。</exception>
    public bool TryReadPacket(out ReadOnlyMemory<byte> packet)
    {
        packet = default;

        int available = bufferedCount - startOffset;
        if (available < 4)
        {
            // 头不完整：将剩余的少量字节搬到缓冲开头（也涵盖全部已消费的空缓冲场景）
            if (startOffset > 0)
            {
                Buffer.BlockCopy(buffer, startOffset, buffer, 0, available);
                bufferedCount = available;
                startOffset = 0;
            }
            return false;
        }

        int packetLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(startOffset, 4));
        if (packetLength <= 0)
        {
            throw new InvalidDataException($"Invalid packet length: {packetLength}");
        }
        if (packetLength > maxPacketLength)
        {
            // 防 DoS：拒绝声明超大长度的包。抛出后由调用方关闭连接。
            throw new InvalidDataException(
                $"Packet length {packetLength} 超过最大允许 {maxPacketLength} 字节，已拒绝（疑似 DoS 攻击）");
        }

        int total = 4 + packetLength;
        if (available < total)
        {
            // 慢速/悬空 DoS 防护：长度前缀已声明（≤ maxPacketLength）但载荷迟迟不补齐，
            // 且已缓冲的未解析字节已超过 单包上限+4(前缀) —— 说明对端在持续投喂数据却从不构成完整包，
            // 内部缓冲会随 Append 无限增长导致 OOM（此前无此上限）。超出即抛异常，由调用方关闭连接。
            // 说明：合法在途的单包最多占用 maxPacketLength+4 字节，超过即判定为异常输入。
            if (available > maxPacketLength + 4)
            {
                throw new InvalidDataException(
                    $"数据包载荷未补齐且缓冲超限（已缓冲 {available} 字节，上限 {maxPacketLength + 4}），疑似慢速 DoS 攻击，已拒绝");
            }
            return false;
        }

        byte[] payload = new byte[packetLength];
        Buffer.BlockCopy(buffer, startOffset + 4, payload, 0, packetLength);
        startOffset += total;

        // 惰性压缩：仅当已消费前缀较大且不小于剩余数据时整段搬移
        if (startOffset >= CompactThreshold && startOffset >= bufferedCount - startOffset)
        {
            int remaining = bufferedCount - startOffset;
            Buffer.BlockCopy(buffer, startOffset, buffer, 0, remaining);
            bufferedCount = remaining;
            startOffset = 0;
        }

        packet = payload;
        return true;
    }

    /// <summary>
    /// 确保内部缓冲区的长度至少可容纳 requiredEnd（相对缓冲开头的绝对下标）；若当前容量不足，则按 2 的倍数增长。
    /// P3 修复：防止 requiredEnd/required 整数溢出导致的负容量或倍增死循环。
    /// </summary>
    private void EnsureCapacity(int requiredEnd)
    {
        if (requiredEnd < 0)
        {
            throw new InvalidDataException($"无效的缓冲要求（整数溢出）: {requiredEnd}");
        }
        int required = requiredEnd + startOffset;
        if (required < requiredEnd)
        {
            throw new InvalidDataException("缓冲大小计算溢出");
        }
        if (required <= buffer.Length)
        {
            return;
        }

        int newSize = buffer.Length;
        while (newSize < required)
        {
            if (newSize > int.MaxValue / 2)
            {
                throw new InvalidDataException($"缓冲需求 {required} 过大（疑似异常输入）");
            }
            newSize *= 2;
        }

        Array.Resize(ref buffer, newSize);
    }
}
