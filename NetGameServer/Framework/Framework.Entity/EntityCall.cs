using System.IO;
using Framework.Core;

namespace Framework.Entity;

/// <summary>
/// 实体远程方法处理器签名（对标 KBE 脚本实体方法）：
/// 接收解码后的参数对象，返回可序列化的结果对象（或 null）。
/// </summary>
public delegate object? EntityMethodHandler(object?[] args);

/// <summary>
/// 跨进程实体调用（对标 KBE EntityCallAbstract/Mailbox）：
/// - Entity 上注册方法（RegisterMethod），本进程内可直接调用
/// - 通过 EntityCall 引用远端实体：Call() 序列化参数 → 打包为 EntityRemoteCall 消息
///   → 经 Center/节点路由投递到目标节点 → 目标实体分发执行 → EntityRemoteCallResult 回传
/// 消息协议见 Protocol/defs/Center.def 的 EntityRemoteCall(91001)/EntityRemoteCallResult(91002)。
/// </summary>
public sealed class EntityCall
{
    /// <summary>无操作回调（异步调用未提供回调时的默认占位）。</summary>
    private static readonly Action<bool, object?> NoopCallback = static (_, _) => { };

    /// <summary>目标节点 ID（如 "Battle-127.0.0.1:31307"；null 表示本节点）</summary>
    public string? TargetNodeId { get; }

    /// <summary>目标实体 ID</summary>
    public long EntityId { get; }

    /// <summary>发送委托：把 EntityRemoteCall 消息发送到目标节点（由宿主注入）</summary>
    private readonly Action<Framework.Protocol.Generated.EntityRemoteCall>? sendAction;

    public EntityCall(long entityId, string? targetNodeId = null, Action<Framework.Protocol.Generated.EntityRemoteCall>? sendAction = null)
    {
        EntityId = entityId;
        TargetNodeId = targetNodeId;
        this.sendAction = sendAction;
    }

    /// <summary>本节点引用（直接执行，不走网络）。</summary>
    public static EntityCall Local(long entityId, EntityManager manager) =>
        new(entityId, null, call => manager.DispatchRemoteCall(call));

    /// <summary>跨节点引用（消息经节点路由投递）。</summary>
    public static EntityCall Remote(string targetNodeId, long entityId, Action<Framework.Protocol.Generated.EntityRemoteCall> sendAction) =>
        new(entityId, targetNodeId, sendAction);

    /// <summary>
    /// 调用远端实体方法（fire-and-forget，无回执/超时）。参数经 PropertyCodec 的通用值序列化打包。
    /// 需要回执/超时的调用请使用 <see cref="CallAsync"/>。
    /// </summary>
    public void Call(string methodName, params object?[] args)
    {
        SendCall(methodName, args, callId: 0);
    }

    /// <summary>
    /// 异步调用远端实体方法并等待回执（对标 KBE EntityCall 带回调调用）：
    /// - 分配唯一 CallId 并注册到 EntityCallHub（含超时截止）
    /// - 远端处理后将 EntityRemoteCallResult（携带同一 CallId）回传
    /// - 回执到达 → onComplete(Success=true, Result)；超时未回执 → onComplete(Success=false, null)
    /// 宿主需周期调用 EntityCallHub.SweepExpired 驱动超时判定。
    /// </summary>
    /// <returns>本次调用的 CallId（0 表示发送失败）。</returns>
    public long CallAsync(string methodName, object?[] args, Action<bool, object?>? onComplete, int timeoutMs = 5000)
    {
        if (sendAction == null)
        {
            Log.Warn($"EntityCall 未配置发送委托，异步调用被忽略 EntityId:{EntityId} Method:{methodName}");
            return 0;
        }

        long callId = EntityCallHubRegistry.Default.NextCallId();
        EntityCallHubRegistry.Default.Register(callId, new EntityCallHub.PendingCall
        {
            CallId = callId,
            TargetNodeId = TargetNodeId,
            MethodName = methodName,
            DeadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs)),
            Callback = onComplete ?? NoopCallback
        });

        SendCall(methodName, args, callId);
        return callId;
    }

    private void SendCall(string methodName, object?[] args, long callId)
    {
        if (sendAction == null)
        {
            Log.Warn($"EntityCall 未配置发送委托，调用被忽略 EntityId:{EntityId} Method:{methodName}");
            return;
        }

        byte[] argBytes = ArgCodec.Serialize(args);
        sendAction(new Framework.Protocol.Generated.EntityRemoteCall
        {
            TargetNodeId = TargetNodeId ?? string.Empty,
            EntityId = EntityId,
            MethodName = methodName,
            Args = argBytes,
            CallId = callId
        });
    }
}

/// <summary>
/// 实体方法参数编解码：支持常用标量/字符串/Float3，序列化为紧凑二进制。
/// 格式：[count(2)][type(1)][value...]...
/// </summary>
public static class ArgCodec
{
    /// <summary>参数数组上限（防 DoS：攻击者发 count=65535 触发大数组分配）。</summary>
    public const int MaxArgCount = 32;

    /// <summary>字符串/列表单字段长度上限（防 DoS：超大字符串触发 OOM）。</summary>
    public const int MaxStringLength = 64 * 1024;

    private enum ArgType : byte
    {
        Int32 = 1,
        Int64 = 2,
        Float = 3,
        Double = 4,
        Bool = 5,
        String = 6,
        Float3 = 7,
        Null = 8,
    }

    public static byte[] Serialize(object?[] args)
    {
        if (args.Length > MaxArgCount)
        {
            throw new ArgumentException(
                $"EntityCall 参数数量 {args.Length} 超过上限 {MaxArgCount}", nameof(args));
        }
        using var ms = new MemoryStream(32);
        WriteUInt16(ms, (ushort)args.Length);

        foreach (var arg in args)
        {
            WriteValue(ms, arg);
        }
        return ms.ToArray();
    }

    public static object?[] Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return Array.Empty<object?>();
        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (count > MaxArgCount)
        {
            // 安全修复：拒绝声明超大数量的参数包
            throw new InvalidDataException(
                $"ArgCodec 参数数量 {count} 超过上限 {MaxArgCount}，疑似 DoS 攻击");
        }
        int offset = 2;
        var result = new object?[count];

        for (int i = 0; i < count; i++)
        {
            if (offset >= data.Length) break;
            var type = (ArgType)data[offset++];
            result[i] = ReadValue(data, ref offset, type);
        }
        return result;
    }

    private static void WriteValue(MemoryStream ms, object? value)
    {
        switch (value)
        {
            case null:
                ms.WriteByte((byte)ArgType.Null);
                break;
            case int i:
                ms.WriteByte((byte)ArgType.Int32);
                WriteInt32(ms, i);
                break;
            case long l:
                ms.WriteByte((byte)ArgType.Int64);
                WriteInt64(ms, l);
                break;
            case float f:
                ms.WriteByte((byte)ArgType.Float);
                WriteSingle(ms, f);
                break;
            case double d:
                ms.WriteByte((byte)ArgType.Double);
                WriteDouble(ms, d);
                break;
            case bool b:
                ms.WriteByte((byte)ArgType.Bool);
                ms.WriteByte(b ? (byte)1 : (byte)0);
                break;
            case string s:
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                if (bytes.Length > MaxStringLength)
                {
                    throw new ArgumentException(
                        $"EntityCall 字符串参数长度 {bytes.Length} 超过上限 {MaxStringLength}", nameof(value));
                }
                ms.WriteByte((byte)ArgType.String);
                WriteUInt16(ms, (ushort)bytes.Length);
                ms.Write(bytes);
                break;
            }
            case Float3 v:
                ms.WriteByte((byte)ArgType.Float3);
                WriteSingle(ms, v.X);
                WriteSingle(ms, v.Y);
                WriteSingle(ms, v.Z);
                break;
            default:
                throw new NotSupportedException($"EntityCall 参数类型不支持: {value.GetType()}");
        }
    }

    private static void WriteInt32(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteInt64(MemoryStream ms, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteSingle(MemoryStream ms, float value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteDouble(MemoryStream ms, double value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteUInt16(MemoryStream ms, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        ms.Write(buf);
    }

    private static object? ReadValue(ReadOnlySpan<byte> data, ref int offset, ArgType type)
    {
        switch (type)
        {
            case ArgType.Null:
                return null;
            case ArgType.Int32:
                if (offset + 4 > data.Length) return null;
                var i = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;
                return i;
            case ArgType.Int64:
                if (offset + 8 > data.Length) return null;
                var l = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
                offset += 8;
                return l;
            case ArgType.Float:
                if (offset + 4 > data.Length) return null;
                var f = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
                offset += 4;
                return f;
            case ArgType.Double:
                if (offset + 8 > data.Length) return null;
                var d = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset, 8));
                offset += 8;
                return d;
            case ArgType.Bool:
                if (offset >= data.Length) return null;
                var b = data[offset++] != 0;
                return b;
            case ArgType.String:
                if (offset + 2 > data.Length) return null;
                int len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                offset += 2;
                if (len > MaxStringLength)
                {
                    // 安全修复：拒绝声明超大长度的字符串
                    throw new InvalidDataException(
                        $"ArgCodec 字符串长度 {len} 超过上限 {MaxStringLength}，疑似 DoS 攻击");
                }
                if (offset + len > data.Length) return null;
                var s = System.Text.Encoding.UTF8.GetString(data.Slice(offset, len));
                offset += len;
                return s;
            case ArgType.Float3:
                if (offset + 12 > data.Length) return null;
                var v = new Float3(
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4)),
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 8, 4)));
                offset += 12;
                return v;
            default:
                return null;
        }
    }
}
