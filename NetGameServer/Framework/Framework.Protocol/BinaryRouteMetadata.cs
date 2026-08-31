using System.Buffers.Binary;
using System.Text;

namespace Framework.Protocol;

/// <summary>
/// 二进制路由元数据（对标旧 Shared.RouteMetadata 的 JSON 实现）。
/// 元数据以尾部附加块形式存在：[body][metadataJson][magic(4)][metadataLength(4)]
/// 相比 JSON 逐跳解析-重序列化，二进制块附加/剥离只需一次 JSON 小对象操作（仅元数据），
/// 且 body 保持原样零拷贝。P1 阶段将替换旧 JSON 元数据。
/// </summary>
public static class BinaryRouteMetadata
{
    // 与旧 Shared.RouteMetadata 二进制分支保持同一魔数，实现新旧互通
    private const uint Magic = 0x4154454D; // "META"
    private const int FooterSize = 8;

    // 元数据字段名（与旧 RouteMetadata 保持一致以便兼容过渡）
    public const string ClientSessionIdField = "__clientSessionId";
    public const string TargetSessionIdField = "__targetSessionId";
    public const string BroadcastField = "__broadcast";
    public const string RequestIdField = "__requestId";
    public const string UserIdField = "__userId";

    /// <summary>用给定的正文与元数据字段重建完整负载（[body][metadataJson][magic(4)][len(4)]）。供宿主在剥离/清理元数据字段后重建包。</summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> body, Dictionary<string, string> fields) => BuildCore(body.Span, fields);

    /// <summary>追加一个 long 字段到负载尾部。</summary>
    public static byte[] AttachLong(ReadOnlySpan<byte> body, string field, long value) =>
        Attach(body, field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>追加一个字符串字段到负载尾部。</summary>
    public static byte[] AttachString(ReadOnlySpan<byte> body, string field, string value) =>
        Attach(body, field, value);

    /// <summary>追加一个布尔字段到负载尾部。</summary>
    public static byte[] AttachBool(ReadOnlySpan<byte> body, string field, bool value) =>
        Attach(body, field, value ? "1" : "0");

    /// <summary>
    /// 批量追加多个字段（性能优化 P-H1）：只解析一次已有元数据、只构建一次尾部块，
    /// 避免逐字段 Attach 造成的对同一 body 的重复拷贝与重复 JSON 序列化（Gateway 每客户端消息节省 3 次拷贝 + 3 次 JSON）。
    /// </summary>
    /// <param name="body">原始正文（可带或不带既有尾部块）。</param>
    /// <param name="fields">要附加/覆盖的字段集合（后出现的同名键覆盖先前的）。</param>
    public static byte[] AttachMany(ReadOnlySpan<byte> body, IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        bool parsed = TryParse(body, out var existingBody, out var existingFields);
        if (parsed)
        {
            foreach (var (k, v) in existingFields) merged[k] = v;
        }
        foreach (var (k, v) in fields) merged[k] = v;
        return parsed ? BuildCore(existingBody.Span, merged) : BuildCore(body, merged);
    }

    /// <summary>提取 long 字段并返回剥离后的 body；字段不存在时返回 false。</summary>
    public static bool TryExtractLong(ReadOnlySpan<byte> payload, string field, out long value, out byte[] body)
    {
        if (TryExtract(payload, field, out var raw, out body))
        {
            return long.TryParse(raw, out value);
        }
        value = 0;
        return false;
    }

    /// <summary>提取字符串字段并返回剥离后的 body；字段不存在时返回 false。</summary>
    public static bool TryExtractString(ReadOnlySpan<byte> payload, string field, out string value, out byte[] body) =>
        TryExtract(payload, field, out value!, out body);

    /// <summary>提取布尔字段并返回剥离后的 body；字段不存在时返回 false。</summary>
    public static bool TryExtractBool(ReadOnlySpan<byte> payload, string field, out bool value, out byte[] body)
    {
        if (TryExtract(payload, field, out var raw, out body))
        {
            value = raw == "1";
            return true;
        }
        value = false;
        return false;
    }

    private static byte[] Attach(ReadOnlySpan<byte> body, string field, string value)
    {
        // 解析已有元数据（若存在），合并字段
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryParse(body, out var existingBody, out var existingFields))
        {
            foreach (var (k, v) in existingFields) fields[k] = v;
            fields[field] = value;
            return BuildCore(existingBody.Span, fields);
        }
        fields[field] = value;
        return BuildCore(body, fields);
    }

    private static bool TryExtract(ReadOnlySpan<byte> payload, string field, out string value, out byte[] body)
    {
        value = string.Empty;
        body = payload.ToArray();
        if (!TryParse(payload, out var cleanBody, out var fields))
        {
            return false;
        }
        if (!fields.TryGetValue(field, out value!))
        {
            value = string.Empty;
            return false;
        }
        fields.Remove(field);
        body = fields.Count == 0 ? cleanBody.ToArray() : BuildCore(cleanBody.Span, fields);
        return true;
    }
    /// <summary>解析尾部元数据。格式：[body][json][magic(4)][len(4)]</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ReadOnlyMemory<byte> body, out Dictionary<string, string> fields)
    {
        body = default;
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.Length < FooterSize) return false;

        int footerStart = payload.Length - FooterSize;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(footerStart, 4));
        if (magic != Magic) return false;

        int metaLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(footerStart + 4, 4));
        if (metaLength <= 0 || metaLength > footerStart) return false;

        int metaStart = footerStart - metaLength;
        string json = Encoding.UTF8.GetString(payload.Slice(metaStart, metaLength));
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    fields[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            return false;
        }

        body = payload.Slice(0, metaStart).ToArray();
        return true;
    }

    private static byte[] BuildCore(ReadOnlySpan<byte> body, Dictionary<string, string> fields)
    {
        // 用 System.Text.Json 序列化小对象（比 Newtonsoft 快）
        byte[] metaBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fields);
        int total = body.Length + metaBytes.Length + FooterSize;
        byte[] result = new byte[total];
        body.CopyTo(result);
        metaBytes.CopyTo(result.AsSpan(body.Length));
        var footer = result.AsSpan(body.Length + metaBytes.Length, FooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(4, 4), metaBytes.Length);
        return result;
    }
}
