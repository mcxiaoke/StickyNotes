# 便签网络同步协议设计 v1（文件即协议：WebDAV / Cloudflare R2 双后端）

- **日期**：2026-10-04 (GMT+8)
- **状态**：v1.1 已评审；Phase 1/2 已实施并通过全部测试（2026-10-04，见 §11 与 CHANGES-20261004）
- **关联文档**：[`docs/APP-ARCHITECTURE.md`](file:///c:/Home/Projects/StickyNotes/docs/APP-ARCHITECTURE.md) / [`docs/APP-PRODUCT.md`](file:///c:/Home/Projects/StickyNotes/docs/APP-PRODUCT.md)
- **评审输入**：[`temp/docs/StickyNotes-Sync-Architecture-20261004.md`](file:///c:/Home/Projects/StickyNotes/temp/docs/StickyNotes-Sync-Architecture-20261004.md)（取舍记录见 §12）
- **修订**：v1.1 采纳评审决议（§12），吸收评审输入的可取项；v1.2 按参考实现逐节复核为"可交付第三方实现"精度（§2.2 接口语义细则、§3.2 编码与解析规则、§4.1 裁决语义补全、§5 wire 级细节、§6 精确防抖值、§8 清单扩至 15 条、新增 §11.1 参考实现指引）

---

## 一、目标与非目标

### 1.1 目标

1. **简单可靠**：桌面端（本应用）与将来的 Android 端通过同一套协议同步便签；协议面足够小，一个开发者几天可以在任一平台实现完成。
2. **零自建服务**：不开发、不部署服务端程序。第一梯队支持 **WebDAV**（坚果云 / Nextcloud / Alist / nginx dav）与 **Cloudflare R2**（S3 兼容 API）两种存储后端，二者在第一天就是并列的一等公民。
3. **不丢数据**：常规操作（编辑、删除、断网、同步中断、时钟偏差）下最坏结果是"某台设备的某条便签旧一版"，不允许出现数据凭空消失或同步死循环。
4. **合并规则与现有导入一致**：同步引擎的冲突裁决直接复用现有备份导入已验证的 LWW 逻辑。

### 1.2 非目标（明确不做，这是"简单"的来源）

| 不做 | 理由 |
|---|---|
| 端到端加密 | 是 safenotes 的职责；本应用接受 HTTPS + 应用专用密码的传输与静态存储 |
| CRDT / 向量时钟 / 版本向量 | 便签是小段纯文本，按 `UpdatedAt` 的 LWW 足够；增量一致性协议是复杂度的主要来源 |
| 增量协议 / 同步游标 / delta log | 数据量（几百 KB～几 MB）下，无状态全量对账比增量更可靠且更省代码 |
| ETag / If-Match 乐观锁 | LWW 不需要；且各 WebDAV 服务端对 ETag 的支持差异大，踩不起 |
| 实时推送 / WebSocket | 定时 + 事件触发的轮询对本场景延迟足够 |
| 多用户 / 协作 / 冲突副本 UI | 单用户多设备；冲突裁决已拍板为时间戳 LWW，冲突副本不做（见 §4.3） |
| 窗口几何同步 | 窗口坐标/尺寸是设备本地属性（见 §3.2） |

---

## 二、总体架构：文件即协议 + 存储后端抽象

### 2.1 核心思想

**协议本体 = 目录布局 + 便签 JSON schema + 合并规则**。任何"能按 key 存取小文件"的服务都可以充当后端；同步引擎不感知 WebDAV 还是 S3。

```
┌─────────────────────────────────────────────┐
│ SyncEngine（协议层）                          │
│  · 全量对账 / LWW 合并 / 墓碑管理 / 单飞调度     │
│  · 只依赖 IStorageBackend 与 INoteRepository  │
└──────────────┬──────────────────────────────┘
               │ IStorageBackend（4 个语义）
       ┌───────┴────────┐
       │                │
┌──────┴───────┐  ┌─────┴─────────┐
│ WebDavBackend│  │ S3Backend(R2) │
│ PROPFIND/GET │  │ ListV2/GetObj │
│ PUT/DELETE   │  │ PutObj/DelObj │
└──────────────┘  └───────────────┘
```

### 2.2 存储后端接口（`Sync/IStorageBackend.cs`）

```csharp
/// <summary>远端对象条目。Key 为相对后端根的路径（如 "notes/<uuid>.json"），统一 '/' 分隔</summary>
public sealed record RemoteItem(string Key, long? Size, DateTimeOffset? LastModified);

/// <summary>
/// 存储后端抽象：同步引擎只依赖此接口。
/// 实现约定：幂等；失败抛异常；全部方法支持取消；对象不存在不算错误（Get 返回 null，Delete 视为成功）。
/// 实现通常持有 HttpClient 等资源，须实现 IDisposable。
/// </summary>
public interface IStorageBackend : IDisposable
{
    /// <summary>列出全部便签对象（仅形如 notes/<uuid>.json 的条目，扁平列表）</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken ct = default);

    /// <summary>读取文本，对象不存在返回 null</summary>
    Task<string?> GetTextAsync(string key, CancellationToken ct = default);

    /// <summary>写入/覆盖文本（小文件单请求覆盖，直接 PUT，不做 tmp+rename）</summary>
    Task PutTextAsync(string key, string content, CancellationToken ct = default);

    /// <summary>删除对象（仅墓碑 GC 使用，v1 不调用；对象不存在视为成功）</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>连通性测试（设置页「测试连接」）</summary>
    Task TestAsync(CancellationToken ct = default);
}
```

**接口语义细则**（两个后端都必须遵守）：

- **List 过滤与隔离**：`ListAsync` 只返回 `notes/<小写Guid>.json` 形态的对象。其他对象（陌生前缀、陌生扩展名、文件名非合法 Guid）一律**忽略且绝不删除**——这是向前兼容规则（§3.1）的执行点，在后端层落实。
- **不存在语义**：`GetTextAsync` 对不存在的对象返回 null（WebDAV 404 / S3 NoSuchKey）；`DeleteAsync` 对不存在视为成功。
- **TestAsync**：WebDAV = `PROPFIND <root>/ Depth: 0`；S3 = `ListObjectsV2?max-keys=1`（同时验证端点、凭据、桶与前缀）。
- **首次自举（仅 WebDAV）**：`ListAsync` 遇 `notes/` 集合 404 时，先 `MKCOL <root>/` 再 `MKCOL <root>/notes/`（服务器返回 405/409/403 视为"已存在或不可创建"，留痕继续），然后重列一次；仍不存在按空集合处理。S3 无目录概念，天然无需自举。
- **超时与连接**：每请求超时 30s；`HttpClient` 实例复用（`PooledConnectionLifetime` 约 10 分钟），禁止每请求新建。

**决策记录——为什么直接 PUT 而不做「PUT 到 .tmp 再 MOVE」**：S3/R2 没有原子 rename（Copy+Delete 两步且非原子），WebDAV 的 MOVE 各服务端行为也不完全一致；为统一两个后端且少一个失败分支，v1 采用直接 PUT。单文件 <10KB，HTTP + TLS 的传输完整性足以保证；万一出现截断文件，防御性解析（§4.1）会跳过它，该设备下一轮重新上传即可自愈。

### 2.3 模块落点

| 新增 | 职责 |
|---|---|
| `Sync/IStorageBackend.cs` | 上述接口 + `RemoteItem` |
| `Sync/WebDavBackend.cs` | WebDAV 实现（HttpClient + System.Xml，零第三方依赖） |
| `Sync/S3Backend.cs` | S3/R2 实现（手写最小 SigV4 签名，见 §5.2） |
| `Sync/SyncEngine.cs` | 对账/合并/上传下载/单飞调度（`SemaphoreSlim(1,1)`） |
| `Sync/SyncSettings.cs` | 同步配置 POCO，挂入现有 `settings.json` |
| `Services/SettingsViewModel` 扩展 | 设置页 UI（§6） |

复用且不改动的现有设施：

- [`INoteRepository`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Data/INoteRepository.cs)：`GetAllAsync()`（含墓碑行）读、`SaveBatchAsync()` 单事务写；
- [`NoteRepository.cs:148-160`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Data/NoteRepository.cs#L148-L160) 的 Upsert **原样保留调用方传入的 `UpdatedAt`**（`excluded.UpdatedAt`），同步下行的业务时间戳不会被覆盖——这是同步可行性的关键前提，已实证；
- [`ExportImportService.cs:183`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/ExportImportService.cs#L183) 已验证的 LWW 规则（`existing.UpdatedAt >= incoming.UpdatedAt` → 本地保留）；
- `AppLog`、`AppPaths`（数据目录/便携模式/测试重定向）、`SettingsService` 的 `settings.json` + `.bak` 恢复机制。

---

## 三、协议 v1 规范

### 3.1 目录与命名

```
<根>/notes/<uuid小写>.json        ← 每条便签一个文件，含已删除的墓碑
```

- `<根>`：WebDAV 后端 = 用户配置的目录 URL；S3 后端 = 桶 + **必填前缀子目录**（如 `stickynotes/`，无论专用桶还是复用桶一律加前缀，评审决议 4）。
- 文件名 = 便签 `Guid`（小写 D 格式）+ `.json`；除 `notes/` 外 v1 不引入其它前缀（若服务商限制单目录文件数，预留按 UUID 前两位分片的逃生舱 `notes/3f/<uuid>.json`，v1 不启用）。
- **演进规则**：非破坏性变更 = `schemaVersion` 递增 + 忽略未知字段；破坏性变更 = 启用新前缀目录（如 `notes2/`），老客户端按"陌生前缀"自然无视，两代协议可并存。
- **向前兼容规则**：列表结果中非 `notes/*.json` 形态的条目（陌生文件、子目录、其它扩展名）一律忽略，绝不删除、绝不让它中断同步。未来扩展（如共享元数据）用新前缀（`meta/`），老客户端天然无视。

### 3.2 便签文件 schema v1

```json
{
  "schemaVersion": 1,
  "id": "9f3c8a4e-1b2d-4c3e-a5f6-0123456789ab",
  "content": "便签正文（纯文本，\\n 换行）",
  "color": "yellow",
  "isPinnedInList": false,
  "alwaysOnTop": false,
  "isDeleted": false,
  "createdAt": "2026-10-04T03:00:00.0000000Z",
  "updatedAt": "2026-10-04T07:30:00.0000000Z",
  "deviceId": "win-mcx-01"
}
```

| 字段 | 类型 | 必填 | 语义与合并规则 |
|---|---|---|---|
| `schemaVersion` | int | 是 | 固定 1。解析时发现未知更高版本 → 跳过该文件并记日志（老客户端不吃坏新格式） |
| `id` | string(Guid) | 是 | 与文件名一致；不一致按解析失败跳过 |
| `content` | string | 是 | 纯文本，**协议层强制 `\n` 换行**：序列化时 `\r\n`→`\n`（实证：现有代码无任何换行规范化，WPF `TextBox` 产出 `\r\n`；不规范化则两端内容比对永不相等，防乒乓与 LWW 全部失效）。本地存储不迁移，规范化只发生在同步 DTO 边界 |
| `color` | string 枚举 | 是 | 与现有 `JsonStringEnumConverter` 序列化对齐 |
| `isPinnedInList` | bool | 是 | 协议始终同步；客户端自行决定是否采用（评审决议 2） |
| `alwaysOnTop` | bool | 是 | 协议始终同步；客户端自行决定是否采用（Android 端忽略，评审决议 2） |
| `isDeleted` | bool | 是 | 墓碑标志 |
| `createdAt` / `updatedAt` | string | 是 | **ISO-8601 UTC round-trip 格式**（.NET "o"）。这是 .NET 与 Kotlin 之间最容易踩的坑，必须写死在协议里 |
| `deviceId` | string | 否 | 设备标识，格式 `平台-6位随机串`（如 `win-8xf7ad`，首启生成后存 `settings.json`，评审决议 5）；仅诊断用，**不参与裁决** |

**不含窗口几何字段**（`WindowX/Y/Width/Height/IsOpen`），理由：[`UpdateWindowPlacementAsync`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Data/NoteRepository.cs) 移动窗口不 bump `UpdatedAt`，文件里若带坐标必然是过期快照；坐标是设备本地属性，Android 端无意义。新设备收到便签一律用默认落点、`IsOpen=false`（不自动弹窗，与导入行为一致）。

序列化约定（`SyncNoteDto`）：`System.Text.Json`，`PropertyNamingPolicy = CamelCase` + `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`；`DateTime` 经专用转换器强制 ISO-8601 UTC round-trip 输出。

**编码与解析规则**（实现方必须逐条对齐）：

1. 文件内容为**单个 UTF-8 JSON 对象**，无 BOM。
2. **时间戳 Kind 规范化**：写出时一律换算/标注为 UTC（`Local` → 换算，`Unspecified` → 按 UTC 处理）；读入时同样规范化为 UTC。任何一端不得把无时区时间戳当本地时间解释。
3. **枚举大小写**：`color` 写出为 camelCase 枚举名（如 `yellow`、`charcoal`）；解析必须大小写不敏感（容忍 `Yellow`）。
4. **防御性校验**（任一不满足 → 该文件按解析失败跳过，绝不中断整轮）：
   - JSON 语法合法；
   - `id` 非空且与文件名中的 Guid 一致；
   - `schemaVersion` ≤ 本端支持的最高版本（大于则跳过并记日志——老客户端不吃坏新格式）。
5. 文件名约定：Guid 的小写 "D" 格式（8-4-4-4-12 带连字符）+ `.json`。

### 3.3 与现有备份格式（`NoteBackupItem`）的关系

字段语义完全同源，差异：去掉 `Window*` / `IsOpen` / 兼容字段 `IsPinned`，新增 `schemaVersion` / `deviceId`。**不直接复用 `NoteBackupItem`**（它的可选三态字段是备份往返语义，同步需要更严格的必填语义），新建精简 DTO，避免备份格式演进与同步协议互相牵制。

---

## 四、合并与删除规则

### 4.1 全量对账算法（SyncEngine 主循环）

```
若 !singleFlight.Wait(0): return            // 单飞：上一轮未结束直接放弃本轮
try:
    remote  = backend.ListAsync()           // 1 次列表
    rNotes  = {}
    for item in remote（并发 4–8 个 GET）:    // v1：全量 GET（优化见 §7.2）
        text = backend.GetTextAsync(item.Key)
        // 单文件 > 4MB 视为异常数据：跳过并记日志（防呆上限）
        dto = TryParse(text) ?? { log; continue }   // 坏文件/陌生文件隔离，不中断整轮
        rNotes[dto.Id] = dto

    local = repository.GetAllAsync()        // 含 IsDeleted 行（墓碑也要参与对账）
    downloads = []; uploads = []
    for id in union(local.keys, rNotes.keys):
        l = local[id]; r = rNotes[id]
        if l == null:  downloads += r       // 远端新便签
        elif r == null: uploads += l        // 本地新便签（含首传）
        else:
            // LWW：updatedAt 大者胜；相等本地胜（与导入规则 >= 一致，确定性）
            if l.UpdatedAt >= r.UpdatedAt:
                if l != r: uploads += l     // 内容/标志位有差异才传（防乒乓）
            else:
                if l != r: downloads += r

    repository.ApplyRemoteBatchAsync(downloads, 快照, 几何=默认, IsOpen=false)
        // 条件更新：仅覆盖"快照后未被本地编辑"的行（下行守卫，见下）
    for n in uploads: backend.PutTextAsync(key(n), Serialize(n))
    记录 lastSyncAt / 轮次日志（listed=N, downloaded=…, uploaded=…, errors=…）
finally: 释放单飞
```

关键性质：

- **幂等**：任意一轮中途断网/崩溃，下一轮全量对账自动收敛，不需要任何恢复逻辑。
- **防乒乓**：合并结果与远端一致就不 PUT。没有这条，两台设备会永远互相覆盖上传。
- **上行逐个 PUT，整文件覆盖**：每次上传写入**完整**便签 JSON（无增量/补丁语义）；失败即整轮终止，本轮已上传的文件有效，下轮续传其余——按 `UpdatedAt` 比较天然只补差量。
- **全部下载失败必须中止本轮**（正确性关键）：若列表非空但每个对象的 GET 都因网络/认证失败，本轮必须抛错终止，**绝不允许**把这些失败当作"远端为空"继续对账——否则本地会把远端全部当作"缺失"重新灌回，等效于一次误覆盖式清空重建。部分失败（至少一个对象成功）则跳过失败对象、本轮继续。
- **两类"跳过"语义不同**：网络/认证失败（下载异常）与数据类跳过（坏 JSON、陌生文件、超限、schemaVersion 过高）必须分开计数与记录——前者可能意味着后端不可用，后者永远不该影响整轮成败。
- **时间戳不回填、不对齐**：业务内容相同但时间戳不同（例如"改了又改回"）→ 双方都**不传输**，时间戳保持各端现状。时间戳是裁决输入而非需要同步一致的数据；任何"顺手把时间戳改成一致"的实现都会制造无意义的写放大甚至乒乓。
- **单飞语义**：同一时刻只允许一轮运行；并发触发**直接放弃**（立即返回"跳过"），不排队、不等待、不合并结果。触发器无需去重，全部汇入引擎由单飞闸兜底。
- **下行守卫**（吸收自评审输入的 `local_seq` 思想，适配全量对账）：对账在"读本地快照 → 下载解析 → 写库"之间存在秒级窗口，若用户恰好在此窗口编辑了将被远端覆盖的便签，无条件写库会把本地新编辑连带冲掉（双端同时丢改）。因此下行写入用存储方法 `ApplyRemoteBatchAsync`：单事务内逐条处理——本地无此行 → **强制**按几何默认落点、`IsOpen=false` 插入（不信任调用方携带的几何/打开状态）；本地存在且当前 `UpdatedAt` 与快照值**时刻相等**（比较时间点，不是字符串）→ 覆盖业务字段（content/color/isPinnedInList/alwaysOnTop/isDeleted/updatedAt），**窗口几何与 IsOpen 保持本地现状**；时刻不等（快照后被本地编辑）→ 跳过并记日志，下一轮对账自动收敛。上行方向无此问题——本地是该方向的新的一方，窗口内的新编辑下一轮重传即可自愈。
- **为什么不需要 dirty / remote_version 本地状态**（对比评审输入方案 B 的 P0 模型改造）：全量对账每轮从"本地库 + 远端列表"两个真值现场重新推导差量，不维护任何"上次同步到哪"的状态，因此不存在"推送成功却未清 dirty"一类状态机 bug，本地表结构零迁移。
- 请求级超时 30s；失败放弃本轮，等下一次触发，不做应用层重试退避（触发器天然限频）。

### 4.2 删除与墓碑（本协议唯一真正的坑）

- 删除 = `IsDeleted=true` + bump `UpdatedAt`（现有 [`ArchiveNoteAsync`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Data/NoteRepository.cs#L219-L226) 语义），上传墓碑文件；**远端文件永不物理删除**。
- 本地「清空回收站」（`HardDeleteAsync`，删除所有 `IsDeleted=1` 行）**不动远端**。远端墓碑会在下轮对账中以 `IsDeleted=1` 行回流本地——不可见、无害、幂等，不需要"已硬删 ID"之类的额外记账。
- 删除与编辑的冲突无需特判——单记录单时间戳的 LWW 天然给出正确结果：

  | 场景 | LWW 结果 |
  |---|---|
  | A 删除于 T5，B 编辑于 T6 > T5 | B 的编辑胜出，便签存活（"编辑胜过删除"） |
  | A 删除于 T5，B 编辑于 T4 < T5 | 墓碑胜出，便签保持删除 |

- **墓碑 GC：v1 不实现**。按 500 条便签 × 2KB 估算，十年墓碑也不足 10MB，远小于其实现风险。预留规则（未来可加）：`IsDeleted && UpdatedAt < UtcNow - 180d` 的记录本地删行 + `backend.DeleteAsync(key)`；180 天窗口保证长期离线设备不会复活便签。

### 4.3 已知限制与缓解

| 限制 | 缓解 |
|---|---|
| 时钟偏差大的设备其 LWW 裁决可能"选错版本"（丢一侧编辑） | 已拍板接受（评审决议 3：直接用最新时间戳）。缓解：依赖 OS 时间同步；若日后需要，可加「冲突时保留副本」（败方另存 `<uuid>.conflict-<deviceId>.json`，下轮作为新便签回流） |
| 两台设备各自新建 → UUID 不同 → 内容相近的两条便签并存 | 属预期行为，用户手动合并；不做内容指纹去重 |
| 坚果云免费版有月度流量配额 | 见 §7.2 流量测算与 mtime 跳过缓存 |

---

## 五、两个存储后端实现

### 5.1 WebDavBackend

| 接口语义 | HTTP 操作 |
|---|---|
| `ListAsync` | `PROPFIND <root>/notes/`，`Depth: 1`，解析 207 multistatus |
| `GetTextAsync` | `GET <root>/notes/<key>`，404 → null |
| `PutTextAsync` | `PUT <root>/notes/<key>`，`Content-Type: application/json` |
| `DeleteAsync` | `DELETE <root>/notes/<key>`，404 → 成功 |
| `TestAsync` | `PROPFIND <root>/ Depth: 0` |

**wire 细节**（实现方逐条对齐）：

- 请求体（List/Test 的 PROPFIND 均使用）：
  `<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:getlastmodified/><d:getcontentlength/><d:resourcetype/></d:prop></d:propfind>`，请求头带 `Depth`。
- **multistatus 解析规则**：对每个 `response` 元素取 `href`；href 做 URL 解码后以 `/` 结尾的是集合项，跳过；文件名取 href 最后一段。属性读取按**元素 LocalName 匹配**（`getlastmodified`/`getcontentlength`），不依赖服务器的前缀写法（有的用 `<D:`、有的无前缀）。`getlastmodified` 优先按 RFC 1123（`R` 格式）解析，解析失败回落通用 `DateTimeOffset` 解析。LastModified 仅作展示/缓存，**绝不参与合并裁决**（裁决只看文件内 `updatedAt`）。
- 认证：`Authorization: Basic base64(user:pass)`（UTF-8）；凭据为服务商的应用专用密码。401/403 必须映射为用户可读的"认证失败/权限不足"错误文案。
- URL 拼装：`<root>` 规范化为以 `/` 结尾；key 逐段 `URI.EscapeDataString` 后拼接。
- 依赖：仅 `HttpClient` + XML 解析，零第三方包。
- 兼容目标：坚果云、Nextcloud、Alist、Apache `mod_dav`、`rclone serve webdav`。**nginx 原生 dav_module 对 PROPFIND 支持不完整，不列为支持目标**（吸收自评审输入的兼容性清单）。本协议不使用 LOCK / If-Match，无需 WebDAV 能力探测；遇不支持的方法即报错放弃本轮。
- 仅在用户显式选择"信任明文 HTTP（内网 NAS）"时允许 `http://`，设置页给出警告。

### 5.2 S3Backend（Cloudflare R2）

| 接口语义 | S3 API |
|---|---|
| `ListAsync` | `ListObjectsV2?prefix=notes/&max-keys=1000`（处理 continuation-token 分页），取 `LastModified` / `Size` |
| `GetTextAsync` | `GetObject` |
| `PutTextAsync` | `PutObject`（幂等覆盖） |
| `DeleteAsync` | `DeleteObject` |
| `TestAsync` | `HeadBucket` 或空 prefix `ListObjectsV2&max-keys=1` |

- R2 端点：`https://<ACCOUNT_ID>.r2.cloudflarestorage.com`，path-style（`/<bucket>/<BasePrefix>/notes/<key>`），签名 region 固定 `auto`，AWS SigV4（含 `x-amz-content-sha256` 头）。凭据为 R2 API Token（建议只授予目标前缀/桶读写权限）。**BasePrefix 必填**（如 `stickynotes/`，评审决议 4）：`ListAsync` 以 `BasePrefix + "notes/"` 过滤，复用桶时与其他数据天然隔离。
- **前缀规范化**：BasePrefix 去除首尾 `/` 后追加单个尾部 `/`（`/stickynotes` → `stickynotes/`）；空串表示桶根。桶名两端去空白。

**SigV4 签名规范**（参考实现已通过 AWS 官方文档测试向量验证；Android 端可复用任意 SigV4 库，但签名形态必须一致）：

- 参与签名的头**固定**为：`host`、`x-amz-content-sha256`、`x-amz-date`；SignedHeaders 串恒为 `host;x-amz-content-sha256;x-amz-date`。
- `x-amz-date` 格式 `yyyyMMdd'T'HHmmss'Z'`（UTC）；scope = `<dateStamp>/auto/s3/aws4_request`。
- payload hash = `SHA256(UTF-8 请求体)`；无 body 请求（GET/DELETE）使用空串哈希常量 `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`。
- 规范 URI：路径逐段 RFC 3986 转义（保留 `A-Za-z0-9-_.~`），分隔符 `/` 不转义（如 `test$file.text` → `test%24file.text`）。
- 规范查询串：参数按 key 再按 value 的字典序排序，key/value 先解码再按 RFC 3986 重编码，**空值参数保留 `key=` 形态**（不可省略 `=`）。
- 规范请求 = `METHOD\n规范URI\n规范查询\n规范头(每行 name:value\n)\nSignedHeaders\npayloadHash`；规范头块末行自带换行后再接一个空行，再是 SignedHeaders 行。
- 错误处理：非 2xx 时解析响应体 `<Error><Code>/<Message>` 生成可读错误（如 `NoSuchKey`、`InvalidAccessKeyId`）；GET 的 404/NoSuchKey → null，DELETE 的 404 → 成功。
- `ListObjectsV2` 参数：`list-type=2`、`max-keys=1000`、`prefix=<BasePrefix>notes/`；`IsTruncated=true` 时携带 `continuation-token` 翻页直至取完。`PUT` 的 `Content-Type: application/json`。
- **依赖决策：手写最小 SigV4 签名器（约 150 行，HMAC-SHA256 即可，纯确定性、可单测）**，不引入 AWSSDK.S3（数 MB，与项目 Fody/Costura 单文件打包目标冲突）。签名器只实现本次用到的 4 个动词 + `ListObjectsV2` 查询串签名。
- 该实现天然兼容任意 S3 兼容存储（MinIO、Backblaze B2 等），配置里 endpoint/bucket 均可改。

### 5.3 后端通用约束

- 每请求超时 30s；`HttpClient` 复用单例。
- 凭据存储在 `settings.json`；**密码/SecretKey 用 DPAPI（`ProtectedData`）加密后落盘**（当前机器/用户域），明文不落盘。Android 端对应 EncryptedSharedPreferences。
- 单配置档案（一套后端一组凭据），v1 不做多档案。
- 同步日志只记 key、字节数与数量摘要，**不得记录便签正文**（隐私底线，吸收自评审输入）。

---

## 六、同步触发与设置页 UX

**触发时机**（全部走单飞的同一入口 `SyncNowAsync(reason)`）：

1. 启动完成后延迟 10s；
2. 便签新增/内容修改后防抖 **5s**（挂自动保存事件，新建即首次保存，天然覆盖，评审决议 1）；
3. 便签删除（归档）/恢复后防抖 **2s**（墓碑尽快传播）；
4. 后台定时，默认 **15 分钟**（可配 5–120，越界回 15，评审决议 1）；
5. 设置页「立即同步」按钮（同步中置忙禁用；引擎单飞兜底）；
6. 网络恢复事件（延迟 3s）。

**触发合并语义**：防抖期内的新触发取消并重置旧计时（不是叠加排队）；所有触发源无需相互感知，漏触发由定时轮询兜底。

**设置界面**（决议 6：独立窗口，主设置页只留入口行避免页面过长）：

- 主设置页「网络同步」入口行：状态摘要（最近成功时间/异常提示）+「配置…」按钮打开独立同步设置窗口（单例管理，重复打开仅激活）；
- 独立同步设置窗口包含：启用开关；后端类型（WebDAV / Cloudflare R2）；WebDAV：服务器地址、用户名、密码；R2：Endpoint、Bucket、BasePrefix、AccessKey、SecretKey；「测试连接」（调 `TestAsync`）；同步间隔；状态行：最近成功同步时间 / 上次错误摘要（认证失败/断网只影响同步，**绝不影响本地使用、绝不触发本地数据清理**）；设备标识展示。

配置字段并入现有 `settings.json`（沿用其 `.bak` 恢复与损坏防护机制），同步的**运行时状态**（`lastSyncAt`、`lastError`、mtime 缓存）单独存数据目录 `sync_state.json`，不与用户设置混淆。

---

## 七、流量预算与性能优化

### 7.1 测算（按 300 条便签 × 2KB ≈ 600KB 全量、后台 15 分钟一轮 ≈ 96 轮/天）

| 后端 | 每轮成本 | 每月成本（单设备） | 配额评估 |
|---|---|---|---|
| R2（全量 GET） | 1 LIST + ~300 GET + 少量 PUT | Class B ≈ 86 万次/月；Class A 微量 | R2 免费 Class B 1000 万/月 → **放心全量** |
| 坚果云（全量 GET） | 下载 600KB | 下行 ≈ 56MB/月 | 免费版下行 3GB/月 → 占 ~2%，**可接受**；便签数或频率增大后需 §7.2 |

### 7.2 mtime 跳过缓存（Phase 1.5，协议不变的可选优化）

- 本地 `sync_state.json` 维护 `key → 服务器 LastModified 原值`；`ListAsync` 后，仅对 `LastModified > 缓存值` 的对象执行 GET，下载后更新缓存。
- **正确性不依赖缓存**：服务器覆盖写必然使 mtime 单调前进，缓存只是"跳过必然没变的下载"；缓存丢失/损坏 → 退化为全量 GET，自动重建。比较必须使用服务器返回的 mtime 原值，本地时钟不参与。
- v1 可先不做（上表测算在当前量级安全），做成 `SyncEngine` 内的一个可选开关即可。

### 7.3 边界情况（无状态对账的自愈能力）

| 场景 | 行为 |
|---|---|
| 首次启用同步，两端各有数据 | UUID 不同 → 合集，互不覆盖 |
| 远端被整桶清空 / 桶重建 / 对象误删 | 远端列表为空 → 本地全部按"新增"重新灌回，自动恢复远端；**无需 epoch / cursor_ahead / 重置协议**（无状态对账的免费红利，对比评审输入方案 B 需要专门的 epoch 机制） |
| 令牌/密码失效 | 轮次报"认证失败"，本地功能不受影响，不清理任何本地数据 |
| 同步中用户继续编辑 | 上行方向下一轮重传；下行方向由条件更新守卫保护（§4.1） |

---

## 八、可靠性清单（实现验收标准，逐条可测）

1. 单飞：并发触发第二轮直接放弃，不排队不叠加。
2. 幂等：任意轮中断（列表后断网 / 下载一半 / 上传一半 / 进程崩溃），下一轮自动收敛，无恢复代码。
3. 坏文件隔离：`notes/` 下出现非法 JSON / id 与文件名不符 / `schemaVersion` 更高 → 跳过 + 日志，整轮继续。
4. **全部下载失败必须中止本轮**（不得当作"远端为空"继续对账）；部分失败跳过失败对象继续。
5. 不覆盖本地较新：`>=` LWW 规则与导入服务一致（相等且内容不同 → 本地胜）。
6. 防乒乓：业务内容无差异不 PUT；无差异不重复写本地库。
7. **时间戳不回填**：内容相同、时间戳不同 → 双方零传输。
8. 删除传播：A 删 → B 收到墓碑；A 清空回收站后，B 的墓碑回流不复活、不可见。
9. 时间戳全链路 UTC ISO-8601；下行的 `UpdatedAt` 原样入库（不落服务器时间/本地时间）。
10. 轮次摘要日志：listed / downloaded / uploaded / skipped / guarded（两类跳过分开计数）。
11. 陌生文件只忽略不清理（向前兼容）。
12. 凭据不以明文出现在 settings.json 与日志中。
13. 换行规范化：`content` 在同步 DTO 边界统一为 `\n`，内容比对发生在规范化之后（否则防乒乓失效）。
14. 下行条件更新守卫生效：对账窗口内的本地编辑不被远端覆盖；同步插入强制默认几何与 `IsOpen=false`。
15. 同步日志不包含便签正文。

---

## 九、Android 端实现要点（前瞻，本期不实现）

- 存储：Room/SQLDelight，字段与 schema v1 一致；`kotlinx.serialization` 按本协议写死（ISO-8601 UTC、字符串枚举、camelCase）。
- 后端：WebDAV 用 OkHttp 手写 4 个动词或 sardine-android；S3 用官方 `aws-sdk-android` v4 签名或手写。实现同一个 `IStorageBackend` 四语义接口。
- 合并：同一套 LWW + 墓碑规则；`alwaysOnTop` 忽略，`isPinnedInList` 可映射为列表置顶。
- 同步触发：前台拉取 + WorkManager 定时（15 分钟）。
- **共享协议文档而非共享代码**：本文档 §3/§4 即 Android 端的实现规格。

## 十、自建服务器演进路径（当前不做）

1. **路线 A（推荐）**：需要自建时直接挂标准 WebDAV（nginx dav / `rclone serve webdav` / Alist），客户端零改动。
2. **路线 B**：写一个 REST 网关，实现与 `IStorageBackend` 相同的 4 个语义（list/get/put/delete），作为第三个 backend 接入，合并引擎零改动。

---

## 十一、实施计划

| 阶段 | 内容 | 验收 |
|---|---|---|
| **Phase 1** ✅ 已完成（2026-10-04） | `IStorageBackend` + `WebDavBackend` + `SyncEngine` + `ApplyRemoteBatchAsync` 仓储方法 + 同步设置 UI + 触发器接线 | §8 清单 1–13 的单元测试全绿（`FakeStorageBackend` 内存实现 + 测试仓储，镜像 `NoteRepositoryTests` 风格）；另通过本机 webdav.exe 真实 HTTP 双设备全链路集成用例 |
| **Phase 1.5（可选）** | mtime 跳过缓存 | 缓存丢失退化为全量；mtime 比较单测 |
| **Phase 2** ✅ 已完成（2026-10-04） | `S3Backend`（R2，手写 SigV4） | 签名器 AWS 官方向量单测通过 + 真实桶集成冒烟通过（测试连接/同步/往返/清理，环境变量门控） |
| **Phase 3** | Android 端（独立项目） | 与桌面端互相同步：新建/编辑/删除/清空回收站全场景 |

**测试计划要点**：`FakeStorageBackend`（内存字典 + 可注入故障：超时/断网/坏文件/串改 mtime）驱动 `SyncEngine` 的全部合并规则用例；真实网络路径不进 CI，手动清单验证（坚果云 + R2 各一轮）。吸收自评审输入的两项增强：

1. **语言无关 JSON 测试向量**：合并规则用例以 `tests/SyncVectors/*.json` 表达（local/remote 快照 + 期望结果），.NET 引擎现在跑、将来 Kotlin 引擎跑同一份向量，保证两端行为一致（覆盖 §4 的每一行裁决与 §8 的每个场景）。
2. **收敛性仿真**：两个 `SyncEngine` 实例共享同一个 `FakeStorageBackend`，随机交替执行 编辑/删除/清空回收站/同步，断言有限轮内两端收敛到相同状态。

### 11.1 参考实现与可执行规格（Android / 第三方实现者必读）

- **参考实现**：`src/StickyNotes/Sync/`（协议序列化、两个后端、引擎、凭据保护、状态存储）；触发与 UI 接线在 `src/StickyNotes/Services/SyncHost.cs`。
- **可执行规格**：`tests/StickyNotes.Tests/Sync/` 下的用例是协议语义的权威表达——`SyncEngineTests`（LWW 四分支、墓碑传播、硬删不复活、删除 vs 编辑、坏文件隔离、全失败中止/部分失败继续、单飞、换行规范化、双设备收敛）、`SyncProtocolTests`（wire 格式、防御解析、下行守卫）、`SigV4SignerTests`（AWS 官方向量）、`SyncWebDavIntegrationTests`（真实服务器全链路）。第三方实现以这些用例的**语义**为准，而非代码结构。
- **JSON 向量的落地时机**：语言无关向量机制在 Phase 3 启动时落地（把 §8 清单逐条转成向量供两端互验）；在此之前上述 C# 用例即唯一可执行规格。
- **文档与实现的冲突裁决**：本文档如与参考实现/测试出现不一致，以实现与测试为准，并回改本文档（协议的权威定义 = 本文档 + 测试语义的交集）。

---

## 十二、评审决议（2026-10-04）

| # | 问题 | 决议 |
|---|---|---|
| 1 | 后台默认间隔 | 15 分钟可配；便签新增/修改后防抖 5s 再触发一轮同步（§6） |
| 2 | `isPinnedInList` / `alwaysOnTop` 跨设备 | 协议始终同步；客户端自行决定是否采用（Android 忽略 `alwaysOnTop`） |
| 3 | 冲突副本 | 不做，统一按 `UpdatedAt` 时间戳 LWW 裁决（§4.1/§4.2） |
| 4 | S3/R2 前缀 | 无论是否专用桶，一律必填 BasePrefix 子目录（§5.2） |
| 5 | `deviceId` 生成规则 | 平台前缀 + 6 位随机串（如 `win-8xf7ad`），首启生成后存 `settings.json`（§3.2） |
| 6 | 同步设置界面形态（2026-10-04 追加） | 独立同步设置窗口；主设置页只留状态入口行，避免设置页过长（§6） |

**评审输入取舍记录**（[`temp/docs/StickyNotes-Sync-Architecture-20261004.md`](file:///c:/Home/Projects/StickyNotes/temp/docs/StickyNotes-Sync-Architecture-20261004.md)）：

- **已吸收**：换行规范化（`\n` 强制，§3.2）；下行写守卫（`local_seq` 思想适配为条件更新，§4.1）；语言无关 JSON 测试向量（§11）；双引擎收敛性仿真（§11）；WebDAV 兼容性提示（nginx dav_module 不支持目标，§5.1）；网络恢复触发（§6）；"认证失败不影响本地使用"原则（§6/§7.3）；日志不记正文（§5.3）；单目录分片逃生舱与路径版本化演进规则（§3.1）。
- **未采纳**（与"不自建服务、最简协议"的项目约束或评审决议冲突）：自建 HTTP 服务器主方案；rev/ETag 条件写（If-Match/412）替代时间戳 LWW；cursor 增量拉取；本地 dirty/remote_version 字段改造（全量对账下无此必要，理由见 §4.1）；WebDAV 能力探测（协议不使用条件写，无需探测）。
- **列为远期可选项**：三方合并（base/local/remote 行级合并）、端到端加密、SSE 推送。
