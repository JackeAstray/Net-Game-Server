using System.Buffers.Binary;
using System.IO;

namespace Network.Routing;

internal sealed class LengthPrefixedPacketReader
{
    private byte[] buffer;
    private int bufferedCount;

    public LengthPrefixedPacketReader(int initialCapacity = 8192)
    {
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
    /// 尝试从内部缓冲区读取下一个以 4 字节小端整型为长度前缀的包；成功时将有效负载作为 ReadOnlyMemory<byte> 返回并从缓冲区移除。
    /// </summary>
    /// <remarks>长度前缀为 4 字节小端整型。方法为非阻塞且可以被反复调用以逐个提取包。</remarks>
    /// <param name="packet">输出参数；成功时包含包的有效负载（已复制到新的字节数组），失败时为 default(ReadOnlyMemory<byte>)。</param>
    /// <returns>成功读取并移除一个完整包时返回 true；缓冲区数据不足以构成完整包时返回 false。</returns>
    /// <exception cref="InvalidDataException">当长度前缀小于等于 0 或被视为无效的长度值时抛出。</exception>
    public bool TryReadPacket(out ReadOnlyMemory<byte> packet)
    {
        packet = default;

        if (bufferedCount < 4)
        {
            return false;
        }

        int packetLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));
        if (packetLength <= 0)
        {
            throw new InvalidDataException($"Invalid packet length: {packetLength}");
        }

        if (bufferedCount < 4 + packetLength)
        {
            return false;
        }

        byte[] payload = new byte[packetLength];
        Buffer.BlockCopy(buffer, 4, payload, 0, packetLength);

        int remaining = bufferedCount - 4 - packetLength;
        if (remaining > 0)
        {
            Buffer.BlockCopy(buffer, 4 + packetLength, buffer, 0, remaining);
        }

        bufferedCount = remaining;
        packet = payload;
        return true;
    }

    /// <summary>
    /// 确保内部缓冲区的长度至少为指定的最小容量；若当前容量不足，则按 2 的倍数增长直到满足要求。
    /// </summary>
    /// <remarks>扩容通过 Array.Resize 完成，保留现有元素。增长策略按 2 的倍数扩展，可能会超出所需容量以减少频繁重分配。</remarks>
    /// <param name="required">所需的最小容量（小于等于当前容量时不做任何操作）。</param>
    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
        {
            return;
        }

        int newSize = buffer.Length;
        while (newSize < required)
        {
            newSize *= 2;
        }

        Array.Resize(ref buffer, newSize);
    }
}