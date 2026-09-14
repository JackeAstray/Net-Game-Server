using System.Text;
using Newtonsoft.Json;

namespace ClientGen;

/// <summary>
/// 客户端协议脚本生成器。
/// 用法：ClientGen &lt;defs目录(兼容保留，不再解析)&gt; &lt;输出目录&gt;
/// 协议声明唯一来源是 Framework.Protocol.Generated.ProtocolManifest.Json
/// （由 Framework.Protocol.Generator 源生成器从 [GameMessage]/[GameStruct] 编译期产出），
/// 这里筛选客户端可见消息后输出：
///   protocol.json       —— 协议清单（客户端可见消息 + 结构体）
///   Unity/              —— C# 脚本（MemoryPackCodec / Messages / MessageIds / NetClient / Demo）
///   UE/                 —— C++ 脚本（MemoryPack.h / Messages.h / NetClient / Demo / README）
/// 生成产物与服务器 MemoryPack 二进制格式逐字节兼容，可直接导入 Unity / Unreal。
/// </summary>
public static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: ClientGen <defs目录(兼容保留)> <输出目录>");
            return 1;
        }

        string defsDir = Path.GetFullPath(args[0]);
        string outputDir = Path.GetFullPath(args[1]);

        if (!Directory.Exists(defsDir))
        {
            Console.Error.WriteLine($"defs 目录不存在: {defsDir}");
            return 1;
        }

        // 协议来源：源生成器产出的 ProtocolManifest.Json（.def 解析管线已删除，defs 目录参数仅兼容保留）
        var all = ClientModel.ParseManifest(Framework.Protocol.Generated.ProtocolManifest.Json);
        Console.WriteLine($"协议清单: 消息 {all[0].Messages.Count} 条 / 结构体 {all[0].Structs.Count} 个");

        // 客户端可见协议（排除 internal / Db）
        var client = ClientModel.Filter(all);
        foreach (var p in client)
        {
            Console.WriteLine($"  {p.Name}: 消息 {p.Messages.Count} / 结构体 {p.Structs.Count}");
        }

        Directory.CreateDirectory(Path.Combine(outputDir, "Unity"));
        Directory.CreateDirectory(Path.Combine(outputDir, "UE"));

        // C# / JSON：UTF-8 **无** BOM（Roslyn 与 JSON 工具均默认按 UTF-8 解析）
        void Write(string relative, string content) =>
            File.WriteAllText(Path.Combine(outputDir, relative), content, new UTF8Encoding(false));

        // C++：必须带 UTF-8 BOM。
        // 根因（实测）：产物里的注释是中文（UTF-8 多字节），而 MSVC 在中文 Windows 默认 936 代码页下
        // 会把**无 BOM** 的 UTF-8 源文件当 ANSI 解码 —— 多字节序列错位后可能吞掉后续代码行，
        // 实测报 “error C2039: ReadCount 不是 mp::Reader 的成员”（声明明明在类内）等一片错误，
        // 整个 UE 产物开箱编译不过；同一份文件加 /utf-8 则 0 错通过。
        // BOM 是 MSVC 无需任何命令行开关就正确识别 UTF-8 的唯一途径（clang/gcc 也接受 BOM）。
        void WriteCpp(string relative, string content) =>
            File.WriteAllText(Path.Combine(outputDir, relative), content, new UTF8Encoding(true));

        // protocol.json 清单
        Write("protocol.json", BuildManifest(client));

        // Unity / C#
        Write(Path.Combine("Unity", "MessageIds.cs"), UnityGenerator.GenerateMessageIds(client));
        Write(Path.Combine("Unity", "MemoryPackCodec.cs"), UnityGenerator.GenerateCodec());
        Write(Path.Combine("Unity", "Messages.cs"), UnityGenerator.GenerateMessages(client));
        Write(Path.Combine("Unity", "NetClient.cs"), UnityGenerator.GenerateNetClient());
        Write(Path.Combine("Unity", "Demo.cs"), UnityGenerator.GenerateDemo());

        // UE / C++
        WriteCpp(Path.Combine("UE", "MemoryPack.h"), UeGenerator.GenerateMemoryPackH());
        WriteCpp(Path.Combine("UE", "Messages.h"), UeGenerator.GenerateMessagesH(client));
        WriteCpp(Path.Combine("UE", "NetClient.h"), UeGenerator.GenerateNetClientH());
        WriteCpp(Path.Combine("UE", "NetClient.cpp"), UeGenerator.GenerateNetClientCpp());
        WriteCpp(Path.Combine("UE", "Demo.cpp"), UeGenerator.GenerateDemoCpp());
        Write(Path.Combine("UE", "README.md"), UeGenerator.GenerateReadme());

        Console.WriteLine($"生成完成: {outputDir}");
        return 0;
    }

    private static string BuildManifest(List<ProtocolModel> protocols)
    {
        var msgs = new List<object>();
        var structs = new List<object>();
        foreach (var p in protocols)
        {
            foreach (var m in p.Messages)
            {
                msgs.Add(new
                {
                    id = m.Id,
                    name = m.Name,
                    target = m.Target,
                    fields = m.Fields.Select(f => new { name = f.Name, type = f.Type, optional = f.Optional }).ToArray(),
                });
            }
            foreach (var s in p.Structs)
            {
                structs.Add(new
                {
                    name = s.Name,
                    fields = s.Fields.Select(f => new { name = f.Name, type = f.Type }).ToArray(),
                });
            }
        }
        return JsonConvert.SerializeObject(new { version = 1, messages = msgs, structs = structs }, Formatting.Indented);
    }
}
