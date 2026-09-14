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
    /// 直接打包带二进制尾部路由元数据的包（性能优化 P-M2）：一次性把
    /// [TotalLength(4)][MsgId(4)][body][metadataJson][magic(4)][metaLength(4)] 写入池化缓冲，
    /// 取代"先 Attach 元数据拼出中间数组、再 BuildPacket 拷贝一次"的两步两拷贝。
    /// 返回值所有权交由 PacketSender.Send 释放（调用方不手动 Return）。
    /// </summary>
    public static byte[] BuildPacketWithMetadata(int msgId, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> metadataJson, out int totalLength)
    {
        const uint metadataMagic = 0x4154454D; // "META"，与 BinaryRouteMetadata 保持一致
        const int footerSize = 8;

        int innerLength = 4 + payload.Length + metadataJson.Length + footerSize;
        totalLength = 4 + innerLength;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), innerLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), msgId);
        payload.CopyTo(buffer.AsSpan(8));

        int footerStart = 8 + payload.Length;
        metadataJson.CopyTo(buffer.AsSpan(footerStart));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(footerStart + metadataJson.Length, 4), metadataMagic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(footerStart + metadataJson.Length + 4, 4), metadataJson.Length);
        return buffer;
    }
}