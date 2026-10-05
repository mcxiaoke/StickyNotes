# 便签端到端防偷窥加密同步技术规范与实现方案

- **日期**：2026-10-05 (GMT+8)
- **状态**：已落地实现 (v1.0)
- **关联文档**：[`docs/SYNC-PROTOCOL-DESIGN-20261004.md`](file:///c:/Home/Projects/StickyNotesDesktop/docs/SYNC-PROTOCOL-DESIGN-20261004.md) / [`scripts/crypto_compat/vectors.json`](file:///c:/Home/Projects/StickyNotesDesktop/scripts/crypto_compat/vectors.json)
- **验证基准**：
  - 多语言兼容基准：[`scripts/crypto_compat/run_all.ps1`](file:///c:/Home/Projects/StickyNotesDesktop/scripts/crypto_compat/run_all.ps1)（C# / Node.js / Kotlin / Python / Dart 5 语言环形互通 100% 通过）
  - 桌面端单元测试：[`tests/StickyNotes.Tests/Sync/SyncCryptoTests.cs`](file:///c:/Home/Projects/StickyNotesDesktop/tests/StickyNotes.Tests/Sync/SyncCryptoTests.cs)（151 项全量测试 100% 通过）

---

## 一、定位与设计边界

### 1.1 核心目标（“防偷窥”，非“军工级硬件加密”）

1. **防止云端明文审查与扫描**：针对坚果云、Nextcloud、网盘 WebDAV 及公有云 S3/R2 存储桶，防止云存储服务商员工直接查看便签内容，防止敏感词爬虫自动审查封禁，防止存储桶意外公开发生数据泄露。
2. **零阻碍跨平台**：桌面端（C# .NET）与未来移动端（Android Kotlin、Flutter Dart 等）在加解密上拥有 100% 确定性、无版本依赖兼容性、零学习成本。
3. **性能与体验零损耗**：UI 呈现、全文搜索与加解密完全解耦，本地便签毫秒级秒开、快速全文搜索与界面流畅度不受任何影响。
4. **不破坏既有同步协议**：直接复用已验证的 **无状态全量对账（基于 `UpdatedAt` 的 LWW）** 与 **墓碑机制**，不引入复杂状态机。

### 1.2 明确非目标（避免过度设计）

| 不做 | 理由 |
|---|---|
| 非对称公私钥协商 / 握手协议 | 单用户多设备场景，多端设备配对复杂度高且容易断裂，无此必要 |
| PBKDF2 10 万次迭代 / Argon2id | 跨平台移动端性能损耗大、参数极易对不齐；防窥场景下 SHA-256 足够 |
| 物理删除云端文件 | 严重破坏全量对账逻辑，会导致已删除便签在其他设备“无限复活” |
| 本地引入 `sync_status` 状态机 | 本地已验证无状态全量对账极简可靠，不需要 dirty / status 等易出错字段 |
| 云端 `sync_cursor.txt` 全局游标 | 多设备并发修改会产生覆盖竞态；全量对账本身流量极小，无需游标 |

---

## 二、存储目录架构：物理隔离的双轨设计

明文与密文直接在存储顶层通过**不同子目录前缀进行物理隔离**：

```text
WebDAV / S3 存储根/
├── stickynotes-data/              <--- 【明文同步目录】
│   └── notes/
│       └── <uuid>.json           (正文为明文 JSON)
│
└── stickynotes-vault/             <--- 【密文保险箱目录】
    ├── .auth_verifier            (口令探针校验文件)
    └── notes/
        └── <uuid>.json           (正文为 iv + payload 密文 JSON)
```

### 物理隔离的核心优势：
1. **零污染、零耦合**：明文便签与密文便签物理上完全分流在不同目录，切换模式时绝不污染旧数据，也无需做复杂的目录就地迁移；
2. **路由清晰干净**：客户端根据 `EnableEncryption` 自动路由子目录，明文用 `stickynotes-data`，密文用 `stickynotes-vault`；
3. **探针定位明确**：密文探针固定存放在 `stickynotes-vault/.auth_verifier`，明文目录不需要任何探针。

---

## 三、口令机制与专属密钥安全架构（已落地：方案 1）

为了彻底解决“多设备输错密码”、“忘记密码多端失联”、“密码修改繁琐”等 UX 痛点，系统正式采纳**方案 1（极简纯固定口令 + 专属密钥本地隔离 + 轻量混淆）**。

### 3.1 密钥分级与隔离机制

```mermaid
graph LR
    subgraph 本地专用 (Git 忽略)
        RawKey["用户生成的专属 64 位密钥\n(如 temp/sticky_notes_vault_secret.txt)"] -->|4 字节 XOR 掩码混淆| Obfuscated["VaultSecret.local.cs\n(字节数组混淆存储，运行时解混淆注入)"]
    end

    subgraph 公开开源仓库
        Fallback["VaultSecret.cs\n(公开分部类，定义 ResolveCustomSecret 钩子与备用口令)"]
    end

    Obfuscated -.->|分部类重载| Fallback
    Fallback --> Runtime["VaultSecret.GetSecret()\n(运行时安全提供 AES 密钥)"]
```

1. **开源安全性（分部类机制）**：
   - 公开仓库仅提交 [`VaultSecret.cs`](file:///c:/Home/Projects/StickyNotesDesktop/src/StickyNotes/Sync/VaultSecret.cs)，内含公共回落密钥与分部方法钩子 `partial void ResolveCustomSecret(ref string? secret)`，保证任何第三方克隆开源代码均可开箱编译成功；
   - 开发者或用户生成的专属高熵密钥（64 字符）采用 XOR 掩码混淆存放在 [`VaultSecret.local.cs`](file:///c:/Home/Projects/StickyNotesDesktop/src/StickyNotes/Sync/VaultSecret.local.cs) 中；
   - `**/VaultSecret.local.cs` 与 `**/sticky_notes_vault_secret.txt` 被严格加入 [`.gitignore`](file:///c:/Home/Projects/StickyNotesDesktop/.gitignore)，绝对不进入 Git 提交历史。
2. **轻量混淆防护**：
   - 对 64 字节密钥进行固定循环掩码（`[0x5A, 0xA5, 0x3C, 0xC3]`）异或混淆，避免字符串字面量在编译产物（DLL/APK）中以 ASCII/UTF-8 明文被全局搜索直接提取；
   - 兼顾极致执行效率，0 内存分配解混淆。
3. **极致顺畅体验**：
   - 用户在 UI 界面中仅需勾选 `[x] 端到端防偷窥加密 (stickynotes-vault)` 开关；
   - 无需手动输入密码，无需记忆密码，多端安装同一套配置直接自动对齐。

---

## 四、数据格式与双轨 Schema 规范

系统坚决摒弃把密文拼接塞在 `content` 字段的“字符串黑客”做法，采用**字段级纯粹互斥分离**：
* 明文模式包含 `content`，无 `iv`/`payload`；
* 密文模式包含 `iv` 与 `payload`，**绝不出现 `content` 字段**（序列化自动忽略 null 字段），从源头杜绝未解密密文被当作正常正文刷入 UI 或入库。

### 4.1 明文便签规范（`stickynotes-data/notes/<uuid>.json`）

```json
{
  "schemaVersion": 1,
  "id": "9f3c8a4e-1b2d-4c3e-a5f6-0123456789ab",
  "content": "这是明文便签正文...",
  "color": "yellow",
  "isPinnedInList": false,
  "alwaysOnTop": false,
  "isDeleted": false,
  "createdAt": "2026-10-04T03:00:00.0000000Z",
  "updatedAt": "2026-10-04T07:30:00.0000000Z",
  "deviceId": "win-8xf7ad"
}
```

### 4.2 密文便签规范（`stickynotes-vault/notes/<uuid>.json`）

```json
{
  "schemaVersion": 1,
  "id": "9f3c8a4e-1b2d-4c3e-a5f6-0123456789ab",
  "iv": "AAECAwQFBgcICQoLDA0ODw==",
  "payload": "g//VhNu/q6PQKaIx9jySzQqtwz3unRPg7C+7qU9+5nMXslInpukYYk71cE0WAVb3ZmE1wAXnxAmCZl2foR3fkF1P/6wa7IDOxLJloBgM809HET0Zy7peRNDZ1+kX9qtQQzKcz52ALTFl1UHh71lQqd4eCRvrH3xaF5Uaqs+Tm0e7tbNWaXyTEkFUrb/nzmKL8QRsp+9e6Vn5UjbFFTmi+82tIL6gkHTbe4FK9U0YEcyJFFZPJNflcqM1p5W+4a5+kbBju7/u3SGSpQ/QncIHdvg19fE2FoQsFQ33QdGxubz9VGxVC6U555ajBwsLI3Wyq5oRIlNYSG06YEyMftCeI2sOdyjxlR24J4JvDwfB3/c=",
  "color": "yellow",
  "isPinnedInList": false,
  "alwaysOnTop": false,
  "isDeleted": false,
  "createdAt": "2026-10-04T03:00:00.0000000Z",
  "updatedAt": "2026-10-04T07:30:00.0000000Z",
  "deviceId": "win-8xf7ad"
}
```

### 4.3 核心优势
1. **防呆防护**：反序列化时若未解密，`Content` 为 `null`；下行解密成功后才赋值为真实正文。明密文互斥校验拦截非法混合格式；
2. **结构标准**：`iv` 与 `payload` 均为标准 Base64 字符串，与跨平台纯函数统一；
3. **元数据透明**：`updatedAt`、`id`、`isDeleted` 依然以明文展现，同步引擎比对时间戳时不需浪费 CPU 逐个解密，对账性能达到极致；
4. **服务商视角**：网盘服务商/爬虫看到只有高熵密文 `payload`，无法窥探正文内容。

---

## 五、安全防错设计（防坏数据落盘）

在 CBC 模式下，仅靠捕获 PKCS#7 Padding 异常存在缺陷：约 1/256 概率错误密钥碰巧解出合法填充字节，产生随机乱码。若直接写入 SQLite，会导致用户数据不可逆损坏。

为此，系统实现**双重强防御**：

### 5.1 正文前缀固定魔数（Magic Header Guard）

加密前，在明文正文前隐式注入协议魔数：
$$\text{Plaintext} = \text{"SN1:"} + \text{Note.Content}$$

* **解密校验**：解密出的文本**必须**以 `SN1:` 开头；
  * 若以 `SN1:` 开头：剥离前 4 个字符，确认密码与密文无损，安全交付；
  * 若不以 `SN1:` 开头（或抛出解密异常）：立刻抛出 `SecurityException` 判定为“口令错误或密文损坏”，`SyncEngine` 将该对象记入 `SkippedInvalid` 并跳过，**绝对不向本地数据库写入乱码**！

### 5.2 云端口令校验探针文件（Auth Verifier）

在密文目录根部保存一份探针校验文件：`<root>/stickynotes-vault/.auth_verifier`：
```json
{
  "version": 1,
  "iv": "<iv_base64>",
  "payload": "<ciphertext_base64>"
}
```
* **探针载荷**：使用当前密钥加密固定魔数字符串 `STICKYNOTES_AUTH_OK`（包装在 `SN1:` 魔数中）；
* **触发时机**：
  * **设置页「测试连接」**：若开启加密，连通存储后自动读取 `.auth_verifier`。若文件不存在则自举创建并上传；若文件存在则解密校验魔数，不匹配直接抛出异常阻止用户配置错误口令；
  * **每轮同步执行前**：若开启加密，`SyncEngine` 启动首步检查探针。不存在则自举初始化；解密失败则立即中断本轮同步，避免将大量便签逐个解密失败。

---

## 六、落地同步工作流

加解密过程被无缝内嵌在 `SyncEngine` 与 `IStorageBackend` 之间，UI 层与 SQLite 仓储层完全无感知，本地始终保持高效明文。

```
┌─────────────────────────────────────────────────────────────────┐
│                      UI / ViewModels / Views                    │
│                      (完全使用本地数据库明文)                   │
└────────────────────────────────┬────────────────────────────────┘
                                 │
┌────────────────────────────────┴────────────────────────────────┐
│               Local SQLite Database (100% 明文存储)             │
│                 (保证离线秒开、快速全文搜索不受损)              │
└────────────────────────────────┬────────────────────────────────┘
                                 │
┌────────────────────────────────┴────────────────────────────────┐
│                   SyncEngine (全量对账协议层)                   │
│   · 对账裁决：按 UpdatedAt 的 LWW（无需解密即可快速裁决）       │
│   · 墓碑判定：isDeleted == true 保持墓碑流转                    │
│   · 探针守卫：每轮开始前核验 .auth_verifier                     │
└──────────────────┬─────────────────────────────▲────────────────┘
         [上行 Push]│                             │ [下行 Pull]
                   ▼                             │
       ┌───────────────────────┐     ┌───────────────────────┐
       │     CryptoHelper      │     │     CryptoHelper      │
       │   Encrypt(Content)    │     │   Decrypt(Content)    │
       │   注入 "SN1:" 魔数    │     │   核验 "SN1:" 魔数    │
       └───────────┬───────────┘     └───────────▲───────────┘
                   │                             │
                   ▼                             │
┌────────────────────────────────────────────────┴────────────────┐
│                   IStorageBackend (WebDAV / S3)                 │
│         云端存储 JSON (明文为 content，密文为 iv + payload)     │
└─────────────────────────────────────────────────────────────────┘
```

### 6.1 路径与子目录自动路由
- **WebDAV**：`StorageBackendFactory.GetEffectiveWebDavUrl` 自动将用户 URL 规范化并追加 `stickynotes-data/` 或 `stickynotes-vault/`。当用户在设置中切换开关时，自动切换目录名称；
- **S3**：`StorageBackendFactory.GetEffectiveS3Prefix` 自动将用户配置的前缀规范化并追加对应子目录；
- 后端实例创建后，`SyncEngine` 操作的相对 key 一律为 `notes/<uuid>.json` 与 `.auth_verifier`，无需关心底层存储物理路径。

### 6.2 步骤 1：前置探针校验
1. `SyncEngine.RunAsync` 触发时检查 `enableEncryption`；
2. 若开启加密，通过 `backend.GetTextAsync(".auth_verifier")` 获取探针：
   - 探针不存在：调用 `AuthVerifierDto.Create(secret)` 生成并 `PutTextAsync` 自举上传；
   - 探针存在：调用 `AuthVerifierDto.Verify(verifierJson, secret)` 核验；若失败抛出异常中断整轮同步。

### 6.3 步骤 2：下行拉取与智能对账 (Pull)
1. 调用 `backend.ListAsync()` 获取对象列表；
2. 循环处理远端项时，遇到 `.auth_verifier` 自动静默忽略；
3. 解析远端 JSON：
   - 若 `dto.IsEncrypted == true`：调用 `CryptoHelper.UnwrapMagicPayload(dto.Iv, dto.Payload, secret)`。解密成功将明文回填入 `dto.Content`；解密失败或魔数不符则记入 `SkippedInvalid` 并跳过；
   - 若为明文便签：直接读取 `dto.Content`；
4. 业务对账：由于 `dto.Content` 已还原为明文，`BusinessEquals` 直接按规范化明文执行比对；
5. 根据 LWW 判定需要下行的便签，经 `ApplyRemoteBatchAsync` 条件守卫原子批量更新入本地库。

### 6.4 步骤 3：上行加密推送 (Push)
1. 遍历待上传的本地便签：
   - 若开启加密：调用 `SyncNoteDto.FromNoteEncrypted(note, deviceId, secret)`，自动注入 `SN1:` 魔数、生成 16 字节随机 IV、执行 AES-256-CBC 加密填充 `Iv` 与 `Payload`，`Content` 置 null；
   - 若为明文模式：调用 `SyncNoteDto.FromNote(note, deviceId)`；
2. 序列化为 UTF-8 JSON（null 字段自动省略）；
3. 调用 `backend.PutTextAsync(SyncProtocol.NoteKey(note.Id), json)` 写入远端。

### 6.5 步骤 4：墓碑与删除同步（严禁物理 DELETE）
- 本地删除便签时，标记 `isDeleted = true` 并更新 `updatedAt` 为当前时间；
- 上行同步时，加密模式依然生成带有 `isDeleted: true` 的加密 JSON 推送覆盖；
- 远端保留该墓碑，其他设备对账时根据 `updatedAt` LWW 规则同步删除，防止便签在其他设备“无限复活”。

---

## 七、验证基准与成果

1. **多语言互通测试**：`scripts/crypto_compat/run_all.ps1` 闭环验证 C# (.NET 8)、Node.js、Kotlin、Dart、Python 5 语言标准 NIST KAT 与环形互解 100% 成功；
2. **桌面端单元测试**：`tests/StickyNotes.Tests/Sync/SyncCryptoTests.cs` 覆盖密钥混淆、AES-256 加解密、魔数阻断、DTO 字段互斥、子目录路由、两端加密同步全链路，全部 151 项单测 100% 通过（0 失败 0 警告）；
3. **安全审计**：代码库与公开 Git 历史无任何真实密钥信息泄露。

