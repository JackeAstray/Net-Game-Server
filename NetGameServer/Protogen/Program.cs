using System.Text;
using System.Xml.Linq;

namespace Protogen;

/// <summary>
/// 解析 def 文件并生成 C# 协议代码（消息类 + MessageIds + RouterTable）。
/// 用法：Protogen &lt;defs目录&gt; &lt;输出目录&gt;
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: Protogen <defs目录> <输出目录>");
            return 1;
        }

        string defsDir = Path.GetFullPath(args[0]);
        string outputDir = Path.GetFullPath(args[1]);

        if (!Directory.Exists(defsDir))
        {
            Console.Error.WriteLine($"defs 目录不存在: {defsDir}");
            return 1;
        }

        var protocols = new List<ProtocolModel>();
        foreach (var file in Directory.GetFiles(defsDir, "*.def").OrderBy(f => f))
        {
            Console.WriteLine($"解析 {Path.GetFileName(file)} ...");
            protocols.Add(ProtocolParser.Parse(file));
        }

        Directory.CreateDirectory(outputDir);

        // 1. MessageIds
        var messageIds = CodeGenerator.GenerateMessageIds(protocols);
        File.WriteAllText(Path.Combine(outputDir, "MessageIds.g.cs"), messageIds, new UTF8Encoding(true));

        // 2. 消息类
        var messages = CodeGenerator.GenerateMessages(protocols);
        File.WriteAllText(Path.Combine(outputDir, "Messages.g.cs"), messages, new UTF8Encoding(true));

        // 3. RouterTable（配置化路由）
        var routerTable = CodeGenerator.GenerateRouterTable(protocols);
        File.WriteAllText(Path.Combine(outputDir, "RouterTable.g.cs"), routerTable, new UTF8Encoding(true));

        Console.WriteLine($"生成完成: {outputDir}");
        return 0;
    }
}
