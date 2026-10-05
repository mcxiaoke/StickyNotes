/**
 * 跨平台便签端到端加密纯函数实现 (Node.js / JavaScript)
 * 依赖环境：Node.js 原生 crypto 模块（零 npm 依赖）
 * 算法标准：AES-256-CBC, PKCS7 填充, Key = SHA-256(UTF-8(password)), IV = 16 字节
 */

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const CryptoHelper = {
  /**
   * 从用户明文密码派生 32 字节 (256-bit) AES 密钥
   * @param {string} password 
   * @returns {Buffer}
   */
  deriveKey(password) {
    return crypto.createHash('sha256').update(password, 'utf8').digest();
  },

  /**
   * 加密纯函数：将 UTF-8 字符串加密并返回 Base64 格式的 { iv, data }
   * @param {string} plainText 明文字符串
   * @param {string} password 同步密码
   * @param {Buffer|null} fixedIv 可选固定 IV（16 字节），用于确定性测试
   * @returns {{ iv: string, data: string }}
   */
  encrypt(plainText, password, fixedIv = null) {
    const key = this.deriveKey(password);
    const iv = fixedIv ? Buffer.from(fixedIv) : crypto.randomBytes(16);

    const cipher = crypto.createCipheriv('aes-256-cbc', key, iv);
    cipher.setAutoPadding(true); // 默认启用 PKCS7 填充

    const cipherBytes = Buffer.concat([
      cipher.update(plainText, 'utf8'),
      cipher.final()
    ]);

    return {
      iv: iv.toString('base64'),
      data: cipherBytes.toString('base64')
    };
  },

  /**
   * 解密纯函数：输入 Base64 格式的 iv 和 data 以及同步密码，还原 UTF-8 明文字符串
   * @param {string} ivBase64 
   * @param {string} dataBase64 
   * @param {string} password 
   * @returns {string}
   */
  decrypt(ivBase64, dataBase64, password) {
    const key = this.deriveKey(password);
    const iv = Buffer.from(ivBase64, 'base64');
    const cipherBytes = Buffer.from(dataBase64, 'base64');

    const decipher = crypto.createDecipheriv('aes-256-cbc', key, iv);
    decipher.setAutoPadding(true); // PKCS7 填充校验

    const plainBytes = Buffer.concat([
      decipher.update(cipherBytes),
      decipher.final()
    ]);

    return plainBytes.toString('utf8');
  },

  /**
   * 原始 AES-256-CBC 加密（无填充，严格 16 字节整块），用于验证 NIST 标准 Known Answer Test (KAT)
   * @param {Buffer} plainBytes 
   * @param {Buffer} key 
   * @param {Buffer} iv 
   * @returns {Buffer}
   */
  encryptRawNoPadding(plainBytes, key, iv) {
    const cipher = crypto.createCipheriv('aes-256-cbc', key, iv);
    cipher.setAutoPadding(false);
    return Buffer.concat([cipher.update(plainBytes), cipher.final()]);
  },

  /**
   * 原始 AES-256-CBC 解密（无填充，严格 16 字节整块）
   * @param {Buffer} cipherBytes 
   * @param {Buffer} key 
   * @param {Buffer} iv 
   * @returns {Buffer}
   */
  decryptRawNoPadding(cipherBytes, key, iv) {
    const decipher = crypto.createDecipheriv('aes-256-cbc', key, iv);
    decipher.setAutoPadding(false);
    return Buffer.concat([decipher.update(cipherBytes), decipher.final()]);
  }
};

// 导出纯函数模块供外部使用
module.exports = CryptoHelper;

// 如果直接运行该文件，执行自测与跨平台向量比对
if (require.main === module) {
  const args = process.argv.slice(2);
  if (args.length >= 3 && args[0] === 'encrypt-b64') {
    const plainText = Buffer.from(args[1], 'base64').toString('utf8');
    const password = args[2];
    const fixedIv = args[3] ? Buffer.from(args[3], 'base64') : null;
    const res = CryptoHelper.encrypt(plainText, password, fixedIv);
    console.log(JSON.stringify(res));
    process.exit(0);
  }
  if (args.length >= 4 && args[0] === 'decrypt-b64') {
    const ivBase64 = args[1];
    const dataBase64 = args[2];
    const password = args[3];
    const plain = CryptoHelper.decrypt(ivBase64, dataBase64, password);
    console.log(Buffer.from(plain, 'utf8').toString('base64'));
    process.exit(0);
  }
  if (args.length >= 2 && args[0] === 'encrypt') {
    const plainText = args[1];
    const password = args[2];
    const fixedIv = args[3] ? Buffer.from(args[3], 'base64') : null;
    const res = CryptoHelper.encrypt(plainText, password, fixedIv);
    console.log(JSON.stringify(res));
    process.exit(0);
  }
  if (args.length >= 4 && args[0] === 'decrypt') {
    const ivBase64 = args[1];
    const dataBase64 = args[2];
    const password = args[3];
    const plain = CryptoHelper.decrypt(ivBase64, dataBase64, password);
    process.stdout.write(plain);
    process.exit(0);
  }

  console.log('==================================================');
  console.log('  Node.js (node:crypto) Compatibility & NIST Test');
  console.log('==================================================');

  const vectorsPath = path.resolve(__dirname, '../vectors.json');
  if (!fs.existsSync(vectorsPath)) {
    console.error(`[ERROR] vectors.json not found at ${vectorsPath}`);
    process.exit(1);
  }

  const vectors = JSON.parse(fs.readFileSync(vectorsPath, 'utf8'));

  // 1. NIST KAT
  console.log('\n[1] Testing NIST KAT Vectors (Raw AES-256-CBC, No Padding)...');
  let nistPassed = 0;
  for (const item of vectors.nist_kat) {
    const key = Buffer.from(item.key_hex, 'hex');
    const iv = Buffer.from(item.iv_hex, 'hex');
    const pt = Buffer.from(item.plaintext_hex, 'hex');
    const expectedCtHex = item.ciphertext_hex.toLowerCase();

    const actualCt = CryptoHelper.encryptRawNoPadding(pt, key, iv);
    const actualCtHex = actualCt.toString('hex').toLowerCase();

    if (actualCtHex !== expectedCtHex) {
      console.error(`  [FAIL] ${item.name}: expected ${expectedCtHex}, got ${actualCtHex}`);
      process.exit(1);
    }

    const actualPt = CryptoHelper.decryptRawNoPadding(actualCt, key, iv);
    if (actualPt.toString('hex').toLowerCase() !== item.plaintext_hex.toLowerCase()) {
      console.error(`  [FAIL] ${item.name} Decrypt: plaintext mismatch`);
      process.exit(1);
    }

    console.log(`  [PASS] ${item.name}`);
    nistPassed++;
  }
  console.log(`  NIST KAT Result: ${nistPassed}/${vectors.nist_kat.length} passed.`);

  // 2. App Scheme Cases
  console.log('\n[2] Testing App Scheme Cases (AES-256-CBC, PKCS7, Key=SHA256(password))...');
  const cases = vectors.app_scheme.cases;
  let appPassed = 0;

  for (const c of cases) {
    const fixedIv = Buffer.from(c.iv_base64, 'base64');
    const encResult = CryptoHelper.encrypt(c.plaintext, c.password, fixedIv);

    if (encResult.iv !== c.iv_base64) {
      console.error(`  [FAIL] ${c.id}: IV mismatch`);
      process.exit(1);
    }
    if (encResult.data !== c.expected_ciphertext_base64) {
      console.error(`  [FAIL] ${c.id} (${c.description}): Ciphertext mismatch!`);
      console.error(`    Expected: ${c.expected_ciphertext_base64}`);
      console.error(`    Actual:   ${encResult.data}`);
      process.exit(1);
    }

    const decrypted = CryptoHelper.decrypt(c.iv_base64, c.expected_ciphertext_base64, c.password);
    if (decrypted !== c.plaintext) {
      console.error(`  [FAIL] ${c.id} (${c.description}): Decrypted text mismatch!`);
      process.exit(1);
    }

    console.log(`  [PASS] ${c.id} - ${c.description}`);
    appPassed++;
  }
  console.log(`  App Scheme Result: ${appPassed}/${cases.length} passed.`);

  // 3. Dynamic Roundtrip
  console.log('\n[3] Testing Dynamic Random IV Roundtrip...');
  const testText = 'Node.js 动态加密测试：包含多语言与表情符号 💡🔥🎉 ~!@#$%^&*()_+';
  const dynResult = CryptoHelper.encrypt(testText, 'NodeDynamicPass!2026');
  const dynDecrypted = CryptoHelper.decrypt(dynResult.iv, dynResult.data, 'NodeDynamicPass!2026');
  if (dynDecrypted !== testText) {
    console.error('  [FAIL] Dynamic roundtrip text mismatch!');
    process.exit(1);
  }
  console.log(`  [PASS] Dynamic roundtrip passed (IV=${dynResult.iv.substring(0, 8)}...)`);

  console.log('\n>>> NODE.JS ALL TESTS PASSED SUCCESSFULLY! <<<\n');
}
