using System.Xml.Linq;

namespace Protogen;

/// <summary>协议解析结果模型</summary>
public class ProtocolModel
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1";
    public List<MessageModel> Messages { get; } = new();
    public List<StructModel> Structs { get; } = new();
}

public class MessageModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = "Game";   // Login/Game/Center/Battle/Db/All
    public string? Reply { get; set; }              // 对应响应消息名（可选）
    public bool Internal { get; set; }              // 内部消息（不直接面向客户端）
    public List<FieldModel> Fields { get; } = new();
}

public class StructModel
{
    public string Name { get; set; } = string.Empty;
    public List<FieldModel> Fields { get; } = new();
}

public class FieldModel
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Optional { get; set; }
}

/// <summary>解析 .def XML 文件</summary>
public static class ProtocolParser
{
    public static ProtocolModel Parse(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root!;
        if (root.Name.LocalName != "Protocol")
        {
            throw new InvalidDataException($"文件 {filePath} 根元素不是 Protocol");
        }

        var model = new ProtocolModel
        {
            Name = (string?)root.Attribute("name") ?? Path.GetFileNameWithoutExtension(filePath),
            Version = (string?)root.Attribute("version") ?? "1"
        };

        foreach (var msgEl in root.Elements("Message"))
        {
            var msg = new MessageModel
            {
                Id = int.Parse((string?)msgEl.Attribute("id") ?? throw new InvalidDataException("Message 缺少 id")),
                Name = (string?)msgEl.Attribute("name") ?? throw new InvalidDataException("Message 缺少 name"),
                Target = (string?)msgEl.Attribute("target") ?? "Game",
                Reply = (string?)msgEl.Attribute("reply"),
                Internal = bool.Parse((string?)msgEl.Attribute("internal") ?? "false")
            };

            foreach (var fieldEl in msgEl.Elements("Field"))
            {
                msg.Fields.Add(new FieldModel
                {
                    Name = (string?)fieldEl.Attribute("name") ?? throw new InvalidDataException($"Message {msg.Name} 字段缺少 name"),
                    Type = (string?)fieldEl.Attribute("type") ?? "string",
                    Optional = bool.Parse((string?)fieldEl.Attribute("optional") ?? "false")
                });
            }

            model.Messages.Add(msg);
        }

        foreach (var structEl in root.Elements("Struct"))
        {
            var s = new StructModel
            {
                Name = (string?)structEl.Attribute("name") ?? throw new InvalidDataException("Struct 缺少 name")
            };
            foreach (var fieldEl in structEl.Elements("Field"))
            {
                s.Fields.Add(new FieldModel
                {
                    Name = (string?)fieldEl.Attribute("name") ?? throw new InvalidDataException($"Struct {s.Name} 字段缺少 name"),
                    Type = (string?)fieldEl.Attribute("type") ?? "string"
                });
            }
            model.Structs.Add(s);
        }

        return model;
    }
}
