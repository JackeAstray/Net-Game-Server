namespace ClientGen;

// ============================================================
// 协议模型类（自 Protogen 迁入，去掉 .def 解析器依赖）。
// 现在协议声明唯一来源是 Framework.Protocol.Generated.ProtocolManifest.Json
// （由 Framework.Protocol.Generator 源生成器从 [GameMessage]/[GameStruct] 产出），
// 模型字段与清单 JSON 一一对应。
// ============================================================

/// <summary>协议解析结果模型（对应 ProtocolManifest 中一个来源分组）。</summary>
public class ProtocolModel
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1";
    public List<MessageModel> Messages { get; } = new();
    public List<StructModel> Structs { get; } = new();
}

/// <summary>协议消息。</summary>
public class MessageModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = "Game";   // Login/Game/Center/Battle/Db/All
    public string? Reply { get; set; }              // 对应响应消息名（可选，元数据）
    public bool Internal { get; set; }              // 内部消息（不直接面向客户端）
    public List<FieldModel> Fields { get; } = new();
}

/// <summary>协议结构体（内部引用类型）。</summary>
public class StructModel
{
    public string Name { get; set; } = string.Empty;
    public List<FieldModel> Fields { get; } = new();
}

/// <summary>协议字段。</summary>
public class FieldModel
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Optional { get; set; }
}
