package stickynotes.crypto

import java.io.File
import java.nio.charset.StandardCharsets
import java.util.Base64

fun hexToBytes(hex: String): ByteArray {
    val clean = hex.trim()
    val len = clean.length
    val data = ByteArray(len / 2)
    var i = 0
    while (i < len) {
        data[i / 2] = ((Character.digit(clean[i], 16) shl 4) + Character.digit(clean[i + 1], 16)).toByte()
        i += 2
    }
    return data
}

fun bytesToHex(bytes: ByteArray): String {
    val sb = StringBuilder()
    for (b in bytes) {
        sb.append(String.format("%02x", b))
    }
    return sb.toString()
}

fun main(args: Array<String>) {
    if (args.size >= 3 && args[0] == "encrypt-b64") {
        val plainText = String(Base64.getDecoder().decode(args[1]), StandardCharsets.UTF_8)
        val password = args[2]
        val fixedIv = if (args.size >= 4) Base64.getDecoder().decode(args[3]) else null
        val res = CryptoHelper.encrypt(plainText, password, fixedIv)
        println("""{"iv":"${res["iv"]}","data":"${res["data"]}"}""")
        return
    }

    if (args.size >= 4 && args[0] == "decrypt-b64") {
        val ivBase64 = args[1]
        val dataBase64 = args[2]
        val password = args[3]
        val plain = CryptoHelper.decrypt(ivBase64, dataBase64, password)
        println(Base64.getEncoder().encodeToString(plain.toByteArray(StandardCharsets.UTF_8)))
        return
    }

    if (args.size >= 3 && args[0] == "encrypt") {
        val plainText = args[1]
        val password = args[2]
        val fixedIv = if (args.size >= 4) Base64.getDecoder().decode(args[3]) else null
        val res = CryptoHelper.encrypt(plainText, password, fixedIv)
        println("""{"iv":"${res["iv"]}","data":"${res["data"]}"}""")
        return
    }

    if (args.size >= 4 && args[0] == "decrypt") {
        val ivBase64 = args[1]
        val dataBase64 = args[2]
        val password = args[3]
        val plain = CryptoHelper.decrypt(ivBase64, dataBase64, password)
        print(plain)
        return
    }

    println("==================================================")
    println("  Kotlin (javax.crypto) Compatibility & NIST Test")
    println("==================================================")

    val currentDir = File(".").canonicalFile
    var vectorsFile = File(currentDir, "scripts/crypto_compat/vectors.json")
    if (!vectorsFile.exists()) {
        vectorsFile = File(currentDir, "vectors.json")
    }
    if (!vectorsFile.exists()) {
        vectorsFile = File(currentDir, "../vectors.json")
    }

    if (!vectorsFile.exists()) {
        System.err.println("[ERROR] vectors.json not found at ${vectorsFile.absolutePath}")
        System.exit(1)
    }

    val content = vectorsFile.readText(StandardCharsets.UTF_8)

    // 1. NIST KAT Tests
    println("\n[1] Testing NIST KAT Vectors (Raw AES-256-CBC, No Padding)...")
    val nistRegex = Regex("""\{\s*"name":\s*"([^"]+)",\s*"key_hex":\s*"([^"]+)",\s*"iv_hex":\s*"([^"]+)",\s*"plaintext_hex":\s*"([^"]+)",\s*"ciphertext_hex":\s*"([^"]+)"\s*\}""")
    val nistMatches = nistRegex.findAll(content).toList()
    var nistPassed = 0

    for (m in nistMatches) {
        val name = m.groupValues[1]
        val key = hexToBytes(m.groupValues[2])
        val iv = hexToBytes(m.groupValues[3])
        val pt = hexToBytes(m.groupValues[4])
        val expectedCt = m.groupValues[5].lowercase()

        val actualCt = CryptoHelper.encryptRawNoPadding(pt, key, iv)
        val actualCtHex = bytesToHex(actualCt).lowercase()

        if (actualCtHex != expectedCt) {
            System.err.println("  [FAIL] $name: expected $expectedCt, got $actualCtHex")
            System.exit(1)
        }

        val actualPt = CryptoHelper.decryptRawNoPadding(actualCt, key, iv)
        if (bytesToHex(actualPt).lowercase() != m.groupValues[4].lowercase()) {
            System.err.println("  [FAIL] $name Decrypt: plaintext mismatch")
            System.exit(1)
        }

        println("  [PASS] $name")
        nistPassed++
    }
    println("  NIST KAT Result: $nistPassed/${nistMatches.size} passed.")

    // 2. App Scheme Cases
    println("\n[2] Testing App Scheme Cases (AES-256-CBC, PKCS5Padding, Key=SHA256(password))...")
    
    // Parse cases block
    val caseRegex = Regex("""\{\s*"id":\s*"([^"]+)",\s*"description":\s*"([^"]+)",\s*"password":\s*"([^"]+)",\s*"iv_base64":\s*"([^"]+)",\s*"plaintext":\s*("(?:\\.|[^"\\])*"),\s*"expected_ciphertext_base64":\s*"([^"]+)"\s*\}""")
    val caseMatches = caseRegex.findAll(content).toList()
    var appPassed = 0

    for (m in caseMatches) {
        val id = m.groupValues[1]
        val desc = m.groupValues[2]
        val password = m.groupValues[3]
        val ivBase64 = m.groupValues[4]
        val rawPlaintext = m.groupValues[5]
        val expectedCt = m.groupValues[6]

        // Unescape standard JSON string escapes
        val plaintext = unescapeJsonString(rawPlaintext)

        val fixedIv = Base64.getDecoder().decode(ivBase64)
        val result = CryptoHelper.encrypt(plaintext, password, fixedIv)

        if (result["iv"] != ivBase64) {
            System.err.println("  [FAIL] $id: IV mismatch")
            System.exit(1)
        }
        if (result["data"] != expectedCt) {
            System.err.println("  [FAIL] $id ($desc): Ciphertext mismatch!")
            System.err.println("    Expected: $expectedCt")
            System.err.println("    Actual:   ${result["data"]}")
            System.exit(1)
        }

        val decrypted = CryptoHelper.decrypt(ivBase64, expectedCt, password)
        if (decrypted != plaintext) {
            System.err.println("  [FAIL] $id ($desc): Decrypted text mismatch!")
            System.exit(1)
        }

        println("  [PASS] $id - $desc")
        appPassed++
    }
    println("  App Scheme Result: $appPassed/${caseMatches.size} passed.")

    // 3. Dynamic Random IV Roundtrip
    println("\n[3] Testing Dynamic Random IV Roundtrip...")
    val testText = "Kotlin 动态加密测试：包含多语言与表情符号 💡🔥🎉 ~!@#$%^&*()_+"
    val dynResult = CryptoHelper.encrypt(testText, "KotlinDynamicPass!2026")
    val dynDecrypted = CryptoHelper.decrypt(dynResult["iv"]!!, dynResult["data"]!!, "KotlinDynamicPass!2026")
    if (dynDecrypted != testText) {
        System.err.println("  [FAIL] Dynamic roundtrip text mismatch!")
        System.exit(1)
    }
    println("  [PASS] Dynamic roundtrip passed (IV=${dynResult["iv"]!!.substring(0, 8)}...)")

    println("\n>>> KOTLIN ALL TESTS PASSED SUCCESSFULLY! <<<\n")
}

fun unescapeJsonString(jsonStr: String): String {
    // strip outer quotes
    val s = if (jsonStr.startsWith("\"") && jsonStr.endsWith("\"")) {
        jsonStr.substring(1, jsonStr.length - 1)
    } else {
        jsonStr
    }
    val sb = StringBuilder()
    var i = 0
    while (i < s.length) {
        val c = s[i]
        if (c == '\\' && i + 1 < s.length) {
            when (val next = s[i + 1]) {
                '\\' -> { sb.append('\\'); i += 2 }
                '"' -> { sb.append('"'); i += 2 }
                'n' -> { sb.append('\n'); i += 2 }
                'r' -> { sb.append('\r'); i += 2 }
                't' -> { sb.append('\t'); i += 2 }
                'u' -> {
                    if (i + 5 < s.length) {
                        val hex = s.substring(i + 2, i + 6)
                        sb.append(hex.toInt(16).toChar())
                        i += 6
                    } else {
                        sb.append(c); i++
                    }
                }
                else -> { sb.append(next); i += 2 }
            }
        } else {
            sb.append(c)
            i++
        }
    }
    return sb.toString()
}
