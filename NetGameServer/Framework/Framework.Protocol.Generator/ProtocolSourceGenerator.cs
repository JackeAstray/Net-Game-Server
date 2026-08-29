using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Framework.Protocol.Generator;

/// <summary>
/// 方案 A 源生成器：读取带 [GameMessage] / [GameStruct] 的 C# 类型，编译期产出：
///   - MessageIds.g.cs            （消息 ID 常量）
///   - RouterTable.g.cs           （路由表 + MessageRouteInfo + FillFromAttributes）
///   - MessagePlumbing.g.cs       （每个消息的 IGameMessage 管线：MsgId/TargetServer/Serialize/Deserialize）
///   - ProtocolManifest.g.cs      （protocol.json 字符串，供 ClientGen 生成客户端代码）
/// 序列化由 MemoryPack 源生成器（[MemoryPackable]）负责；消息字段由开发者手写。
/// 这是协议声明的唯一生成来源（原 .def + Protogen 管线已删除）。
/// </summary>
[Generator]
public sealed class ProtocolSourceGenerator : IIncrementalGenerator
{
    private const string MessageAttr = "Framework.Protocol.GameMessageAttribute";
    private const string StructAttr = "Framework.Protocol.GameStructAttribute";
    private const string GenNs = "Framework.Protocol.Generated";

    private static readonly DiagnosticDescriptor DuplicateId = new(
        "NGSGEN001", "消息 ID 重复",
        "[GameMessage] ID {0}（{1}）与 {2} 冲突：消息 ID 必须全局唯一",
        "Protocol", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor DuplicateName = new(
        "NGSGEN002", "消息名重复",
        "[GameMessage] 消息名 {0}（ID {1}）与 ID {2} 冲突：消息名必须全局唯一",
        "Protocol", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor IdTakenByDef = new(
        "NGSGEN003", "消息 ID 与 .def 冲突",
        "[GameMessage] ID {0}（{1}）已被 .def 生成的常量 {2} 占用：请先删除对应的 .def 消息",
        "Protocol", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor NameTakenByDef = new(
        "NGSGEN004", "消息名与 .def 冲突",
        "[GameMessage] 消息名 {0}（ID {1}）与 .def 生成的常量 {2} 重复：请先删除对应的 .def 消息",
        "Protocol", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var messages = context.SyntaxProvider.ForAttributeWithMetadataName(
                MessageAttr,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => MessageDef.TryCreate(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var structs = context.SyntaxProvider.ForAttributeWithMetadataName(
                StructAttr,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => StructDef.TryCreate(ctx, ct))
            .Where(static s => s is not null)
            .Select(static (s, _) => s!);

        var combined = messages.Collect()
            .Combine(structs.Collect())
            .Combine(context.CompilationProvider);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (msgs, structsArr) = pair.Left;
            Emit(spc, msgs, structsArr, pair.Right);
        });
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<MessageDef> messages,
        ImmutableArray<StructDef> structs,
        Compilation compilation)
    {
        ReportCollisions(spc, messages, compilation);

        spc.AddSource("ProtocolSourceGenerator.MessageIds.g.cs",
            SourceText.From(BuildMessageIds(messages), Encoding.UTF8));
        spc.AddSource("ProtocolSourceGenerator.RouterTable.g.cs",
            SourceText.From(BuildRouterTable(messages), Encoding.UTF8));
        spc.AddSource("ProtocolSourceGenerator.MessagePlumbing.g.cs",
            SourceText.From(BuildPlumbing(messages), Encoding.UTF8));

        (string json, string manifestCs) = BuildManifest(messages, structs);
        spc.AddSource("ProtocolSourceGenerator.ProtocolManifest.g.cs",
            SourceText.From(manifestCs, Encoding.UTF8));
    }

    /// <summary>重复 ID / 名字检查，含与 .def 生成的 MessageIds 常量比对（迁移期防止同 ID 双源定义）。</summary>
    private static void ReportCollisions(SourceProductionContext spc, ImmutableArray<MessageDef> messages, Compilation compilation)
    {
        var seenIds = new Dictionary<int, string>();
        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (seenIds.TryGetValue(m.Id, out var idOwner))
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, m.Location, m.Id, m.Name, idOwner));
            else
                seenIds[m.Id] = m.Name;

            if (seenNames.TryGetValue(m.Name, out var nameOwner))
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateName, m.Location, m.Name, m.Id, nameOwner));
            else
                seenNames[m.Name] = m.Id;
        }

        if (compilation.GetTypeByMetadataName($"{GenNs}.MessageIds") is { } mids)
        {
            foreach (var member in mids.GetMembers())
            {
                if (member is not IFieldSymbol { HasConstantValue: true } f || f.ConstantValue is not int existingId)
                    continue;
                foreach (var m in messages)
                {
                    if (m.Id == existingId)
                        spc.ReportDiagnostic(Diagnostic.Create(IdTakenByDef, m.Location, m.Id, m.Name, f.Name));
                    if (string.Equals(m.Name, f.Name, StringComparison.Ordinal))
                        spc.ReportDiagnostic(Diagnostic.Create(NameTakenByDef, m.Location, m.Name, m.Id, f.Name));
                }
            }
        }
    }

    private static string BuildMessageIds(ImmutableArray<MessageDef> msgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> 由 Framework.Protocol.Generator 源生成器生成，请勿手动修改。");
        sb.AppendLine("namespace Framework.Protocol.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>协议消息 ID 常量（[GameMessage] 声明）。</summary>");
        sb.AppendLine("public static partial class MessageIds");
        sb.AppendLine("{");
        foreach (var m in msgs)
            sb.AppendLine($"    public const int {Sanitize(m.Name)} = {m.Id};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildRouterTable(ImmutableArray<MessageDef> msgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> 由 Framework.Protocol.Generator 源生成器生成，请勿手动修改。");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace Framework.Protocol.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>消息路由信息</summary>");
        sb.AppendLine("public readonly record struct MessageRouteInfo(int MsgId, string Name, string TargetServer, Type? MessageType, bool IsInternal);");
        sb.AppendLine();
        sb.AppendLine("/// <summary>配置化路由表：由 Framework.Protocol.Generator 源生成器从 [GameMessage] 声明产出。</summary>");
        sb.AppendLine("public static partial class RouterTable");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>MsgId -> 路由信息</summary>");
        sb.AppendLine("    public static readonly IReadOnlyDictionary<int, MessageRouteInfo> Routes = BuildRoutes();");
        sb.AppendLine();
        sb.AppendLine("    static IReadOnlyDictionary<int, MessageRouteInfo> BuildRoutes()");
        sb.AppendLine("    {");
        sb.AppendLine("        var map = new Dictionary<int, MessageRouteInfo>();");
        sb.AppendLine("        FillFromAttributes(map);");
        sb.AppendLine("        return map;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void FillFromAttributes(Dictionary<int, MessageRouteInfo> map)");
        sb.AppendLine("    {");
        foreach (var m in msgs)
            sb.AppendLine($"        map[{m.Id}] = new MessageRouteInfo({m.Id}, \"{m.Name}\", \"{m.Target}\", typeof({Sanitize(m.Name)}), {(m.IsInternal ? "true" : "false")});");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>根据 MsgId 查询目标服务器；未定义时返回 null。</summary>");
        sb.AppendLine("    public static string? GetTargetServer(int msgId)");
        sb.AppendLine("    {");
        sb.AppendLine("        return Routes.TryGetValue(msgId, out var info) ? info.TargetServer : null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>根据 MsgId 查询消息类型；未定义时返回 null。</summary>");
        sb.AppendLine("    public static Type? GetMessageType(int msgId)");
        sb.AppendLine("    {");
        sb.AppendLine("        return Routes.TryGetValue(msgId, out var info) ? info.MessageType : null;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildPlumbing(ImmutableArray<MessageDef> msgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> 由 Framework.Protocol.Generator 源生成器生成，请勿手动修改。");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Framework.Protocol;");
        sb.AppendLine("using MemoryPack;");
        sb.AppendLine();
        sb.AppendLine("namespace Framework.Protocol.Generated;");
        sb.AppendLine();
        foreach (var m in msgs)
        {
            string name = Sanitize(m.Name);
            sb.AppendLine($"public partial class {name} : IGameMessage");
            sb.AppendLine("{");
            sb.AppendLine($"    public const int MsgId = {m.Id};");
            sb.AppendLine($"    public const string TargetServer = \"{m.Target}\";");
            sb.AppendLine("    int IGameMessage.MessageId => MsgId;");
            sb.AppendLine("    /// <summary>序列化为 MemoryPack 二进制负载（不含帧头）。</summary>");
            sb.AppendLine($"    public byte[] Serialize() => MemoryPackSerializer.Serialize(this);");
            sb.AppendLine("    /// <summary>从二进制负载反序列化。</summary>");
            sb.AppendLine($"    public static {name}? Deserialize(System.ReadOnlySpan<byte> payload) => MemoryPackSerializer.Deserialize<{name}>(payload);");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static (string Json, string Cs) BuildManifest(ImmutableArray<MessageDef> msgs, ImmutableArray<StructDef> structs)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":1,\"messages\":[");
        bool first = true;
        foreach (var m in msgs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(m.Id)
              .Append(",\"name\":\"").Append(Escape(m.Name)).Append('"')
              .Append(",\"target\":\"").Append(Escape(m.Target)).Append('"')
              .Append(",\"reply\":").Append(m.Reply is null ? "null" : "\"" + Escape(m.Reply) + "\"")
              .Append(",\"internal\":").Append(m.IsInternal ? "true" : "false")
              .Append(",\"fields\":[");
            bool f2 = true;
            foreach (var f in m.Fields)
            {
                if (!f2) sb.Append(',');
                f2 = false;
                sb.Append("{\"name\":\"").Append(Escape(f.Name)).Append('"')
                  .Append(",\"type\":\"").Append(Escape(f.Type)).Append('"')
                  .Append(",\"optional\":").Append(f.Optional ? "true" : "false").Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("],\"structs\":[");
        first = true;
        foreach (var s in structs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(Escape(s.Name)).Append("\",\"fields\":[");
            bool f3 = true;
            foreach (var f in s.Fields)
            {
                if (!f3) sb.Append(',');
                f3 = false;
                sb.Append("{\"name\":\"").Append(Escape(f.Name)).Append('"')
                  .Append(",\"type\":\"").Append(Escape(f.Type)).Append('"')
                  .Append(",\"optional\":").Append(f.Optional ? "true" : "false").Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");

        string json = sb.ToString();

        var cs = new StringBuilder();
        cs.AppendLine("// <auto-generated> 由 Framework.Protocol.Generator 源生成器生成，请勿手动修改。");
        cs.AppendLine("namespace Framework.Protocol.Generated;");
        cs.AppendLine();
        cs.AppendLine("/// <summary>协议清单（protocol.json，[GameMessage] 迁移消息，供 ClientGen 生成客户端代码）。</summary>");
        cs.AppendLine("public static class ProtocolManifest");
        cs.AppendLine("{");
        cs.Append("    public const string Json = \"\"\"").Append(json).AppendLine("\"\"\";");
        cs.AppendLine("}");
        return (json, cs.ToString());
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length + 2);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append('_');
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}

/// <summary>一条消息定义（从 [GameMessage] 类型提取）。</summary>
internal sealed record MessageDef(
    int Id,
    string Name,
    string Target,
    string? Reply,
    bool IsInternal,
    string Namespace,
    IReadOnlyList<FieldDef> Fields,
    Location Location)
{
    public static MessageDef? TryCreate(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        ct.ThrowIfCancellationRequested();

        var attr = ctx.Attributes[0];
        int id = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int i
            ? i
            : 0;
        string target = "Game";
        string? reply = null;
        bool isInternal = false;
        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Target": target = named.Value.Value as string ?? "Game"; break;
                case "Reply": reply = named.Value.Value as string; break;
                case "Internal": isInternal = named.Value.Value is bool b && b; break;
            }
        }

        return new MessageDef(
            id,
            type.Name,
            target,
            reply,
            isInternal,
            type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            TypeMapper.GetFields(type, ctx.SemanticModel.Compilation),
            ctx.TargetNode.GetLocation());
    }
}

/// <summary>一条结构体定义（从 [GameStruct] 类型提取）。</summary>
internal sealed record StructDef(string Name, string Namespace, IReadOnlyList<FieldDef> Fields)
{
    public static StructDef? TryCreate(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
        ct.ThrowIfCancellationRequested();
        return new StructDef(
            type.Name,
            type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            TypeMapper.GetFields(type, ctx.SemanticModel.Compilation));
    }
}

/// <summary>一个字段：C# 属性 -> def 类型名。</summary>
internal sealed record FieldDef(string Name, string Type, bool Optional);

/// <summary>C# 符号类型 -> def 类型表达式（int32 / string / bytes / list:X / map:K,V / 结构体名）。</summary>
internal static class TypeMapper
{
    public static List<FieldDef> GetFields(INamedTypeSymbol type, Compilation compilation)
    {
        var list = new List<FieldDef>();
        foreach (var member in type.GetMembers())
        {
            if (member is IPropertySymbol p
                && p.DeclaredAccessibility == Accessibility.Public
                && !p.IsStatic
                && p.GetMethod is not null
                && p.SetMethod is not null)
            {
                list.Add(new FieldDef(p.Name, ToDefType(p.Type, compilation), IsOptional(p)));
            }
        }
        return list;
    }

    /// <summary>读取 [GameField(Optional = true)]：仅影响 manifest 元数据，不改线格式。</summary>
    private static bool IsOptional(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() != "Framework.Protocol.GameFieldAttribute")
                continue;
            foreach (var n in a.NamedArguments)
            {
                if (n.Key == "Optional" && n.Value.Value is bool b)
                    return b;
            }
            return false;
        }
        return false;
    }

    public static string ToDefType(ITypeSymbol t, Compilation compilation)
    {
        // Nullable<T> 解包
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return ToDefType(nullable.TypeArguments[0], compilation);

        // 枚举 -> 底层类型
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol en)
            return ToDefType(en.EnumUnderlyingType ?? en, compilation);

        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean: return "bool";
            case SpecialType.System_SByte: return "int8";
            case SpecialType.System_Byte: return "uint8";
            case SpecialType.System_Int16: return "int16";
            case SpecialType.System_UInt16: return "uint16";
            case SpecialType.System_Int32: return "int32";
            case SpecialType.System_UInt32: return "uint32";
            case SpecialType.System_Int64: return "int64";
            case SpecialType.System_UInt64: return "uint64";
            case SpecialType.System_Single: return "float";
            case SpecialType.System_Double: return "double";
            case SpecialType.System_String: return "string";
        }

        if (t is IArrayTypeSymbol arr)
            return arr.ElementType.SpecialType == SpecialType.System_Byte
                ? "bytes"
                : $"list:{ToDefType(arr.ElementType, compilation)}";

        if (t is INamedTypeSymbol named && named.IsGenericType)
        {
            string? kind = MatchCollection(named, compilation);
            if (kind == "list")
                return $"list:{ToDefType(named.TypeArguments[0], compilation)}";
            if (kind == "map")
                return $"map:{ToDefType(named.TypeArguments[0], compilation)},{ToDefType(named.TypeArguments[1], compilation)}";
            return named.Name;
        }

        return t.Name; // 自定义类型 / 结构体引用直接用类型名
    }

    /// <summary>
    /// 判断泛型类型是否为 List/Dictionary 家族（含 IList/IDictionary/IReadOnly* 接口）。
    /// SpecialType 枚举只覆盖了 List/IList，没有 Dictionary，因此用 compilation 解析已知定义 +
    /// SymbolEqualityComparer 比较（不依赖 ToDisplayString 的格式）。返回 "list"/"map"/null。
    /// </summary>
    private static string? MatchCollection(INamedTypeSymbol named, Compilation compilation)
    {
        var eq = SymbolEqualityComparer.Default;
        var orig = named.OriginalDefinition;

        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        var ilist = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");
        var ireadonlyList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        if ((list is not null && eq.Equals(orig, list))
            || (ilist is not null && eq.Equals(orig, ilist))
            || (ireadonlyList is not null && eq.Equals(orig, ireadonlyList)))
            return "list";

        var dict = compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2");
        var idict = compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2");
        var ireadonlyDict = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2");
        if ((dict is not null && eq.Equals(orig, dict))
            || (idict is not null && eq.Equals(orig, idict))
            || (ireadonlyDict is not null && eq.Equals(orig, ireadonlyDict)))
            return "map";

        return null;
    }
}
