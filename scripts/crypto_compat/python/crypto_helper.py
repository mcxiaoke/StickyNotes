"""
跨平台便签端到端加密纯函数实现 (Python)
使用标准 cryptography 库（经由 uv 零配置秒级执行）
算法标准：AES-256-CBC, PKCS7 填充, Key = SHA-256(UTF-8(password)), IV = 16 字节
"""

import base64
import hashlib
import json
import os
import sys
from pathlib import Path
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives import padding


class CryptoHelper:
    @staticmethod
    def derive_key(password: str) -> bytes:
        """从密码派生 32 字节 (256-bit) AES 密钥"""
        return hashlib.sha256(password.encode("utf-8")).digest()

    @staticmethod
    def encrypt(plain_text: str, password: str, fixed_iv: bytes = None) -> tuple[str, str]:
        """
        加密纯函数：将 UTF-8 字符串加密并返回 Base64 格式的 (iv_b64, data_b64)
        """
        key = CryptoHelper.derive_key(password)
        iv = fixed_iv if fixed_iv is not None else os.urandom(16)

        # PKCS7 填充 (128-bit block size)
        padder = padding.PKCS7(128).padder()
        padded_bytes = padder.update(plain_text.encode("utf-8")) + padder.finalize()

        cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
        encryptor = cipher.encryptor()
        cipher_bytes = encryptor.update(padded_bytes) + encryptor.finalize()

        return base64.b64encode(iv).decode("utf-8"), base64.b64encode(cipher_bytes).decode("utf-8")

    @staticmethod
    def decrypt(iv_b64: str, data_b64: str, password: str) -> str:
        """
        解密纯函数：输入 Base64 格式的 iv 和 data 以及同步密码，还原 UTF-8 明文字符串
        """
        key = CryptoHelper.derive_key(password)
        iv = base64.b64decode(iv_b64)
        cipher_bytes = base64.b64decode(data_b64)

        cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
        decryptor = cipher.decryptor()
        padded_bytes = decryptor.update(cipher_bytes) + decryptor.finalize()

        unpadder = padding.PKCS7(128).unpadder()
        plain_bytes = unpadder.update(padded_bytes) + unpadder.finalize()

        return plain_bytes.decode("utf-8")

    @staticmethod
    def encrypt_raw_no_padding(plain_bytes: bytes, key: bytes, iv: bytes) -> bytes:
        """原始 AES-256-CBC 加密（无填充，16 字节整数倍），用于 NIST KAT 验证"""
        cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
        encryptor = cipher.encryptor()
        return encryptor.update(plain_bytes) + encryptor.finalize()

    @staticmethod
    def decrypt_raw_no_padding(cipher_bytes: bytes, key: bytes, iv: bytes) -> bytes:
        """原始 AES-256-CBC 解密（无填充，16 字节整数倍），用于 NIST KAT 验证"""
        cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
        decryptor = cipher.decryptor()
        return decryptor.update(cipher_bytes) + decryptor.finalize()


def run_tests():
    # 强制标准输出 UTF-8
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

    args = sys.argv[1:]
    if len(args) >= 3 and args[0] == "encrypt-b64":
        plain_text = base64.b64decode(args[1]).decode("utf-8")
        password = args[2]
        fixed_iv = base64.b64decode(args[3]) if len(args) >= 4 else None
        iv, data = CryptoHelper.encrypt(plain_text, password, fixed_iv)
        print(json.dumps({"iv": iv, "data": data}))
        sys.exit(0)

    if len(args) >= 4 and args[0] == "decrypt-b64":
        iv_b64 = args[1]
        data_b64 = args[2]
        password = args[3]
        plain = CryptoHelper.decrypt(iv_b64, data_b64, password)
        print(base64.b64encode(plain.encode("utf-8")).decode("utf-8"))
        sys.exit(0)

    if len(args) >= 3 and args[0] == "encrypt":
        plain_text = args[1]
        password = args[2]
        fixed_iv = base64.b64decode(args[3]) if len(args) >= 4 else None
        iv, data = CryptoHelper.encrypt(plain_text, password, fixed_iv)
        print(json.dumps({"iv": iv, "data": data}))
        sys.exit(0)

    if len(args) >= 4 and args[0] == "decrypt":
        iv_b64 = args[1]
        data_b64 = args[2]
        password = args[3]
        plain = CryptoHelper.decrypt(iv_b64, data_b64, password)
        sys.stdout.write(plain)
        sys.exit(0)

    print("==================================================")
    print("  Python (cryptography) Compatibility & NIST Test")
    print("==================================================")

    current_dir = Path(__file__).parent.resolve()
    vectors_path = (current_dir / "../vectors.json").resolve()
    if not vectors_path.exists():
        vectors_path = Path("scripts/crypto_compat/vectors.json").resolve()

    if not vectors_path.exists():
        print(f"[ERROR] vectors.json not found at {vectors_path}", file=sys.stderr)
        sys.exit(1)

    with open(vectors_path, "r", encoding="utf-8") as f:
        vectors = json.load(f)

    # 1. NIST KAT
    print("\n[1] Testing NIST KAT Vectors (Raw AES-256-CBC, No Padding)...")
    nist_passed = 0
    for item in vectors["nist_kat"]:
        key = bytes.fromhex(item["key_hex"])
        iv = bytes.fromhex(item["iv_hex"])
        pt = bytes.fromhex(item["plaintext_hex"])
        expected_ct = item["ciphertext_hex"].lower()

        actual_ct = CryptoHelper.encrypt_raw_no_padding(pt, key, iv)
        actual_ct_hex = actual_ct.hex().lower()

        if actual_ct_hex != expected_ct:
            print(f"  [FAIL] {item['name']}: expected {expected_ct}, got {actual_ct_hex}", file=sys.stderr)
            sys.exit(1)

        actual_pt = CryptoHelper.decrypt_raw_no_padding(actual_ct, key, iv)
        if actual_pt.hex().lower() != item["plaintext_hex"].lower():
            print(f"  [FAIL] {item['name']} Decrypt: plaintext mismatch", file=sys.stderr)
            sys.exit(1)

        print(f"  [PASS] {item['name']}")
        nist_passed += 1

    print(f"  NIST KAT Result: {nist_passed}/{len(vectors['nist_kat'])} passed.")

    # 2. App Scheme Cases
    print("\n[2] Testing App Scheme Cases (AES-256-CBC, PKCS7, Key=SHA256(password))...")
    cases = vectors["app_scheme"]["cases"]
    app_passed = 0
    for c in cases:
        fixed_iv = base64.b64decode(c["iv_base64"])
        actual_iv, actual_ct = CryptoHelper.encrypt(c["plaintext"], c["password"], fixed_iv)

        if actual_iv != c["iv_base64"]:
            print(f"  [FAIL] {c['id']}: IV mismatch", file=sys.stderr)
            sys.exit(1)
        if actual_ct != c["expected_ciphertext_base64"]:
            print(f"  [FAIL] {c['id']} ({c['description']}): Ciphertext mismatch!", file=sys.stderr)
            print(f"    Expected: {c['expected_ciphertext_base64']}", file=sys.stderr)
            print(f"    Actual:   {actual_ct}", file=sys.stderr)
            sys.exit(1)

        decrypted = CryptoHelper.decrypt(c["iv_base64"], c["expected_ciphertext_base64"], c["password"])
        if decrypted != c["plaintext"]:
            print(f"  [FAIL] {c['id']} ({c['description']}): Decrypted text mismatch!", file=sys.stderr)
            sys.exit(1)

        print(f"  [PASS] {c['id']} - {c['description']}")
        app_passed += 1

    print(f"  App Scheme Result: {app_passed}/{len(cases)} passed.")

    # 3. Dynamic Roundtrip
    print("\n[3] Testing Dynamic Random IV Roundtrip...")
    test_text = "Python 动态加密测试：包含多语言与表情符号 💡🔥🎉 ~!@#$%^&*()_+"
    dyn_iv, dyn_ct = CryptoHelper.encrypt(test_text, "PythonDynamicPass!2026")
    dyn_decrypted = CryptoHelper.decrypt(dyn_iv, dyn_ct, "PythonDynamicPass!2026")
    if dyn_decrypted != test_text:
        print("  [FAIL] Dynamic roundtrip text mismatch!", file=sys.stderr)
        sys.exit(1)
    print(f"  [PASS] Dynamic roundtrip passed (IV={dyn_iv[:8]}...)")

    print("\n>>> PYTHON ALL TESTS PASSED SUCCESSFULLY! <<<\n")


if __name__ == "__main__":
    run_tests()
