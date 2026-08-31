using Framework.Protocol.Generated;
using MemoryPack;

namespace Framework.Protocol;

/// <summary>
/// 协议编解码器：MemoryPack 二进制序列化 + 长度前缀帧。
/// 帧格式：[TotalLength(4)][MsgId(4)][Payload]（TotalLength 为 MsgId+Payload 的长度）
/// </summary>
public static class ProtocolCodec
{
    /// <summary>将消息序列化为完整数据包（含长度前缀与 MsgId 头）。</summary>
    public static byte[] Encode(IGameMessage message)
    {
        int msgId = message.MessageId;
        byte[] payload = message.Serialize();
        return Encode(msgId, payload);
    }

    /// <summary>将消息序列化为完整数据包。</summary>
    public static byte[] Encode<T>(int msgId, T message) where T : class, IGameMessage
    {
        byte[] payload = message.Serialize();
        return Encode(msgId, payload);
    }

    /// <summary>组装 [Length(4)][MsgId(4)][Payload] 帧。</summary>
    public static byte[] Encode(int msgId, ReadOnlySpan<byte> payload)
    {
        int innerLength = 4 + payload.Length;
        byte[] buffer = new byte[4 + innerLength];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), innerLength);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), msgId);
        payload.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>
    /// 解析完整帧 [Length(4)][MsgId(4)][Body]（frame 参数应是不含 Length 的 [MsgId(4)][Body]）。
    /// 返回 msgId 与去掉 MsgId 头后的纯 body，可直接反序列化。
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out int msgId, out ReadOnlyMemory<byte> payload)
    {
        msgId = 0;
        payload = default;
        // P3 修复：原实现 frame.Length<8 会把"仅 MsgId、空 Body"的合法帧（长度恰好 4）误判为不完整。
        // 只需保证 MsgId 4 字节存在；Body 允许为空。
        if (frame.Length < 4) return false;
        msgId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(0, 4));
        payload = frame.Slice(4).ToArray();
        return true;
    }

    /// <summary>反序列化消息体（泛型调用，编译期绑定 formatter）。</summary>
    public static T? Decode<T>(ReadOnlySpan<byte> payload) where T : class, IGameMessage =>
        MemoryPackSerializer.Deserialize<T>(payload);
}
