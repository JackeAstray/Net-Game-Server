using Protogen;

namespace ClientGen;

/// <summary>def 字段类型分类（供 Unity C# 与 UE C++ 生成器共用）。</summary>
public enum FieldKind
{
    Bool,
    Int32,
    Int64,
    Float,
    String,
    Bytes,
    List,
    Map,
    Struct,
}

public static class TypeHelpers
{
    public static FieldKind KindOf(string type, List<ProtocolModel> protocols)
    {
        if (type.StartsWith("list:", StringComparison.Ordinal)) return FieldKind.List;
        if (type.StartsWith("map:", StringComparison.Ordinal)) return FieldKind.Map;
        return type switch
        {
            "bool" => FieldKind.Bool,
            "int32" => FieldKind.Int32,
            "int64" => FieldKind.Int64,
            "float" => FieldKind.Float,
            "string" => FieldKind.String,
            "bytes" => FieldKind.Bytes,
            _ => FieldKind.Struct,
        };
    }

    public static string InnerOf(string type) => type["list:".Length..];

    public static (string Key, string Value) MapPartsOf(string type)
    {
        var parts = type["map:".Length..].Split(',');
        return (parts[0], parts[1]);
    }

    /// <summary>类型表达式的「叶子」（去掉 list:/map: 前缀后最内层类型名，用于判断是否结构体）。</summary>
    public static string Leaf(string type)
    {
        while (type.StartsWith("list:", StringComparison.Ordinal))
        {
            type = type["list:".Length..];
        }
        if (type.StartsWith("map:", StringComparison.Ordinal))
        {
            type = type["map:".Length..];
            var comma = type.IndexOf(',');
            return Leaf(type.Substring(0, comma));
        }
        return type;
    }
}
