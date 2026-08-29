using Protogen;

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
