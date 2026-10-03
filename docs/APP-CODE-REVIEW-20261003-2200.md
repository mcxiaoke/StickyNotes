# StickyNotes 全量代码审查报告（第二轮 · 独立复核）

> **审查时间**：2026-10-03 21:38 (GMT+8)
> **审查对象**：commit `025ca62`（master，工作区干净）——即托盘迁移到 H.NotifyIcon.Wpf 之后的最新代码
> **审查方法**：逐文件通读全部 45 个源文件（src 全量 + tests 全量 + 构建脚本/配置），关键结论均在当前代码上重新定位行号并交叉验证（含 `git blame` 溯源、`rg` 死代码引用扫描、`dotnet build -c Release` 与 `dotnet test` 实测），未采用任何未经核实的推断。
> **基线实测**：`dotnet build -c Release` → **0 错误 0 警告**；`dotnet test` → **103/103 通过**（14s）。
> **与上轮报告的关系**：本报告不重复 `APP-REVIEW-FINAL-20261003.md` 的全文，只做两件事：① 报告**本轮新发现**的问题；② 核验上轮已知缺陷与已修复项在当前代码中的**真实存留状态**。缺陷编号沿用上轮的 `F-*` 体系，新发现编号为 `N-*`。

---

## 1. 总体结论

项目当前状态**明显好于上轮审查时**：上轮 2 个 P0 与大部分 P1 修复经本次逐项抽验**确认真实生效**（详见 §4），构建质量闸门（0 警告、`TreatWarningsAsErrors`）与 103 项测试全绿。本轮新发现的问题中**没有 P0 级缺陷**；有 1 个影响多显示器用户的 P1（便签被强制搬回主屏），1 个上轮日志批次引入的小回归（备份清理循环嵌套错误），其余为轻微问题与工程整洁项。上轮遗留的 P2/P3（双写、sync-over-async、搜索规则双实现等）多数未动，与上轮 §11.5/§12.3 记录一致，不算新问题。

---

## 2. 本轮新发现缺陷（N 系列）

### N-1　多显示器下便签位置保护用主屏工作区判断，副屏便签每次重开/启动都被强制搬回主屏

| 项 | 内容 |
|:---|:---|
| 等级 | **P1**（对多显示器用户）|
| 位置 | `WindowManager.cs:63-74`（`OpenOrActivateNote` 的屏幕边界保护分支）；`WindowManager.cs:385-427`（`CalculateSmartRightPlacement` 同样只用 `SystemParameters.WorkArea`）|

`SystemParameters.WorkArea` 只返回**主显示器**的工作区。判断条件为：

```csharp
var workArea = SystemParameters.WorkArea;
if (note.WindowX + 50 > workArea.Right || note.WindowY + 50 > workArea.Bottom ||
    note.WindowX + note.WindowWidth < workArea.Left || note.WindowY < workArea.Top)
{
    // 强制改用 CalculateSmartRightPlacement 落回主屏
}
```

副屏上的便签（`WindowX` 在主屏宽度之外，无论副屏在主屏右侧还是左侧负坐标方向）必然命中条件，于是：

- **每次启动恢复**（`App.OnStartup` → `OpenOrActivateNote`，全部 IsOpen 便签都走这条路径）；
- **每次从列表重新打开一张已关闭的副屏便签**。

两个场景下，便签都会被搬到主屏，**用户手工摆放的桌面布局丢失**。这与产品卖点「可记忆位置与尺寸」直接冲突。对照：`NotesListWindow.RestoreWindowPlacement`（`NotesListWindow.xaml.cs:665-671`）用的是 `VirtualScreen*`（全虚拟桌面），是对的——两处口径不一致。上轮报告 §6.1 将多显示器列为「未真机验证」的风险项，本次从代码上确认这是一个**确定的逻辑缺陷**而非待验证项。

**建议**：用 `Screen.AllScreens` / `Screen.FromPoint`（或 `MonitorFromWindow`）按便签中心点取其所在屏幕的工作区做判定；`CalculateSmartRightPlacement` 亦应基于主窗口所在屏幕。

### N-2　每日备份轮转清理的 foreach 被嵌套重复两层，旧备份被反复删除并产生误导性警告日志

| 项 | 内容 |
|:---|:---|
| 等级 | **P2** |
| 位置 | `BackupService.cs:38-47` |
| 引入 | `git blame` 确认为 `237e0c8`（batch 6 补日志）引入；原外层循环来自 `27878a7` |

```csharp
if (backupFiles.Count > 14)
{
    foreach (var oldFile in backupFiles.Skip(14))       // ← 外层 oldFile 从未被使用
    {
    foreach (var oldBackup in backupFiles.Skip(14))     // ← 被嵌进了内层
    {
        try { oldBackup.Delete(); } catch (Exception ex) { AppLog.Warn($"...清理过期备份 {oldBackup.Name} 失败...");
    }
    }
}
```

功能后果有限（第一轮内层循环已把过期文件全部删掉，后续重复尝试 `Delete` 抛 `FileNotFoundException` 被 catch 记为 WARN），但：

1. 超过 14 份备份时，**每次启动都会产生 N-14 条假的「清理过期备份失败」警告日志**，污染排查视线；
2. 这是日志批次引入的回归，说明「补日志」批次缺一处自查。

**建议**：删除外层循环（保留一层 `foreach (var oldBackup in ...)`）。另注：同方法 `:33-35` 的 `.tmp` 过滤是死代码——`GetFiles("notes_*.db")` 模式本身匹配不到 `.tmp` 结尾的文件（上轮 F-P3-6 已指出，本次确认仍在）。

### N-3　便签打开瞬间与快速关闭之间存在 IsOpen 写入竞态（低概率，窗口极小）

| 项 | 内容 |
|:---|:---|
| 等级 | **P2**（概率极低）|
| 位置 | `WindowManager.cs:140`（`_ = PersistIsOpenOnOpenAsync(note)` fire-and-forget）vs `:111-118`（`Closed` 回调写 `isOpen:false`）|

打开便签时用 fire-and-forget 写 `IsOpen=true`；若用户在写入完成前立即关闭窗口，`Closed` 回调写 `IsOpen=false`，两条 UPDATE 各自独立连接、执行顺序无保证。若「打开写入」后落地，用户已关闭的便签下次启动会重新出现。SQLite 同一进程内执行顺序大概率与发起顺序一致，实际触发需要毫秒级手速，故仅定级 P2。**建议**：`PersistIsOpenOnOpenAsync` 与 `Closed` 回调共用一个按 noteId 串行的写入队列，或打开写入改为同步等待后再 `Show()`。

### N-4　托盘迁移后遗留的误导性日志文案

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `TrayIconService.cs:72` |

`AppLog.Info("...托盘图标创建成功 (Hardcodet.NotifyIcon.Wpf)")` —— 项目已从 Hardcodet 迁移到其继任分支 **H.NotifyIcon.Wpf**，日志里写的还是旧库名。排查问题时会误导。1 行修复。

### N-5　托盘单击与双击命令重复绑定，双击会触发 3 次激活

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `TrayIconService.cs:58-59` |

`LeftClickCommand` 与 `DoubleClickCommand` 绑定同一命令。双击时 Shell 会先送左键抬起再送双击事件，激活动作被幂等地执行多次。无可见副作用（`OpenOrActivateNotesListWindow` 幂等），但属于冗余写法；若未来激活逻辑变重（如带动画）会放大。

### N-6　归档窗口搜索框占位文案承诺了不存在的 Ctrl+F 快捷键

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `ArchivedNotesWindow.xaml:89`（`PlaceholderText="搜索已归档便签... (Ctrl+F)"`）；`ArchivedNotesWindow.xaml.cs` 全文无任何 Ctrl+F / `PreviewKeyDown` 处理 |

主列表窗口有完整的 Ctrl+F 处理，归档窗口没有。用户按提示按键无响应。要么补快捷键，要么去掉占位文案中的括号提示。另：归档窗口的键盘可达性整体缺失（上轮 F-P1-10 剩余项，见 §3）。

### N-7　SearchService 高耗时日志把「扫描便签数」错打成命中数

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `SearchService.cs:171` |

`AppLog.Warn($"...扫描便签数={results.Count}，命中={results.Count}")` —— 两个占位符都用了 `results.Count`，「扫描便签数」永远是命中数（未命中时甚至打出 0），统计信息失真。

### N-8　`RestoreWindowPlacement` 的异常日志文案复制粘贴错误

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `NotesListWindow.xaml.cs:684` |

恢复窗口位置失败时日志写的是「**保存**主窗口位置失败」。1 行修复。

### N-9　一批托盘测试在迁移后断言的是已死代码，绿灯是假信心

| 项 | 内容 |
|:---|:---|
| 等级 | **P2**（测试有效性问题）|
| 位置 | `LifecycleAndReliabilityTests.cs:470-541`（`F_P1_8_TrayIcon_DeclaresVersion4`、`F_P1_8_TrayEventCode_MustBeDecodedFromLowWord`、`F_P1_8_TrayIconFlags_KeepStandardTooltipUnderV4`、`F_P1_8_ContextMenuClose_PostsCancelMode`）与 `:858-891`（读源码字符串断言）|

托盘迁移到 H.NotifyIcon 后，生产代码**不再调用** `ClassifyTrayEvent` / `TrayNotifyIconFlags` / `NIM_SETVERSION` / `WM_CANCELMODE`（`rg` 确认这些符号只剩 `NativeMethods.cs` 定义与测试引用）。上述测试仍在为这些「生产路径已死」的常量与纯函数断言，且 `F_P1_8_ContextMenuClose_PostsCancelMode` 只是断言「常量等于它自己」（`Assert.AreEqual(0x001F, NativeMethods.WM_CANCELMODE)`），对真实托盘行为零覆盖。另外 `F_P1_8_TrayIcon_DoesNotStealFocusFromShell` 通过**读取 TrayIconService.cs 源码文本**做字符串断言——源码扫描式测试与目录布局强耦合，且锁死实现细节而非行为。**建议**：迁移后这些用例应改写为对 `TaskbarIcon` 配置的行为断言（已有的 `H_NotifyIcon_InitializesProperly_AndDisposesCleanly` 是正确方向），删除常量断言与源码扫描用例；`NativeMethods` 的整个托盘 interop 区段（`Shell_NotifyIcon`/`NOTIFYICONDATA`/`ExtractIcon`/`CopyIcon`/`GetCursorPos` 等，`NativeMethods.cs:193-353`）如无保留计划应一并删除（约 160 行死代码）。

### N-10　`App.OnStartup` 的后半段（托盘/热键初始化、主窗口构建）仍无异常兜底

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `App.xaml.cs:138-164` |

F-P1-3 的修复覆盖了数据库初始化（步骤 5）与便签恢复（步骤 7）两段，但步骤 9-10（`TrayIconService.Initialize()`、`HotKeyService.Initialize()`、`new NotesListWindow()` + `Show()`）不在任何 try/catch 内。此处若抛异常（如 XAML 解析失败），会落入 `DispatcherUnhandledException` 且 `IsRecoverable` 返回 false → 进程崩溃退出。与修复前的「僵尸进程占死 Mutex」相比已安全得多（`Program.Main` 的 finally 会释放 Mutex，重启可用），但用户只会看到一次无提示的崩溃。低概率，可顺手把这几步并入 try/catch + `ShowStartupFailure`。

### N-11　`HotKeyService` 部分注册成功后不重试失败的那一个

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `HotKeyService.cs:64,92` |

`_isRegistered = successN || successH`（F-P1-9 修复）解决了「全失败仍置位」，但引入了新边界：若只成功一个（如 N 成功、H 被占用），`_isRegistered=true` 使后续 `RegisterHotKeys()` 直接 return，**成功的那个也不会重试失败的那个**，直到用户手动关/开一次开关（关时会 Unregister 两个，重开时两个都重试）。另外注册结果只进日志，设置页没有任何可见反馈（上轮修复建议的「设置页显示实际注册状态」未做）。

### N-12　`IsRecoverable` 的注释与行为相反

| 项 | 内容 |
|:---|:---|
| 等级 | **P3** |
| 位置 | `App.xaml.cs:171-189` |

`// 纯 UI/输入层的瞬时异常，不涉及数据与资源状态` 注释下面列出的 `InvalidOperationException`/`NotSupportedException`/`XamlParseException` 全部映射为 `false`（不可恢复）。注释读起来像「这些算可恢复」，实际语义相反。改注释即可，行为本身（从严放行）是上轮 F-P2-28 的正确修复。

---

## 3. 上轮已知缺陷的存留核验（重点条目）

以下逐条在当前代码复核过行号与状态，**均为上轮已记录、本轮确认仍未修复**的项（不是新发现）：

| 编号 | 状态 | 当前位置与说明 |
|:--|:--|:---|
| F-P2-11 | ❌ 仍在 | `NoteViewModel.cs:284-285`（`FlushSaveAsync`：`FlushAsync` 内部已 `SaveAsync`，外层又 `SaveAsync(Note)` 一次）；同模式 `:253-254`（`FlushSaveBlocking`）。每次失焦/关窗双写一次全量 UPDATE |
| F-P2-13 | ❌ 仍在 | `WindowManager.cs:328`（`NavigateToHit` 同步阻塞 `GetByIdAsync`）；`:217-223`（退出落盘逐窗口 `.GetAwaiter().GetResult()`，上轮已证伪死锁、实为阻塞） |
| F-P2-14 | ❌ 仍在 | 主窗口激活逻辑三份：`WindowManager.cs:241-308`、`NotesListWindow.xaml.cs:126-145`、`WndProc :159-175`（含 `:169-170` 的 `Topmost=true/false` 抢焦点 hack） |
| F-P2-19 | ❌ 仍在 | `NotesListViewModel.cs:352,405,435,470` 在内容/元数据/删除/恢复回调里手动重入 `OnSearchTextChanged`，搜索风暴根因未动 |
| F-P2-3 | ❌ 仍在 | `SqliteDatabaseContext.cs:21` `Cache = SqliteCacheMode.Shared` 与 WAL 并用 |
| F-P2-6 | ❌ 仍在 | 备份仍只在启动时跑（`App.xaml.cs:115`），连续运行多日只有一份；文件名本地日期 vs 内容 UTC 口径不一；无恢复入口 |
| F-P2-8 | ❌ 仍在 | `ArchivedNotesViewModel.cs:29-64` 手写复制的匹配规则（比 `SearchService` 多匹配 `DisplayTitle`），两份实现继续漂移 |
| F-P2-9 | ❌ 仍在 | `NoteRepository.cs:133-165` 全字段覆盖 UPSERT（主列表与便签窗口持有不同实例的隐患同上轮） |
| F-P2-15 | ❌ 仍在 | `SettingsViewModel.cs:188,203,216,238,247,271,284,306` VM 直接调 `PinSetupDialog`/`MessageBox`/文件对话框（已加 `GetOwnerWindow` 归属窗口，属缓解非根治） |
| F-P2-17 | ❌ 仍在 | `ArchivedNotesViewModel.cs:99-102` async void lambda 注册（`LoadArchivedNotesAsync` 抛异常即崩进程）；`AppMessages.cs:26-37` 的 `NoteUpdatedMessage`/`NoteDeletedMessage` 全仓无发送方，`NotesListViewModel.cs:65-66` 仍注册监听（rg 确认） |
| F-P2-23 | ❌ 仍在 | `AutoSaveCoordinator.cs:55` 每次调度一个 `Task.Run` + CTS（F-P1-7 的所有权修复正确，但短命对象 churn 仍在） |
| F-P2-24 | ⚠️ 部分 | 保留期清理已实现（`AppLog.cs:87-106`）；每行 `File.AppendAllText` 开关文件仍在（`AppLog.cs:56`） |
| F-P2-25 | ❌ 仍在 | `SearchService.cs:50-52` 每便签最多 3 轮全文扫描（`isDirectMatch`/`isCompactMatch`/`isAllTokensMatch`） |
| F-P2-26 | ❌ 仍在 | 截图测试仍无像素级断言（有 40+ 条属性/键盘断言，口径按上轮 §12.3 更正后的表述） |
| F-P2-27 | ❌ 仍在 | `TestEnvironment.cs:164` `waitHandle.Wait()` 无超时；`:213,249` 截图路径 `..\..\..\..\..` 上溯 5 层；5 个测试文件各自实现 `INoteRepository` Fake（rg 确认 6 处定义） |
| F-P1-10 | ⚠️ 部分 | `ArchivedNotesWindow.xaml:137-141` 仍是 `ScrollViewer > ItemsControl` 无虚拟化；无键盘导航；`:79,:95` 归档 `MessageBox` 未设 `Owner`。UTC 时间与标题已修（本次确认） |
| F-P1-11 | ⚠️ 部分 | Tab 被困编辑器仍在（`DesignTokens.xaml:185` `TabNavigation="None"` + `NoteWindow.xaml:272` `AcceptsTab="True"`）；`FocusVisualStyle="{x:Null}"` 全站仍在；无高对比度主题。无障碍标注与 LiveSetting 已补（本次确认） |
| F-P3-2 | ⚠️ 部分 | 本轮 rg 复核仍存活的死符号：`PinDialogResult`（`PinSetupDialog.xaml.cs:15`）、`NoteColorExtensions.GetThemeColors`（`NoteColor.cs:47`）、`BuildSegments`（仅自测调用）、`SoftDeleteAsync` 别名（仅仓储测试调用 + 5 份 Fake 实现）、`NoteUpdatedMessage`/`NoteDeletedMessage`、`SearchBox_KeyDown`（仅测试调用）、`NativeMethods` 托盘区段（见 N-9）、设计令牌 `Elevation.Window`/`Elevation.CardHover`/`CornerRadius.Window`/`Type.BodyStrong`/`Spacing.*`（rg 确认 `Elevation.Card`/`Elevation.Popup` 在用，勿误删） |
| F-P3-4 | ❌ 仍在 | `Note.cs:63-72` `IsPinned` 陷阱属性仍被 `NoteRepository.BindNoteParameters:351` 使用（读=列表置顶、写=两字段同写） |
| F-P3-20 | ⚠️ 部分 | 仍在的子项：`FriendlyDateTimeConverter.cs:19` 未来时间显示「刚刚」+ culture 被忽略；`SettingsViewModel.cs:157` RuntimeInfo 硬编码 ".NET 8.0"；置顶筛选为空无空状态提示（`NotesListWindow.xaml` 仅 `HasNoNotes` 全空态）；`PinSetupDialog` 重输时错误提示不清除（`ShowError` 无对应清除逻辑） |

---

## 4. 上轮修复项抽验结果（确认真实生效的）

本次对上轮声称「已修复」的高优先级项做了代码级抽验，结论如下（均为正面确认）：

| 编号 | 抽验结论 |
|:--|:---|
| F-P0-1 | ✅ `IsOpen` 单一写入口成立：创建写 true（`WindowManager.cs:130-140`）/用户关闭写 false（`:103-120`）；退出走 `BeginShutdownAndPersistPinnedPlacement`（先存坐标 → 再标记退出，`:186-193`）；托盘退出菜单顺序正确（`TrayIconService.cs:209-219`） |
| F-P0-2 | ✅ 广播载荷为 `Note.Content`（`NoteViewModel.cs:300`，含防回退注释） |
| F-P1-1（子项 1） | ✅ `HandlePreviewKeyDown` 首行 `if (PinOverlay.IsLocked) return;`（`NotesListWindow.xaml.cs:397`），锁定态由遮罩 `PinBox` 接管焦点（`PinLockOverlay.cs:47-53`） |
| F-P1-4 | ✅ `Closing` 改同步 `FlushSaveBlocking()`（`NoteWindow.xaml.cs:59-69`） |
| F-P1-5 | ✅ V1 建表不含 V2 两列（`SqliteDatabaseContext.cs:85-99`）；V2 迁移 `PRAGMA table_info` 探测后按需 ALTER、失败回滚并 throw（`:118-152`）；新旧库走同一路径 |
| F-P1-6 | ✅ `.tmp` + `File.Replace` + `.bak` 兜底（`SettingsService.cs:159-187`）；PIN 数据损坏显式告警（`:205-219` → 设置页红色警示 `SettingsWindow.xaml:299-304`） |
| F-P1-7 | ✅ CTS 所有权单一化实现正确：`ScheduleSave` 只 `Cancel`（`AutoSaveCoordinator.cs:51`），持有任务 `finally` 释放（`:82`），落盘前 `TryRemove(KeyValuePair)` 比对（`:66`） |
| F-P1-9 | ✅ `_isRegistered = successN \|\| successH`（`HotKeyService.cs:92`）；衍生小边界见 N-11 |
| F-P1-12 | ✅ 底部状态绑定真实 `SaveState`，失败态危险色 + 重试命令（`NoteWindow.xaml:293-333`） |
| F-P1-2 | ✅ 导出 DTO 含 `IsPinnedInList`/`AlwaysOnTop`/`IsOpen`，导入回落安全、批量落库（`ExportImportService.cs`）；导入后经 `NotesReloadedRequestedMessage` 全量重载（`SettingsViewModel.cs:280` → `NotesListViewModel.cs:68-79`），幽灵便签路径已消除 |
| F-P2-21 / F-P2-22 | ✅ 转换器全部改查表 + `Freeze()`（`NoteColorConverters.cs:15-56`）；7 处 Trigger 引用固定效果单例（`DesignTokens.xaml:67-70`），无内联重建 |
| F-P2-28 | ✅ `IsRecoverable` 从严放行（`App.xaml.cs:171-189`；注释问题见 N-12） |
| F-P2-10 | ✅ `Deactivated` 有 `HasPendingChanges` 短路（`NoteWindow.xaml.cs:51`） |
| F-P3-12/14/16/17/18、F-P3-5/9、F-P3-13 | ✅ 抽验均落实（`Type.CaptionSubtle` 带 Wrap、三窗口标题各异、MoreMenu 只开不关由 Popup 收口、`CaretIndex` 替代 `SelectAll`、右键先选中、`ConvertBack` 返回 `Binding.DoNothing`、几何常量单源 + SQL 默认对齐、自启联动禁用） |

---

## 5. 工程架构与代码质量评价

**做对的（应保持）**：

1. **分层清晰**：Models / Data / Services / ViewModels / Views 边界清楚，依赖方向正确（除 F-P2-15 的设置页 VM→View 泄漏外无越层）。DI 注册与消费一致（F-P2-12/16 的绕过 DI 路径已根除）。
2. **退出时序设计正确**：`IsOpen` 单一写入口 + `BeginShutdownAndPersistPinnedPlacement` 原子顺序，是对 WPF「窗口 Closed → App.OnExit」时序的正面应对，注释把原因写得很清楚（这是本项目注释质量的普遍优点——注释解释「为什么」而不是复述「是什么」）。
3. **依赖克制**：6 个包各有所用（WPF-UI 的 `ui:` 命名空间在全部窗口实际使用；H.NotifyIcon 取代了约 160 行手写互操作，是正确的减法）。
4. **构建质量闸门真实有效**：`TreatWarningsAsErrors` + `global.json` + 可复现版本号 + `.editorconfig`，本次实测 0 警告。
5. **数据可靠性机制齐全**：WAL + `VACUUM INTO` 快照备份 + 原子设置写入 + PIN 损坏显式告警 + 启动失败可读弹窗。

**结构性短板（与上轮一致，未恶化）**：

1. **数据一致性模型是「多实例 + 全字段覆盖」**：同一便签在主列表、便签窗口持有不同 `Note` 实例，靠消息单向同步 + 全量 UPSERT 落库（F-P2-9/F-P2-11）。F-P0-2 那类污染事故的土壤仍在，任何新的「写回」路径都可能重演。根治方向是部分字段更新方法或单一权威实例。
2. **UI 线程上仍有 sync-over-async**（F-P2-13），退出路径与 `NavigateToHit` 的阻塞是可感知卡顿的潜在来源。
3. **消息总线缺少统一线程契约**：`NoteContentChangedMessage` 从后台线程发出、`HandleNoteContentChanged` 手工 `Dispatcher.Invoke`，`ArchivedNotesViewModel` 则用 async void lambda 直连——两种模式并存，后者是已知崩溃点（F-P2-17）。建议统一「消息处理一律经 Dispatcher」的辅助封装。
4. **多显示器与混合 DPI 是当前最大的未覆盖场景**（N-1 是第一个实证缺陷，`app.manifest` 声明了 PerMonitorV2 但窗口定位代码全部按主屏思考）。

---

## 6. 测试体系评价

- **103 项全绿、覆盖面广**是真实投入：仓储/迁移/导入导出/备份/PIN/搜索/并发/生命周期/键盘导航/截图渲染各有专门用例；`F_P1_5_*`（新旧库两条迁移路径）、`F_P1_2_*`（三态往返 + 旧备份回落）等回归用例与缺陷一一对应，是好实践。
- **两类测试需要清理**：① 断言死代码的托盘常量测试（N-9）；② 源码文本扫描测试。它们给出「全绿」的表象但不对行为负责，且会在下次重构时误报。
- **基建短板未动**（F-P2-27）：`RunInSta` 无超时意味着一条挂死用例会挂死整个测试进程；截图路径上溯 5 层耦合目录结构；6 份重复的 `INoteRepository` Fake（`rg "class .*: INoteRepository"` 可数）约 200 行重复。这三项是低成本高收益的清理对象。
- F-P0-1 漏检的教训（测试顺序写反给假绿灯）在本轮新增用例中未见重演，值得肯定。

---

## 7. 建议处理顺序

1. **立即（小改动）**：N-2（删一层 foreach）、N-4/N-7/N-8/N-12（四处 1 行文案/注释）、N-6（补 Ctrl+F 或改占位文案）。
2. **短期**：N-1 多显示器定位（影响核心卖点，建议用 `Screen.FromPoint` 按便签所在屏判定）；N-9 清理死托盘测试 + `NativeMethods` 死区段；N-3 打开/关闭 IsOpen 竞态串行化；N-10 启动后半段补兜底。
3. **与上轮清单合并推进**：F-P2-11 双写（删 `FlushSaveAsync`/`FlushSaveBlocking` 里的外层 `SaveAsync`，1-2 行）→ F-P2-19 搜索风暴 → F-P1-10 剩余（归档虚拟化 + 键盘）→ F-P1-11 剩余（Tab 移焦 + 焦点样式）→ F-P2-27 测试基建。
4. **长期**：F-P2-9 部分字段更新、深色/高对比主题、i18n。

---

*报告生成：2026-10-03 21:38 (GMT+8) · 基线 commit `025ca62` · 构建 0 错误 0 警告 · 测试 103/103 通过。本报告所有缺陷均在当前代码逐行核实，未复述任何未经验证的历史结论。*
