using System.Text;
using ExcelToJson;

// ExcelDataReader 在 .NET Core 上解析某些字符串需要 Windows 代码页（如 1252），必须先注册
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Excel → JSON 配置转换工具（服务端）
// 用法：ExcelToJson [输入目录] [输出目录]
//   - 默认输入 Configs/Excel（相对当前工作目录），默认输出 Configs/Json
//   - 递归扫描 *.xlsx / *.xls，每表转换第一个工作表为 <表名>.json
//   - 输出格式与 Unity 客户端 ReunionMovement Editor/Excel 工具完全一致，
//     服务器端用 Shared.ConfigDatabase（Newtonsoft List<T>）读取同一份 JSON，两端天然一致。

if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
{
    Console.WriteLine("用法: ExcelToJson [输入目录] [输出目录]");
    Console.WriteLine("  默认输入目录: Configs/Excel，默认输出目录: Configs/Json");
    Console.WriteLine("  递归扫描 *.xlsx / *.xls，每个文件转换第一个工作表为 <文件名>.json");
    return 0;
}

string inputDir = args.Length > 0 ? args[0] : "Configs/Excel";
string outputDir = args.Length > 1 ? args[1] : "Configs/Json";

if (!Directory.Exists(inputDir))
{
    Console.WriteLine($"[ExcelToJson] 输入目录不存在: {inputDir}");
    return 1;
}
Directory.CreateDirectory(outputDir);

var files = new List<string>();
foreach (var pattern in new[] { "*.xlsx", "*.xls" })
{
    files.AddRange(Directory.GetFiles(inputDir, pattern, SearchOption.AllDirectories));
}

if (files.Count == 0)
{
    Console.WriteLine($"[ExcelToJson] 输入目录 {inputDir} 下没有找到表格文件。");
    return 1;
}

int ok = 0, failed = 0;
foreach (var path in files)
{
    var errors = new List<string>();
    string json = ExcelConverter.ConvertFile(path, errors);
    if (string.IsNullOrEmpty(json))
    {
        Console.WriteLine($"[ExcelToJson] 跳过(空/失败): {path}");
        foreach (var e in errors) Console.WriteLine($"  !! {e}");
        failed++;
        continue;
    }

    string output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + ".json");
    // 与 Unity 参考工具一致：UTF-8 带 BOM（SaveFileSync 产出 EF BB BF 头）
    File.WriteAllText(output, json, new System.Text.UTF8Encoding(true));
    Console.WriteLine($"[ExcelToJson] 生成: {output} ({json.Length} 字节)");
    foreach (var e in errors) Console.WriteLine($"  !! {e}");
    ok++;
}

Console.WriteLine($"[ExcelToJson] 完成: 成功 {ok} 个, 失败 {failed} 个。");
return failed > 0 ? 1 : 0;
