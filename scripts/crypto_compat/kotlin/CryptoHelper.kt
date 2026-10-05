package stickynotes.crypto

import java.io.File
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 跨平台便签端到端加密纯函数实现 (Kotlin / Android)
 * 标准库：javax.crypto (零第三方依赖，兼容 Android SDK 与 JVM)
 * 算法：AES-256-CBC, PKCS5Padding (等同于 PKCS7), Key = SHA-256(UTF-8(password)), IV = 16 字节
 */
object CryptoHelper {

    /**
     * 从密码派生 32 字节 AES 密钥
     */
    fun deriveKey(password: String): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(password.toByteArray(StandardCharsets.UTF_8))
    }

    /**
     * 加密纯函数：返回 Map("iv" to Base64, "data" to Base64)
     * @param fixedIv 可选固定 IV（16 字节），用于确定性测试验证
     */
    fun encrypt(plainText: String, password: String, fixedIv: ByteArray? = null): Map<String, String> {
        val key = deriveKey(password)
        val iv = fixedIv ?: ByteArray(16).apply { SecureRandom().nextBytes(this) }

        val cipher = Cipher.getInstance("AES/CBC/PKCS5Padding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec(iv))

        val cipherBytes = cipher.doFinal(plainText.toByteArray(StandardCharsets.UTF_8))

        return mapOf(
            "iv" to Base64.getEncoder().encodeToString(iv),
            "data" to Base64.getEncoder().encodeToString(cipherBytes)
        )
    }

    /**
     * 解密纯函数：输入 Base64 格式的 iv 和 data，还原 UTF-8 字符串
     */
    fun decrypt(ivBase64: String, dataBase64: String, password: String): String {
        val key = deriveKey(password)
        val iv = Base64.getDecoder().decode(ivBase64)
        val cipherBytes = Base64.getDecoder().decode(dataBase64)

        val cipher = Cipher.getInstance("AES/CBC/PKCS5Padding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec(iv))

        val plainBytes = cipher.doFinal(cipherBytes)
        return String(plainBytes, StandardCharsets.UTF_8)
    }

    /**
     * 原始 AES-256-CBC 加密（无填充），用于 NIST KAT 验证
     */
    fun encryptRawNoPadding(plainBytes: ByteArray, key: ByteArray, iv: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/CBC/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec(iv))
        return cipher.doFinal(plainBytes)
    }

    /**
     * 原始 AES-256-CBC 解密（无填充），用于 NIST KAT 验证
     */
    fun decryptRawNoPadding(cipherBytes: ByteArray, key: ByteArray, iv: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/CBC/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), IvParameterSpec(iv))
        return cipher.doFinal(cipherBytes)
    }
}
