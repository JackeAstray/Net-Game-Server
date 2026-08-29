# ExcelToJson —— Excel 配置转 JSON 工具（服务端）

把 Excel 配置表转换为 JSON 数组文件，输出格式与 Unity 客户端
`ReunionMovement/Assets/ReunionMovement/Editor/Excel` 工具**逐字节一致**，
服务器端与客户端读取同一份 JSON、同一种格式。

## 用法

```bash
dotnet run --project NetGameServer/Tools/ExcelToJson/ExcelToJson.csproj -c Release -- [输入目录] [输出目录]
# 或编译后
ExcelToJson [输入目录] [输出目录]
```

- 默认输入目录：`Configs/Excel`（相对当前工作目录）
- 默认输出目录：`Configs/Json`
- 递归扫描 `*.xlsx` / `*.xls`，每个文件转换**第一个工作表**为 `<文件名>.json`
- 输出：UTF-8 **带 BOM**（EF BB BF，与 Unity 参考工具 `SaveFileSync` 一致）

## 表格布局（与 Unity 参考工具一致）

```
Row0  备注/中文名称（跳过）
Row1  数据类型（int / float / double / string / bool / [int] / int[] / string[] ...）
Row2  英文名称/字段名（含 "Id"；自适应搜索前 10 行）
Row3+ 数据行
```

字段名行按「含 `Id`」自适应定位，类型行 = 字段名行的上一行（不强制固定在第 1/2 行）。

## 输出格式（与参考工具 GetJson 一一对应）

- `JsonConvert.SerializeObject(List<Dictionary<string,object>>)` —— 紧凑 JSON 数组 `[{...},{...}]`
- 类型严格：`int` 仍为 int、`float`/`double` 保持数值、`string` 为字符串
- 数组字段（`[int]` / `int[]` / `string[]` ...）产出**真正的 JSON 数组**，支持 `;` `；` `,` `，` 分隔
- 空单元格按类型给默认值：数值→0、string→""、bool→false、数组→`[]`
- 解析失败（非数字）→ 数值默认 0 并记录错误日志（与参考一致）
- 数值解析统一 `CultureInfo.InvariantCulture`（不受系统区域设置影响，zh-CN 下与参考结果一致）

## 读取端（保证「生成 ↔ 读取」一致）

服务器端用 `Shared.ConfigDatabase` 读取同一份 JSON：

```csharp
// 全量列表
var list = ConfigDatabase.LoadList<LanguagesConfig>("Configs/Json/LanguagesConfig.json");

// 按主键索引（O(1)）
var dict = ConfigDatabase.LoadIndexed<int, LanguagesConfig>(
    "Configs/Json/LanguagesConfig.json", c => c.Number);
```

一致性保证：
- 工具端与读取端共用 **Newtonsoft.Json**（`Shared.Json` 与 Unity 参考工具同款序列化器）
- `ConfigDatabase` 自动剥离 UTF-8 BOM，读取带 BOM 产物无障碍
- 重复主键后者覆盖并告警（与 Unity 端 `LanguagesSystem` 行为一致）

## 验证

已用参考工程 `Resources/LanguagesConfig.xlsx`、`SoundConfig.xlsx` 验证：
工具产物与 `Resources/AutoDatabase/*.json` **SHA256 逐字节一致**。
