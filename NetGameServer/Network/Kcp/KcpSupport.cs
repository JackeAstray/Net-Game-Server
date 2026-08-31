using System.Buffers;

namespace Network.Kcp;

/// <summary>KCP 输出回调：把 KCP 生成的数据报通过 UDP 发送给远端。</summary>
internal sealed class KcpOutputCallback : System.Net.Sockets.Kcp.IKcpCallback
{
    private readonly Action<ReadOnlyMemory<byte>> sendAction;

    public KcpOutputCallback(Action<ReadOnlyMemory<byte>> sendAction)
    {
        this.sendAction = sendAction;
    }

    public void Output(IMemoryOwner<byte> buffer, int avalidLength)
    {
        try
        {
            sendAction(buffer.Memory.Slice(0, avalidLength));
        }
        finally
        {
            buffer.Dispose();
        }
    }
}

/// <summary>内存租借：基于 ArrayPool 的 IMemoryOwner 实现（减少 GC）。</summary>
internal sealed class PooledMemoryOwner : IMemoryOwner<byte>
{
    private byte[]? array;
    private readonly int length;

    public PooledMemoryOwner(int length)
    {
        this.length = length;
        array = ArrayPool<byte>.Shared.Rent(length);
    }

    public Memory<byte> Memory => array.AsMemory(0, length);

    public void Dispose()
    {
        var a = array;
        array = null;
        if (a != null)
        {
            ArrayPool<byte>.Shared.Return(a);
        }
    }
}

/// <summary>KCP 内存租借工厂。</summary>
internal sealed class PooledRentable : System.Net.Sockets.Kcp.IRentable
{
    public static readonly PooledRentable Instance = new();

    public IMemoryOwner<byte> RentBuffer(int length) => new PooledMemoryOwner(length);
}
