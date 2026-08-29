using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    public const string UidField = "__uid";
    public const string NicknameField = "__nickname";

    private const uint BinaryMetadataMagic = 0x4154454D;
    private const int BinaryMetadataFooterSize = 8;

    /// <summary>
    /// 向载荷中附加或更新客户端会话标识字段，并返回更新后的字节数组。
    /// 使用二进制尾部元数据块（零拷贝 body，性能优于 JSON 内嵌）。
    /// </summary>
    public static byte[] AttachClientSessionId(ReadOnlyMemory<byte> payload, long clientSessionId)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachLong(payload.Span, ClientSessionIdField, clientSessionId);
    }

    /// <summary>
    /// 向载荷中附加或更新目标会话标识字段，并返回更新后的字节数组。
    /// 使用二进制尾部元数据块。
    /// </summary>
    public static byte[] AttachTargetSessionId(ReadOnlyMemory<byte> payload, long targetSessionId)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachLong(payload.Span, TargetSessionIdField, targetSessionId);
    }

    /// <summary>
    /// 向载荷中附加或更新广播标记字段，并返回更新后的字节数组。
    /// 使用二进制尾部元数据块。
    /// </summary>
    public static byte[] AttachBroadcast(ReadOnlyMemory<byte> payload, bool broadcast)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachBool(payload.Span, BroadcastField, broadcast);
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
        // 新格式：二进制尾部元数据
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractBool(payload.Span, BroadcastField, out broadcast, out var binaryClean))
        {
            cleanPayload = binaryClean;
            return true;
        }

        // 旧格式：JSON 对象字段
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
        return Framework.Protocol.BinaryRouteMetadata.AttachLong(payload.Span, RequestIdField, requestId);
    }

    public static byte[] AttachUserId(ReadOnlyMemory<byte> payload, int userId)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachLong(payload.Span, UserIdField, userId);
    }

    public static byte[] AttachUid(ReadOnlyMemory<byte> payload, string uid)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachString(payload.Span, UidField, uid);
    }

    public static byte[] AttachNickname(ReadOnlyMemory<byte> payload, string nickname)
    {
        return Framework.Protocol.BinaryRouteMetadata.AttachString(payload.Span, NicknameField, nickname);
    }

    /// <summary>
    /// 批量附加客户端路由元数据（性能优化 P-H1）：一次解析 + 一次构建，替代逐字段 Attach
    /// （每客户端消息从 4 次 body 拷贝 + 4 次 JSON 序列化降为 1 次）。
    /// 可选字段传 null/空值时跳过；客户端会话标识为必填。
    /// </summary>
    public static byte[] AttachClientRouteMetadata(ReadOnlyMemory<byte> payload, long clientSessionId, int? userId, string? uid, string? nickname)
    {
        var fields = new List<KeyValuePair<string, string>>(4);
        fields.Add(new KeyValuePair<string, string>(ClientSessionIdField, clientSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (userId.HasValue && userId.Value > 0)
        {
            fields.Add(new KeyValuePair<string, string>(UserIdField, userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrWhiteSpace(uid))
        {
            fields.Add(new KeyValuePair<string, string>(UidField, uid));
        }
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            fields.Add(new KeyValuePair<string, string>(NicknameField, nickname));
        }
        return Framework.Protocol.BinaryRouteMetadata.AttachMany(payload.Span, fields);
    }

    /// <summary>
    /// 剥离客户端可注入的路由元数据：移除负载中所有以 "__" 开头的字段（JSON 内嵌键 或 二进制尾部元数据键）。
    /// Gateway 在附加自身元数据前调用，防止客户端伪造 __userId/__uid/__nickname 等身份字段冒充他人（P0 安全修复）。
    /// </summary>
    public static byte[] StripClientFields(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty) return payload.ToArray();

        // 二进制尾部元数据路径：剥离客户端伪造的 "__" 键，并清理正文中残留的 JSON "__" 字段（防双重伪造）
        if (Framework.Protocol.BinaryRouteMetadata.TryParse(payload.Span, out var body, out var fields))
        {
            bool footerChanged = false;
            foreach (var k in new List<string>(fields.Keys))
            {
                if (k.StartsWith("__", StringComparison.Ordinal))
                {
                    fields.Remove(k);
                    footerChanged = true;
                }
            }

            byte[] cleanBody = StripJsonClientFields(body, out bool bodyChanged);
            if (footerChanged || bodyChanged)
            {
                // 无剩余元数据字段时直接返回正文，避免遗留空 META 尾部
                return fields.Count == 0 ? cleanBody : Framework.Protocol.BinaryRouteMetadata.Rebuild(cleanBody, fields);
            }
            return payload.ToArray();
        }

        // 纯 JSON 负载：移除所有 "__" 前缀键
        return StripJsonClientFields(payload, out _);
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
        // 安全修复（P0）：身份字段只接受 Gateway 附加的二进制元数据，
        // 拒绝"旧格式 JSON 内嵌"回退，防止客户端伪造 __userId 冒充任意用户。
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractLong(payload.Span, UserIdField, out long value, out cleanPayload))
        {
            userId = (int)value;
            return true;
        }
        userId = 0;
        cleanPayload = payload.ToArray();
        return false;
    }

    public static bool TryExtractUid(ReadOnlyMemory<byte> payload, out string uid, out byte[] cleanPayload)
    {
        // 安全修复（P0）：同 TryExtractUserId，只接受二进制元数据，拒绝 JSON 内嵌伪造。
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractString(payload.Span, UidField, out uid, out cleanPayload))
        {
            return true;
        }
        uid = string.Empty;
        cleanPayload = payload.ToArray();
        return false;
    }

    public static bool TryExtractNickname(ReadOnlyMemory<byte> payload, out string nickname, out byte[] cleanPayload)
    {
        // 安全修复（P0）：只接受二进制元数据，拒绝 JSON 内嵌伪造。
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractString(payload.Span, NicknameField, out nickname, out cleanPayload))
        {
            return true;
        }
        nickname = string.Empty;
        cleanPayload = payload.ToArray();
        return false;
    }

    /// <summary>
    /// 解析负载并尝试将指定字段的数值提取为 long；成功时移除该字段并返回移除后的负载副本。
    /// 优先二进制尾部元数据（新格式，零拷贝 body），失败回退 JSON 对象字段（旧格式兼容）。
    /// </summary>
    private static bool TryExtractLongField(ReadOnlyMemory<byte> payload, string fieldName, out long value, out byte[] cleanPayload)
    {
        // 新格式：二进制尾部元数据
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractLong(payload.Span, fieldName, out value, out var binaryClean))
        {
            cleanPayload = binaryClean;
            return true;
        }

        // 旧格式：JSON 对象字段
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

    private static byte[] UpsertStringField(ReadOnlyMemory<byte> payload, string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return payload.ToArray();
        }

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

    private static bool TryExtractStringField(ReadOnlyMemory<byte> payload, string fieldName, out string value, out byte[] cleanPayload)
    {
        // 新格式：二进制尾部元数据
        if (Framework.Protocol.BinaryRouteMetadata.TryExtractString(payload.Span, fieldName, out value, out var binaryClean))
        {
            cleanPayload = binaryClean;
            return true;
        }

        // 旧格式：JSON 对象字段
        if (TryParseObject(payload, out var obj))
        {
            JToken? token = obj[fieldName];
            if (token != null && token.Type == JTokenType.String)
            {
                value = token.Value<string>() ?? string.Empty;
                obj.Remove(fieldName);
                cleanPayload = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
                return !string.IsNullOrWhiteSpace(value);
            }

            cleanPayload = payload.ToArray();
            value = string.Empty;
            return false;
        }

        cleanPayload = payload.ToArray();
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// 为二进制元数据附加或更新名为 fieldName 的 64 位整数字段，并返回包含该字段的新二进制负载。
    /// </summary>
    /// <remarks>如果原始负载已包含二进制元数据，现有字段将与新字段合并；同名字段由新值覆盖。</remarks>
    /// <param name="payload">原始负载（可能包含已有的二进制元数据）；如果包含元数据，将分离出正文并保留已有字段。</param>
    /// <param name="fieldName">要附加或更新的字段名；使用 Ordinal（区分大小写）比较。</param>
    /// <param name="value">要设置的 64 位整数字段值。</param>
    /// <returns>包含指定字段的新二进制负载，其中正文与字段集合按元数据格式编码。</returns>
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

    /// <summary>
    /// 尝试从二进制元数据中提取指定字段的 long 值。
    /// </summary>
    /// <remarks>内部先使用 TryParseBinaryMetadata 解析元数据，再在成功时移除字段并通过 BuildBinaryMetadataPayload
    /// 或原始正文重建返回的有效载荷副本；返回的 cleanPayload 为副本。</remarks>
    /// <param name="payload">包含带元数据的原始二进制有效载荷。</param>
    /// <param name="fieldName">要提取的元数据字段名称。</param>
    /// <param name="value">当找到并成功解析字段时，设置为提取的 long 值；否则为 0。</param>
    /// <param name="cleanPayload">返回已移除指定字段后的有效载荷副本；在解析失败或字段不存在时返回原始有效载荷的副本。</param>
    /// <returns>如果成功提取并移除指定字段则为 true；否则为 false。</returns>
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

    /// <summary>
    /// 尝试从 payload 尾部解析嵌入的二进制元数据，并提取主体与数值字段。
    /// </summary>
    /// <remarks>元数据格式为：尾部包含 4 字节魔数（小端）、4 字节 metadata 长度（小端），前面为 UTF-8 编码的 JSON 元数据。方法验证魔数与长度、解析 UTF‑8
    /// JSON，并将属性中类型为 Integer 或 Float 的值以 long 提取到 fields 中。任何编码、格式或解析错误均导致返回 false。</remarks>
    /// <param name="payload">包含可能附加元数据的只读字节序列。</param>
    /// <param name="body">输出去除元数据后的主体；解析失败时保持为原始 payload。</param>
    /// <param name="fields">输出只包含数值类型字段名及其 long 值的字典；解析失败时为空字典。</param>
    /// <returns>成功解析并提取元数据则返回 true，否则返回 false。</returns>
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

    /// <summary>
    /// 构建包含原始主体、UTF-8 编码 JSON 元数据和固定大小尾部的二进制有效载荷。
    /// </summary>
    /// <remarks>使用 Newtonsoft.Json.Linq.JObject 生成无格式化 JSON 并以 UTF-8 编码；尾部大小由常量 BinaryMetadataFooterSize
    /// 指定，且在尾部按小端写入魔数与元数据长度。</remarks>
    /// <param name="body">作为有效载荷前段复制的原始二进制主体。</param>
    /// <param name="fields">要序列化为紧凑 JSON（无格式化）的元数据字段集合，键为名称，值为数字。</param>
    /// <returns>包含 [body][metadata UTF-8 JSON][footer] 的字节数组；footer 包含 4 字节魔数（UInt32，小端）和 4 字节元数据长度（Int32，小端）。</returns>
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

    /// <summary>判断负载去掉前导空白后是否为 JSON 对象（以 '{' 开头）。避免对二进制负载做无谓的 JSON 解析。</summary>
    private static bool LooksLikeJsonObject(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        int i = 0;
        while (i < span.Length && (span[i] == (byte)' ' || span[i] == (byte)'\t' || span[i] == (byte)'\r' || span[i] == (byte)'\n'))
        {
            i++;
        }
        return i < span.Length && span[i] == (byte)'{';
    }

    /// <summary>若负载是 JSON 对象，则移除所有以 "__" 开头的键并返回重建字节；否则原样返回副本。</summary>
    private static byte[] StripJsonClientFields(ReadOnlyMemory<byte> payload, out bool changed)
    {
        changed = false;
        if (!LooksLikeJsonObject(payload)) return payload.ToArray();
        if (!TryParseObject(payload, out var obj)) return payload.ToArray();

        var keys = new List<string>();
        foreach (var prop in obj.Properties())
        {
            if (prop.Name.StartsWith("__", StringComparison.Ordinal))
            {
                keys.Add(prop.Name);
            }
        }
        if (keys.Count == 0) return payload.ToArray();
        foreach (var k in keys)
        {
            obj.Remove(k);
        }
        changed = true;
        return Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
    }
}