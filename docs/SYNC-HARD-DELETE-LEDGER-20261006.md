# 同步硬删除台账（Hard-Delete Ledger）实施摘要

> **生成时间**：2026-10-06 11:15 (GMT+8)
> **背景**：代码审查报告 `APP-CODE-REVIEW-20261006-0951.md` 缺陷 P2-1 的修复。
> **受众**：StickyNotes 移动端（`C:\Home\Projects\StickyNotesApp`）实现同一同步协议时的对齐参考。
> **桌面端基线**：v1.3.0 @ P2-1 修复提交（2026-10-06）。
> **协议影响**：**零**。同步协议仍为 v1，wire 格式、DTO、对账流程不变，新旧客户端完全互通。台账是纯本地产物，**不参与同步**。

---

## 1. 问题（P2-1）

同步引擎的全量对账规则是「本地不存在的 id + 远端存在 → 判为远端新便签，下行插入」。而「彻底删除 / 清空归档」只在本地物理 `DELETE` 行，对云端没有副作用。后果：

1. 本机彻底删除后，云端残留对象（活动版本或墓碑）在下一轮被判为「远端新便签」插回本地——回收站（归档）内容反复回填，永远清不空；
2. 若云端仍是删除前的活动版本，回流时 `IsDeleted = false`，便签以**活动状态**复活并出现在桌面。

## 2. 方案一句话

本地持久化一份「我彻底删除过哪些便签、何时删的」台账；对账时对台账内的 id **否决下行回流**，并在云端版本不新于删除时刻时**主动推送墓碑覆盖云端**，从根上止住回流。

不采用云端删除命令（`DeleteAsync` 仍不启用）、不引入墓碑 GC 协议、不改 wire 格式。

## 3. 台账存储规范

| 项 | 桌面端取值 | 说明 |
|:---|:---|:---|
| 文件名 | `hard_deleted.json` | 移动端可自定，**台账纯本地、永不同步**，存储方式任选 |
| 位置 | 应用数据目录（与 `notes.db`、`settings.json` 同级） | — |
| 格式 | `{"<uuid>": "<UTC ISO-8601 删除时刻>", ...}` | Guid 小写 D 格式；时间必须 UTC |
| 写入 | tmp + 原子替换 | 防止进程中断留下半个文件 |
| 损坏处理 | 降级为空台账并记日志 | 最坏结果：回收站回填一次，可再清一次；**绝不能**因此阻断启动/同步 |
| GC | 加载时清理超过 **365 天**的条目 | 极老条目清理后最坏回填一次 |

桌面端实现：`Services/HardDeleteLedger.cs`（锁内惰性加载、单写者、约 130 行）。

**不进备份**：桌面端每日 DB 冷备份（VACUUM INTO）不含此文件，属已知取舍（丢失后果见上）。

## 4. 记录时机（必须全覆盖）

每一个**物理 DELETE** 的入口都要记录 `now()`（UTC）：

1. 彻底删除单条便签（回收站内操作）→ 记录该 id；
2. 清空全部归档 → **先查出待删 id 集合再删除**，整批记录。

只删除本地行、不影响其他删除入口。记录动作在删除成功后同步完成（先于任何同步轮），因此进程随时退出都不会漏记。

## 5. 对账裁决规则（核心，移动端必须语义一致）

在现有全量对账循环中，`本地无此行 && 远端有此行` 分支前置一次台账检查：

```text
hardDeleted = load_ledger()          // id -> deletedAt(UTC)

for id in union(local_ids, remote_ids):
    if id in incomplete_ids:         // P0-1 隔离：读取失败/无效的对象本轮跳过
        continue
    local = local_snapshot[id]       // 可能为 null
    remote = remote_objects[id]      // 可能为 null

    if local == null and remote != null:
        if id in hardDeleted:
            deletedAt = hardDeleted[id]

            if remote.updatedAt <= deletedAt:
                // 云端版本不新于本机删除时刻 → 推墓碑覆盖云端，止住回流
                tombstone = copy(remote)
                tombstone.isDeleted = true
                tombstone.updatedAt = deletedAt
                if !businessEquals(tombstone, remote):   // 幂等：已是同内容墓碑则零传输
                    upload(tombstone)
            else:
                // 云端在删除后被其他设备编辑过 → 编辑胜过删除（与 LWW 一致），
                // 但台账仍否决下行：不把已彻底删除的便签插回本机
                skip_download()   // 记日志
            continue              // 两个分支都绝不 download

        download(remote)          // 正常的远端新便签路径，行为不变
```

要点：

- **台账只否决「本地无行」的回流**。若该 id 后来又被本地重建（如 JSON 导入），对账走正常的双边 LWW 分支，台账不干预；
- 推送的墓碑**保留云端现有正文**（只翻转 `isDeleted`、改写 `updatedAt`），与软删除墓碑语义完全一致，其他设备收到后照常收敛为不可见行；
- 幂等性由既有 `businessEquals`（比内容与标志位、不比时间戳）保证：推送一轮后云端即收敛，后续轮次零传输，**不会乒乓**；
- 时间比较全部基于 UTC；`updatedAt <= deletedAt` 用 `<=`（相等也算「不新于删除」）。

## 6. 桌面端改动清单（对照用）

| 文件 | 改动 |
|:---|:---|
| `Services/HardDeleteLedger.cs` | 新增：台账加载/记录/原子落盘/365 天 GC |
| `Data/NoteRepository.cs` | `HardDeleteAsync`、`ClearAllArchivedAsync` 删除成功后记录台账 |
| `Sync/SyncEngine.cs` | 对账 `localNote == null && remoteDto != null` 分支前置台账否决 + 推墓碑 |
| `App.xaml.cs` | 台账注册为 DI 单例，仓储与引擎注入 |

## 7. 桌面端测试（移动端可对照设计用例）

| 用例 | 断言 |
|:---|:---|
| 硬删除后推送墓碑且幂等 | 首轮 `uploaded=1`、云端对象 `isDeleted:true`；次轮零传输 |
| 双设备各自硬删除 | 回收站不再回填，本地无行、无上传、无下载 |
| 删除后远端编辑 | 云端保留远端编辑内容；删除方本机不回流（`GetById == null`） |
| 无台账（旧路径回归） | 墓碑照常以不可见行回流——老客户端行为不变 |

## 8. 已知边界（有意为之）

1. **删除后被其他设备编辑**：编辑胜过删除（与既有 LWW 语义一致），但删除方本机不会复活；若希望「删除绝对不可逆」，在台账里把比较改为无条件推墓碑即可，代价是压掉其他设备的合法编辑；
2. 台账文件丢失/损坏 → 一次性回填，再清一次即可；
3. 台账条目只增不减（有 365 天 GC），单条约 60 字节，硬删除是低频操作，存储可忽略；
4. 老版本客户端（无台账）仍会看到墓碑回流到它的回收站——无害且幂等，不受本次改动影响。
