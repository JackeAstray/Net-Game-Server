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

        public static T? DeserializeFromUtf8Bytes<T>(System.ReadOnlySpan<byte> utf8Json)
        {
            return JsonConvert.DeserializeObject<T>(System.Text.Encoding.UTF8.GetString(utf8Json));
        }
    }
}
