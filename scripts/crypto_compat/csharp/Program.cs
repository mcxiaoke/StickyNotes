using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoCompat;

public class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // CLI 模式支持 (支持 Base64 输入输出以防止 Windows 命令行拆分换行符与引号)
        if (args.Length >= 3 && args[0] == "encrypt-b64")
        {
            string plainText = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            string password = args[2];
            byte[]? fixedIv = args.Length >= 4 ? Convert.FromBase64String(args[3]) : null;
            var (iv, data) = CryptoHelper.Encrypt(plainText, password, fixedIv);
            Console.WriteLine(JsonSerializer.Serialize(new { iv, data }));
            return 0;
        }

        if (args.Length >= 4 && args[0] == "decrypt-b64")
        {
            string ivBase64 = args[1];
            string dataBase64 = args[2];
            string password = args[3];
            string decrypted = CryptoHelper.Decrypt(ivBase64, dataBase64, password);
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(decrypted)));
            return 0;
        }

        if (args.Length >= 3 && args[0] == "encrypt")
        {
            string plainText = args[1];
            string password = args[2];
            byte[]? fixedIv = args.Length >= 4 ? Convert.FromBase64String(args[3]) : null;
            var (iv, data) = CryptoHelper.Encrypt(plainText, password, fixedIv);
            Console.WriteLine(JsonSerializer.Serialize(new { iv, data }));
            return 0;
        }

        if (args.Length >= 4 && args[0] == "decrypt")
        {
            string ivBase64 = args[1];
            string dataBase64 = args[2];
            string password = args[3];
            string decrypted = CryptoHelper.Decrypt(ivBase64, dataBase64, password);
            Console.Write(decrypted);
            return 0;
        }

        Console.WriteLine("==================================================");
        Console.WriteLine("  C# (.NET 8) Crypto Compatibility & NIST KAT Test");
        Console.WriteLine("==================================================");

        string vectorsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vectors.json");
        if (!File.Exists(vectorsPath))
        {
            vectorsPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "scripts", "crypto_compat", "vectors.json"));
        }
        if (!File.Exists(vectorsPath))
        {
            vectorsPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "vectors.json"));
        }

        if (!File.Exists(vectorsPath))
        {
            Console.Error.WriteLine($"[ERROR] Could not find vectors.json at {vectorsPath}");
            return 1;
        }

        var root = JsonNode.Parse(File.ReadAllText(vectorsPath, Encoding.UTF8))!.AsObject();

        // 1. NIST KAT
        Console.WriteLine("\n[1] Testing NIST KAT Vectors (Raw AES-256-CBC, No Padding)...");
        var nistList = root["nist_kat"]!.AsArray();
        int nistPassed = 0;
        foreach (var item in nistList)
        {
            string name = item!["name"]!.GetValue<string>();
            byte[] key = Convert.FromHexString(item["key_hex"]!.GetValue<string>());
            byte[] iv = Convert.FromHexString(item["iv_hex"]!.GetValue<string>());
            byte[] pt = Convert.FromHexString(item["plaintext_hex"]!.GetValue<string>());
            string expectedCtHex = item["ciphertext_hex"]!.GetValue<string>().ToLowerInvariant();

            byte[] actualCt = CryptoHelper.EncryptRawNoPadding(pt, key, iv);
            string actualCtHex = Convert.ToHexString(actualCt).ToLowerInvariant();

            if (actualCtHex != expectedCtHex)
            {
                Console.Error.WriteLine($"  [FAIL] {name}: Expected {expectedCtHex}, got {actualCtHex}");
                return 1;
            }

            byte[] actualPt = CryptoHelper.DecryptRawNoPadding(actualCt, key, iv);
            if (Convert.ToHexString(actualPt).ToLowerInvariant() != Convert.ToHexString(pt).ToLowerInvariant())
            {
                Console.Error.WriteLine($"  [FAIL] {name} Decrypt: Decrypted plaintext mismatch!");
                return 1;
            }

            Console.WriteLine($"  [PASS] {name}");
            nistPassed++;
        }
        Console.WriteLine($"  NIST KAT Result: {nistPassed}/{nistList.Count} passed.");

        // 2. App Scheme Cases (PKCS7, SHA256 Key)
        Console.WriteLine("\n[2] Testing App Scheme Cases (AES-256-CBC, PKCS7, Key=SHA256(password))...");
        var appScheme = root["app_scheme"]!.AsObject();
        var cases = appScheme["cases"]!.AsArray();
        int appPassed = 0;

        foreach (var c in cases)
        {
            string id = c!["id"]!.GetValue<string>();
            string desc = c["description"]!.GetValue<string>();
            string password = c["password"]!.GetValue<string>();
            string ivBase64 = c["iv_base64"]!.GetValue<string>();
            string plaintext = c["plaintext"]!.GetValue<string>();
            string expectedCtBase64 = c["expected_ciphertext_base64"]!.GetValue<string>();

            byte[] fixedIv = Convert.FromBase64String(ivBase64);

            // Test Encrypt
            var (actualIvBase64, actualCtBase64) = CryptoHelper.Encrypt(plaintext, password, fixedIv);
            if (actualIvBase64 != ivBase64)
            {
                Console.Error.WriteLine($"  [FAIL] {id} ({desc}): IV mismatch");
                return 1;
            }
            if (actualCtBase64 != expectedCtBase64)
            {
                Console.Error.WriteLine($"  [FAIL] {id} ({desc}): Ciphertext mismatch!");
                Console.Error.WriteLine($"    Expected: {expectedCtBase64}");
                Console.Error.WriteLine($"    Actual:   {actualCtBase64}");
                return 1;
            }

            // Test Decrypt
            string actualPt = CryptoHelper.Decrypt(ivBase64, expectedCtBase64, password);
            if (actualPt != plaintext)
            {
                Console.Error.WriteLine($"  [FAIL] {id} ({desc}): Decrypted text mismatch!");
                return 1;
            }

            Console.WriteLine($"  [PASS] {id} - {desc}");
            appPassed++;
        }
        Console.WriteLine($"  App Scheme Result: {appPassed}/{cases.Count} passed.");

        // 3. Dynamic Random IV Roundtrip
        Console.WriteLine("\n[3] Testing Dynamic Random IV Roundtrip...");
        string testText = "Dynamic Test: 这是一个随机 IV 动态加解密测试！🔥 Special chars: <>?:\"{}|_+";
        var (dynIv, dynCt) = CryptoHelper.Encrypt(testText, "DynamicRandomPass!@#");
        string dynDecrypted = CryptoHelper.Decrypt(dynIv, dynCt, "DynamicRandomPass!@#");
        if (dynDecrypted != testText)
        {
            Console.Error.WriteLine("  [FAIL] Dynamic roundtrip text mismatch!");
            return 1;
        }
        Console.WriteLine($"  [PASS] Dynamic roundtrip passed (IV={dynIv.Substring(0, 8)}...)");

        Console.WriteLine("\n>>> C# ALL TESTS PASSED SUCCESSFULLY! <<<\n");
        return 0;
    }
}
