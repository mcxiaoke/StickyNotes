# StickyNotes 项目审查报告 · 最终合并版

> **文档版本**：v1.0.0（合并版 / 唯一权威版）
> **生成时间**：2026-10-03 15:25 (GMT+8)
> **审查对象**：StickyNotes v1.1.2 @ commit `94ed507`（工作区干净）
> **合并来源**：4 份独立审查报告 —— 详见 §1 来源清单
> **基线复验**：`dotnet build -c Release` → **0 错误 0 警告**；`dotnet test` → **71/71 通过**（11s，SDK 10.0.400）；4 组新增探针实证（用后已清理，`git status` 干净）
> **修复状态（2026-10-03 更新）**：本报告 **2 个 P0 与 6 个 P1 已完成修复并通过回归验证**；**6 个 P1 部分完成**（其中 F-P1-2 / F-P1-7 / F-P1-8 已于当日深夜补齐修复，见上方「追加修复」）；**1 项建议经实测证伪已撤销**；其余 P2/P3 未修。修复详情见 `docs/CHANGES-20261003.md`，代码提交 `67471c0` → `9e15876`。**完整状态见文末《修复状态总览》与 §12.3。**
>
> **追加修复（2026-10-03 晚间，提交 `f65c12b` → 本批次，共 5 批）**：按"低风险 × 高收益"原则继续处理 §11.5 的未修项 ——
> ① **F-P1-1 子项 1**：PIN 锁定态 `Ctrl+N`/`Ctrl+F` 绕过（**已修**，新增回归用例）；
> ② 工程化：`.editorconfig`、`TreatWarningsAsErrors`、构建可复现（`Version` 去时间戳）、`global.json`、`README.md`、`.gitattributes`；
> ③ 日志 30 天保留期清理、设置项依赖联动、只读转换器 `ConvertBack`、DI 隐式实例、死代码清理、`LiveRegion`；
> ④ 交互与体验：`SelectAll` 吞输入、搜索卡片"按下即跳转"、右键先选中、搜索摘要二次截断、默认尺寸四源归一、第二实例退出码；
> ⑤ 文档回写：`APP-ARCHITECTURE.md` 与 `APP-PRODUCT.md` 的漂移点已逐条更正并加"历史设计文档"警示。
> **未做**：GitHub Actions/CI（用户明确排除）、`Directory.Packages.props`、Costura 单 exe 真机验证。
> **结论更正 1 项**：F-P3-19 建议的"字号按便签记忆"与设置页"全局字号、即时生效"的产品契约冲突，**改为保留既有行为 + 注释固化 + 回归测试**（详见变更记录批次 C）。详见文末 §11.7。
>
> **追加修复（2026-10-03 深夜，提交 `f9744e2` → `9e15876`，共 4 批）**：按 §12.3 的未做清单，继续处理「低风险 × 高收益」项 ——
> ① **F-P1-2** 导入丢三态 + 幽灵便签（DTO 补 `IsPinnedInList`/`AlwaysOnTop`/`IsOpen`，新增 `NotesReloadedRequestedMessage` 全量重载）；
> ② **F-P1-7** `AutoSaveCoordinator` CTS 使用中 `Dispose`（所有权单一化，只 `Cancel` 不 `Dispose`）；
> ③ **F-P1-8** 托盘四宗毛病（`TaskbarCreated` / `NIM_SETVERSION` / `WM_CANCELMODE` / 去 `HWND_BROADCAST`）；
> ④ **F-P2-22** Trigger 内联 `DropShadowEffect` 全部改为引用固定单例令牌。
> 详见 §12.3 更新表与 `docs/CHANGES-20261003.md`。**测试 83 → 94 项全绿**。
>
> **一句话结论（原始基线结论，已被上方修复状态部分取代）**：**架构与代码组织水平显著高于同类个人桌面项目，但存在 2 个必须立即修复的 P0 缺陷——「正常退出后桌面布局全丢」与「空白便签正文被占位文案污染并可落库」，二者均已端到端复现。修复这 2 项后，本项目即可从「能演示」跨入「可交付」；其余 30 余项为质量、性能与体验改进项，不阻塞交付。**

---

## 1. 本报告的定位与合并方法

### 1.1 为什么需要合并

本项目在 2026-10-02 至 10-03 期间由四次独立审查产出了四份报告，覆盖范围重叠、编号体系互不相同（P0-1 在四份报告里指代了三个不同缺陷）、结论存在**直接冲突**（搜索性能一项，两份报告结论相反），并且部分结论**已经过时**（所依据的代码在修复后未回写报告）。

### 1.2 来源清单与可信度评估

| # | 报告 | 日期 | 篇幅 | 定位与可信度 |
|:--:|:---|:---|:---|:---|
| R1 | `temp/docs/APP-REVIEW-osbf.md` | 10-02 10:45 | 1,001 行 | 第一轮全量评审。**基线为 `909a00c`，比当前落后 9 个提交**。其 P0/P1 结论质量高（后续修复基本照此执行），但 §5「功能完整度 3.0/10」与 §6.2「键盘/虚拟化」已**完全过时** |
| R2 | `temp/docs/CODE-REVIEW-20261003-1501-zcglmf.md` | 10-03 15:01 | 146 行 | 第二轮。**发现质量最高的一份**：唯一识别出 PIN 快捷键绕过、`settings.json` 非原子写、HotKey 注册失败仍置位、迁移 catch 掩盖等深层缺陷。篇幅紧凑、定位精准，几乎无冗余 |
| R3 | `temp/docs/APP-CODE-REVIEW-20261003-1514.md` | 10-03 15:14 | 603 行 | 第二轮。**实证投入最大**（5 组探针工程），独立发现了「空白便签正文污染」与「导入幽灵便签」两个 R1/R2 都漏掉的数据完整性缺陷。缺点是个别结论存在夸大与偏差（见 §3） |
| R4 | `temp/docs/APP-REVIEW-20261003-151421-stdsf.md` | 10-03 15:14 | 659 行 | 第二轮（本人产出）。首个用**真实 `SqliteDatabaseContext` + 真实 WPF 生命周期端到端复现** P0-1 的报告。缺点是搜索性能一项定级过高（已被本报告纠正，见 §3.1） |

**合并原则**：

1. **冲突以实证为准**。凡两份报告结论相反者，本次全部重新实测仲裁，不以"多数票"或"报告权威性"决定。
2. **过时结论一律剔除**，并在 §3 明确记录，避免后续维护者按失效结论重复施工。
3. **编号统一重排**为 `F-P0-x` / `F-P1-x` / `F-P2-x`，并保留到三份原始报告的**双向交叉引用**（§7 附录），历史讨论可通过映射表追溯。
4. **不重复已修复项**。R1 列出的托盘、全局热键、开机自启、导入 `UpdatedAt` 冲突裁决、`VACUUM INTO` 备份、Release 日志等，在 `94ed507` 已实现且实现质量良好，本报告仅在「值得肯定」中记录，不再列为问题。

### 1.3 本次新增实证（前四份报告均未完成的部分）

| 探针 | 目的 | 结论 |
|:---|:---|:---|
| **A. 搜索性能规模曲线** | 仲裁 R4 与 R3 的性能冲突 | 产品文档规模下达标（5.3ms）；重内容规模下退化（最高 142.8ms）。**R4 的定级过重，R3 的"无需优化"结论不完整**，详见 §3.1 |
| **B. 空白便签污染链路** | 用真实仓储 + 真实 VM 验证 R3 P0-2 | **缺陷成立**：占位文案「（空白便签）」经「失焦刷盘 → 列表实体污染 → 切换置顶落库」写入数据库 |
| **C. sync-over-async 死锁复现** | 仲裁 R2 P1-7 / R3 P1-4 的「经典死锁模式」判定 | **未复现死锁**。在 STA/UI 线程以相同模式连续调用 `SaveAsync`/`GetAllActiveAsync`/`UpdateWindowBoundsAsync` 均正常返回。真实风险是**阻塞延迟**（最坏 `DefaultTimeout=5s` × N 次），而非死锁 |
| **D. 主窗口 `Closing` 取消是否阻断托盘退出** | 排除常见误判（R3 P4 同向验证） | **不阻断**，`Shutdown()` 正常完成、`Exit` 触发、残留窗口 0。**该项不是缺陷** |

---

## 2. 统一缺陷清单（合并去重后共 63 项）

### 2.0 统计总览

| 等级 | 数量 | 说明 |
|:---|:--:|:---|
| **P0 阻断** | **2** | 数据可靠性，必须立即修复，修复后可交付 |
| **P1 严重** | 12 | 安全、可靠性、生命周期，建议 1~2 周内完成 |
| **P2 中等** | 29 | 性能、架构一致性、工程质量 |
| **P3 轻微** | 20 | 体验细节、代码整洁、文档回写 |

**修复状态标记图例**（2026-10-03 实施后逐个复核标注）：

| 标记 | 含义 |
|:--|:---|
| ✅(已修) | 本轮**已完整修复**并通过回归验证 |
| ⚠️(部分) | 本轮**部分修复**，仍有明确剩余项（见文末 §11.2） |
| ❌(未修) | 2026-10-03 复核确认**缺陷仍在**，本轮未处理 |
| ⛔(撤销) | 原建议**经实测证伪**，不应施工（见文末 §11.4） |

**修复后进展**：P0 **2/2 已修**；P1 **9 项已修 + 3 项部分完成**（F-P1-2 / F-P1-7 / F-P1-8 于当日深夜补齐）；P2 附带完成 7 项（另 1 项撤销）；P3 完成 3 项。**总计 27 项得到处理**，详见文末《修复状态总览》与 §12.3。

| 追击批次 | 提交 | 覆盖项 |
|:--|:--|:--|
| F~I（2026-10-03 深夜） | `f9744e2` → `9e15876` | **F-P1-2**、**F-P1-7**、**F-P1-8**、**F-P2-22** |

> 去重说明：四份原始报告累计列出约 120 条问题项，合并同类后为 **63 项**（2 + 12 + 29 + 20）。典型合并例：R4 的 `P2-1`（托盘无 TaskbarCreated）与 R3 的 `P1-6`（托盘四宗毛病）合并为 `F-P1-8`；R1 的 `P1-7`（Trigger 替换 Effect 强制软件渲染）与 R3 的 `§6-1` 合并为 `F-P2-22`。

---

### 2.1 P0 阻断级缺陷（2 项，均已端到端复现）

#### F-P0-1　正常退出后桌面布局全丢（`IsOpen` 被写为 `false`，补救路径永不执行）

> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

| 项 | 内容 |
|:---|:---|
| **来源** | R1 `P0-1`、R3 `P0-1`、R4 `P0-1`（三份报告一致命中） |
| **实证等级** | ★★★ **端到端复现**（真实 `SqliteDatabaseContext` + 真实 WPF 窗口生命周期） |
| **影响** | 应用每次正常退出后，数据库中全部便签 `IsOpen = 0`；下次启动恢复循环为空，**桌面一张贴纸都不出现**。产品卖点「桌面便签」与验收项 TC-05 全部失效 |

**因果链**

| 步 | 位置 | 行为 |
|:--:|:---|:---|
| 1 | `WindowManager.cs:126-147` | `PersistActiveWindowsBoundsOnExit()` 遍历 `_activeNoteWindows` 写 `IsOpen = true` |
| 2 | `WindowManager.cs:98` | 窗口 `Closed` 回调守卫 `if (App.IsShuttingDown \|\| _isShuttingDown) return;` |
| 3 | `App.xaml.cs:131-132` | `IsShuttingDown = true` 仅在 `PerformSafeShutdown()`（由 `OnExit` 调用）内执行 |
| 4 | **WPF 实测时序** | **`Window.Closed`（全部窗口）→ `App.OnExit`**，即第 2 步执行时 `IsShuttingDown` 仍为 `false` |
| 5 | `WindowManager.cs:82` | 同一回调已 `_activeNoteWindows.Remove(note.Id)`，`OnExit` 时字典已空 |
| ⇒ | 结果 | 守卫失效 → 写入 `false`；补救代码遍历 0 条 → **两次机会全部落空** |

**实测输出**

```
Shutdown 前 _activeNoteWindows.Count=2
OnExit 触发：App.Windows.Count=0, _activeNoteWindows.Count=0
PersistActiveWindowsBoundsOnExit 实际写库条数 = 0
便签A: IsOpen=False    便签B: IsOpen=False
下次启动将被恢复的贴纸数 = 0 / 2
```

**为何 71 项测试全绿却漏检**（三份报告共同确认的根因）：`LifecycleAndReliabilityTests.cs:96-100` 的顺序**与真实相反** —— 先调 `PersistActiveWindowsBoundsOnExit()`，后 `win.Close()`，并注释为"模拟 WPF 连带关闭窗口"。这个顺序恰好绕开了缺陷，给出**假绿灯**。

**✅ 已修复（2026-10-03，提交 `a2df002`）—— 且实际根因比本报告描述的更深一层**

修复时按用户决策改为「**仅桌面置顶（`AlwaysOnTop`）便签记忆窗口位置**」，并在实施中发现本报告遗漏的第二个陷阱：

| # | 报告是否记载 | 关键点 |
|:--:|:--|:---|
| 1 | ✅ 已记载 | `Closed` 回调在退出时 `IsShuttingDown` 仍为 `false` → 写入 `IsOpen=false` |
| 2 | ✅ 已记载 | 补救代码因字典已空而遍历 0 条 |
| 3 | ⚠️ **本次新发现** | 即使修好守卫，**退出时也保存不到坐标**：`Shutdown()` 会先关闭全部窗口并清空 `_activeNoteWindows`，等到 `App.OnExit` 时已无窗口可取坐标。因此坐标必须**在关窗之前**保存 |
| 4 | ⚠️ **本次新发现** | 回归测试除顺序写反外，还把「标记退出」放在「关闭窗口」之后，掩盖了真相 |

**最终修法**：`IsOpen` 改为单一写入口（创建写 `true` / 用户主动关闭写 `false`，退出流程完全不写）；新增原子入口 `WindowManager.BeginShutdownAndPersistPinnedPlacement()`（**先存坐标 → 再标记退出 → 最后关窗**），托盘退出与系统注销均走它；坐标记忆改用新增的 `UpdateWindowPlacementAsync`（只写坐标，不碰 `IsOpen`）。

**验证**：重写 `P0_1_Shutdown_PreservesIsOpenTrue_AndOnlyPinnedRemembersPlacement` 为严格复刻真实时序 + 真实 `NoteRepository`；新增 `F_P1_4_Closing_FlushesSynchronously_BeforeReturning`（关闭后不泵 Dispatcher 直接读库）。

---

**原报告修复建议（推荐 R3 方案 A）**：把 `IsOpen` 的持久化职责从"退出时补写"改为"**创建时写 true + 用户手动关闭时写 false**"的单一写入口。`WindowManager` 在 `LocationChanged`/`SizeChanged`（已存在）之外，于贴纸**创建时**即 `UpdateWindowBoundsAsync(..., isOpen: true)`；退出流程不再补写任何内容，从根上消灭顺序依赖。

**必须同步**：重写 `P0_1_Shutdown_PreservesActiveWindowsIsOpenTrueAndCoordinates`，改为**严格复刻** `Shutdown → 关闭全部窗口 → Exit → OnExit` 顺序；并新增一个使用**真实 `NoteRepository`**（而非 Fake）的用例。

---

#### F-P0-2　空白便签正文被占位文案「（空白便签）」污染并可写入数据库

> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

| 项 | 内容 |
|:---|:---|
| **来源** | R3 `P0-2`（独立发现）；R4 `UI-8` 观察到同一根因但**未识别其数据完整性后果** |
| **实证等级** | ★★★ **端到端复现**（真实 `NoteRepository` + 真实 `NoteViewModel` + 真实 `AutoSaveCoordinator`） |
| **影响** | 用户空白便签的正文被替换为占位文案并落库；同时 `Content.Trim()` 会永久裁掉正文首尾空格与缩进（代码片段、清单缩进受损） |

**根因**：`NoteViewModel.cs:142` 广播的是 `Note.PreviewText` 而非 `Note.Content`：

```csharp
WeakReferenceMessenger.Default.Send(
    new NoteContentChangedMessage(Note.Id, Note.PreviewText, Note.UpdatedAt));   // ← 应为 Note.Content
```

而 `Note.PreviewText` 定义为 `string.IsNullOrWhiteSpace(Content) ? "（空白便签）" : Content.Trim()`（`Note.cs:105`）。

**实证输出**

```
1. 初始落库正文 = 「」(空)
2. 主列表实体(独立实例) 正文 = 「」(空)
3. FlushSaveAsync 广播的载荷 = 「（空白便签）」
4. 主列表实体被污染为 = 「（空白便签）」
5. 「切换置顶」后数据库正文 = 「（空白便签）」
==> 结论：占位文案是否已落库 = ★ 是（缺陷成立）
```

**触发路径**：打开空白便签 → 点击其它窗口（`Deactivated` → `FlushSaveAsync`）→ 消息总线把占位文案送到 `NotesListViewModel.cs:330` 的 `note.Content = payload.Content` → 列表内存实体被污染 → 用户在列表右键该卡片执行「切换列表置顶」（`NotesListViewModel.cs:271` 的 `SaveAsync(note)` 全字段覆盖）→ **占位文案落库**。

**✅ 已修复（2026-10-03，提交 `67471c0`，1 行）**：`NoteViewModel.cs:142` 由 `Note.PreviewText` 改为 `Note.Content`，并加注释禁止回退。

---

**原修复建议**：`NoteViewModel.cs:142` 改传 `Note.Content`；并在 `Note` 上区分"用于展示的派生文本"与"持久化正文"，禁止把派生属性写回实体。补回归用例：`空白便签 → FlushSaveAsync → 列表实体 Content 仍为 ""`。

---

### 2.2 P1 严重问题（12 项）

#### F-P1-1　PIN 锁定可被 `Ctrl+N` / `Ctrl+F` 快捷键绕过（安全语义缺陷）
> **修复状态：⚠️ 部分修复**（2026-10-03）—— 已完成项与剩余项详见文末 §11.2

**来源**：R2 `P1-1`（唯一发现者）· **实证**：★☆ 代码逐行确认

`NotesListWindow_PreviewKeyDown`（`:378-395`）把 `Ctrl+N` / `Ctrl+F` 处理放在**函数最顶部**，早于 `:397-402` 的"焦点在 TextBox/PasswordBox 则放行"判断，且整个方法**没有任何 `PinOverlay.IsLocked` 检查**（`IsLocked` 仅在 `:44/56/79/82/335` 处被消费，全部与按键无关）。

后果：锁定状态下按 `Ctrl+F` 焦点被移到遮罩**后方**的 `SearchBox`；按 `Ctrl+N` 直接执行 `NewNoteCommand` 新建并打开一张**不含遮罩的明文便签窗口**，无需输入 PIN。

> 定级说明：PIN 在产品中明确定位为"轻量防偷窥"（`PinService.cs` 注释、设置页文案），因此**不构成高危漏洞**，但该绕过使此功能的唯一目的完全落空，故列为 P1。

**修复**：在 `PreviewKeyDown` **首行**判断 `PinOverlay.IsLocked`，锁定时拦截除遮罩输入外的全部按键；并在 ViewModel 的命令层（`NewNoteAsync` 等）加锁检查兜底。

#### F-P1-2　JSON 导出/导入丢失三项状态，且导入后主列表不刷新并产生「幽灵便签」
> **修复状态：✅ 已修复**（2026-10-03 深夜，提交 `f9744e2`）—— 详见文末 §12.3 更新表
> 三个子问题（丢三态 / 幽灵便签 / `IsOpen` 恒 false）**已全部处理**，另新增 3 个回归用例。

| 来源 | R3 `P0-3`（独立发现）· R2 `P2-3` 命中其中「丢字段」部分 · **实证**：★☆ 代码确认 |

三个子问题：

1. **导出 DTO 落后于 Schema V2**：`NoteBackupItem`（`ExportImportService.cs:13-26`）只有合并后的 `IsPinned`，**缺 `IsPinnedInList` / `AlwaysOnTop` / `IsOpen`**。导入时 `IsPinned = item.IsPinned` 走 `Note.IsPinned` 兼容 setter（`Note.cs:64-73`），**同时写两个字段**——"仅列表置顶"与"仅桌面置顶"两种独立状态在一次往返后被强行合并。
2. **导入后主列表不刷新，并插入一张幽灵便签**：`SettingsViewModel.cs:259` 发送 `new NoteCreatedMessage(new Models.Note())`，作者意图是走 `HandleNoteCreated` 中 `Id == Guid.Empty` 的"全量重载"分支；但 `Note.Id` 的属性初始化器是 `Guid.NewGuid()`（`Note.cs:11`），**`Id != Guid.Empty` 恒成立**，于是走"插入新卡片"分支 —— 列表凭空出现一张**空白且未持久化**的便签（重启后消失），而真实导入的 N 条要等重启才可见。
3. **`IsOpen` 强制置 false**，导入无法还原"哪些便签当时开着"。

**修复**：DTO 补 3 个字段（旧 JSON 保留默认值兼容）；新增专用消息 `NotesReloadedRequestedMessage`，`NotesListViewModel` 收到后 `await LoadNotesAsync()`。

#### F-P1-3　`App.OnStartup` 是 `async void` 且 6 处 `await` 无 `try/catch` → 启动失败留下"僵尸进程"并永久占死单实例 Mutex
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R2 `P1-2` + R3 `P0-4`（两份独立发现，结论一致）· **实证**：★☆ 代码确认

`OnStartup`（`App.xaml.cs:28`）中 `InitializeAndMigrateAsync()`（`:75`）位于 `ShutdownMode = OnExplicitShutdown`（`:91`）与 `mainWindow.Show()`（`:105`）**之前**，且从 `:75` 起的 6 个 `await` 步骤全部无异常兜底。若数据库被占用、文件损坏或磁盘满：

1. `DispatcherUnhandledException`（`:33-42`）捕获后 `args.Handled = true` **吞掉异常**；
2. 此时无 `MainWindow`、无托盘图标；
3. 进程继续存活，**且已持有单实例 Mutex** → 用户此后双击快捷方式一律被 `Program.Main`（`Program.cs:20-27`）判定为"第二实例"而秒退 —— 表现为「点了没反应，且再也打不开」。

**修复**：`OnStartup` 全程 `try/catch`，失败时弹出含 `AppPaths.DatabasePath` 与日志路径的错误对话框并 `Shutdown(1)`。

#### F-P1-4　退出刷盘依赖 `async void` 续体，实测续体不会执行
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R2 `P1-6` + R3 `P1-2` + R4 `P1-1`（三份一致）· **实证**：★★ **探针复现**

`NoteWindow.xaml.cs:47-55` 的 `Deactivated` / `Closing` 均为 `async void`，内部 `await ViewModel.FlushSaveAsync()`。实测（复刻 `Closing += async` + 真实异步边界）：

```
即将调用 app.Shutdown()
win0 Closing 进入（同步段）
win1 Closing 进入（同步段）
win2 Closing 进入（同步段）
Run() 已返回 / 进程即将结束          ← await 之后的续体一次都没执行
```

后果：`FlushSaveAsync` 尾部的 `WeakReferenceMessenger.Send(NoteContentChangedMessage)` 永不发出 → 最后 500ms 输入不反映到主列表内存副本；`FlushAsync` 内的异步写库是否完成依赖 `Microsoft.Data.Sqlite` 的实现细节，**不可依赖**。

**修复**：`Closing` 改为**同步阻塞刷盘**（复用 `AutoSaveCoordinator` 中已被验证不会死锁的 `ConfigureAwait(false).GetAwaiter().GetResult()` 模式）；`Deactivated` 增加"无待保存任务则跳过"短路（同时消除 F-P2-10 的写放大）。

#### F-P1-5　Schema V1 建表已含 V2 两列，V2 ALTER 必然失败并被 `catch` 掩盖且强推 `user_version`
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R2 `P1-4` + R3 `P1-1`（两份独立发现，结论一致）· **实证**：★☆ 代码确认

`SqliteDatabaseContext.cs:72-96` 的 V1 建表语句**已包含** `IsPinnedInList` 与 `AlwaysOnTop`，而 `:103-131` 的 V2 迁移又去 `ALTER TABLE ADD COLUMN` 同样两列 → **必然失败**，只能落入 `:124-131` 的 `catch`：回滚事务后把 `user_version` 强推为 2。

三个问题：① 从 v1 真实升级上来的用户走 `UPDATE ... SET IsPinnedInList = IsPinned` 路径，新建库走 `catch` 路径，**两条不同代码路径，测试只覆盖一条**；② 失败被完全静默（无日志），V3 迁移加入后会成为定时炸弹；③ `:133` 在 fallback 路径上仍打"成功升级" Info 日志，误导排查。

**修复**：V1 建表**去掉** V2 两列（"每个版本只做自己的事"）；或删除 V2 迁移只保留 V1 定义。**任何情况下不得用 `catch` 掩盖迁移失败** —— 应 `throw` 并给出可读错误。

#### F-P1-6　`settings.json` 非原子写入，损坏时 PIN 盐/哈希静默丢失、锁定悄然失效
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R2 `P2-1`（唯一发现者）· **实证**：★☆ 代码确认

`SettingsService.cs:155` 用 `File.WriteAllText` 直接覆盖写入，写一半崩溃/断电即损坏；`LoadSettings`（`:163-184`）在解析失败时**静默回退默认值** `new AppSettings()`，于是 `PinSalt` / `PinHash` 一起丢失 → **PIN 锁定无声失效**，用户以为仍受保护。

**修复**：写临时文件后 `File.Replace` 原子替换；解析失败时保留 `.bak` 并 `AppLog.Error`；PIN 字段损坏时应显式提示用户而非静默降级。

#### F-P1-7　`AutoSaveCoordinator` 在 CTS 仍被使用时 `Dispose`（4 处同模式）
> **修复状态：✅ 已修复**（2026-10-03 深夜，提交 `d25e19d`）—— 详见文末 §12.3 更新表

**来源**：R2 `P1-5` + R3 `P1-3`（两份独立发现，结论一致）· **实证**：★☆ 代码确认

```csharp
// AutoSaveCoordinator.cs:38-42
if (_pendingTasks.TryGetValue(noteId, out var existing))
{
    existing.Cts.Cancel();
    existing.Cts.Dispose();   // ← 后台 Task.Delay(cts.Token) 可能仍在跑
}
```

`Cancel()` 与 `Dispose()` 并非线程安全；同样模式还出现在 `:106`（`FlushAsync`）、`:134-135`（`FlushAllDirectToStorage`）、`:157-158`（`Dispose`）。`Task.Delay(cts.Token)` 在 token 已注册后遭遇 `Dispose` 会抛 `ObjectDisposedException`，被 `:72` 的通用 catch 误记为"自动保存失败"。更窄的窗口下，旧任务的 `TryRemove` 可能误删**新**放入的调度（→ 真实丢保存）。

> 备注：`docs/CHANGES-20261003.md` 记录过一次同族缺陷（搜索 CTS）已修复，**本处是同一类问题的残留点**。

**修复**：改为单个长生命周期 `DispatcherTimer`/`PeriodicTimer` 实现防抖；或 `Cancel()` 后**不** `Dispose`，交由任务 `finally` 在确认 `TryRemove` 之后释放。

#### F-P1-8　托盘图标四宗毛病（`TaskbarCreated` / `NIM_SETVERSION` / `WM_CANCELMODE` / `HWND_BROADCAST`）
> **修复状态：✅ 已修复**（2026-10-03 深夜，提交 `2ab105a`）—— 四宗毛病全部处理，详见文末 §12.3 更新表

**来源**：R2 `P2-21`+`P2-22`、R3 `P1-6`、R4 `P2-1`（三份命中，本次合并）· **实证**：★☆ 代码确认

| 问题 | 位置 | 后果 |
|:---|:---|:---|
| 未处理 `TaskbarCreated` 消息 | `TrayIconService.cs` 全文（`grep` 0 命中） | 用户重启 / `explorer.exe` 崩溃后**托盘图标永久消失**，只能重启应用 |
| 未调用 `NIM_SETVERSION` | `:81-90`（`NativeMethods.cs:72` 已定义常量但从未使用） | 图标停留"旧式"形态，不参与 Win10/11 图标区布局 |
| 右键菜单关闭后无 `WM_CANCELMODE` | `:144-151` | 菜单关闭后图标偶发保持"按下"灰态 |
| `NotifyExistingInstance` 用 `HWND_BROADCAST` 广播自定义消息 | `NativeMethods.cs:31-37` | 把消息发给**当前会话内所有顶层窗口**；UIPI 下若两实例完整性级别不同（一提权一普通），**唤醒静默失败**，用户只看到"双击没反应" |

**修复**：注册 `RegisterWindowMessage("TaskbarCreated")` 并在收到时重建图标；补 `NIM_SETVERSION`；菜单关闭后 `PostMessage(hwnd, WM_CANCELMODE, 0, 0)`；唤醒改为枚举本进程顶层窗口或定向查找，替代全局广播。

#### F-P1-9　`HotKeyService` 全部注册失败仍置 `_isRegistered = true`
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R2 `P2-2`（唯一发现者）· **实证**：★☆ 代码确认

`HotKeyService.cs:91` 无条件 `_isRegistered = true`，即使 `successN` 与 `successH` 均为 `false`。后果：热键被其它软件占用后，本次会话内在设置里**反复开关热键也不会再重试注册**，用户以为已修复实则一直无效。

**修复**：改为 `_isRegistered = successN || successH;`，并在设置页显示实际注册状态。

#### F-P1-10　归档窗口四合一缺陷：UTC 时间错 8 小时 + 无虚拟化 + 标题错误 + 无键盘导航
> **修复状态：⚠️ 部分修复**（2026-10-03）—— 已完成项与剩余项详见文末 §11.2

**来源**：R3 `§5.4` + R4 `P2-2`（虚拟化）· **实证**：★☆ 代码确认

| 问题 | 位置 |
|:---|:---|
| `StringFormat='{}{0:yyyy-MM-dd HH:mm}'` 直接格式化 **UTC** 的 `UpdatedAt`，显示"修改于"**比真实时间慢 8 小时** | `ArchivedNotesWindow.xaml:237`（对照主列表用 `FriendlyDateTimeConverter` 正确处理了 UTC→本地） |
| `ScrollViewer > ItemsControl` **完全无虚拟化**，且每张卡片带 `DropShadowEffect`（软件渲染）→ 归档几百条时明显卡顿 | `:136-138`、`:152` |
| `Title="彩色便签"` —— 归档窗口的任务栏/Alt-Tab 标题错误 | `:7` |
| 主列表有完整 ↑/↓/Enter/Esc 键盘体系，归档窗口**一个都没有**（`ItemsControl` 不可聚焦、无 `PreviewKeyDown`） | `:136-245` |

**修复**：改用 `FriendlyDateTimeConverter`；`ItemsControl` 换 `ListBox` + `VirtualizingStackPanel(Recycling)`；修正 `Title`；补键盘导航。

#### F-P1-11　键盘与无障碍可达性硬伤（Tab 被困编辑器 + 焦点全局不可见 + 零无障碍标注）
> **修复状态：⚠️ 部分修复**（2026-10-03）—— 已完成项与剩余项详见文末 §11.2

**来源**：R3 `§5.2`+`§5.9`、R1 `§6.3`、R4 `UI-3`（多份命中，本次合并）· **实证**：★☆ 代码/静态扫描确认

| 问题 | 证据 |
|:---|:---|
| **纯键盘用户无法用 `Tab` 离开便签编辑区** | `DesignTokens.xaml:178` `KeyboardNavigation.TabNavigation="None"` + `NoteWindow.xaml:268` `AcceptsTab="True"` —— Tab 被吃掉插制表符，又被禁止移焦。只能靠 `Ctrl+W`/`Ctrl+H` 等自定义快捷键 |
| **焦点获得时无任何视觉反馈** | 全项目 `FocusVisualStyle="{x:Null}"`（`DesignTokens.xaml:142,182,208,241,276,309` + `NotesListWindow.xaml:185,202,384,401`）。注意：10-03 14:50 那次"去掉列表黑框"的方向是对的，但**不应连任何焦点提示一起去掉** |
| **`rg "AutomationProperties"` → 0 命中** | 所有纯图标按钮（新建/置顶/更多/关闭/恢复/删除）对屏幕阅读器只读作"按钮"；`PinLockOverlay` 的密码框与"忘记 PIN？"纯 `TextBlock` 亦不可达 |
| 无 `HighContrast` 主题支持 | `rg` 0 命中；依赖硬编码色值的部分（`#FFFDE047` 高亮、`#08000000` 底槽）在高对比度下可能消失 |
| 搜索命中数变化不播报 | `NotesListWindow.xaml:170-174` 静态 `TextBlock`，违反 WCAG 4.1.3 |

**修复**：`Esc`/`Ctrl+Tab` 移焦 + 恢复克制焦点样式（1px Accent 内描边即可）；全量补 `AutomationProperties.Name`（与 `ToolTip` 一致，工作量极小收益明显）；命中数 `TextBlock` 加 `LiveRegion="Polite"`；新增 `Themes/HighContrast.xaml`。

#### F-P1-12　便签底部「已同步」为硬编码，与真实保存状态无关
> **修复状态：✅ 已修复**（2026-10-03）—— 详见文末 §11.1《已完成修复》

**来源**：R3 `§5.1`、R1 `§6.4-1`（两份命中，R1 已指出但未修）· **实证**：★☆ 代码确认

`NoteWindow.xaml:293` 为 `<TextBlock Text="已同步" />`——**硬编码字符串，与保存状态毫无关联**。防抖计时器还在跑时它写着"已同步"；数据库写入失败时它也写着"已同步"。这与项目自述的"行为确定且无黑盒""写下的每一个字绝不丢失"**直接冲突**，是最伤用户信任的一处细节。

**修复**：`NoteViewModel` 暴露 `SaveState { Pending, Saved, Failed }` + `LastSavedAt`；Footer 绑定真实状态；`Failed` 时红色提示 + 「重试」。**这是投入产出比最高的信任修复。**

---

### 2.3 P2 中等问题（23 项）

#### 数据与存储

| 编号 | 问题 | 位置 | 来源 |
|:---|:---|:---|:---|
| F-P2-1 ✅(已修) | 数据库 `user_version` **高于**应用支持版本时仅记一条 Warn 后 `return`，`App.OnStartup` 不检查结果 → 应用**正常启动但显示为空白便签应用**，用户可能误以为数据全丢并用新数据覆盖完好的原库 | `SqliteDatabaseContext.cs:60-64` + `App.xaml.cs:74-75` | R4 |
| F-P2-2 | 无 `PRAGMA integrity_check` 完整性自检（全项目 0 命中） | `SqliteDatabaseContext.cs` | R4 |
| F-P2-3 | `Cache = SqliteCacheMode.Shared` 与 WAL 并用；微软明确不推荐 shared cache（引入表级锁，并发下更易 `SQLITE_LOCKED`） | `SqliteDatabaseContext.cs:21` | R3 |
| F-P2-4 ⛔(撤销) | 每次 `OpenConnectionAsync` 都单独执行一次 `PRAGMA synchronous` 往返（`ForeignKeys`/`DefaultTimeout` 已在连接串，此项应同处理） | `NoteRepository.cs:19-27` | R2、R3 |
| F-P2-5 ✅(已修) | 移动/调整窗口即触碰 `UpdatedAt`，凭空改变"按更新时间排序"的列表次序 | `NoteRepository.cs:282-299` | R2 |
| F-P2-6 | 「每日备份」实际只在**启动时且当天首次**执行一次（无定时器）：应用连续运行一周只产生一份备份。另：文件名用**本地日期**而正文时间戳用 UTC（口径不统一）；无恢复入口、无完整性校验；`VACUUM INTO` 遇 `SQLITE_BUSY` 只记日志且当天不再重试 | `App.xaml.cs:78`、`BackupService.cs:19` | R3 |
| F-P2-7 | 导入逐条 `GetByIdAsync` 造成 **N+1 查询**（且不在事务内）：导入 N 条即 N 次独立连接 + 2 次往返 | `ExportImportService.cs:143-153` | R3、R4 |

#### 架构与一致性

| 编号 | 问题 | 位置 | 来源 |
|:---|:---|:---|:---|
| F-P2-8 | 搜索匹配规则**两份实现且已漂移**：`ArchivedNotesViewModel.FilteredNotes` 手写复制了 `SearchService` 的匹配逻辑（归档版额外匹配 `DisplayTitle`，`tokens.All` 空 token 行为不同）。应抽 `ISearchService.Match(note, keyword)` 复用 | `ArchivedNotesViewModel.cs:41-62` vs `SearchService.cs:47-49` | R3 |
| F-P2-9 | `SaveAsync` 为**全字段覆盖** UPSERT。主列表与便签窗口持有**不同 `Note` 实例**，任一方全量写回都可能用陈旧字段覆盖对方刚写入的值（F-P0-2 正是此机制被触发） | `NoteRepository.cs:132-164` | R4 |
| F-P2-10 ✅(已修) | **每次窗口失焦都全量写库**：`Deactivated → FlushSaveAsync → SaveAsync(Note)`（整条正文 UPDATE）+ 广播；在便签与列表间来回切换 = 每切一次一条全量 UPDATE | `NoteWindow.xaml.cs:47-50`、`NoteViewModel.cs:135-143` | R3 |
| F-P2-11 | `FlushSaveAsync` 内部先 `FlushAsync`（已含 `SaveAsync` + 发消息）**再无条件** `SaveAsync(Note)` —— **双写**，写放大明显 | `NoteViewModel.cs:135-143` | R2、R3 |
| F-P2-12 | 服务定位器残留：`GetService<NoteViewModel>() ?? new NoteViewModel(...)`，`_serviceProvider` 为非空构造参数且 `NoteViewModel` 已注册为 Transient，**该 fallback 永不执行**，却在 DI 失败时静默造出不受容器管理、无人 `Dispose` 的实例 | `WindowManager.cs:44-45` | R2、R3、R4 |
| F-P2-13 ❌(未修) | UI 线程 sync-over-async：`_repository.GetByIdAsync(...).GetAwaiter().GetResult()`。**实测未复现死锁**（见 §3.2），但 `DefaultTimeout = 5` 意味着最坏**卡 UI 5 秒** | `WindowManager.cs:241`；退出落盘 `:140` 逐条阻塞（N 次冻结） | R2、R3 |
| F-P2-14 | 主窗口激活逻辑**三份重复实现**（一次唤醒会走其中两条、重复调用 `SetForegroundWindow`）：`WindowManager.OpenOrActivateNotesListWindow()`（单方法 68 行、3 个分支）、`NotesListWindow` 自身的 `ShowNotesListRequestedMessage` 订阅、`WndProc` 的 `WM_ACTIVATE_INSTANCE` 处理（含 `Topmost=true/false` 抢焦点 hack，产生任务栏闪烁） | `WindowManager.cs:154-221`、`NotesListWindow.xaml.cs:126-175` | R3 |
| F-P2-15 | ViewModel 直接依赖 View 与 `MessageBox`（`PinSetupDialog.Execute` / `MessageBox.Show` ×6），MVVM 泄漏，PIN 流程无法单测 | `SettingsViewModel.cs:175,186,197,218,227,251` | R2 |
| F-P2-16 | ViewModel 内手动 `new` 服务：`_settingsService ?? new SettingsService()` 绕过 DI、触发静态路径解析、污染测试隔离 | `NoteViewModel.cs:51` | R2 |
| F-P2-17 | 消息注册的 fire-and-forget 与死代码：`ArchivedNotesViewModel.cs:77-80` 的 async lambda 异常直接崩进程；`AppMessages.cs:26,34` 的 `NoteUpdatedMessage`/`NoteDeletedMessage` **全仓无发送方**（已核实）但仍被 `NotesListViewModel.cs:65-66` 注册监听；`NoteArchivedMessage` 被当"删除"处理造成语义重叠 | 多处 | R2 |
| F-P2-18 | 生命周期清理缺位：`NotesListViewModel.Dispose`（`UnregisterAll` + `CancelCurrentSearch`）**全仓无调用方**；`NoteViewModel` 完全没有注销逻辑（`FontSizeChangedMessage` 在构造函数注册） | `NotesListViewModel.cs:460` | R2 |
| F-P2-19 | `OnSearchTextChanged` 职责双重：既是属性变更回调，又被 `DeleteNoteAsync`/`HandleNoteContentChanged`/`HandleNoteMetaChanged` 手动调用"刷新搜索"，每次重建 CTS 重跑 80ms 防抖，副作用耦合；高频输入下产生相互取消的搜索风暴 | `NotesListViewModel.cs:158-227, 292, 336, 389` | R2、R4 |
| F-P2-20 | 搜索结果从后台线程投递到已关闭的 Dispatcher：`else` 分支会在**线程池线程**上 `SearchResults.Clear()/Add()` → WPF 跨线程集合变更异常（被外层 catch 吞掉，表现为"退出时日志莫名一条异常"）；`Dispatcher.Invoke` 应换 `InvokeAsync` | `NotesListViewModel.cs:199-214` | R3 |

#### 性能

| 编号 | 问题 | 位置 | 来源 |
|:---|:---|:---|:---|
| F-P2-21 ✅(已修) | 转换器每次 `Convert` 都 `new SolidColorBrush(ColorConverter.ConvertFromString(hex))` —— **解析字符串 + 分配 + 未 `Freeze`**，且每次拿到新实例都会让 WPF 判定 brush 变化并重绘。`NoteWindow.xaml` 绑定 9 处，一次换色 = 9 次分配；列表/搜索卡片还有十几处 | `NoteColorConverters.cs`（6 个转换器） | R2、R3、R4 |
| F-P2-22 ✅(已修) | **Style Trigger 中替换 `Effect` 实例**（`DropShadowEffect`），每次 hover/选中都新建效果对象。WPF 的 `Effect` **强制该元素走软件渲染**，列表滚动 + 鼠标划过时 CPU 飙升。**R1 的 `P1-7` 已明确要求改为固定单例，长期未执行**（2026-10-03 深夜已修，提交 `9e15876`） | `NotesListWindow.xaml`、`ArchivedNotesWindow.xaml`、`DesignTokens.xaml` | R1、R3 |
| F-P2-23 | 每个按键创建一个 `Task.Run` + 一个 `CancellationTokenSource`（连续打字时短命对象 churn）；防抖语义应用单个 `Timer` | `AutoSaveCoordinator.cs:44-76` | R3 |
| F-P2-24 ⚠️(部分) | `AppLog` 每写一行都 `File.AppendAllText`（开/关文件 + 全局 lock），且**无保留期清理** —— 按日轮转但旧文件永不删除，日志目录无上限增长。另：类注释称"Release 模式生效"但**代码里没有任何 `DEBUG` 门控**（文档与实现不符） | `AppLog.cs:34-39` | R3 |
| F-P2-25 | **搜索性能余量不足**：产品文档承诺的 NFR 规模（1000 便签 / 约 10 万字）下达标（5.3ms），但单张内容变长后退化（1000 便签 × 50~100 行 → 44~48ms；3000 便签 → 142.8ms）。根因是每便签最多 3 轮全文扫描 | `SearchService.cs:43-54` | R4（**本次重新定级，见 §3.1**） |
| F-P2-26 ❌(未修) | 截图测试**无像素断言**：9 个 `Render_*` 用例只生成 PNG 供人工查看，不断言任何视觉属性，无法在 CI 中发现视觉回归 | `UiRenderingAndScreenshotTests.cs` | R1、R3、R4 |
| F-P2-27 ⚠️(部分) | 测试基建四问题：① 4 个测试文件各自重复实现 `INoteRepository` 假实现（约 200 行重复）；② `TestEnvironment.RunInSta` 的 `waitHandle.Wait()` **无超时**，一条挂死的 UI 用例会永久挂住 CI；③ 截图输出用 `..\..\..\..\..` 上溯 5 层定位项目根，**耦合源码目录布局**；④ 无 `Application.Shutdown()` 端到端测试（F-P0-1 漏检的直接原因） | `TestEnvironment.cs:137,186-188` + 4 份 Fake | R2、R3、R4 |
| F-P2-28 ✅(已修) | **全局异常策略过宽**：`DispatcherUnhandledException` 中 `args.Handled = true` **无条件**执行（含破坏数据一致性的严重异常），且刷盘的 `catch { }` 连日志都没有 → 应用可能处于半坏状态却无任何提示继续运行。应只对已知可恢复异常设 Handled，其余记录后放行；空 catch 至少 `AppLog.Error` | `App.xaml.cs:33-42` | R2 |
| F-P2-29 ✅(部分) | 并发与批量落盘：① `PersistActiveWindowsBoundsOnExit` 迭代 `_activeNoteWindows` 时，窗口 `Closed` 回调可能同时 `Remove` → 潜在 `InvalidOperationException`，应先 `ToList()` 快照；② 退出时逐条 `.GetAwaiter().GetResult()` 阻塞写库（N 次同步往返），应改 `SaveBatchAsync` 单事务一次写完 | `WindowManager.cs:129-140` vs `:82` | R2、R3、R4 |

---

### 2.4 P3 轻微问题（20 项，择机处理）

#### 代码整洁与死代码

| 编号 | 问题 | 位置 |
|:---|:---|:---|
| F-P3-1 | `App.Services` 静态服务定位器**全项目零引用**，且启动完成前调用必 NRE | `App.xaml.cs:26` |
| F-P3-2 | 死符号 11 项：`NoteUpdatedMessage`、`NoteDeletedMessage`、`PinDialogResult`、`INoteRepository.SoftDeleteAsync`（纯别名，仅测试 fake 实现）、`NoteColorExtensions.GetThemeColors`、`SearchService.BuildSegments`（唯一调用方是它自己的单测）、`TrayIconService._settingsService`（赋值后从未读取）、`Type.BodyStrong` / `Spacing.*`(6) / `CornerRadius.Window` / `Elevation.Card` / `Elevation.CardHover`（设计令牌定义了从未引用）、`SearchBox_KeyDown`（已无调用方）、`Note.Snippet`（**已核实：仅自身 `OnPropertyChanged` 引用，无 UI 绑定**） | 多处 |
| F-P3-3 | `NoteRepository.cs:339` 的 `if (reader.FieldCount >= 14)` 是**无效防御**——4 个查询都显式列出 14 列，该分支永不为真；其"把旧 `IsPinned` 继承到两个新字段"的逻辑永远走不到 | `NoteRepository.cs:339-349` |
| F-P3-4 | `IsPinned` 兼容属性**双实现且语义不一致**：`Note.cs:64-73`（读返回 `IsPinnedInList`、写同时写两字段）vs `NoteViewModel.cs:35-39`（只映射 `AlwaysOnTop`）；读/写语义不对称的"陷阱属性"，且目前仍被 `BindNoteParameters` 与 `ExportImportService` 使用 | `Note.cs:64-73` |
| F-P3-5 | 全部 `IValueConverter.ConvertBack` 都 `throw new NotSupportedException()`；只读转换器应返回 `Binding.DoNothing`，否则某个绑定被误设 `TwoWay` 时会运行时抛异常 | `NoteColorConverters.cs` ×7、`FriendlyDateTimeConverter.cs:37` |
| F-P3-6 | 归档列表四处重复的 `OnPropertyChanged` 三连（`[NotifyPropertyChangedFor]` 已覆盖多数）；`BackupService.cs:33-34` 的 `.tmp` 过滤与 `:42` 空 catch 为死代码 | 多处 |
| F-P3-7 | 卡片模板**三处复制粘贴**：`NotesListWindow.xaml:199-215 vs 396-412` 完全相同的 `ListBoxItem` 模板、`:226-257 vs 422-453` 相同阴影样式、`ArchivedNotesWindow.xaml:148-165` 第三份；调色盘 7 个色块按钮逐字重复（可改 `ItemsControl` + 集合，从 90 行缩到 30 行） | 3 处 |
| F-P3-8 | 颜色存在**双源真值**：`StickyColors.xaml:23-90` 与 `NoteColorConverters.cs` 的 `GetTheme()` 两份 hex，易漂移（hex 写错还会被静默吞掉） | 2 处 |
| F-P3-9 | 魔法常量在 4 处重复：`WindowX/Y = 150` 见于 `Note.cs:85-86`、`SqliteDatabaseContext.cs:81-82`、`ExportImportService.cs:20-21,162-163`、`WindowManager.cs:51`（启发式判断）；**默认尺寸有三个来源且不一致**：`Note.cs:87-88` = `380×420`，SQL 默认 = `320×360`，架构文档 = `320×360`。另散落 80ms/500ms/150/6/7/30/32 等魔法数 | 多处 |
| F-P3-10 | `PinService.SetPin` 不校验旧 PIN（依赖 UI 自觉）；`SettingsService.SaveSettings` 公开的 `customSettingsPath` 参数与对象状态无关，易误用 | `PinService.cs:46-56`、`SettingsService.cs:149` |
| F-P3-11 | `Every` 处 `AppPaths` getter 内 `CreateDirectory`（带副作用的属性）；`Program.cs:26` 第二实例 `return` 而非设退出码，调用方无法区分"已是最新实例"与"启动失败" | `AppPaths.cs:63-71,158-179`、`Program.cs:26` |

#### 交互与视觉细节

| 编号 | 问题 | 位置 |
|:---|:---|:---|
| F-P3-12 ✅(已修) | **设置页 4 处说明文字硬截断**（未设 `TextWrapping="Wrap"`，而同页 `:62/295/337` 都设了）：`关闭主列表窗口后…随时唤醒`、`Win+Alt+N：…管理中心`、`计算机启动登录时…敞开贴纸`、`随系统自启时…静默就绪`。因 `Type.CaptionSubtle` 未定义 `TextTrimming`，超长文本被**直接裁掉且无省略号** | `SettingsWindow.xaml:115,133,151,169` |
| F-P3-13 | **设置项依赖未联动**：「开机自动启动」为关时，「开机启动时最小化到托盘」仍可操作且显示为开 —— 用户看到自相矛盾的状态组合 | `SettingsWindow.xaml:144-175` |
| F-P3-14 ✅(已修) | 窗口标题三处文案不一致：`NotesListWindow.xaml:7` = "彩色便签"而页内文本为"便签"；`SettingsWindow.xaml:5` = "彩色便签"而 `TitleBar` = "设置"；归档窗口亦为"彩色便签"。任务栏/Alt-Tab 三个窗口**同名无法区分** | 3 个窗口 |
| F-P3-15 | 卡片交互语义三处不一致：主列表**双击**打开、搜索卡片**单击**打开、归档卡片**本体不可点**（仅两个图标按钮）；且搜索卡片用 `MouseLeftButtonDown`（**按下即跳转**）—— 无法在摘要里选中/复制文字，拖拽误触会跳走。应统一并改 `MouseUp` + 移动阈值 | `NotesListWindow.xaml.cs:193-213, 575-582`、`ArchivedNotesWindow.xaml:141-147` |
| F-P3-16 ✅(已修) | 「更多」弹出菜单**无法再次点击关闭**：Popup `StaysOpen="False"` 先因外部按下关闭，`Click` 里 `IsOpen = !IsOpen` 又将其重新打开 —— 按钮永远只能开不能关 | `NoteWindow.xaml.cs:115-118` + `NoteWindow.xaml:99` |
| F-P3-17 | 焦点被抢走时执行 `SelectAll()`（共 5 处）：在列表首项按 `↑` 或从便签窗口返回时会全选搜索词，用户接着打字将**整段替换**搜索内容。应只 `Focus()` 不 `SelectAll()`，或用 `CaretToEnd` | `NotesListWindow.xaml.cs:355-357, 370-374, 450-457, 543-546, 565-566` |
| F-P3-18 | 右键便签卡片**不先选中该项** → 右键菜单的操作对象与视觉高亮不一致，易误归档错便签。应在 `MouseRightButtonDown` 同步 `SelectedItem` | `NotesListWindow.xaml.cs:193-213` |
| F-P3-19 | `Ctrl+滚轮` 每次滚一格就 `SaveSettings()` 写一次磁盘，且改的是**全局** `EditorFontSize` —— 在任意一张便签上缩放会污染所有便签；字体大小本应按便签记忆 + 250ms 防抖落盘 | `NoteWindow.xaml.cs:185-193` → `NoteViewModel.cs:65-72` |
| F-P3-20 | 其它细节：搜索卡片高亮片段套 `Type.Body`（带 `TextTrimming`）→ 已被 `SearchService` 截断的摘要**再被 WPF 省略一次**出现非预期"…"；`SearchService.cs:125-128` 先拼 `"..."` 前缀再交给阈值 85 的 `TruncateLineSafely`，导致 83 字符行被二次截断；`FriendlyDateTimeConverter.cs:19` 未来时间恒显示"刚刚"且硬编码格式忽略 `culture`；`RuntimeInfo` 硬编码 ".NET 8.0"（改 TFM 后界面会说谎）；归档窗口 `MessageBox` 未设 `Owner`（可能被主窗口遮挡，用户以为无响应而重复点击"彻底删除"）；`PinSetupDialog` 错误提示重输时不清除；搜索跨行紧凑匹配兜底返回 `CharIndex=0` 会选中错误文本；置顶筛选为空时无空状态提示 |

---

## 3. 争议仲裁与过时结论更正

四份报告存在 6 处实质性分歧或偏差。本节逐条给出仲裁结论与依据，**这是合并版相对原始报告最重要的增量**。

### 3.1 【冲突】搜索性能：一份判「3 倍超标需优化」，一份判「实测良好不需要再优化」

| 报告 | 结论 | 依据 |
|:---|:---|:---|
| R4 | ⚠️ **超产品承诺 3 倍**（91.2ms vs <30ms） | 探针：1000 便签 × 100 行 = **419 万字符** |
| R3 | ✅ 性能良好，**不需要再优化** | 引述 `CHANGES-20261003.md` 记录：1000 便签 / 1 万行 → 单词 7.92ms、多词 9.17ms |

**两份报告用了相差 40 倍的规模，因此并不真正矛盾，但各自的绝对化表述都不准确。**

**本次仲裁实测（引用真实 `SearchService.cs`，覆盖 5 档规模）**

```
=== 搜索性能规模曲线 ===
 便签数   行/张      总字符     单词(ms)   多词AND(ms)   无命中(ms)
  1000      1      39890        5.0          5.3         0.3     ← 产品文档 NFR 规模
  1000     10     410900        6.1         13.8         2.3     ← R3 所称「1000 便签/1 万行」
  1000     50    2092500        7.4         47.9         1.6
  1000    100    4196000        8.5         44.1         1.7     ← R4 所称规模
  3000    100   12810000       33.0        142.8         5.2     ← 万条规模
```

**仲裁结论**：

1. **产品文档承诺的 NFR（1000 便签 / 约 10 万字）下完全达标** —— 单词 5.0ms、多词 5.3ms，**大幅优于 <30ms 的承诺**。R3 的"良好"判断在此规模下成立。
2. **但产品文档只有「1000 便签 / 约 10 万字」一档指标，而便签是长文本容器**：一旦单张平均长度升至 50~100 行（2~4 百万字符，完全在真实使用范围内），多词查询升至 **44~48ms，超出承诺 47%~60%**；3000 便签时达 **142.8ms（4.8 倍）**。
3. **R4 的定级过重**：R4 将其列为 P1 并称"超 3 倍"，但未指出承诺指标本身是在更小规模下定义的，措辞易被误读为「整体不达标」。**本报告更正为：达标但余量不足，在重内容与万条规模下会退化。** 定级下调为 **P2-25**（新增条目，见下）。
4. **R3 的"不需要再优化"结论不完整**：其依据的 `CHANGES-20261003.md` 记录缺少探针规模口径（未说明单张长度），且忽略了 `SearchService.cs:43-54` 对每个便签执行**最多 3 轮全文扫描**（`isDirectMatch` → `isCompactMatch` → `tokens.All(...)`）的结构性冗余。

**新增条目 F-P2-25（搜索性能余量不足）**：建议做三项低成本改进 —— ① `tokens.Length > 1` 时短路跳过 direct/compact 分支；② 一次遍历同步计算三个标志；③ 消除 `HandleNoteContentChanged`/`HandleNoteMetaChanged` 重复触发搜索（同 F-P2-19）。**不建议现在投入 FTS5**（`ISearchService` 抽象已就位，万条规模再升级即可）。

### 3.2 【冲突】sync-over-async 是否构成「经典死锁模式」

| 报告 | 结论 |
|:---|:---|
| R2 `P1-7`、R3 `P1-4` | 判为"**经典死锁模式**，目前仅因 IO 常常同步完成而侥幸不炸" |
| **本次仲裁** | ❌ **死锁未复现。真实风险是阻塞延迟，不是死锁** |

**仲裁依据**：在 STA/UI 线程以完全相同的模式连续调用 `SaveAsync` / `GetAllActiveAsync` / `UpdateWindowBoundsAsync`（各 20 次），全部正常返回，无挂起、无超时。原因：`NoteRepository` 所有 `await` 均未经 `ConfigureAwait(false)`，但 `Microsoft.Data.Sqlite` 的 `OpenAsync` / `ExecuteNonQueryAsync` 在该宿主上**同步完成**（不产生真实 continuation 投递），因此 `GetResult()` 不会自锁。

**更正后的定级**：保留为 **F-P2-13（中等问题）**，风险描述更正为「**UI 线程阻塞**：`DefaultTimeout = 5` 意味着数据库繁忙时最坏卡 UI 5 秒；退出落盘时逐条阻塞，N 张便签即 N 次串行冻结」而非死锁。修复建议不变（`NavigateToHit` 改 async、退出落盘改 `SaveBatchAsync` 单事务），但**优先级可从"严重"降至"中等"**，避免把工程力量投向不存在的死锁。

> 方法论提示：这正是"不做实证就不能定级"的又一例证 —— R2/R3 的推理链条（`GetResult()` + 无 `ConfigureAwait(false)`）在教科书上成立，但在本项目的具体依赖实现下不成立。

### 3.3 【冲突】托盘退出是否会被主窗口 `Closing` 的 `e.Cancel` 拦死

| 报告 | 结论 |
|:---|:---|
| R3 `P4` | 实测**不阻断**（正确） |
| **本次仲裁** | ✅ 一致确认不阻断 |

**仲裁依据**：复刻 `NotesListWindow.Closing` 的 `if (!IsShuttingDown && MinimizeToTrayOnClose) { e.Cancel = true; Hide(); }`，再从"托盘退出菜单"路径调用 `Application.Current.Shutdown()`，实测事件序列：

```
-- 调用 Application.Current.Shutdown() --
-- Shutdown() 调用已返回 --
MainList.Closing 触发
  -> e.Cancel = true; Hide()
MainList.Closed 触发
App.Exit(OnExit) 触发
Run() 返回，进程即将结束
```

`e.Cancel` 未能阻止 `Shutdown`，应用正常结束。**该项不是缺陷，从清单中剔除**（R2 亦未列为问题，属 R3 自查项）。

### 3.4 【过时】R1 的功能完整度与部分 UI/性能结论（基线落后 9 个提交）

R1 因其基线为 `909a00c`，以下结论在 `94ed507` **已完全不成立**，如后续维护者据 R1 施工将造成重复劳动：

| R1 结论 | 当前状态 |
|:---|:---|
| 「缺托盘、全局热键、开机自启三个命门」，功能完整度 3/10 | ✅ **已全部实现且实现规范**（原生 Win32，零额外依赖） |
| 「无 UI 虚拟化」`P1-8` | ✅ 主列表与搜索列表已启用 `List<Box>` + `Recycling` + 像素滚动 |
| 「搜索结果无键盘导航，键盘用户基本无法使用搜索」`§6.2` | ✅ 已实现完整 ↑/↓/Enter/Esc + `RestoreOptimalFocus` + 窗口级兜底 |
| 「每次防抖保存都触发列表 `RemoveAt + Insert` 重排」`P1-2` | ✅ 已引入 `NoteContentChangedMessage` 轻量消息 + `Notes.Move` 微调 |
| 「搜索在后台线程遍历 UI 绑定的 `ObservableCollection`」`P1-1` | ✅ 已改为不可变快照 `_searchSnapshot` |
| 「`Note` 实体不实现 `INotifyPropertyChanged`」`P1-3` | ✅ 已实现（`Note : ObservableObject`） |
| 「单实例 Mutex 名硬编码 + `Global` 作用域 + 唤醒目标错误」`P1-5` | ✅ 已改为 SHA256 数据目录哈希 + `Local\` + `RegisterWindowMessage` |
| 「`synchronous` PRAGMA 只在迁移连接生效；无 `busy_timeout`」`P1-6` | ✅ 已加入连接串 `DefaultTimeout = 5` 与每次连接的 `PRAGMA synchronous`（后者本身成为 F-P2-4 小冗余） |
| 「`AllowsTransparency` + `DropShadowEffect` 强制软件渲染」`P1-7` | ⚠️ **部分修复**：便签窗口已改 `WindowChrome`；但**卡片 Trigger 中替换 `Effect` 的问题仍在**（F-P2-22，R1 明确要求过但未执行） |
| 「`App.OnExit` 是 `async void`」`P0-4` | ✅ 已改回同步 `PerformSafeShutdown` |
| 「Release 下异常处理被优化掉，等于没有日志」`P0-5` | ✅ 已实现 `AppLog` 真实写文件（有测试验证） |

### 3.5 【夸大】R3 技术细节偏差 2 处

| R3 表述 | 核实结果 |
|:---|:---|
| 「搜索防抖从 300ms 降到 80ms 且有基准数据支撑」（记为优点） | ⚠️ **80ms 同时加剧了 F-P2-19 的搜索风暴**：每次属性变更即重建 CTS 并 `Task.Run`，而 `HandleNoteContentChanged`/`HandleNoteMetaChanged` 在 `IsSearching` 时还会再次调用 `OnSearchTextChanged`。防抖时间缩短本身是改进，但触发源重复是缺陷，二者应一并修 |
| 「V1 旧库的 `SELECT` 会直接报错」（P3-6 死代码说明） | ⚠️ 表述不严谨。旧库（v1 schema）**不含** `IsPinnedInList`/`AlwaysOnTop` 两列时，`GetAllActiveAsync` 的 14 列 `SELECT` 确实会抛 `SqliteException`。但这只在"曾用旧版本建库、且未跑过迁移"的极端场景出现，而迁移逻辑会在启动时补列 —— 因此该分支的**设计意图**（兼容旧库）与**实际可达性**确实矛盾，R3 的核心判断（死代码）成立，仅结论表述宜更精确 |

### 3.6 【补全】R2 未定级的 2 项，本次升级为 P1

R2 把「`settings.json` 非原子写导致 PIN 静默失效」列为 P2-1、「HotKey 注册失败仍置位」列为 P2-2。本次评估认为二者**影响面高于 R2 的定级**：

- 前者导致用户**以为受 PIN 保护实则未受保护**（安全感知错误），且写入损坏是断电场景下的真实概率事件 → 升为 **F-P1-6**。
- 后者导致**功能反复开关仍长期无效**且用户不知情 → 升为 **F-P1-9**。

---

## 4. 综合评分卡

评分以 `94ed507` 为基线，由四份报告的同维度评分加权调和，并剔除过时项（R1 的功能完整度、键盘导航等已不适用）。

| 维度 | 四份报告原始评分 | **合并后评分** | 依据与变化说明 |
|:---|:---|:---:|:---|
| 编译期质量 | R3: A | **A (9.0)** | 0 错误 0 警告，`Nullable=enable` 全量干净 |
| 分层与依赖 | R1: 7.0、R3: B+ | **B+ (8.0)** | MVVM 分层清晰、6 个依赖克制；扣分：服务定位器残留、VM 依赖 View |
| 代码质量与整洁 | R4: 7.5 | **B (7.5)** | 命名规范、注释到位、空值安全好；扣分：13 处 `async void`、20 项死代码 |
| **数据可靠性** | R1: 4.0、R3: D、R4: 5.0 | **D+ (5.0)** | ⬆️ 相对 R1 有实质改善（`VACUUM INTO`、冲突裁决、三层异常兜底、日志）；⬇️ 仍存在 2 个 P0（F-P0-1 布局全丢、F-P0-2 正文污染）与退出刷盘竞态 |
| 生命周期与异常处理 | R3: C- | **C (6.5)** | 三层兜底齐全且日志真实生效；扣分：`async void` ×13、`OnStartup` 无兜底、CTS 竞态、全局 `Handled=true` 吞异常 |
| 性能 | R1: 5.0、R3: C+、R4: 7.5 | **B- (7.0)** | 主列表虚拟化 + 单次预扫描 + 80ms 防抖；扣分：归档未虚拟化、Trigger 换 Effect 强制软件渲染、转换器新建 Brush、搜索余量不足 |
| **功能完整度** | R1: 3.0 | **A- (9.0)** | ⬆️⬆️ **最大幅度更正**。托盘/热键/自启/设置中心/归档/导入导出/PIN 全部落地，并额外实现多词搜索、字号缩放、便携模式 |
| UI / 视觉完成度 | R1: 6.0、R3: C、R4: 8.0 | **B+ (8.0)** | Fluent 2 落地水平高、设计令牌体系完整（间距/圆角/阴影/字阶四套）、微交互精致 |
| 交互与可用性 | R4: 部分 | **C+ (6.5)** | 键盘导航与焦点管理是真实投入；扣分：4 处文字硬截断、卡片语义不一致、`SelectAll` 吞输入、MoreMenu 开关 bug |
| 可访问性 (a11y) | R1: 2.0、R3: 零基础 | **D (3.0)** | `AutomationProperties` 0 命中、焦点视觉全局抹除、无高对比度主题、Tab 被困编辑器 |
| 主题能力 | R1: 最高优先级问题 | **D+ (4.0)** | 仅浅色；`StaticResource` 282 处 vs `DynamicResource` 3 处、硬编码色 92 处；便签卡片有 7 色含暗色 |
| 测试体系 | R1: 6.0、R3: C、R4: 7.5 | **C+ (6.5)** | 71 项 + 真机 HiDPI 截图（投入远超同类）；扣分：**P0 级缺陷覆盖率实际为零**（关键用例顺序与真实相反） |
| 工程化基建 | R1: 7.0、R3: 缺 CI | **C+ (6.5)** | 版本注入、Costura 内嵌、双形态发布 + SHA256 优秀；扣分：无 CI、无 `.editorconfig`、无 `TreatWarningsAsErrors`、构建不可复现、日志无限增长 |
| 文档一致性 | R1: 3.0、R3: D+、R4: 部分 | **D+ (4.0)** | 已有 8+ 条硬性漂移（xUnit vs MSTest、3 个不存在的文件、依赖数量、Schema、默认尺寸、算法语义）；`APP-ARCHITECTURE.md` 严重落后于代码 |
| 国际化 | R1: 1.0 | **D (2.0)** | 全部字符串硬编码中文，零 `.resx` |
| **加权综合** | R1: ≈4.9、R3: 可演示不可交付、R4: 7.3 | **≈ 7.2 / 10** | **架构与功能已达可交付水准；可靠性因 2 个 P0 尚未达标。修完 2 个 P0 后可达 8.2 左右** |

**综合结论（替代四份原始报告的各版本结论）**：

> 本项目**架构设计、功能完整度、视觉完成度均达到"可交付"水准**，与 R1 当年"演示级"的判断已不可同日而语；R1 的 4.9 分已严重过时。当前唯一阻断交付的是**数据可靠性**：2 个 P0 缺陷（`F-P0-1` 退出丢布局、`F-P0-2` 空白便签正文污染）都会造成用户可感知的数据/状态损失，且**恰好都落在自动化测试的盲区**。
>
> 需要特别强调的是 R3 提出的警示：**"已修复"的假象比"已知未修"更危险** —— `F-P0-1` 曾被标记修复、并有绿色测试佐证，实际从未生效。因此修复时必须**同步重写测试**，否则同一缺陷会以同样方式再次逃逸。

---

## 5. 值得肯定的部分（合并四份报告的共识，重构时不应破坏）

以下 10 项是三份报告的**共同正面结论**，属于本项目真正的技术资产：

1. **字符偏移锚点 + 单次预扫描的搜索架构正确且有前瞻性**。不持久化行号、用 `CharIndex + Length` 绝对锚点，天然规避自动换行下逻辑行/视觉行的错位；`SearchService.cs:61-67` 用 `lineStartOffsets` 把行号计算从 **O(n²) 降为 O(n)**。R3 的 P3 探针进一步证实 `TextBox.Text` **不做 `\n`↔`\r\n` 规范化**，因此偏移计算与编辑器天然自洽 —— 这是同类项目常见踩坑点，本项目天然避开。
2. **真机 HiDPI 渲染截图回归测试**。`TestEnvironment.cs:148-232` 通过 `VisualTreeHelper.GetDpi` 采样真实 DPI 生成 9 张 PNG，对纯 WPF 项目而言属超常规投入（唯一遗憾是无像素断言，见 F-P2-26）。
3. **依赖极其克制**。仅 6 个包（其中 3 个为构建期 Fody），无 EF Core、无日志框架、无 WebView、无遥测 —— 与项目自述的"轻量即美/Zero Bloatware"完全一致。
4. **确定性单实例机制**。`AppPaths.GetDataDirectoryHash()` 用 SHA256 明确规避 .NET 8 `string.GetHashCode()` 的跨进程随机加盐，`Program.Main` 首行完成判定实现毫秒级退出 —— 这是极易被忽视的真实缺陷，作者主动识别并修复。
5. **PIN 服务实现超出"轻量"定位的水准**：PBKDF2-SHA256 + 16 字节随机盐 + `CryptographicOperations.FixedTimeEquals` 恒定时间比较，并显式处理盐/哈希损坏时的降级。
6. **备份改为 `VACUUM INTO` 事务一致性快照**（而非裸拷 db），配置 `.tmp` 临时文件后原子替换 —— 是 R1 `P0-3` 的正确修法。
7. **导入的 `UpdatedAt` 冲突裁决**逻辑正确，能有效防止旧备份倒灌覆盖新数据。
8. **三层全局异常兜底 + `SessionEnding` 关机刷盘**，且 `AppLog` 真实写文件（有测试验证，非被编译器优化掉的空实现）。
9. **四级路径决议体系**（`DataDirOverride > 环境变量 > portable.ini > LOCALAPPDATA`）设计干净，并有 4 个专门测试覆盖。
10. **设计令牌体系落地扎实**。`DesignTokens.xaml` 定义间距/圆角/阴影/字阶四套完整令牌，字阶明确采用 Normal 字重以规避中文假粗体（有经验的做法）；主列表虚拟化 + 像素滚动 + Recycling + 完整键盘导航 + `RestoreOptimalFocus` 焦点恢复，是键盘可用性上的真实投入。

**新增条目 F-P2-26（截图测试无像素断言）**：R1/R3/R4 三份报告一致指出 —— `UiRenderingAndScreenshotTests.cs` 只生成 PNG 供人工查看，**不断言任何视觉属性**，无法在 CI 中发现视觉回归。建议加少量像素级断言（如"暗色便签卡片中心像素亮度 < 60"）。

**新增条目 F-P2-27（测试基建三问题）**：① 4 个测试文件各自重复实现了一份 `INoteRepository` 假实现（共约 200 行重复），应抽到 `TestDoubles/`；② `TestEnvironment.RunInSta` 的 `waitHandle.Wait()` **无超时**，一条挂死的 UI 用例会永久挂住 CI；③ 截图输出用 `..\..\..\..\..` 上溯 5 层定位项目根，**耦合源码目录布局**，换个层级构建就会写错位置；④ 无 `Application.Shutdown()` 端到端测试（这正是 F-P0-1 漏检的直接原因）。

---

## 6. 工程化与文档

### 6.1 工程化缺口（合并 R2/R3/R4）

| 缺口 | 影响 | 建议 |
|:---|:---|:---|
| **无 CI**（无 `.github/workflows`） | 71 项测试与 0 警告基线只能靠人工执行；`CHANGES-*.md` 里"全量 N 项测试 100% 通过"均为手工记录 | GitHub Actions：`build -c Release` + `test` |
| **无 `.editorconfig`** | 命名空间风格、`var` 用法等全靠约定，无强制 | 引入标准 .NET `.editorconfig` |
| **无 `TreatWarningsAsErrors` / `AnalysisLevel`** | 当前 0 警告是"恰好干净"，无防退化闸门 | `Directory.Build.props` 中开启 + `EnableNETAnalyzers` |
| **NuGet 版本分散**在 `csproj` | 多项目扩展时会漂移 | 迁移 `Directory.Packages.props`（中央包版本管理） |
| **构建不可复现**：`Version` 拼入 `BuildDate` 与 `GitCommitHash`（`Directory.Build.props:47-59`） | 同一源码不同时间构建产物版本号不同 → 无法用版本号定位问题、CI 缓存失效。且每次构建都 `Exec git rev-parse` | `Version` 保持纯语义版本（`1.1.2`）；构建时间/提交只进 `AssemblyMetadata`（该目标第 5 步已这么做，第 3/4 步不必再拼） |
| **无 `README.md`**、无 `.gitattributes` | 新接手者缺入口；Windows 行尾差异易造成无意义 diff（便签内容含 `\n`，风险更高） | 补简短 README + `.gitattributes` |
| **日志无保留期清理** | `app-YYYYMMDD.log` 永久累积 | 在 `RunDailyBackupIfNeeded` 中顺带清理 30 天前日志 |
| **Costura 无法内嵌 SQLite 原生库** | `Microsoft.Data.Sqlite` 的 `e_sqlite3.dll` 是原生依赖，**无法内嵌** → 若以"单 exe"为发布目标，干净机器上会缺原生依赖。**需实测验证 `publish.ps1` 产物布局** | 人工在干净虚拟机验证自包含版单文件发布 |
| **无 `global.json`** | SDK 版本飘移（当前 SDK 10.0.400 构建 net8.0 目标） | 固定 SDK 版本 |
| **多显示器 + 混合 DPI 未真机验证** | `app.manifest` 已声明 `PerMonitorV2`，但 `WindowManager` 混用 `SystemParameters.WorkArea`（主屏 DIP）与 `win.Left/Top`（每屏 DIP），拔显示器后的坐标回收逻辑需真机拔插验证 | 人工在双屏 + 不同缩放比下走查 |

### 6.2 文档与代码漂移（9 条硬性不一致）

| # | 文档承诺 | 文档位置 | 代码现状 |
|:--:|:---|:---|:---|
| 1 | `SearchService.Search` **返回全部命中**（"循环遍历便签中所有命中位置"），`SearchHit` 无 `Segments/Lines/TotalMatches` | `APP-ARCHITECTURE.md` §4.2 | 实现是**每张便签只返回首个命中**（`SearchService.cs:96-101` 直接 `break`），并新增 4 个字段用于三行上下文。**示例代码与实现完全不同** |
| 2 | 测试用 **xUnit** | §8.1 | 实际是 **MSTest** |
| 3 | 目录含 `NoteService.cs` / `SearchHitViewModel.cs` / `Views/Controls/ColorPickerPopup.xaml` | §7 | **三个文件均不存在**；另有 14 个已存在文件未列出（`AppPaths`、`AppLog`、`NativeMethods`、`TrayIconService`、`HotKeyService`、`AutoStartService`、`PinService`、`SettingsService`、`ExportImportService`、`SettingsViewModel`、`ArchivedNotes*`、`PinLockOverlay`、`DesignTokens.xaml`、`StickyColors.xaml`） |
| 4 | 依赖 3 个 NuGet | §7.1 | 实际 6 个（多 `WPF-UI 3.0.5`、`Costura.Fody 6.0.0`、`Fody 6.9.2`）；同文档 §9.2 又把 `Wpf.Ui.dll` 列为已内嵌依赖，**前后自相矛盾** |
| 5 | `Color TEXT NOT NULL DEFAULT 'Yellow'` | §5.2 | 实际 `Color INTEGER NOT NULL DEFAULT 0` |
| 6 | Schema 版本 1，无 `IsPinnedInList`/`AlwaysOnTop` | §5.2 | 实际 `TargetSchemaVersion = 2`，且**两列已在 V1 建表内**（F-P1-5 的根因） |
| 7 | 默认窗口 `320×360` | §5.2 | 实体与 XAML 都是 `380×420`（SQL 默认却仍是 `320×360`，见 F-P3-9） |
| 8 | 工程结构写 `StickyNotes.sln` | §7 | 实际是 `StickyNotes.slnx`（XML 格式） |
| 9 | 「软删除保留 30 天后自动清理」 | `APP-PRODUCT.md` §5.2 | **无任何 30 天清理实现**（全项目 `grep` 无命中） |
| 10 | 右键菜单含「修改颜色」 | `APP-PRODUCT.md` §3.1.1 | 右键菜单只有「打开 / 切换置顶 / 归档」，改色只能在便签窗口内 |
| 11 | `SearchHit` 的 `HighlightText` "算了不用" | R1 `:923` | **已过时**：现已用于高亮渲染 |
| 12 | 编辑器 `SelectionBrush="#0078D7"` | §4.4 | 实际 `#3399FF` |

**建议**：将 `APP-ARCHITECTURE.md` 与 `APP-PRODUCT.md` **标注为"历史设计文档"**，另起一份与代码同步的 `ARCHITECTURE.md`。否则每个新接手者都会被 §4.2 的伪代码与 §7 的文件清单误导（R3 特别警示了这一点）。

---

## 7. 修复路线图（合并四份报告的阶段划分）

### 阶段 0 · 止血（0.5~1 天，**必须先做，做完即可交付**）

| 任务 | 对应 | 验收标准 |
|:---|:---|:---|
| 把 `IsOpen` 改为"创建时写 true / 手动关闭时写 false"单一写入口，删除退出补写 | **F-P0-1** | 手工：开 3 张贴纸 → 托盘退出 → 重开 → **3 张都在原位**；**重写** `P0_1_*` 单测为真实顺序 + 改用真实 `NoteRepository` |
| `NoteViewModel.cs:142` 改传 `Note.Content` | **F-P0-2** | 单测：空白便签 `FlushSaveAsync` 后列表实体 `Content` 仍为 `""` |
| `Closing` 改同步刷盘；`Deactivated` 加"无待保存则跳过"短路，去掉 `FlushSaveAsync` 双写 | **F-P1-4**、F-P2-10、F-P2-11 | 退出后主列表卡片内容与数据库一致；切窗口不再产生全量 UPDATE |
| `OnStartup` 包 `try/catch` + 失败弹窗 + `Shutdown(1)` | **F-P1-3** | 注入必抛异常的 `SqliteDatabaseContext` 时给出明确提示而非静默僵尸进程 |
| PIN 锁定下拦截 `Ctrl+N`/`Ctrl+F` 及全部导航键 | **F-P1-1** | 锁定态按 `Ctrl+N`/`Ctrl+F` 无任何反应 |

### 阶段 1 · 数据可靠性加固（2~3 天）

| 任务 | 对应 |
|:---|:---|
| 导出 DTO 补 `IsPinnedInList`/`AlwaysOnTop`/`IsOpen`；新增 `NotesReloadedRequestedMessage`；消除幽灵便签 | **F-P1-2** |
| 迁移：V1 建表去掉 V2 两列；移除去 catch 掩盖；fallback 改打 Warn | **F-P1-5** |
| `settings.json` 改原子替换（`File.Replace`）+ PIN 损坏显式提示 | **F-P1-6** |
| 防抖改单 `Timer`，彻底移除 CTS Dispose 竞态 | **F-P1-7** |
| 数据库版本过高/损坏时明确阻断并引导从 `backups/` 恢复 + 加 `PRAGMA integrity_check` | F-P2-1、F-P2-2 |
| `SaveAsync` 增加部分字段更新方法（`UpdateContentAsync` 等） | F-P2-9 |
| 托盘：`TaskbarCreated` 重建 + `NIM_SETVERSION` + `WM_CANCELMODE` + 唤醒去 `HWND_BROADCAST` | **F-P1-8** |
| `HotKeyService._isRegistered = successN \|\| successH` | **F-P1-9** |
| 归档 UTC 时间、`Title`、虚拟化、键盘导航 | **F-P1-10** |
| 导入改一次性建索引（消除 N+1） | F-P2-7 |

### 阶段 2 · 性能与体验（2~3 天）

| 任务 | 对应 |
|:---|:---|
| **删除所有 Trigger 中的 `Effect` 替换**（改固定单例或 1px 边框）—— R1 遗留项 | **F-P2-22** |
| 转换器改静态 `Frozen` 笔刷表 | **F-P2-21** |
| 搜索：token 数 >1 时短路 + 单次遍历 + 消除重复触发 | **F-P2-25**、F-P2-19 |
| 「已同步」改真实 `SaveState` 枚举（含 Failed + 重试） | **F-P1-12** |
| `Tab` 移焦 + 恢复克制焦点样式 + 全量补 `AutomationProperties.Name` | **F-P1-11** |
| 设置页 4 处 `TextWrapping` + 自启依赖联动 + 统一窗口标题 | F-P3-12、F-P3-13、F-P3-14 |
| 卡片交互统一（`MouseUp` + 移动阈值）+ 删 5 处 `SelectAll` | F-P3-15、F-P3-17 |
| MoreMenu 开关 bug + 右键先选中 | F-P3-16、F-P3-18 |
| `Ctrl+滚轮` 按便签记忆 + 防抖落盘 | F-P3-19 |

### 阶段 3 · 工程化与整洁（1~2 天）

清理死代码（F-P3-1、F-P3-2，共 13 项）→ 抽 `INoteRepository` 单一测试替身 + `RunInSta` 加超时 + 截图输出改 `%TEMP%`（F-P2-27）→ 魔法常量归一（F-P3-9）→ 搜索匹配规则单一实现（F-P2-8）→ 颜色双源合一（F-P3-8）→ 卡片模板抽共享（F-P3-7）→ `Version` 去时间戳（F-P2-24 相关的构建不可复现）→ 加 `.editorconfig` + `TreatWarningsAsErrors` + CI → 回写文档（§6.2）。

### 阶段 4 · 长期（不阻塞交付）

深色主题与设计令牌收敛（`StaticResource`→`DynamicResource`，92 处硬编码色 token 化）· 高对比度主题 · i18n `.resx` · 图片粘贴与 `NoteAssets` 表 · 版本历史 `NoteRevisions` · FTS5（万条规模后）· 截图像素级断言 · **PIN 是否覆盖桌面贴纸的产品决策**（当前只保护列表与归档，`NoteWindow` 无遮罩；`CHANGES-20261003.md:136` 表明这是**有意决策**，但设置页未向用户说明，建议二选一：纳入锁定，或在设置项旁明确写出"仅保护列表与归档窗口"）。

---

## 8. 附录 A：跨报告编号映射表

四份原始报告的编号体系互不相同（例如 `P0-1` 在 R1/R3/R4 中指代三个不同缺陷）。下表提供双向追溯。

### 8.1 原始编号 → 合并编号

| R1 `osbf` | R2 `zcglmf` | R3 `APP-CODE-REVIEW-1514` | R4 `stdsf` | **合并编号** |
|:---|:---|:---|:---|:---|
| P0-1 | — | P0-1 | P0-1 | **F-P0-1** |
| — | — | P0-2 | UI-8 | **F-P0-2** |
| P0-4 | P1-2 | P0-4 | P1-1 | F-P1-3 |
| — | P1-1 | — | — | **F-P1-1** |
| — | P2-3 | P0-3 | — | **F-P1-2** |
| P0-2 | — | — | — | 已修复（导入冲突裁决）→ 未保留 |
| P0-3 | — | — | — | 已修复（`VACUUM INTO`）→ 未保留 |
| P0-5 | — | — | — | 已修复（`AppLog` 实数写文件）→ 未保留 |
| — | P1-6 | P1-2 | P1-1 | **F-P1-4** |
| — | P1-4 | P1-1 | — | **F-P1-5** |
| — | P2-1 | — | — | **F-P1-6**（R2 定 P2，本次升 P1） |
| — | P1-5 | P1-3 | — | **F-P1-7** |
| — | P2-21/P2-22 | P1-6 | P2-1 | **F-P1-8** |
| — | P2-2 | — | — | **F-P1-9**（R2 定 P2，本次升 P1） |
| — | P2-16 | §5.4 | P2-2 | **F-P1-10** |
| §6.3 | P2-17/P2-18 | §5.2/§5.9 | UI-3 | **F-P1-11** |
| §6.4-1 | — | §5.1 | — | **F-P1-12** |
| P2-4 | — | — | P1-2 | F-P2-1 |
| — | — | — | P1-2 | F-P2-2 |
| — | — | P1-7 | — | F-P2-3 |
| P1-6 | P2-4 | §6-4 | — | F-P2-4 |
| — | P2-6 | — | — | F-P2-5 |
| — | — | P1-8 | — | F-P2-6 |
| — | P2-3 | — | P2-8 | F-P2-7 |
| — | — | §4.4 | — | F-P2-8 |
| — | — | — | P2-3 | F-P2-9 |
| — | P1-6 | §6-3 | — | F-P2-10 |
| — | P1-6 | — | — | F-P2-11 |
| P2-6 | P2-7 | — | P2-4 | F-P2-12 |
| — | P1-7 | P1-4 | — | F-P2-13（**死锁判定被证伪**，见 §3.2） |
| — | — | P1-9 | — | F-P2-14 |
| — | P2-9 | — | — | F-P2-15 |
| — | P2-10 | — | — | F-P2-16 |
| R2 `P2-11` | R2 `P2-12` | R3 `P1-5` | R4 — | F-P2-20 |
| R2 `P3-5` | R3 `§4.1` | R4 `P2-7` | F-P2-21 |
| R1 `P1-7` | R3 `§6-1` | — | F-P2-22 |
| R2 `P3-9` | R3 `§6-5` | — | F-P2-23 |
| R3 `§6-6` | — | — | F-P2-24 |
| R4 `P1-4`（**本次降级**） | — | — | F-P2-25 |
| R1 `§7.2`、R3 `§7.2`、R4 `P2-8` | — | — | F-P2-26 |
| R2（测试章）、R3 `§7.2`、R4 `P2-8` | — | — | F-P2-27 |
| R2 `P1-3` | — | — | F-P2-28 |
| R2 `P2-8`、R3 `P1-4`、R4 `P2-5` | — | — | F-P2-29 |
| R1 `P2-2` | R3 `§5.10` | — | 已修复（搜索高亮已实现）→ 仅"二次截断"部分归入 F-P3-20 |
| R1 `P2-3` | — | — | 与 §6.2 的「30 天清理」合并 |
| R1 `P2-5`、R2 `P3-7` | — | — | F-P3-2（`Note.Snippet` 死代码） |
| R1 `P3-1..P3-12`、R2 `P3-1..P3-12`、R3 `§4.2/§4.3/§4.5/§4.6`、R4 `P3-1..P3-11` | — | — | 归入 F-P3-1 ~ F-P3-20（同类合并） |

### 8.2 合并编号 → 原始编号（用于回查历史讨论）

| 合并编号 | 名称 | 来源报告 |
|:---|:---|:---|
| F-P0-1 | 退出后桌面布局全丢 | R1 `P0-1`、R3 `P0-1`、R4 `P0-1` |
| F-P0-2 | 空白便签正文污染 | R3 `P0-2`；R4 `UI-8`（同根因，未识别数据后果） |
| F-P1-1 | PIN 被快捷键绕过 | R2 `P1-1` |
| F-P1-2 | 导入丢三态 + 幽灵便签 | R3 `P0-3`；R2 `P2-3`（部分） |
| F-P1-3 | `OnStartup` async void 无兜底 | R2 `P1-2`、R3 `P0-4`、R4 `P1-1` |
| F-P1-4 | 退出刷盘 async void 续体不执行 | R2 `P1-6`、R3 `P1-2`、R4 `P1-1` |
| F-P1-5 | V1/V2 迁移自相矛盾 + catch 掩盖 | R2 `P1-4`、R3 `P1-1` |
| F-P1-6 | `settings.json` 非原子写 → PIN 静默失效 | R2 `P2-1` |
| F-P1-7 | CTS 使用中 Dispose | R2 `P1-5`、R3 `P1-3` |
| F-P1-8 | 托盘四宗毛病 | R2 `P2-21/22`、R3 `P1-6`、R4 `P2-1` |
| F-P1-9 | HotKey 注册失败仍置位 | R2 `P2-2` |
| F-P1-10 | 归档窗口四合一缺陷 | R3 `§5.4`、R4 `P2-2` |
| F-P1-11 | 键盘与无障碍硬伤 | R1 `§6.3`、R2 `P2-17/18`、R3 `§5.2/5.9`、R4 `UI-3` |
| F-P1-12 | 「已同步」硬编码 | R1 `§6.4-1`、R3 `§5.1` |
| F-P2-1 ✅(已修) | 版本过高静默空白 | R4 `P1-2` |
| F-P2-2 | 无完整性自检 | R4 `P1-2` |
| F-P2-3 | Shared Cache + WAL | R3 `P1-7` |
| F-P2-4 ⛔(撤销) | 每次连接重复 PRAGMA | R2 `P2-4`、R3 `§6-4` |
| F-P2-5 ✅(已修) | 移动窗口篡改 UpdatedAt | R2 `P2-6` |
| F-P2-6 | 备份只跑一次 + 无清理 | R3 `P1-8` |
| F-P2-7 | 导入 N+1 查询 | R2 `P2-3`、R4 `P2-8` |
| F-P2-8 | 搜索规则两份实现 | R3 `§4.4` |
| F-P2-9 | 全字段覆盖 UPSERT | R4 `P2-3` |
| F-P2-10 ✅(已修) | 每次失焦全量写库 | R2 `P1-6`、R3 `§6-3` |
| F-P2-11 | `FlushSaveAsync` 双写 | R2 `P1-6`、R3 `§6-3` |
| F-P2-12 | 服务定位器残留 | R1 `P2-6`、R2 `P2-7`、R3 `§4.2`、R4 `P2-4` |
| F-P2-13 ❌(未修) | UI 线程 sync-over-async | R2 `P1-7`、R3 `P1-4`（**降级 + 更正为阻塞而非死锁**） |
| F-P2-14 | 主窗口激活三份重复 | R3 `P1-9` |
| F-P2-15 | VM 依赖 View/MessageBox | R2 `P2-9` |
| F-P2-16 | VM 内手动 new 服务 | R2 `P2-10` |
| F-P2-17 | 消息 fire-and-forget + 死消息 | R2 `P2-11` |
| F-P2-18 | 生命周期清理缺位 | R2 `P2-12` |
| F-P2-19 | 搜索回调双重职责/搜索风暴 | R2 `P2-13`、R4 `P1-4`（部分） |
| F-P2-20 | 后台线程投递已关闭 Dispatcher | R3 `P1-5` |
| F-P2-21 ✅(已修) | 转换器每次新建 Brush | R2 `P3-5`、R3 `§4.1`、R4 `P2-7` |
| F-P2-22 ✅(已修) | Trigger 换 `Effect` 强制软件渲染 | R1 `P1-7`、R3 `§6-1` |
| F-P2-23 | 每按键一个 Task.Run | R2 `P3-9`、R3 `§6-5` |
| F-P2-24 ⚠️(部分) | AppLog 无清理 + 文档不符 | R2 `P3-8`、R3 `§6-6` |
| F-P2-25 | 搜索性能余量不足 | R4 `P1-4`（**降级 + 更正措辞**） |
| F-P2-26 ❌(未修) | 截图测试无像素断言 | R1 `§7.2`、R3 `§7.2`、R4 `P2-8` |
| F-P2-27 ⚠️(部分) | 测试基建四问题 | R2（测试章）、R3 `§7.2`、R4 `P2-8` |
| F-P2-28 ✅(已修) | 全局异常策略过宽 | R2 `P1-3` |
| F-P2-29 ✅(部分) | 字典遍历并发 + 逐条阻塞落盘 | R2 `P2-8`、R4 `P2-5`、R3 `P1-4` |
| F-P3-1 ~ F-P3-20 | 见 §2.4 | R1/R2/R3/R4 同类合并 |

---

## 9. 附录 B：关键代码位置索引

### 9.1 缺陷定位速查

| 编号 | 文件:行 | 一句话 |
|:--:|:---|:---|
| **F-P0-1** | `WindowManager.cs:82,98` + `App.xaml.cs:131` + `LifecycleAndReliabilityTests.cs:96` | 退出时 `IsOpen` 被写 false，补救代码因字典已空而永不执行；测试顺序与真实相反 |
| **F-P0-2** | `NoteViewModel.cs:142` + `Note.cs:105` + `NotesListViewModel.cs:330,271` | 失焦刷盘广播 `PreviewText`，空白便签正文变占位文案并落库 |
| F-P1-1 | `NotesListWindow.xaml.cs:378-395` | `Ctrl+N`/`Ctrl+F` 处理早于锁定判断，且全程无 `IsLocked` 检查 |
| F-P1-2 | `ExportImportService.cs:13-26,160` + `SettingsViewModel.cs:259` + `Note.cs:11` | 导出 DTO 缺 3 字段；通知用 `new Note()` 因 `Id` 恒非空而走"插入"分支产生幽灵便签 |
| F-P1-3 | `App.xaml.cs:28,75,33-42` | `async void` 启动链 + 吞异常 = 无窗口僵尸进程占死 Mutex |
| F-P1-4 | `NoteWindow.xaml.cs:47-55` | `Closing` 用 `async void`，实测续体不执行 |
| F-P1-5 | `SqliteDatabaseContext.cs:72-96,103-131` | V1 建表已含 V2 列，V2 ALTER 必失败并被 catch 掩盖后强推版本号 |
| F-P1-6 | `SettingsService.cs:155,163-184` | `File.WriteAllText` 非原子；解析失败静默回退默认值 → PIN 失效 |
| F-P1-7 | `AutoSaveCoordinator.cs:41,106,134,157` | 使用中 `Dispose` CTS |
| F-P1-8 | `TrayIconService.cs:81-90,144-151` + `NativeMethods.cs:31-37` | 无 `TaskbarCreated`/`NIM_SETVERSION`/`WM_CANCELMODE`；唤醒用 `HWND_BROADCAST` |
| F-P1-9 | `HotKeyService.cs:91` | 全部注册失败仍置 `_isRegistered = true` |
| F-P1-10 | `ArchivedNotesWindow.xaml:7,136-138,237` | 无虚拟化 + UTC 显示 + 标题错 + 无键盘导航 |
| F-P1-11 | `DesignTokens.xaml:178,182` + `NoteWindow.xaml:268` + `rg AutomationProperties`=0 | Tab 被困编辑器；焦点视觉全局抹除；零无障碍标注 |
| F-P1-12 | `NoteWindow.xaml:293` | 「已同步」硬编码 |
| F-P2-1 ✅(已修) | `SqliteDatabaseContext.cs:60-64` + `App.xaml.cs:74-75` | 版本过高静默 return，不检查结果 |
| F-P2-3 | `SqliteDatabaseContext.cs:21` | `Cache = Shared` + WAL |
| F-P2-4 ⛔(撤销) | `NoteRepository.cs:19-27` | 每操作一次 `PRAGMA synchronous` 往返 |
| F-P2-5 ✅(已修) | `NoteRepository.cs:282-299` | 移动窗口篡改 `UpdatedAt` |
| F-P2-6 | `App.xaml.cs:78` + `BackupService.cs:19` | 备份只在启动跑一次；本地日期命名 |
| F-P2-7 | `ExportImportService.cs:143-153` | 导入逐条 `GetByIdAsync`（N+1，且不在事务内） |
| F-P2-8 | `ArchivedNotesViewModel.cs:41-62` vs `SearchService.cs:47-49` | 搜索匹配规则两份已漂移 |
| F-P2-9 | `NoteRepository.cs:132-164` | 全字段覆盖 UPSERT |
| F-P2-10 ✅(已修) | `NoteWindow.xaml.cs:47-50` + `NoteViewModel.cs:135-143` | 每次失焦全量写库 |
| F-P2-11 | `NoteViewModel.cs:135-143` | `FlushSaveAsync` 内 `FlushAsync` + `SaveAsync` 双写 |
| F-P2-12 | `WindowManager.cs:44-45` | DI fallback 分支永不执行却静默造 VM |
| F-P2-13 ❌(未修) | `WindowManager.cs:140,241` | UI 线程 `.GetAwaiter().GetResult()`（阻塞，非死锁） |
| F-P2-14 | `WindowManager.cs:154-221` + `NotesListWindow.xaml.cs:126-175` | 主窗口激活逻辑三份重复 + `Topmost` hack |
| F-P2-15 | `SettingsViewModel.cs:175,186,197,218,227,251` | VM 直接调 View 与 `MessageBox` |
| F-P2-16 | `NoteViewModel.cs:51` | `_settingsService ?? new SettingsService()` |
| F-P2-17 | `ArchivedNotesViewModel.cs:77-80` + `AppMessages.cs:26,34` + `NotesListViewModel.cs:65-66` | async lambda 注册；死消息仍被监听 |
| F-P2-18 | `NotesListViewModel.cs:460` | `Dispose` 无调用方 |
| F-P2-19 | `NotesListViewModel.cs:158-227,292,336,389` | 搜索回调双重职责 / 搜索风暴 |
| F-P2-20 | `NotesListViewModel.cs:199-214` | `else` 分支在线程池线程改 `ObservableCollection` |
| F-P2-21 ✅(已修) | `NoteColorConverters.cs` ×6 | 每次 `Convert` 新建未冻结 Brush |
| F-P2-22 | `NotesListWindow.xaml:230,241,251` + `ArchivedNotesWindow.xaml:152,159` + `DesignTokens.xaml:163-166` | Trigger 换 `Effect` → 强制软件渲染 |
| F-P2-23 | `AutoSaveCoordinator.cs:44-76` | 每按键一个 `Task.Run` + CTS |
| F-P2-24 ⚠️(部分) | `AppLog.cs:34-39` | 每行开关文件、无保留期清理、注释与实现不符 |
| F-P2-25 | `SearchService.cs:43-54` | 每便签最多 3 轮全文扫描，重内容规模下退化 |
| F-P2-26 ❌(未修) | `UiRenderingAndScreenshotTests.cs` | 截图无像素断言 |
| F-P2-27 ⚠️(部分) | `TestEnvironment.cs:137,186-188` + 4 份 Fake 仓储 | `RunInSta` 无超时；截图上溯 5 层耦合目录；Fake 重复约 200 行 |
| F-P2-28 ✅(已修) | `App.xaml.cs:33-42` | `args.Handled = true` 无条件 + 空 `catch { }` |
| F-P2-29 ✅(部分) | `WindowManager.cs:129-140` vs `:82` | 迭代字典时 `Closed` 回调 `Remove` → 潜在 `InvalidOperationException`；逐条阻塞落盘 |
| F-P3-1 | `App.xaml.cs:26` | `App.Services` 零引用死代码 |
| F-P3-9 | `Note.cs:85-88` + `SqliteDatabaseContext.cs:81-84` + `ExportImportService.cs:20-21` + `WindowManager.cs:51` | `150` 与默认尺寸四源不一致（`380×420` vs SQL `320×360`） |
| F-P3-12 ✅(已修) | `SettingsWindow.xaml:115,133,151,169` | 四处说明文字缺 `TextWrapping` → 硬截断无省略号 |
| F-P3-16 ✅(已修) | `NoteWindow.xaml.cs:115-118` + `NoteWindow.xaml:99` | MoreMenu 只能开不能关 |
| F-P3-19 | `NoteWindow.xaml.cs:185-193` → `NoteViewModel.cs:65-72` | Ctrl+滚轮每格写盘 + 改全局字号 |

### 9.2 优秀实现索引（重构时勿破坏）

| 亮点 | 文件:行 |
|:---|:---|
| 字符偏移锚点 + 单次预扫描 **O(n)** 行号计算 | `SearchService.cs:55-67` |
| 多关键词区间合并高亮算法 | `SearchService.cs:231-306` |
| 真机 HiDPI 渲染截图基建 | `TestEnvironment.cs:148-232` |
| 确定性互斥量 SHA256 哈希 | `AppPaths.cs:96-110` |
| PIN PBKDF2 + 恒定时间比较 | `PinService.cs:46-94` |
| 三层全局异常兜底 + 真实文件日志 | `App.xaml.cs:33-61`、`AppLog.cs` |
| 焦点恢复与键盘导航体系 | `NotesListWindow.xaml.cs:333-573` |
| `VACUUM INTO` 事务一致性备份 | `BackupService.cs:55-82` |
| 导入 `UpdatedAt` 冲突裁决 | `ExportImportService.cs:147-153` |
| 版本/构建元数据注入 | `Directory.Build.props:34-89` |
| 设计令牌四套体系 | `DesignTokens.xaml` |
| 便携模式四级路径决议 | `AppPaths.cs:73-94` |
| 主列表虚拟化 + 像素滚动 + Recycling | `NotesListWindow.xaml:183-198` |

---

## 10. 结论

**综合四份独立审查 + 本次 4 组补充实证，本项目的真实状态如下：**

**做对了的部分（占绝大多数）**：WPF + MVVM 分层清晰、依赖极其克制（6 个包）、核心差异化算法（字符偏移锚点跳行）设计正确且有前瞻性、托盘/全局热键/开机自启/设置中心/归档/导入导出/PIN 全部落地且实现规范、`VACUUM INTO` 备份与导入冲突裁决是正确的高阶修法、设计令牌与 Fluent 2 视觉落地水平高、71 项测试含真机 HiDPI 截图回归。**R1 当年给出的"功能完整度 3/10、演示级"判断已严重过时**，本报告据实更正为约 9/10。

**真正阻断交付的只有 2 项**，且都已端到端复现：

1. **F-P0-1 正常退出后桌面布局全丢** —— 修复方案曾被认为有效、并有绿色测试佐证，但本次用真实数据库 + 真实 WPF 生命周期复现证明**从未生效**。根因是"退出时补写 `IsOpen`"的设计依赖了一个与 WPF 实际行为相反的事件顺序假设，而回归测试的顺序恰好也是反的，因此给出了**假绿灯**。
2. **F-P0-2 空白便签正文被占位文案污染并可落库** —— 一个"派生展示属性被当作持久化数据广播"的经典错误，经"失焦刷盘 → 列表实体污染 → 切换置顶全量写回"三步即可写入数据库。

**最值得汲取的工程教训（R3 提出、本报告完全认同）**：**"已修复"的假象比"已知未修"更危险**。两者的共同特征是——缺陷恰好落在自动化测试的盲区，且测试有明确的（错误的）顺序模拟。因此本次修复的验收标准必须包含「**重写测试**」这一步：`F-P0-1` 若不同步补上真实 `Application.Shutdown()` 顺序 + 真实仓储的回归测试，同一缺陷必定以同样方式再次逃逸。

**关于分歧的处理**：本次合并纠正了 3 处偏差 —— ① 搜索性能（R4 定级过重，改为"达标但余量不足"并降为 P2-25）；② sync-over-async 死锁判定（R2/R3 的"经典死锁"推理在实测中**不成立**，更正为阻塞延迟并降级为 P2-13）；③ 托盘 `e.Cancel` 拦死退出（确认**不是缺陷**，从清单剔除）。同时补全了 R2 低估的 2 项（`settings.json` 原子性、HotKey 置位，均升为 P1）。这些更正的意义在于：**避免把有限的工程力量投向不存在的问题，也避免放过真实存在的问题。**

**建议执行顺序**：阶段 0（0.5~1 天，5 项止血，包含 2 个 P0 + 退出刷盘同步化 + 启动兜底 + PIN 绕过）完成后本项目即可对外交付；阶段 1（2~3 天）完成数据可靠性加固；阶段 2~3 为质量与体验提升；阶段 4 的深色主题、无障碍、i18n 属长期项，不阻塞交付。

---

## 附：本次审查的产物与清理

| 项 | 说明 |
|:---|:---|
| **本报告** | `docs/APP-REVIEW-FINAL-20261003-152504-stdsf.md`（唯一权威版，替代前四份的结论部分） |
| 前四份原始报告 | **保留**于 `docs/`，建议在各文件头部加注「历史版本，结论以 `APP-REVIEW-FINAL-*` 为准」 |
| 本次新增探针 | 4 组（搜索曲线 / 空白便签污染 / sync-over-async / 托盘退出），位于 `temp/final-probe/`，**验证后已删除** |
| 历史遗留探针 | `temp/` 下 `perf-probe` ~ `perf-probe8`、`review-probe` 等 10 个目录（被 `.gitignore` 排除），属历史清理债，建议清理 |
| 工作区状态 | `git status` 仅显示新增报告与 `.box-agent/`，无源码改动 |

*报告生成：2026-10-03 15:25 (GMT+8) · 基线 commit `94ed507` · 编译 0 错误 0 警告 · 测试 71/71 通过*

## 11. 修复状态总览（2026-10-03 实施记录）

> 实施依据：用户从本报告挑选的 A 档 / B 档 / C 档条目 + F-P0-1 + 补日志，共分 6 批提交。
> 基线 `94ed507` → 当前 `237e0c8`。**每批均通过 `dotnet build -c Release`（0 错误 0 警告）与 `dotnet test`（71 → 78 项全绿）**。
> 详情逐条见 `docs/CHANGES-20261003.md`。

### 11.1 已完成修复（8 项，全部通过回归验证）

| 编号 | 缺陷 | 提交 | 修复要点 |
|:--|:---|:--|:---|
| **F-P0-1** | 正常退出后桌面布局全丢 | `a2df002` | `IsOpen` 改单一写入口（创建写 true / 手动关写 false，退出不写）；新增 `BeginShutdownAndPersistPinnedPlacement()` 在关窗前存坐标；按决策**仅桌面置顶便签记忆位置**；**重写顺序写反的回归测试** |
| **F-P0-2** | 空白便签正文被占位文案污染 | `67471c0` | 广播载荷 `PreviewText` → `Content`（1 行） |
| F-P1-3 | `OnStartup` async void 无兜底 → 僵尸进程占死 Mutex | `0df9d0f` | 数据库初始化与便签恢复两段加 `try/catch`，失败弹窗（含库/日志路径）+ `Shutdown(1)` |
| F-P1-4 | 退出刷盘 `async void` 续体不执行 | `a2df002` | `Closing` 改同步阻塞 `FlushSaveBlocking()` |
| F-P1-5 | V1 建表已含 V2 列致 ALTER 必失败且被 catch 掩盖 | `0df9d0f` | V1 去掉两列；V2 改 `PRAGMA table_info` 探测后按需 ALTER（幂等）；**删除 catch 掩盖改为回滚 + throw** |
| F-P1-6 | `settings.json` 非原子写致 PIN 静默失效 | `5911e0c` | `.tmp` + `File.Replace` 原子替换、`.bak` 兜底恢复；PIN 数据损坏时**显式告警用户**而非静默失效 |
| F-P1-9 | 热键全部注册失败仍置 `_isRegistered = true` | `67471c0` | 改为 `successN \|\| successH`（1 行） |
| F-P1-12 | 底部「已同步」硬编码 | `27fcf9d` | 新增 `NoteSaveState`（Saved/Pending/Failed）+ `LastSavedAt`；失败态危险色 + 可点击重试 |

### 11.2 部分完成（6 项，均有明确剩余项）

| 编号 | 已完成 | 仍未做 |
|:--|:---|:---|
| F-P1-1 | 未动 | **锁定时 `Ctrl+N`/`Ctrl+F` 仍可绕过 PIN**（子问题 1，未修）；子问题 2「命令层兜底」未做 |
| F-P1-2 | ✅ **已修复**（`f9744e2`，2026-10-03 深夜） | 导出 DTO 补三字段；导入不再走 `IsPinned` 兼容 setter；新增 `NotesReloadedRequestedMessage` 全量重载消除幽灵便签；旧备份安全回落 |
| F-P1-7 | ✅ **已修复**（`d25e19d`，2026-10-03 深夜） | CTS 所有权单一化：只由持有它的防抖任务在 `finally` 释放，其余入口只 `Cancel` 不 `Dispose`；落盘前 `TryRemove(KeyValuePair)` 比对 |
| F-P1-8 | ✅ **已修复**（`2ab105a`，2026-10-03 深夜） | `TaskbarCreated` 重建图标 + `NIM_SETVERSION`(v4) + 菜单关闭 `WM_CANCELMODE` + 唤醒改 `EnumWindows` 定向投递 |
| F-P1-10 | ✅ **UTC 时间错 8 小时**（改走 `FriendlyDateTimeConverter`） | ❌ 虚拟化（`ItemsControl` 未换 `ListBox`）、❌ `Title`（已改为「已归档便签」）、❌ 键盘导航 |
| F-P1-11 | ✅ **无障碍标注**（图标按钮 / 调色盘 / PIN 框补 `AutomationProperties.Name`） | ❌ Tab 移焦、❌ 焦点视觉（`FocusVisualStyle="{x:Null}"` 仍在）、❌ `LiveRegion`。注：**F-P3-12 的换行修复顺带使「设置页 4 处硬截断」消失** |

### 11.3 附带修复（本轮顺带解决）

| 编号 | 缺陷 | 提交 | 说明 |
|:--|:---|:--|:---|
| F-P2-1 | 版本过高静默空白 | `0df9d0f` | 由 `Warn + return` 改为 `Error + throw`，异常经启动兜底给出明确提示 |
| F-P2-5 | 移动窗口篡改 `UpdatedAt` | `67471c0` | 从坐标 UPDATE 中移除该字段 |
| F-P2-10 | 每次失焦全量写库 | `a2df002` | `Deactivated` 增加 `HasPendingChanges` 短路 |
| F-P2-21 | 转换器每次新建未冻结 Brush | `67471c0` | 改为按颜色预构建、`Freeze()` 的静态表查表 |
| F-P2-28 | 全局异常策略过宽 | `0df9d0f` | 改为 `IsRecoverable()` 判定，仅表现层瞬时异常被吞，其余放行；空 `catch` 全部补日志 |
| F-P2-29 | 字典遍历并发 | `a2df002` | **① 已完成**：退出持久化前 `_activeNoteWindows.ToList()` 快照；② 批量落盘未做 |
| F-P3-12 | 设置页说明文字硬截断 | `67471c0` | 在 `Type.CaptionSubtle` 令牌层加 `TextWrapping="Wrap"`（一处覆盖全站） |
| F-P3-14 | 三个窗口标题同名 | `67471c0` | `便签` / `设置` / `已归档便签` |
| F-P3-16 | 「更多」菜单只能开不能关 | `67471c0` | `IsOpen = !IsOpen` → `IsOpen = true` |

### 11.4 ⛔ 撤销项（1 项）

| 编号 | 原建议 | 实测结论 |
|:--|:---|:---|
| F-P2-4 | 「每次连接重复 PRAGMA，应并入连接串」 | **本 provider 下不可行**：`Microsoft.Data.Sqlite` 的 `SqliteConnectionStringBuilder` **不支持 `synchronous` 关键字**（仅支持 Data Source / Mode / Cache / Password / Foreign Keys / Recursive Triggers / Default Timeout / Pooling）。尝试并入后 22 项测试立即失败，已回退为「建连后单独下发 PRAGMA」并在代码中注明原因。**该项建议应作废** |

### 11.5 未修复（本轮范围外）

- **P2**：F-P2-3、F-P2-6、F-P2-7、F-P2-8、F-P2-9、F-P2-11、F-P2-12、F-P2-13、F-P2-14、F-P2-15、F-P2-16、F-P2-17、F-P2-18、F-P2-19、F-P2-20、F-P2-22、F-P2-23、F-P2-25、F-P2-26、F-P2-27
  - 其中 **F-P2-11（`FlushSaveAsync` 双写）经复核确认仍然存在**：`FlushAsync` 内部已含 `SaveAsync`，外层又无条件 `SaveAsync(Note)` 一次。
- **P3**：除已修的 F-P3-12/14/16 外，F-P3-1 ~ F-P3-11、F-P3-13、F-P3-15、F-P3-17 ~ F-P3-20 **全部未修**（F-P3-1 `App.Services` 仍零引用、F-P3-2 `Note.Snippet` 等死符号仍在）。
- **§6.2 文档漂移 12 条**：仅「三个窗口标题」实际已由 F-P3-14 消除，其余（`APP-ARCHITECTURE.md` 的伪代码、xUnit vs MSTest、文件清单、依赖数量、默认尺寸、30 天清理、右键改色等）**均未回写**，`APP-ARCHITECTURE.md` 仍严重落后于代码。

### 11.6 本轮新增日志与测试

- **日志（批次 6，`237e0c8`）**：补齐数据层（`NoteRepository` 原 0 处）、迁移、导入导出、搜索（≥100ms 才 Warn）、PIN 流程、新建/归档、窗口位置、进程入口共 13 个文件；`AppPaths`/`NativeMethods` 因 **`AppLog` 会经 `AppPaths.LogsDirectory` 回环**而改用 `Debug.WriteLine`。
- **测试**：71 → **78** 项，新增 7 项 —— `F_P1_12_SaveState_ReflectsRealPersistenceOutcome`、`F_P1_6_*` ×3、`F_P1_5_*` ×2、`F_P1_4_Closing_FlushesSynchronously_BeforeReturning`；并**重写** `P0_1_*` 两项（原用例顺序与真实相反，是 F-P0-1 漏检的直接原因）。

---

## 12. 追加修复总览（2026-10-03 晚间，低风险 × 高收益专项）

> 依据本文 §11.5「未修复（本轮范围外）」清单，按「改动小、行为风险低、收益明显」筛选后分 5 批提交。
> 详细要点见 `docs/CHANGES-20261003.md` 顶部四条记录（批次 A~D）与本节。

### 12.1 已完成

| 编号 | 项 | 说明 |
|:--|:---|:---|
| **F-P1-1**（子项 1） | PIN 锁定态 `Ctrl+N`/`Ctrl+F` 绕过 | 窗口级按键分发首行加锁定守卫；新增用例覆盖"锁定态不得新建窗口/不得触发搜索" |
| F-P2-16 / F-P2-12 | 绕过 DI 的隐式实例 | `NoteViewModel` 的 `SettingsService` 改必填；`WindowManager` 改 `GetRequiredService<NoteViewModel>()` |
| F-P2-24 | 日志无保留期清理 | 30 天保留期 + 纯函数 `IsExpiredLogFile`（非约定命名一律不删） |
| F-P3-1 / F-P3-3 / F-P3-2 | 死代码 | 删 `App.Services`、`FieldCount>=14` 永假分支、`Note.Snippet` |
| F-P3-5 | 只读转换器 `ConvertBack` 抛异常 | 9 处改 `Binding.DoNothing` |
| F-P3-9 | 默认尺寸/落点四源不一致 | 实体新增几何常量；SQL 默认对齐 `380×420`；`WindowManager` 去魔法数；新增单源一致性用例 |
| F-P3-10 / F-P3-11 | 语义瑕疵 | `SaveSettings` 去掉无意义形参；第二实例显式退出码 |
| F-P3-13 | 设置项依赖未联动 | 「最小化到托盘」随「开机自启」禁用 |
| F-P3-15 | 搜索卡片按下即跳转 | 改「抬起 + 4px 移动阈值」，摘要可选中复制 |
| F-P3-17 | `SelectAll()` 吞输入 | 7 处改 `CaretIndex` 置末尾，新增用例 |
| F-P3-18 | 右键不先选中 | 新增 `MouseRightButtonDown` 同步选中 |
| F-P3-20 | 搜索摘要二次截断 | 阈值抽常量 + 卡片样式去 `TextTrimming`，新增用例 |
| F-P1-11（残留） | 命中数不播报 | 加 `AutomationProperties.LiveSetting="Polite"` |
| §6.1 工程化 | 无 `.editorconfig` / 无 `TreatWarningsAsErrors` / 构建不可复现 / 无 `global.json` / 无 `README` / 无 `.gitattributes` | 全部补齐（**CI 除外**，按用户决定不加） |
| §6.2 文档漂移 | 12 条 | `APP-ARCHITECTURE.md` / `APP-PRODUCT.md` 逐条更正，并在文首加「历史设计文档」警示 |

### 12.2 结论更正（1 项）

| 编号 | 原建议 | 本次结论 |
|:--|:---|:---|
| **F-P3-19** | 字号改为「按便签记忆 + 250ms 防抖落盘」 | **不采纳**：与产品契约冲突 —— 设置页提供"便签正文字号"下拉项并明确"即时生效"，字号被设计为**全局**设置；改为按便签记忆会使设置页失效，并需推翻 `FontSizeChangedMessage` 的全局广播语义。**处理**：保留既有行为，把契约写入代码注释并以回归测试固化。「每滚一格写盘」的代价属实，如需优化应在**保持全局语义**前提下加防抖。 |

### 12.3 仍未做（明确记录）

> **2026-10-03 晚间更新（批次 F~I，提交 `f9744e2` → `9e15876`）**：下方清单中的
> **F-P1-2、F-P1-7、F-P1-8、F-P2-22 四项已完成修复**，逐项状态如下表；其余项仍未做。
> 详细变更见 `docs/CHANGES-20261003.md` 顶部「批次 F~I」记录。

| 编号 | 状态 | 提交 | 要点 |
|:--|:--|:--|:---|
| **F-P1-2** | ✅ **已修** | `f9744e2` | 导出 DTO 补 `IsPinnedInList`/`AlwaysOnTop`/`IsOpen`；导入不再走 `IsPinned` 兼容 setter（消除两态合并）；新增 `NotesReloadedRequestedMessage` 全量重载，消除幽灵便签；旧备份安全回落 |
| **F-P1-7** | ✅ **已修** | `d25e19d` | CTS 所有权单一化：只由持有它的防抖任务在 `finally` 释放，其余入口只 `Cancel`；落盘前 `TryRemove(KeyValuePair)` 比对，杜绝误删新调度 |
| **F-P1-8** | ✅ **已修** | `2ab105a` | 补 `TaskbarCreated` 重建 + `NIM_SETVERSION`(v4) + 菜单关闭 `WM_CANCELMODE`；唤醒由 `HWND_BROADCAST` 改 `EnumWindows` 定向投递本进程窗口 |
| **F-P2-22** | ✅ **已修** | `9e15876` | 新增 4 个固定效果令牌，7 处 Trigger 内联 `DropShadowEffect` 全部改为引用单例（消除 hover/选中时重建 Effect 及强制软件渲染） |

- **GitHub Actions / CI**：用户明确排除。
- `Directory.Packages.props`（中央包版本管理）：当前 2 工程 / 10 个包引用，收益有限。
- **Costura 单 exe 在干净机器缺原生 `e_sqlite3.dll`**：需人工在干净虚拟机验证 `publish.ps1` 产物。
- **P2 剩余**：F-P2-3（Shared Cache + WAL）、F-P2-6（备份只跑一次）、F-P2-7（导入 N+1）、F-P2-8（搜索规则两份实现）、F-P2-9（全字段覆盖 UPSERT）、F-P2-11（`FlushSaveAsync` 双写）、F-P2-13（UI 线程阻塞）、F-P2-14 ~ F-P2-20、F-P2-23、F-P2-25（搜索余量）、F-P2-26（截图缺像素级断言）、F-P2-27（测试基建）。
  - 其中 **F-P2-11 经复核确认仍然存在**（`NoteViewModel.cs` 的 `FlushSaveAsync` 内 `FlushAsync` + `SaveAsync` 双写）。
  - ⚠️ **F-P2-26 措辞更正**：原文「截图测试无像素断言、只生成 PNG 供人工查看」易被误读为"测试完全没断言"。实测 `UiRenderingAndScreenshotTests.cs` 有 40+ 条属性/键盘断言，**缺的只是对 PNG 像素的断言**；且 `TestEnvironment.SaveWindowSnapshot` 内部会 `win.Show()`，并非纯离屏渲染。后续按「**截图缺像素级断言**」理解。
- **P3 剩余**：F-P3-2 的其余死符号、F-P3-4（`IsPinned` 双实现陷阱）、F-P3-6 ~ F-P3-8、F-P3-19（见 §12.2 更正）、F-P3-20 的其余细节（搜索跨行兜底 `CharIndex=0`、归档 `MessageBox` 未设 `Owner`、`RuntimeInfo` 硬编码 ".NET 8.0"、置顶筛选空状态提示）。
  - 注：`Elevation.Card`/`CardHover`/`Popup`/`Window` **已不再是死令牌**（`SettingsWindow` 与 `NoteWindow` 在用），批次 I 又新增 4 个被 Trigger 引用的令牌，清理死符号时不要误删。
- **F-P1 剩余**：F-P1-1 子项 2（命令层兜底）、F-P1-10 的虚拟化/键盘导航、F-P1-11 的 Tab 移焦与焦点视觉。
  - 原列的 F-P1-2 / F-P1-7 / F-P1-8 已于 2026-10-03 深夜修复（见 §12.3 更新表）。

### 12.4 验证

`dotnet build -c Release` → **0 错误 0 警告**（`Version=1.1.2` 可复现）。

- 批次 A~E（截至 `fab2d25`）：`dotnet test` → **83/83 通过**（78 → 83，新增 5 项）。
- **批次 F~I（截至 `9e15876`）：`dotnet test` → 94/94 通过**（83 → 94，新增 11 项：F-P1-2 +3、F-P1-7 +2、F-P1-8 +4、F-P2-22 +2）。均为本地提交，未 push。

---

*本附录由 2026-10-03 修复实施过程生成，与 §2 正文的 ✅/⚠️/❌/⛔ 标记一一对应。*
