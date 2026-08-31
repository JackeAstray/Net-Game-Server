using System.Security.Cryptography;

namespace Network;

/// <summary>
/// 会话 ID 生成器（D3 修复：统一委托 <see cref="Framework.Core.Security.SessionIdGenerator"/>，
/// 消除重复实现——此前 Network 与 Framework.Core.Security 各有一份相同逻辑，行为易分叉）。
/// 格式：高位 32 位为加密随机基座，低位 32 位为递增序列，不可预测且并发唯一。
/// </summary>
internal static class SessionIdGenerator
{
    public static long Next() => Framework.Core.Security.SessionIdGenerator.Next();
}
