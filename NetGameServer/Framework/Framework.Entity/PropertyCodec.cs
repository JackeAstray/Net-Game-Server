using System.Buffers.Binary;
using System.Text;

namespace Framework.Entity;

/// <summary>
/// 属性增量编解码：只序列化脏属性（对标 KBE Witness 增量同步）。
/// 格式：[count(2)][nameLen(1)][nameUtf8][type(1)][value...]... 
/// 全量同步（首次进入视野）使用同一格式，只是传全部属性。
/// </summary>
public static class PropertyCodec
{
    /// <summary>把实体的指定属性集合序列化为增量字节。</summary>
    public static byte[] SerializeChanges(Entity entity, IEnumerable<string> propertyNames)
    {
        using var ms = new MemoryStream(64);
        Span<byte> scratch = stackalloc byte[16];

        // 先写 count 占位（2 字节），写完属性后回填
        ms.WriteByte(0);
        ms.WriteByte(0);

        int count = 0;
        foreach (var name in propertyNames)
        {
            if (!entity.Def.TryGetProperty(name, out var prop))
            {
                continue;
            }
            WriteProperty(ms, scratch, entity, prop);
            count++;
        }

        // 回填 count
        var bytes = ms.ToArray();
        if (count > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), (ushort)count);
        }
        return bytes;
    }

    /// <summary>序列化全部 SyncToClient 属性（玩家首次进入视野时的全量快照）。</summary>
    public static byte[] SerializeAll(Entity entity)
    {
        var names = entity.Def.Properties.Values.Where(p => p.SyncToClient).Select(p => p.Name);
        return SerializeChanges(entity, names);
    }

    /// <summary>把增量字节应用到目标实体。返回被应用的属性名列表。</summary>
    public static string[] DeserializeInto(Entity target, ReadOnlySpan<byte> data) =>
        DeserializeInto(target, data, applyDirty: true);

    /// <summary>
    /// 把增量字节应用到目标实体。
    /// applyDirty=false 用于首次全量快照初始化（不标记脏，避免回环广播）；
    /// applyDirty=true 用于后续增量（标记脏，供 Witness 广播给其他玩家）。
    /// </summary>
    public static string[] DeserializeInto(Entity target, ReadOnlySpan<byte> data, bool applyDirty)
    {
        if (data.Length < 2)
        {
            return Array.Empty<string>();
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int offset = 2;
        var applied = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            if (offset + 1 > data.Length) break;
            int nameLen = data[offset++];
            if (offset + nameLen + 1 > data.Length) break;
            string name = Encoding.UTF8.GetString(data.Slice(offset, nameLen));
            offset += nameLen;
            var type = (EntityPropertyType)data[offset++];

            if (!target.Def.TryGetProperty(name, out var prop) || prop.Type != type)
            {
                // 类型不匹配或属性未定义：跳过该值
                offset = SkipValue(data, offset, type);
                continue;
            }

            switch (type)
            {
                case EntityPropertyType.Int32:
                    if (offset + 4 <= data.Length)
                    {
                        Apply(target, name, BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)), applyDirty);
                        applied.Add(name);
                    }
                    offset += 4;
                    break;
                case EntityPropertyType.Int64:
                    if (offset + 8 <= data.Length)
                    {
                        Apply(target, name, BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8)), applyDirty);
                        applied.Add(name);
                    }
                    offset += 8;
                    break;
                case EntityPropertyType.Float:
                    if (offset + 4 <= data.Length)
                    {
                        Apply(target, name, BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)), applyDirty);
                        applied.Add(name);
                    }
                    offset += 4;
                    break;
                case EntityPropertyType.Double:
                    if (offset + 8 <= data.Length)
                    {
                        Apply(target, name, BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset, 8)), applyDirty);
                        applied.Add(name);
                    }
                    offset += 8;
                    break;
                case EntityPropertyType.Bool:
                    if (offset + 1 <= data.Length)
                    {
                        Apply(target, name, data[offset] != 0, applyDirty);
                        applied.Add(name);
                    }
                    offset += 1;
                    break;
                case EntityPropertyType.String:
                {
                    if (offset + 2 <= data.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                        offset += 2;
                        if (offset + len <= data.Length)
                        {
                            Apply(target, name, Encoding.UTF8.GetString(data.Slice(offset, len)), applyDirty);
                            applied.Add(name);
                        }
                        offset += len;
                    }
                    break;
                }
                case EntityPropertyType.Float3:
                    if (offset + 12 <= data.Length)
                    {
                        var f3 = new Float3(
                            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
                            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4)),
                            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 8, 4)));
                        Apply(target, name, f3, applyDirty);
                        applied.Add(name);
                    }
                    offset += 12;
                    break;
                case EntityPropertyType.Int32List:
                {
                    if (offset + 2 <= data.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                        offset += 2;
                        var list = new List<int>(len);
                        int read = 0;
                        while (read < len && offset + 4 <= data.Length)
                        {
                            list.Add(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));
                            offset += 4;
                            read++;
                        }
                        Apply(target, name, list, applyDirty);
                        applied.Add(name);
                    }
                    break;
                }
                default:
                    return applied.ToArray();
            }
        }

        return applied.ToArray();
    }

    /// <summary>设置属性；applyDirty=false 时直接写入内部存储并跳过脏标记。</summary>
    private static void Apply<T>(Entity target, string name, T value, bool applyDirty)
    {
        if (applyDirty)
        {
            target.Set(name, value);
        }
        else
        {
            target.SetSilent(name, value);
        }
    }

    private static void WriteProperty(MemoryStream ms, Span<byte> scratch, Entity entity, EntityProperty prop)
    {
        // Get<object?> 直接返回 values 中已装箱的引用，无额外分配；随后按类型分派写入
        WriteValueRaw(ms, scratch, prop, entity.Get<object?>(prop.Name));
    }

    /// <summary>从已脱离实体的属性快照写入（备份等场景，对标迭代 8 三-10 修正）。</summary>
    private static void WriteSnapshotProperty(MemoryStream ms, Span<byte> scratch, EntityProperty prop, object? value)
    {
        WriteValueRaw(ms, scratch, prop, value);
    }

    /// <summary>写属性头（UTF8 名缓存 + 类型）+ 值。value 为 null/类型不符时写类型默认值。</summary>
    private static void WriteValueRaw(MemoryStream ms, Span<byte> scratch, EntityProperty prop, object? value)
    {
        // nameLen(1) + name（属性名 UTF8 预缓存，避免每属性每包编码，对标迭代 8 三-8 修正）
        byte[] nameBytes = prop.Utf8Name;
        ms.WriteByte((byte)nameBytes.Length);
        ms.Write(nameBytes);
        ms.WriteByte((byte)prop.Type);

        switch (prop.Type)
        {
            case EntityPropertyType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(scratch.Slice(0, 4), value is int i ? i : 0);
                ms.Write(scratch.Slice(0, 4));
                break;
            case EntityPropertyType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(scratch.Slice(0, 8), value is long l ? l : 0L);
                ms.Write(scratch.Slice(0, 8));
                break;
            case EntityPropertyType.Float:
                BinaryPrimitives.WriteSingleLittleEndian(scratch.Slice(0, 4), value is float f ? f : 0f);
                ms.Write(scratch.Slice(0, 4));
                break;
            case EntityPropertyType.Double:
                BinaryPrimitives.WriteDoubleLittleEndian(scratch.Slice(0, 8), value is double d ? d : 0d);
                ms.Write(scratch.Slice(0, 8));
                break;
            case EntityPropertyType.Bool:
                ms.WriteByte(value is bool b && b ? (byte)1 : (byte)0);
                break;
            case EntityPropertyType.String:
            {
                byte[] s = Encoding.UTF8.GetBytes(value as string ?? string.Empty);
                BinaryPrimitives.WriteUInt16LittleEndian(scratch.Slice(0, 2), (ushort)s.Length);
                ms.Write(scratch.Slice(0, 2));
                ms.Write(s);
                break;
            }
            case EntityPropertyType.Float3:
            {
                if (value is Float3 v)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(scratch.Slice(0, 4), v.X);
                    BinaryPrimitives.WriteSingleLittleEndian(scratch.Slice(4, 4), v.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(scratch.Slice(8, 4), v.Z);
                }
                else
                {
                    scratch.Slice(0, 12).Clear();
                }
                ms.Write(scratch.Slice(0, 12));
                break;
            }
            case EntityPropertyType.Int32List:
            {
                var list = value as List<int> ?? new List<int>();
                BinaryPrimitives.WriteUInt16LittleEndian(scratch.Slice(0, 2), (ushort)Math.Min(list.Count, ushort.MaxValue));
                ms.Write(scratch.Slice(0, 2));
                foreach (var item in list.Take(ushort.MaxValue))
                {
                    BinaryPrimitives.WriteInt32LittleEndian(scratch.Slice(0, 4), item);
                    ms.Write(scratch.Slice(0, 4));
                }
                break;
            }
        }
    }

    /// <summary>
    /// 从已脱离实体的属性快照序列化全部 SyncToClient 属性（备份服务用，对标迭代 8 三-10 修正）：
    /// 主循环线程先把实体属性浅拷贝为快照（O(属性数)），序列化在后台队列线程执行，不再阻塞主循环，
    /// 也不在工作线程读活实体（消除与 tick 线程的数据竞争）。
    /// </summary>
    public static byte[] SerializeAllValues(IReadOnlyDictionary<string, object?> values, EntityDef def)
    {
        using var ms = new MemoryStream(64);
        Span<byte> scratch = stackalloc byte[16];

        // 先写 count 占位（2 字节），写完属性后回填
        ms.WriteByte(0);
        ms.WriteByte(0);

        int count = 0;
        foreach (var prop in def.Properties.Values)
        {
            if (!prop.SyncToClient)
            {
                continue;
            }
            WriteSnapshotProperty(ms, scratch, prop, values.TryGetValue(prop.Name, out var v) ? v : null);
            count++;
        }

        var bytes = ms.ToArray();
        if (count > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), (ushort)count);
        }
        return bytes;
    }

    private static int SkipValue(ReadOnlySpan<byte> data, int offset, EntityPropertyType type)
    {
        switch (type)
        {
            case EntityPropertyType.Int32:
            case EntityPropertyType.Float:
                return offset + 4;
            case EntityPropertyType.Int64:
            case EntityPropertyType.Double:
                return offset + 8;
            case EntityPropertyType.Bool:
                return offset + 1;
            case EntityPropertyType.String:
            case EntityPropertyType.Int32List:
            {
                if (offset + 2 > data.Length) return offset;
                int len = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                return offset + 2 + (type == EntityPropertyType.String ? len : len * 4);
            }
            case EntityPropertyType.Float3:
                return offset + 12;
            default:
                return data.Length;
        }
    }
}
