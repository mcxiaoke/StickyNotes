using System;
using System.Security.Cryptography;
using System.Text;

namespace CryptoCompat;

/// <summary>
/// 跨平台便签端到端加密纯函数实现 (C# / .NET 8+)
/// 算法标准：AES-256-CBC, PKCS7 填充, Key = SHA-256(UTF-8(password)), IV = 16 字节随机/指定向量
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// 从用户明文密码派生 32 字节 (256-bit) AES 密钥
    /// </summary>
    public static byte[] DeriveKey(string password)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    /// <summary>
    /// 加密纯函数：将 UTF-8 字符串加密并返回 Base64 格式的 (iv, data)
    /// </summary>
    /// <param name="plainText">明文字符串</param>
    /// <param name="password">同步密码</param>
    /// <param name="fixedIv">可选固定 IV（16 字节），主要用于确定性测试；生产环境传 null 自动生成密码学安全随机 IV</param>
    public static (string IvBase64, string DataBase64) Encrypt(string plainText, string password, byte[]? fixedIv = null)
    {
        byte[] key = DeriveKey(password);
        byte[] iv = fixedIv ?? RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return (Convert.ToBase64String(iv), Convert.ToBase64String(cipherBytes));
    }

    /// <summary>
    /// 解密纯函数：输入 Base64 格式的 iv 和 data 以及同步密码，还原 UTF-8 明文字符串
    /// </summary>
    public static string Decrypt(string ivBase64, string dataBase64, string password)
    {
        byte[] key = DeriveKey(password);
        byte[] iv = Convert.FromBase64String(ivBase64);
        byte[] cipherBytes = Convert.FromBase64String(dataBase64);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// 原始 AES-256-CBC 加密（无填充，严格 16 字节整数倍），用于验证 NIST 标准 Known Answer Test (KAT) 向量
    /// </summary>
    public static byte[] EncryptRawNoPadding(byte[] plainBytes, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    /// <summary>
    /// 原始 AES-256-CBC 解密（无填充，严格 16 字节整数倍），用于验证 NIST 标准 Known Answer Test (KAT) 向量
    /// </summary>
    public static byte[] DecryptRawNoPadding(byte[] cipherBytes, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
    }
}
