using Newtonsoft.Json;

namespace Shared
{
    /// <summary>
    /// JSON 帮助类，提供序列化和反序列化的快捷方法
    /// </summary>
    public static class Json
    {
        /// <summary>
        /// 将指定的对象序列化为 JSON 字符串，使用给定的格式化选项。
        /// </summary>
        /// <param name="obj">要序列化为 JSON 的对象。可以是任何可序列化的 .NET 对象。</param>
        /// <param name="formatting">指定结果 JSON 字符串的格式化选项。使用 Formatting.Indented 进行漂亮的输出；否则使用 Formatting.None 进行紧凑输出。默认值为 Formatting.None。</param>
        /// <returns>指定对象的 JSON 字符串表示形式。</returns>
        public static string Serialize(object obj, Formatting formatting = Formatting.None)
        {
            return JsonConvert.SerializeObject(obj, formatting);
        }

        /// <summary>
        /// 将对象序列化为 JSON 并以 UTF-8 编码返回字节数组。
        /// </summary>
        /// <remarks>使用 Newtonsoft.Json 的 JsonConvert.SerializeObject 将对象转换为 JSON 文本，然后通过 UTF-8
        /// 编码获取字节表示。</remarks>
        /// <param name="obj">要序列化为 JSON 的对象。</param>
        /// <returns>表示序列化结果的 UTF-8 编码字节数组。</returns>
        public static byte[] SerializeToUtf8Bytes(object obj)
        {
            return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj));
        }

        /// <summary>
        /// 将指定的 JSON 字符串反序列化为类型为 T 的对象。
        /// </summary>
        /// <typeparam name="T">要反序列化为的对象类型。</typeparam>
        /// <param name="value">要反序列化的 JSON 字符串。不能为空。</param>
        /// <returns>从指定的 JSON 字符串反序列化得到的 T 类型的对象，如果输入为空或 null，则返回 null。</returns>
        public static T? Deserialize<T>(string value)
        {
            return JsonConvert.DeserializeObject<T>(value);
        }

        /// <summary>
        /// 将 UTF-8 编码的 JSON 字节序列反序列化为指定类型的对象。
        /// </summary>
        /// <remarks>使用 Newtonsoft.Json 的 JsonConvert，通过将字节序列解码为 UTF-8 字符串后进行反序列化。输入必须为有效的 UTF-8
        /// JSON，否则可能抛出由 JsonConvert 引发的异常（例如 JsonReaderException）。</remarks>
        /// <typeparam name="T">要反序列化为的目标类型。</typeparam>
        /// <param name="utf8Json">包含 UTF-8 编码 JSON 的只读字节序列。</param>
        /// <returns>已反序列化的 T 实例；若 JSON 表示 null 则返回 null。</returns>
        public static T? DeserializeFromUtf8Bytes<T>(System.ReadOnlySpan<byte> utf8Json)
        {
            return JsonConvert.DeserializeObject<T>(System.Text.Encoding.UTF8.GetString(utf8Json));
        }
    }
}
