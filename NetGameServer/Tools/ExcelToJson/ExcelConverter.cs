using System.Data;
using System.Globalization;
using ExcelDataReader;
using Newtonsoft.Json;

namespace ExcelToJson;

/// <summary>
/// Excel → JSON 转换器（对齐 ReunionMovement Unity 客户端 Editor/Excel 工具的输出契约）。
/// 源参考：ReunionMovement/Assets/ReunionMovement/Editor/Excel/Scripts/ExcelUtility.cs (GetJson)
///
/// 表格布局（与参考一致）：
///   Row0  备注/中文名称（跳过）
///   Row1  数据类型（int / float / double / string / bool / [int] / int[] / string[] 等）
///   Row2  英文名称/字段名（含 "Id"，自适应搜索前 10 行）
///   Row3+ 数据行
/// 输出：JsonConvert.SerializeObject(List&lt;Dictionary&lt;string,object&gt;&gt;) —— 紧凑 JSON 数组，
///       类型严格（int 仍为 int、数组字段产出真正的 JSON 数组、空单元格按类型给默认值）。
/// 注意：与参考完全一致，仅转换第一个工作表（GetJson 使用 Tables[0]）。
/// </summary>
public static class ExcelConverter
{
    /// <summary>把单个 Excel 文件转换为与参考工具一致的 JSON 数组文本。</summary>
    /// <param name="path">.xlsx 或 .xls 文件路径。</param>
    /// <param name="errors">输出转换过程中记录的错误信息（参考工具用 Log.Error 记录，控制台工具收集后打印）。</param>
    /// <returns>JSON 数组文本；失败或空表返回空字符串（与参考 GetJson 一致）。</returns>
    public static string ConvertFile(string path, List<string>? errors = null)
    {
        DataSet? resultSet = ReadDataSet(path, errors);
        if (resultSet == null || resultSet.Tables == null || resultSet.Tables.Count < 1)
        {
            return "";
        }

        return GetJson(resultSet.Tables[0], errors);
    }

    /// <summary>读取 Excel 文件为 DataSet（.xls 二进制读取器 / .xlsx OpenXml 读取器，与参考一致）。</summary>
    private static DataSet? ReadDataSet(string path, List<string>? errors)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            using var stream = File.OpenRead(path);
            using IExcelDataReader reader = extension == ".xls"
                ? ExcelReaderFactory.CreateBinaryReader(stream)
                : ExcelReaderFactory.CreateOpenXmlReader(stream);
            return reader.AsDataSet();
        }
        catch (Exception ex)
        {
            errors?.Add($"无法打开 \"{path}\"。也许您应该先关闭 Excel 应用程序（文件被占用或格式不支持）！错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>核心转换（与参考 ExcelUtility.GetJson 一一对应）。</summary>
    private static string GetJson(DataTable mSheet, List<string>? errors)
    {
        if (mSheet.Rows.Count < 1)
        {
            return "";
        }

        var table = new List<Dictionary<string, object>>();

        // 字段名行 / 字段类型行
        List<object> fieldNameRowDatas = new();
        List<object> fieldTypeRowDatas = new();
        int skipRowCount = -1;
        int skipColCount = -1;
        int skipLine = 1;

        // 自适应寻找含 "Id" 的字段名行（前 10 行内）；类型行 = 字段名行的上一行
        for (int i = skipLine; i < 10 && skipColCount == -1; i++)
        {
            var rows = GetRowDatas(mSheet, i);
            for (int j = 0; j < rows.Count; j++)
            {
                if (rows[j] != null && rows[j].Equals("Id"))
                {
                    skipRowCount = i;
                    skipColCount = j;
                    fieldNameRowDatas = rows;
                    fieldTypeRowDatas = GetRowDatas(mSheet, i - 1);
                    break;
                }
            }
        }

        if (skipRowCount == -1)
        {
            errors?.Add("表格数据可能有错，没发现Id字段,请检查");
            return "{}";
        }

        // 读取数据
        for (int i = skipRowCount + 1; i < mSheet.Rows.Count; i++)
        {
            var row = new Dictionary<string, object>();
            for (int j = skipColCount; j < mSheet.Columns.Count; j++)
            {
                // 防止字段名行/类型行比数据行列数少导致越界
                if (j >= fieldNameRowDatas.Count || j >= fieldTypeRowDatas.Count)
                {
                    errors?.Add($"表格数据列索引越界：[{i},{j}]，字段名行有{fieldNameRowDatas.Count}列，类型行有{fieldTypeRowDatas.Count}列");
                    continue;
                }

                string field = fieldNameRowDatas[j]?.ToString() ?? "";
                if (string.IsNullOrEmpty(field))
                {
                    continue;
                }

                var rowdata = mSheet.Rows[i][j];
                if (rowdata == null)
                {
                    errors?.Add($"表格数据为空：[{i},{j}]");
                    continue;
                }

                string fieldType = (fieldTypeRowDatas[j]?.ToString() ?? "").ToLower();
                if (rowdata is DBNull) // 空类型判断，赋默认值
                {
                    if (fieldType == "int" || fieldType == "float" || fieldType == "double")
                    {
                        row[field] = 0;
                    }
                    else if (fieldType == "string")
                    {
                        row[field] = "";
                    }
                    else if (fieldType == "bool")
                    {
                        row[field] = false;
                    }
                    else if (IsArrayType(fieldType)) // 空数组 → 真正的空 JSON 数组
                    {
                        row[field] = new List<object>();
                    }
                }
                else
                {
                    // 数组字段：解析为真正的 List，JsonConvert 直接输出合法 JSON 数组
                    if (IsArrayType(fieldType))
                    {
                        row[field] = ParseArrayField(ToStringInvariant(rowdata), fieldType);
                    }
                    else if (fieldType == "int" || fieldType == "int32")
                    {
                        row[field] = int.TryParse(ToStringInvariant(rowdata), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                            ? value
                            : FailDefault(0, errors, i, j, rowdata);
                    }
                    else if (fieldType == "float")
                    {
                        row[field] = float.TryParse(ToStringInvariant(rowdata), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                            ? value
                            : FailDefault(0f, errors, i, j, rowdata);
                    }
                    else if (fieldType == "double")
                    {
                        row[field] = double.TryParse(ToStringInvariant(rowdata), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                            ? value
                            : FailDefault(0d, errors, i, j, rowdata);
                    }
                    else if (fieldType == "string")
                    {
                        row[field] = ToStringInvariant(rowdata);
                    }
                    else
                    {
                        row[field] = rowdata; // bool/未知类型等：保留原始单元格值
                    }
                }
            }

            if (row.Count > 0)
            {
                table.Add(row);
            }
        }

        return JsonConvert.SerializeObject(table);
    }

    private static object FailDefault<T>(T defaultValue, List<string>? errors, int i, int j, object rowdata)
    {
        errors?.Add($"表格数据出错：{i}-{j}，值：{rowdata}");
        return defaultValue!;
    }

    /// <summary>单元格 → 字符串（InvariantCulture，与参考在 zh-CN 环境下的 ToString 结果一致，且不受区域设置影响）。</summary>
    private static string ToStringInvariant(object value)
    {
        return value is IConvertible c
            ? c.ToString(CultureInfo.InvariantCulture)
            : value.ToString() ?? "";
    }

    /// <summary>判断字段类型是否为数组类型（兼容 [int] / int[] / string[] 等写法，与参考一致）。</summary>
    private static bool IsArrayType(string fieldType)
    {
        return fieldType.Contains("[") || fieldType == "string[]";
    }

    /// <summary>将数组字段字符串解析为真正的 List（产出 JSON 数组，与参考一致）。</summary>
    private static object ParseArrayField(string raw, string fieldType)
    {
        var result = new List<object>();
        if (string.IsNullOrEmpty(raw)) return result;

        var value = raw.Trim();
        // 兼容外层带引号写法："[a,b]"
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            value = value.Substring(1, value.Length - 2).Trim();
        if (value.StartsWith("[") && value.EndsWith("]"))
            value = value.Substring(1, value.Length - 2);
        if (string.IsNullOrWhiteSpace(value)) return result;

        var items = value.Split(new[] { ';', '；', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in items)
        {
            var t = item.Trim().Trim('"');
            if (fieldType == "string[]" || fieldType == "[string]")
            {
                result.Add(t);
            }
            else if (fieldType == "int[]" || fieldType == "[int]" || fieldType == "int32[]")
            {
                result.Add(int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0);
            }
            else if (fieldType == "float[]" || fieldType == "[float]")
            {
                result.Add(float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f);
            }
            else if (fieldType == "double[]" || fieldType == "[double]")
            {
                result.Add(double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d);
            }
            else if (fieldType == "long[]" || fieldType == "[long]")
            {
                result.Add(long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L);
            }
            else if (fieldType == "bool[]" || fieldType == "[bool]")
            {
                result.Add(bool.TryParse(t, out var v) && v);
            }
            else
            {
                // 未知数组类型：保留字符串元素
                result.Add(t);
            }
        }
        return result;
    }

    /// <summary>读取指定行（与参考 GetRowDatas 一致）。</summary>
    private static List<object> GetRowDatas(DataTable mSheet, int index)
    {
        var list = new List<object>();
        if (mSheet.Rows.Count <= index)
        {
            return list;
        }

        int colCount = mSheet.Columns.Count;
        for (int j = 0; j < colCount; j++)
        {
            list.Add(mSheet.Rows[index][j]);
        }
        return list;
    }
}
