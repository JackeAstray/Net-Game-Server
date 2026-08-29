using System.Text;
using Newtonsoft.Json;
using Protogen;

namespace ClientGen;

/// <summary>
/// 客户端协议脚本生成器。
/// 用法：ClientGen &lt;defs目录&gt; &lt;输出目录&gt;
/// 从 Protocol/defs/*.def 生成客户端可见消息，输出：
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
            Console.Error.WriteLine("用法: ClientGen <defs目录> <输出目录>");
            return 1;
        }

        string defsDir = Path.GetFullPath(args[0]);
        string outputDir = Path.GetFullPath(args[1]);

        if (!Directory.Exists(defsDir))
        {
            Console.Error.WriteLine($"defs 目录不存在: {defsDir}");
            return 1;
        }

        var all = new List<ProtocolModel>();
        foreach (var file in Directory.GetFiles(defsDir, "*.def").OrderBy(f => f))
        {
            Console.WriteLine($"解析 {Path.GetFileName(file)} ...");
            all.Add(ProtocolParser.Parse(file));
        }

        // 客户端可见协议（排除 internal / Db）
        var client = ClientModel.Filter(all);
        foreach (var p in client)
        {
            Console.WriteLine($"  {p.Name}: 消息 {p.Messages.Count} / 结构体 {p.Structs.Count}");
        }

        Directory.CreateDirectory(Path.Combine(outputDir, "Unity"));
        Directory.CreateDirectory(Path.Combine(outputDir, "UE"));

        void Write(string relative, string content) =>
            File.WriteAllText(Path.Combine(outputDir, relative), content, new UTF8Encoding(false));

        // protocol.json 清单
        Write("protocol.json", BuildManifest(client));

        // Unity / C#
        Write(Path.Combine("Unity", "MessageIds.cs"), UnityGenerator.GenerateMessageIds(client));
        Write(Path.Combine("Unity", "MemoryPackCodec.cs"), UnityGenerator.GenerateCodec());
        Write(Path.Combine("Unity", "Messages.cs"), UnityGenerator.GenerateMessages(client));
        Write(Path.Combine("Unity", "NetClient.cs"), UnityGenerator.GenerateNetClient());
        Write(Path.Combine("Unity", "Demo.cs"), UnityGenerator.GenerateDemo());

        // UE / C++
        Write(Path.Combine("UE", "MemoryPack.h"), UeGenerator.GenerateMemoryPackH());
        Write(Path.Combine("UE", "Messages.h"), UeGenerator.GenerateMessagesH(client));
        Write(Path.Combine("UE", "NetClient.h"), UeGenerator.GenerateNetClientH());
        Write(Path.Combine("UE", "NetClient.cpp"), UeGenerator.GenerateNetClientCpp());
        Write(Path.Combine("UE", "Demo.cpp"), UeGenerator.GenerateDemoCpp());
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
