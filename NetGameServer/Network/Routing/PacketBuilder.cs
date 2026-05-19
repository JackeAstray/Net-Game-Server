using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Network.Routing;

/// <summary>
/// 表示一个网络数据包的概念结构与打包帮助类。
/// 包结构定义： TotalLength(4 byte) + MsgId(4 byte) + Payload (N byte)
/// </summary>
public static class PacketBuilder
{
    /// <summary>
    /// 对要发送的数据直接进行打包，返回组装完成后的带有Length和MsgId头的内存。
    /// 可以使用 System.Buffers.ArrayPool<byte> 进行零GC封装优化。
    /// 警告：返回值（数组所有权）应该交由底层释放
    /// </summary>
    public static byte[] BuildPacket(int msgId, ReadOnlySpan<byte> payload, out int totalLength)
    {
        // TotalLength 不包含自身的4个字节长度。它表示： MsgId 长度 (4) + Payload 长度
        int innerLength = 4 + payload.Length;
        totalLength = 4 + innerLength;

        // 统一从对象池获取
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);

        // 1. 头: 写入包体总长
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), innerLength);
        // 2. 体: 写入 Msg Id
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), msgId);
        // 3. 尾: 写入实际 Payload
        payload.CopyTo(buffer.AsSpan(8));

        return buffer;
    }

    /// <summary>
    /// 构建包含会话标识、消息标识和负载的数据包：前8字节为会话标识（Little-Endian），接着4字节为消息标识（Little-Endian），随后为负载数据。
    /// </summary>
    /// <remarks>返回的是新分配的缓冲区；整数使用小端字节序写入，负载通过 ReadOnlySpan.CopyTo 复制。</remarks>
    /// <param name="sessionId">会话标识，按小端（Little-Endian）形式写入结果缓冲区的前8字节。</param>
    /// <param name="msgId">消息标识，按小端（Little-Endian）形式写入接下来的4字节。</param>
    /// <param name="payload">只读负载数据，复制到返回缓冲区的末尾。</param>
    /// <returns>包含 sessionId、msgId 和 payload 的字节数组，长度为 12 + payload.Length。</returns>
    public static byte[] BuildSessionWrapperPacket(long sessionId, int msgId, ReadOnlySpan<byte> payload)
    {
        int length = 8 + 4 + payload.Length;
        byte[] buffer = new byte[length];

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, 8), sessionId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), msgId);
        payload.CopyTo(buffer.AsSpan(12));

        return buffer;
    }

    /// <summary>
    /// 构建包含消息 ID、请求 ID 和有效负载的二进制请求包，整数采用小端字节序。
    /// </summary>
    /// <remarks>缓冲区为新分配数组；不执行参数验证；使用 BinaryPrimitives 以小端方式写入整数并复制有效负载。</remarks>
    /// <param name="msgId">消息标识，写入包的前 4 个字节，采用小端（Little-Endian）32 位整数表示。</param>
    /// <param name="requestId">请求标识，写入紧随消息 ID 之后的 8 个字节（偏移量 4），采用小端（Little-Endian）64 位整数表示。</param>
    /// <param name="payload">要附加到包后的只读字节序列，起始偏移量为 12，长度可变并被复制到返回的缓冲区。</param>
    /// <returns>新分配的字节数组，按顺序包含 4 字节消息 ID、8 字节请求 ID 和有效负载，长度等于 12 + payload.Length。</returns>
    public static byte[] BuildDbRequestPacket(int msgId, long requestId, ReadOnlySpan<byte> payload)
    {
        int length = 12 + payload.Length;
        byte[] buffer = new byte[length];

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), msgId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(4, 8), requestId);
        payload.CopyTo(buffer.AsSpan(12));

        return buffer;
    }

    /// <summary>
    /// 解析包含 4 字节消息 ID 与 8 字节请求 ID 的数据库数据包并提取有效负载。
    /// </summary>
    /// <remarks>头部布局：前 4 字节为 msgId（Int32，小端），随后 8 字节为 requestId（Int64，小端），其后为
    /// payload。方法仅解析头部并不验证负载内容。</remarks>
    /// <param name="data">要解析的二进制数据，至少应包含 12 字节头部（4 字节 msgId + 8 字节 requestId）。</param>
    /// <param name="msgId">解析出的消息标识（4 字节，Int32，小端）。</param>
    /// <param name="requestId">解析出的请求标识（8 字节，Int64，小端）。</param>
    /// <param name="payload">头部之后的剩余数据，作为有效负载。</param>
    /// <returns>如果数据长度至少为 12 字节并成功解析头部则返回 true；否则返回 false。</returns>
    public static bool TryParseDbPacket(ReadOnlyMemory<byte> data, out int msgId, out long requestId, out ReadOnlyMemory<byte> payload)
    {
        msgId = 0;
        requestId = 0;
        payload = default;

        if (data.Length < 12)
        {
            return false;
        }

        msgId = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(0, 4));
        requestId = BinaryPrimitives.ReadInt64LittleEndian(data.Span.Slice(4, 8));
        payload = data.Slice(12);
        return true;
    }
}