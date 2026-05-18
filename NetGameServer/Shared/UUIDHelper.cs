using System;

namespace Shared
{
    public static class UUIDHelper
    {
        /// <summary>
        /// 生成一个标准的32个字符的UUID字符串（无连字符）。
        /// </summary>
        /// <returns>32个字符的UUID字符串。</returns>
        public static string Generate()
        {
            // 调用UUID Next插件以生成新的UUID
            // 默认情况下，UUID Next创建一个标准的36个字符的GUID字符串
            // 我们可以返回尽可能短的变体字符串，如base64，或者通过获取子字符串或删除连字符来返回短标识符
            return UUIDNext.Uuid.NewSequential().ToString("N"); // “N”格式删除了连字符，使其为32个字符
        }

        /// <summary>
        /// 生成一个基于顺序 GUID 的 22 字符 URL 安全 Base64 字符串。
        /// </summary>
        /// <remarks>使用 UUIDNext.Uuid.NewSequential() 生成 GUID，采用 Convert.ToBase64String 编码后将 '/' 替换为
        /// '_'、'+' 替换为 '-'，并截断为 22 个字符以去除 Base64 填充。</remarks>
        /// <returns>来自顺序 GUID 的 22 字符 URL 安全 Base64 编码字符串。</returns>
        public static string GenerateShort()
        {
            // 将Guid base64转换为更短的字符串（22个字符）
            return Convert.ToBase64String(UUIDNext.Uuid.NewSequential().ToByteArray())
                          .Replace("/", "_")
                          .Replace("+", "-")
                          .Substring(0, 22);
        }
    }
}