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

    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(bufferedCount + data.Length);
        data.CopyTo(buffer.AsSpan(bufferedCount));
        bufferedCount += data.Length;
    }

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
