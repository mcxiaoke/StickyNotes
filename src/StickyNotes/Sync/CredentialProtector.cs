using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using StickyNotes.Infrastructure;

namespace StickyNotes.Sync;

/// <summary>
/// 凭据落盘保护：Windows DPAPI（CryptProtectData，当前用户域）。
/// 协议设计 §5.3 要求凭据不以明文出现在 settings.json 中。
/// 密文带 "dpapi:" 前缀；遇到无前缀的历史值按原文返回（兼容手工配置），不做明文写出。
/// </summary>
public static class CredentialProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "StickyNotes.Sync.v1.CredentialEntropy"u8.ToArray();

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText; // 已是密文

        try
        {
            var encrypted = ProtectData(Entropy, Encoding.UTF8.GetBytes(plainText));
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[CredentialProtector] DPAPI 加密失败，凭据将不落盘（请检查用户配置环境）: {ex.Message}", ex);
            throw new InvalidOperationException("凭据加密失败，已中止保存以避免明文落盘。", ex);
        }
    }

    public static string Unprotect(string storedText)
    {
        if (string.IsNullOrEmpty(storedText)) return string.Empty;
        if (!storedText.StartsWith(Prefix, StringComparison.Ordinal)) return storedText; // 明文历史值

        try
        {
            var encrypted = Convert.FromBase64String(storedText[Prefix.Length..]);
            var plain = UnprotectData(Entropy, encrypted);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // 通常意味着密文来自另一个 Windows 用户/机器，无法解密——按空凭据处理并留痕
            AppLog.Error($"[CredentialProtector] DPAPI 解密失败（密文可能来自其他用户配置文件），按空凭据处理: {ex.Message}", ex);
            return string.Empty;
        }
    }

    private static byte[] ProtectData(byte[] entropy, byte[] plain)
    {
        var plainBlob = new DATA_BLOB { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        var entropyBlob = new DATA_BLOB { cbData = entropy.Length, pbData = Marshal.AllocHGlobal(entropy.Length) };
        var outBlob = new DATA_BLOB();
        try
        {
            Marshal.Copy(plain, 0, plainBlob.pbData, plain.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);

            // CRYPTPROTECT_UI_FORBIDDEN：服务/非交互场景安全
            if (!CryptProtectData(ref plainBlob, "StickyNotes credential", ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (plainBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(plainBlob.pbData);
            if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(outBlob.pbData);
        }
    }

    private static byte[] UnprotectData(byte[] entropy, byte[] encrypted)
    {
        var encryptedBlob = new DATA_BLOB { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
        var entropyBlob = new DATA_BLOB { cbData = entropy.Length, pbData = Marshal.AllocHGlobal(entropy.Length) };
        var outBlob = new DATA_BLOB();
        try
        {
            Marshal.Copy(encrypted, 0, encryptedBlob.pbData, encrypted.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);

            if (!CryptUnprotectData(ref encryptedBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (encryptedBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(encryptedBlob.pbData);
            if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(outBlob.pbData);
        }
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);
}
