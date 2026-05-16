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
    /// 构建包含 SessionId 路由头的数据包： SessionId(8) + MsgId(4) + Payload(N)
    /// 返回组装好的字节数组
    /// </summary>
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
    /// 构建服务到 DB 的统一协议包：MsgId(4) + RequestId(8) + Payload(N)
    /// </summary>
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
    /// 解析服务与 DB 的统一协议包：MsgId(4) + RequestId(8) + Payload(N)
    /// </summary>
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