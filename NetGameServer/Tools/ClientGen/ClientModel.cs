namespace ClientGen;

/// <summary>
/// 客户端协议模型：从 .def 协议中筛选「客户端可见」消息与结构体。
/// 客户端只与 Gateway 通信；DB 消息与 internal 消息为服务器内部链路，客户端不需要。
/// 判定规则：
///   - 消息 internal=true            → 排除（服务器内部，如 CenterRegisterNode / 全部 DB 消息）
///   - 消息 target=Db                → 排除（DB 节点不面向客户端）
///   - 其余（Login/Game/Center/Battle 客户端消息）→ 保留
/// 结构体仅保留「被至少一个保留消息引用」的（避免导出仅供内部消息使用的类型）。
/// </summary>
public static class ClientModel
{
    public static List<ProtocolModel> Filter(List<ProtocolModel> protocols)
    {
        // 1. 保留非 internal 且非 Db 目标的消息
        var keptMessages = protocols
            .SelectMany(p => p.Messages)
            .Where(m => !m.Internal && !string.Equals(m.Target, "Db", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 2. 递归收集被保留消息引用的结构体名
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in keptMessages)
        {
            foreach (var f in m.Fields) CollectStructs(f.Type, protocols, referenced);
        }

        // 3. 重建协议模型：只保留客户端消息 + 被引用的结构体
        var result = new List<ProtocolModel>();
        foreach (var proto in protocols)
        {
            var kept = proto.Messages
                .Where(m => !m.Internal && !string.Equals(m.Target, "Db", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count == 0) continue;

            var model = new ProtocolModel { Name = proto.Name, Version = proto.Version };
            foreach (var m in kept) model.Messages.Add(m);
            foreach (var s in proto.Structs)
            {
                if (referenced.Contains(s.Name)) model.Structs.Add(s);
            }
            result.Add(model);
        }
        return result;
    }

    /// <summary>
    /// 从源生成器产出的 ProtocolManifest.Json 解析为协议模型（迁移到 [GameMessage] 的消息，如 Login 链路）。
    /// 与 def 解析产物合并后统一走 Filter，客户端可见性判定规则一致。
    /// </summary>
    public static List<ProtocolModel> ParseManifest(string json)
    {
        var root = Newtonsoft.Json.Linq.JObject.Parse(json);
        var none = new Newtonsoft.Json.Linq.JArray();
        var model = new ProtocolModel { Name = "SourceGen", Version = "1" };

        foreach (var m in (Newtonsoft.Json.Linq.JArray?)root["messages"] ?? none)
        {
            var msg = new MessageModel
            {
                Id = (int)m["id"]!,
                Name = (string)m["name"]!,
                Target = (string)m["target"]!,
                Reply = (string?)m["reply"],
                Internal = (bool?)m["internal"] ?? false
            };
            foreach (var f in (Newtonsoft.Json.Linq.JArray?)m["fields"] ?? none)
                msg.Fields.Add(new FieldModel { Name = (string)f["name"]!, Type = (string)f["type"]!, Optional = (bool?)f["optional"] ?? false });
            model.Messages.Add(msg);
        }

        foreach (var s in (Newtonsoft.Json.Linq.JArray?)root["structs"] ?? none)
        {
            var st = new StructModel { Name = (string)s["name"]! };
            foreach (var f in (Newtonsoft.Json.Linq.JArray?)s["fields"] ?? none)
                st.Fields.Add(new FieldModel { Name = (string)f["name"]!, Type = (string)f["type"]! });
            model.Structs.Add(st);
        }

        return new List<ProtocolModel> { model };
    }

    /// <summary>def 类型表达式是否为结构体引用。</summary>
    public static bool IsStructName(string type, List<ProtocolModel> protocols) =>
        protocols.Any(p => p.Structs.Any(s => s.Name == type));

    /// <summary>按名查找结构体。</summary>
    public static StructModel? FindStruct(List<ProtocolModel> protocols, string name)
    {
        foreach (var p in protocols)
        {
            foreach (var s in p.Structs)
            {
                if (s.Name == name) return s;
            }
        }
        return null;
    }

    /// <summary>枚举类型表达式里的结构体引用（含 list:/map: 内层），并递归收集被引用结构体的字段引用。</summary>
    public static void CollectStructs(string type, List<ProtocolModel> protocols, HashSet<string> referenced)
    {
        foreach (var (inner, isStruct) in EnumerateRefs(type, protocols))
        {
            if (isStruct && referenced.Add(inner))
            {
                var st = FindStruct(protocols, inner);
                if (st != null)
                {
                    foreach (var f in st.Fields) CollectStructs(f.Type, protocols, referenced);
                }
            }
        }
    }

    private static IEnumerable<(string Name, bool IsStruct)> EnumerateRefs(string type, List<ProtocolModel> protocols)
    {
        if (type.StartsWith("list:", StringComparison.Ordinal))
        {
            foreach (var r in EnumerateRefs(type["list:".Length..], protocols)) yield return r;
            yield break;
        }
        if (type.StartsWith("map:", StringComparison.Ordinal))
        {
            foreach (var part in type["map:".Length..].Split(','))
            {
                foreach (var r in EnumerateRefs(part, protocols)) yield return r;
            }
            yield break;
        }
        yield return (type, IsStructName(type, protocols));
    }
}
