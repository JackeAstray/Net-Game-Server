using System.Security.Cryptography;

namespace Framework.Core.Security;

/// <summary>
/// 集中式密码哈希服务（PBKDF2-HMACSHA256 + 随机盐 + 常量时间比较）。
/// 统一 DB 账号密码与 Center 房间密码的哈希实现，消除各处散落的弱哈希（如无盐 SHA-256）。
/// 存储格式：'PBKDF2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;'
/// </summary>
public static class PasswordHasher
{
    private const int DefaultIterations = 100_000;

    /// <summary>对明文密码做加盐 PBKDF2 哈希，返回可存储字符串。</summary>
    public static string HashPassword(string rawPassword)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(rawPassword, salt, DefaultIterations, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>判断存储串是否为 PBKDF2 格式。</summary>
    public static bool IsPbkdf2Hash(string storedPassword)
        => !string.IsNullOrWhiteSpace(storedPassword) && storedPassword.StartsWith("PBKDF2$", StringComparison.Ordinal);

    /// <summary>验证明文密码与存储哈希是否匹配（固定时间比较，防御时序攻击）。</summary>
    public static bool VerifyPassword(string rawPassword, string storedPassword)
    {
        if (!IsPbkdf2Hash(storedPassword))
        {
            return false;
        }

        string[] parts = storedPassword.Split('$');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out int iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(rawPassword, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
