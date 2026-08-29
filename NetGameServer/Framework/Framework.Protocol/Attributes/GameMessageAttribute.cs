namespace Framework.Protocol;

/// <summary>
/// 协议消息标注：声明即协议（方案 A 的单一事实来源，替代 .def）。
/// 用法：<c>[GameMessage(10001, Target = "Login", Reply = "LoginResult")]</c>
/// 由 Framework.Protocol.Generator 源生成器在编译期读取，生成：
///   - MessageIds 常量（partial 合并）
///   - RouterTable 路由条目（partial 合并）
///   - IGameMessage 管线（MsgId / TargetServer / Serialize / Deserialize）
///   - ProtocolManifest.Json（供 ClientGen 生成客户端代码）
/// 序列化由 [MemoryPackable]（MemoryPack 源生成器）负责，与本属性无关。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class GameMessageAttribute : Attribute
{
    public GameMessageAttribute(int id) { Id = id; }

    /// <summary>全局唯一消息 ID（线协议常量，一旦发布不可改动）。</summary>
    public int Id { get; }

    /// <summary>目标服务器：Login / Game / Center / Battle / Db / All。</summary>
    public string Target { get; set; } = "Game";

    /// <summary>对应响应消息名（可选，仅供文档/校验/客户端清单）。</summary>
    public string? Reply { get; set; }

    /// <summary>内部消息（不直接面向客户端，Gateway 拒绝伪造）。</summary>
    public bool Internal { get; set; }
}

/// <summary>
/// 协议结构体标注（对应 .def 的 &lt;Struct&gt;，供 ProtocolManifest / 客户端代码生成引用）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class GameStructAttribute : Attribute
{
}

/// <summary>
/// 协议字段标注（对应 .def 的 &lt;Field optional="true"&gt;）。
/// 仅影响 ProtocolManifest 的元数据（optional 标记），**不改变线格式**：
/// 不要用可空值类型表达 optional（如 int? 会改变 MemoryPack 线格式），类型仍写非空、用本属性标注即可。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class GameFieldAttribute : Attribute
{
    /// <summary>该字段在协议里是否可选（客户端可能不发送）。</summary>
    public bool Optional { get; set; }
}
