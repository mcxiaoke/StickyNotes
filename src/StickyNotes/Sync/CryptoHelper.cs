using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace StickyNotes.Sync;

/// <summary>
/// 跨平台便签端到端防偷窥加密纯函数实现。
/// 算法标准：AES-256-CBC, PKCS7 填充, Key = SHA-256(UTF-8(password)), IV = 16 字节随机/指定向量。
/// 附带 SN1: 协议魔数守卫，彻底根除 CBC 伪解密输出乱码损库隐患。
/// </summary>
public static class CryptoHelper
{
    public const string MagicHeader = "SN1:";

    /// <summary>从口令派生 32 字节 (256-bit) AES 密钥</summary>
    public static byte[] DeriveKey(string password)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    /// <summary>
    /// 加密纯函数：输入明文与密码，返回 Base64 格式的 (iv, data)
    /// </summary>
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
    /// 解密纯函数：输入 Base64 格式的 iv 与 data 以及密码，还原 UTF-8 明文
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
    /// 注入魔数并加密：将 "SN1:" + content 进行 AES 加密，返回 (iv, payload)
    /// </summary>
    public static (string IvBase64, string PayloadBase64) CreateMagicPayload(string content, string password, byte[]? fixedIv = null)
    {
        var rawWithMagic = MagicHeader + (content ?? string.Empty);
        return Encrypt(rawWithMagic, password, fixedIv);
    }

    /// <summary>
    /// 解密并核验魔数：解密后强制检查 "SN1:" 前缀，校验通过剥离魔数返回真实正文，否则抛出 SecurityException
    /// </summary>
    public static string UnwrapMagicPayload(string ivBase64, string payloadBase64, string password)
    {
        string decrypted;
        try
        {
            decrypted = Decrypt(ivBase64, payloadBase64, password);
        }
        catch (CryptographicException ex)
        {
            throw new SecurityException("密文解密失败（填充或密文损坏），可能是同步密钥不一致。", ex);
        }

        if (!decrypted.StartsWith(MagicHeader, StringComparison.Ordinal))
        {
            throw new SecurityException("解密结果魔数头不匹配（伪解密乱码拦截），同步密钥不匹配或数据已损坏。");
        }

        return decrypted[MagicHeader.Length..];
    }
}
