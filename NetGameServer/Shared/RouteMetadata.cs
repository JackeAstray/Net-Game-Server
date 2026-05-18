using System;
using System.Buffers.Binary;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Shared;

public static class RouteMetadata
{
    public const string ClientSessionIdField = "__clientSessionId";
    public const string TargetSessionIdField = "__targetSessionId";
    public const string BroadcastField = "__broadcast";
    public const string RequestIdField = "__requestId";
    public const string UserIdField = "__userId";

    private const uint BinaryMetadataMagic = 0x4154454D;
    private const int BinaryMetadataFooterSize = 8;

    /// <summary>
    /// 向 JSON 载荷中附加或更新客户端会话标识字段，并返回更新后的 UTF-8 字节数组。
    /// </summary>
    /// <param name="payload">原始 JSON 载荷字节。</param>
    /// <param name="clientSessionId">客户端会话标识。</param>
    /// <returns>包含客户端会话标识字段的新载荷字节数组；若载荷非 JSON 对象则返回原始副本。</returns>
    public static byte[] AttachClientSessionId(ReadOnlyMemory<byte> payload, long clientSessionId)
    {
        return UpsertLongField(payload, ClientSessionIdField, clientSessionId);
    }

    /// <summary>
    /// 向 JSON 载荷中附加或更新目标会话标识字段，并返回更新后的 UTF-8 字节数组。
    /// </summary>
    /// <param name="payload">原始 JSON 载荷字节。</param>
    /// <param name="targetSessionId">目标会话标识。</param>
    /// <returns>包含目标会话标识字段的新载荷字节数组；若载荷非 JSON 对象则返回原始副本。</returns>
    public static byte[] AttachTargetSessionId(ReadOnlyMemory<byte> payload, long targetSessionId)
    {
        return UpsertLongField(payload, TargetSessionIdField, targetSessionId);
    }

    /// <summary>
    /// 向 JSON 载荷中附加或更新广播标记字段，并返回更新后的 UTF-8 字节数组。
    /// </summary>
    /// <param name="payload">原始 JSON 载荷字节。</param>
    /// <param name="broadcast">是否广播。</param>
    /// <returns>包含广播标记字段的新载荷字节数组；若载荷非 JSON 对象则返回原始副本。</returns>
    public static byte[] AttachBroadcast(ReadOnlyMemory<byte> payload, bool broadcast)
    {
        return UpsertBoolField(payload, BroadcastField, broadcast);
    }


    /// <summary>
    /// 尝试从载荷中提取客户端会话标识字段，并返回移除该字段后的载荷。
    /// </summary>
    /// <param name="payload">待解析的 JSON 载荷字节。</param>
    /// <param name="clientSessionId">提取到的客户端会话标识；失败时为 0。</param>
    /// <param name="cleanPayload">移除客户端会话标识字段后的载荷；失败时为原始载荷副本。</param>
    /// <returns>成功提取并移除字段时返回 true；否则返回 false。</returns>
    public static bool TryExtractClientSessionId(ReadOnlyMemory<byte> payload, out long clientSessionId, out byte[] cleanPayload)
    {
        return TryExtractLongField(payload, ClientSessionIdField, out clientSessionId, out cleanPayload);
    }

    /// <summary>
    /// 尝试从载荷中提取目标会话标识字段，并返回移除该字段后的载荷。
    /// </summary>
    /// <param name="payload">待解析的 JSON 载荷字节。</param>
    /// <param name="targetSessionId">提取到的目标会话标识；失败时为 0。</param>
    /// <param name="cleanPayload">移除目标会话标识字段后的载荷；失败时为原始载荷副本。</param>
    /// <returns>成功提取并移除字段时返回 true；否则返回 false。</returns>
    public static bool TryExtractTargetSessionId(ReadOnlyMemory<byte> payload, out long targetSessionId, out byte[] cleanPayload)
    {
        return TryExtractLongField(payload, TargetSessionIdField, out targetSessionId, out cleanPayload);
    }

    /// <summary>
    /// 尝试从给定的 JSON 字节载荷中提取名为 BroadcastField 的布尔值，并在成功时返回去除该字段后的清洁载荷。
    /// </summary>
    /// <remarks>如果载荷无法解析为 JSON 对象或字段不是布尔类型，则不会修改载荷并返回 false。</remarks>
    /// <param name="payload">要解析的只读 JSON 字节载荷。</param>
    /// <param name="broadcast">找到布尔字段时输出其值；否则输出 false。</param>
    /// <param name="cleanPayload">成功提取时输出已移除 BroadcastField 字段并以 UTF-8 编码的字节；提取失败时输出原始载荷的字节副本。</param>
    /// <returns>成功提取到布尔类型的 BroadcastField 并生成去除该字段的载荷时为 true；否则为 false。</returns>
    public static bool TryExtractBroadcast(ReadOnlyMemory<byte> payload, out bool broadcast, out byte[] cleanPayload)
    {
        if (TryParseObject(payload, out var obj))
        {
            JToken? token = obj[BroadcastField];
            if (token != null && token.Type == JTokenType.Boolean)
            {
                broadcast = token.Value<bool>();
                obj.Remove(BroadcastField);
                cleanPayload = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
                return true;
            }

            cleanPayload = payload.ToArray();
            broadcast = false;
            return false;
        }

        broadcast = false;
        cleanPayload = payload.ToArray();
        return false;
    }

    /// <summary>
    /// 向二进制载荷插入或更新请求标识，并返回包含该字段的新字节数组。
    /// </summary>
    /// <remarks>若载荷已含请求字段则会被覆盖；原始输入保持不变。</remarks>
    /// <param name="payload">要在其上插入或更新请求标识的只读二进制载荷。</param>
    /// <param name="requestId">要插入的请求标识，64 位整数。</param>
    /// <returns>包含请求标识字段的新字节数组。</returns>
    public static byte[] AttachRequestId(ReadOnlyMemory<byte> payload, long requestId)
    {
        return UpsertLongField(payload, RequestIdField, requestId);
    }

    public static byte[] AttachUserId(ReadOnlyMemory<byte> payload, int userId)
    {
        return UpsertLongField(payload, UserIdField, userId);
    }

    /// <summary>
    /// 尝试从给定的负载中提取请求标识符。
    /// </summary>
    /// <remarks>使用预定义的 RequestId 字段进行提取，返回的 cleanPayload 已移除该字段以便后续处理。</remarks>
    /// <param name="payload">要检查以查找请求标识符的二进制负载。</param>
    /// <param name="requestId">方法返回时，如果成功则包含提取的请求标识符；否则为 0。</param>
    /// <param name="cleanPayload">方法返回时，包含已移除请求标识符字段的负载（字节数组）；若未找到请求标识符，则为原始负载的副本。</param>
    /// <returns>如果成功提取到请求标识符则返回 true；否则返回 false。</returns>
    public static bool TryExtractRequestId(ReadOnlyMemory<byte> payload, out long requestId, out byte[] cleanPayload)
    {
        return TryExtractLongField(payload, RequestIdField, out requestId, out cleanPayload);
    }

    public static bool TryExtractUserId(ReadOnlyMemory<byte> payload, out int userId, out byte[] cleanPayload)
    {
        bool ok = TryExtractLongField(payload, UserIdField, out long value, out cleanPayload);
        userId = ok ? (int)value : 0;
        return ok;
    }

    /// <summary>
    /// 解析 JSON 有效载荷并尝试将指定字段的数值提取为 long；成功时移除该字段并返回移除后的有效载荷副本。
    /// </summary>
    /// <remarks>使用 TryParseObject 解析 JSON 并通过 Newtonsoft.Json 读取和移除字段；cleanPayload 通过 UTF-8 编码生成。</remarks>
    /// <param name="payload">包含 JSON 对象的只读字节序列（UTF-8 编码）。</param>
    /// <param name="fieldName">要提取并移除的字段名。</param>
    /// <param name="value">当返回 true 时为提取到的 long 值；失败时为 0。</param>
    /// <param name="cleanPayload">当返回 true 时为移除字段后的 JSON 的 UTF-8 字节数组；失败时为原始有效载荷的副本。</param>
    /// <returns>若字段存在且其类型为整数或浮点数且已成功转换为 long，则返回 true；否则返回 false。</returns>
    private static bool TryExtractLongField(ReadOnlyMemory<byte> payload, string fieldName, out long value, out byte[] cleanPayload)
    {
        if (TryParseObject(payload, out var obj))
        {
            JToken? token = obj[fieldName];
            if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
            {
                value = token.Value<long>();
                obj.Remove(fieldName);
                cleanPayload = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
                return true;
            }

            cleanPayload = payload.ToArray();
            value = 0;
            return false;
        }

        return TryExtractLongFieldFromBinaryMetadata(payload, fieldName, out value, out cleanPayload);
    }

    /// <summary>
    /// 在可解析为 JSON 对象的字节载荷中插入或更新名为 fieldName 的 long 值字段，并返回更新后对象的 UTF‑8 编码字节数组。解析失败时返回追加二进制元数据后的载荷。
    /// </summary>
    /// <remarks>使用 TryParseObject 将负载解析为对象；若非 JSON 对象，则在尾部追加可逆的路由元数据块。</remarks>
    /// <param name="payload">表示 JSON 编码的输入负载的只读字节序列。</param>
    /// <param name="fieldName">要插入或更新的字段名。</param>
    /// <param name="value">要设置的 long 类型字段值。</param>
    /// <returns>若负载为 JSON 对象，返回包含更新字段的对象的 UTF‑8 编码字节数组；否则返回包含二进制元数据的字节副本。</returns>
    private static byte[] UpsertLongField(ReadOnlyMemory<byte> payload, string fieldName, long value)
    {
        if (payload.IsEmpty)
        {
            var emptyObj = new JObject
            {
                [fieldName] = value
            };
            return Encoding.UTF8.GetBytes(emptyObj.ToString(Newtonsoft.Json.Formatting.None));
        }

        if (TryParseObject(payload, out var obj))
        {
            obj[fieldName] = value;
            return Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
        }

        return AttachLongFieldToBinaryMetadata(payload, fieldName, value);
    }

    /// <summary>
    /// 在 JSON 对象中插入或更新布尔字段，并返回更新后的 UTF-8 编码字节数组。
    /// </summary>
    /// <remarks>尝试将 payload 解析为 JSON 对象（使用 TryParseObject）；若成功，使用 Newtonsoft.Json 以无格式序列化并以 UTF‑8
    /// 编码返回。</remarks>
    /// <param name="payload">包含 JSON 文档的只读字节内存；若无法解析为 JSON 对象，则保持原始字节。</param>
    /// <param name="fieldName">要插入或更新的字段名。</param>
    /// <param name="value">要设置的布尔值。</param>
    /// <returns>如果 payload 可解析为 JSON 对象，则返回包含更新字段的 JSON 对象的 UTF‑8 字节数组；否则返回原始 payload 的字节副本。</returns>
    private static byte[] UpsertBoolField(ReadOnlyMemory<byte> payload, string fieldName, bool value)
    {
        if (payload.IsEmpty)
        {
            var emptyObj = new JObject
            {
                [fieldName] = value
            };
            return Encoding.UTF8.GetBytes(emptyObj.ToString(Newtonsoft.Json.Formatting.None));
        }

        if (TryParseObject(payload, out var obj))
        {
            obj[fieldName] = value;
            return Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
        }

        return payload.ToArray();
    }

    private static byte[] AttachLongFieldToBinaryMetadata(ReadOnlyMemory<byte> payload, string fieldName, long value)
    {
        var fields = new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
        var body = payload;

        if (TryParseBinaryMetadata(payload, out var existingBody, out var existingFields))
        {
            body = existingBody;
            fields = existingFields;
        }

        fields[fieldName] = value;
        return BuildBinaryMetadataPayload(body, fields);
    }

    private static bool TryExtractLongFieldFromBinaryMetadata(ReadOnlyMemory<byte> payload, string fieldName, out long value, out byte[] cleanPayload)
    {
        if (!TryParseBinaryMetadata(payload, out var body, out var fields))
        {
            value = 0;
            cleanPayload = payload.ToArray();
            return false;
        }

        if (!fields.TryGetValue(fieldName, out value))
        {
            cleanPayload = payload.ToArray();
            return false;
        }

        fields.Remove(fieldName);
        cleanPayload = fields.Count == 0 ? body.ToArray() : BuildBinaryMetadataPayload(body, fields);
        return true;
    }

    private static bool TryParseBinaryMetadata(ReadOnlyMemory<byte> payload, out ReadOnlyMemory<byte> body, out System.Collections.Generic.Dictionary<string, long> fields)
    {
        body = payload;
        fields = new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);

        if (payload.Length < BinaryMetadataFooterSize)
        {
            return false;
        }

        var span = payload.Span;
        int footerStart = span.Length - BinaryMetadataFooterSize;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(footerStart, 4));
        if (magic != BinaryMetadataMagic)
        {
            return false;
        }

        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(footerStart + 4, 4));
        if (metadataLength <= 0 || metadataLength > footerStart)
        {
            return false;
        }

        int metadataStart = footerStart - metadataLength;
        string metadataJson;
        try
        {
            metadataJson = Encoding.UTF8.GetString(span.Slice(metadataStart, metadataLength));
        }
        catch
        {
            return false;
        }

        JObject metadataObj;
        try
        {
            metadataObj = JObject.Parse(metadataJson);
        }
        catch
        {
            return false;
        }

        foreach (var property in metadataObj.Properties())
        {
            if (property.Value.Type == JTokenType.Integer || property.Value.Type == JTokenType.Float)
            {
                fields[property.Name] = property.Value.Value<long>();
            }
        }

        body = payload.Slice(0, metadataStart);
        return true;
    }

    private static byte[] BuildBinaryMetadataPayload(ReadOnlyMemory<byte> body, System.Collections.Generic.Dictionary<string, long> fields)
    {
        string metadataJson = new JObject(fields).ToString(Newtonsoft.Json.Formatting.None);
        byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
        int totalLength = body.Length + metadataBytes.Length + BinaryMetadataFooterSize;
        byte[] result = new byte[totalLength];

        body.CopyTo(result.AsMemory(0, body.Length));
        metadataBytes.CopyTo(result.AsMemory(body.Length, metadataBytes.Length));

        var footerSpan = result.AsSpan(body.Length + metadataBytes.Length, BinaryMetadataFooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footerSpan.Slice(0, 4), BinaryMetadataMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footerSpan.Slice(4, 4), metadataBytes.Length);
        return result;
    }

    /// <summary>
    /// 将 UTF-8 编码的 JSON 有效负载解析为 JObject。
    /// </summary>
    /// <remarks>解析或编码错误将被捕获并导致返回 false，不会抛出异常。</remarks>
    /// <param name="payload">要解析的 UTF-8 编码 JSON 有效负载。</param>
    /// <param name="obj">当返回 true 时包含解析得到的 JObject；返回 false 时为默认的空 JObject。</param>
    /// <returns>如果 payload 表示一个 JSON 对象且成功解析为 JObject，则为 true；否则为 false。</returns>
    private static bool TryParseObject(ReadOnlyMemory<byte> payload, out JObject obj)
    {
        obj = new JObject();
        try
        {
            string json = Encoding.UTF8.GetString(payload.Span);
            var token = JToken.Parse(json);
            if (token is JObject jObject)
            {
                obj = jObject;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}