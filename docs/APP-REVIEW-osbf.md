# StickyNotes 工程评审报告 —— 架构 / 功能对标 / UI·UX 三维差距与修复路线

> **文档版本**：v1.0.0
> **评审日期**：2026-10-02 10:45 (GMT+8)
> **评审对象**：StickyNotes @ `909a00c`（WPF / .NET 8 / SQLite）
> **评审基线（实测）**：`dotnet build -c Release` → 0 错误 0 警告；`dotnet test -c Release` → 30/30 通过
> **结论摘要**：工程底子扎实、核心差异化（精准跳行）实现正确且有测试覆盖；但**当前状态属"能演示、不能交付"**——存在 5 个 P0 级数据可靠性缺陷（其中"正常退出必丢桌面布局"已实测复现），且缺失桌面便签的三个命门功能（托盘 / 全局热键 / 开机自启），UI/UX 只做了浅色一套设计系统且完全不支持键盘与高对比度。

---

## 目录

- [0. 评审方法与实证基线](#0-评审方法与实证基线)
- [1. 总体评分卡](#1-总体评分卡)
- [2. P0 阻断级缺陷（数据可靠性）](#2-p0-阻断级缺陷数据可靠性)
- [3. P1 严重问题（架构与性能）](#3-p1-严重问题架构与性能)
- [4. P2 中等问题](#4-p2-中等问题)
- [5. 功能对标：与业界优秀同类 app 的差距](#5-功能对标与业界优秀同类-app-的差距)
- [6. UI/UX 差距](#6-uiux-差距)
- [7. 工程化与测试体系](#7-工程化与测试体系)
- [8. 修复路线图（分阶段）](#8-修复路线图分阶段)
- [9. 附录 A：文档与代码不一致清单](#9-附录-a文档与代码不一致清单)
- [10. 附录 B：关键代码位置索引](#10-附录-b关键代码位置索引)

---

## 0. 评审方法与实证基线

### 0.1 验证手段

本报告所有结论均基于**代码逐行核对 + 构建/测试实测 + 一次性探针实验**，不含推测。

| 验证项 | 命令 / 手段 | 结果 |
|:---|:---|:---|
| 编译 | `dotnet build src/StickyNotes/StickyNotes.csproj -c Release` | ✅ 0 错误 0 警告（SDK 10.0.400 / 目标 net8.0-windows） |
| 单元 + UI 渲染测试 | `dotnet test tests/... -c Release` | ✅ 30/30 通过，耗时 ~3s |
| 关键缺陷探针 | 临时新增 `ZZTempProbeTests.cs` 复现"退出后 IsOpen 回写"，运行后**已删除**（`git status` 干净） | ❌ **复现成功：`IsOpen` 被写为 `False`** |
| 缺失能力扫描 | `rg` 全量检索 `SessionEnding / NotifyIcon / RegisterHotKey / HKEY_CURRENT_USER / VirtualizingStackPanel / AutomationProperties / Dark / FTS / busy_timeout / VACUUM / UnobservedTaskException` | 全部 0 命中（下文逐条引用） |
| 主题切换可行性 | 统计 `StaticResource` × 227 vs `DynamicResource` × 3；XAML 内硬编码 hex × 59 | ❌ 运行时换肤不可行 |
| 文件编码 | 字节级校验 `NoteColor.cs` | ✅ 合法 UTF-8（终端乱码仅为控制台代码页问题） |

### 0.2 代码规模

```
src/StickyNotes    15 个 .cs（合计约 1500 行）+ 8 个 .xaml + 2 个资源字典
tests/             6 个测试文件，30 个测试用例
docs/              APP-ARCHITECTURE.md / APP-PRODUCT.md / CHANGES-*.md
```

### 0.3 值得肯定的部分

1. **精准跳行定位（核心差异化）算法正确**。`SearchHit` 采用"绝对字符偏移 + 关键字长度"而非行号（`SearchService.cs:55-64`），规避了自动换行下逻辑行/视觉行的错位问题，这是正确的设计决策，且 `SearchServiceTests` 覆盖了中文、大小写、同行多次命中、首尾行等场景。
2. **UI 真机渲染截图回归测试是业界少见的做法**。`TestEnvironment.cs:148-200` 通过 `VisualTreeHelper.GetDpi` 采样真实物理 DPI 生成 HiDPI PNG，对纯 WPF 项目而言是超出常规的工程投入。
3. **四级路径决议体系设计干净**（`AppPaths.cs:73-94`），`DataDirOverride > 环境变量 > portable.ini > LOCALAPPDATA` 优先级明确，且有专门测试。
4. **依赖克制**。最终 6 个 NuGet（文档写 3 个，见附录 A），无 ORM、无 WebView、无遥测。
5. **软删除 + 归档中心 + 恢复**已实现，比 v1 产品文档承诺的更完整。

---

## 1. 总体评分卡

| 维度 | 评分 | 一句话结论 |
|:---|:---:|:---|
| 架构分层清晰度 | 7.0 / 10 | View / VM / Service / Data 边界基本清晰，但存在 View 间直接依赖具体类型、实体跨窗口共享可变状态两处硬伤 |
| **数据可靠性** | **4.0 / 10** | **5 个 P0 缺陷，其中"退出必丢桌面布局"已实测复现。与项目第一页"可靠性高于一切"直接冲突** |
| 性能 | 5.0 / 10 | 无虚拟化、`AllowsTransparency + DropShadow` 强制软件渲染、搜索 O(n·m)、每次保存重建列表视觉树 |
| 测试与可测性 | 6.0 / 10 | 渲染测试是亮点，但竞态/迁移/备份/导入冲突四类高危场景**零覆盖**，正是 P0 缺陷所在区 |
| 文档与代码一致性 | 3.0 / 10 | 产品文档有 12 处承诺未落地或已漂移（含 1 条 NFR 被实测证伪） |
| 功能完整度（对标 Win Sticky Notes） | 3.0 / 10 | 缺托盘、全局热键、开机自启三个命门；缺图片、清单、链接、标签、版本历史 |
| UI/UX 完成度 | 6.0 / 10 | 浅色下视觉完成度不错（Fluent 2 + 设计 token）；暗色、键盘可达性、高对比度全部缺失 |
| 可访问性 | 2.0 / 10 | 4 处 `FocusVisualStyle={x:Null}`、图标按钮无 `AutomationProperties.Name` |
| 国际化 | 1.0 / 10 | 全部字符串硬编码中文，无 resx，Mutex 名硬编码作者名 |
| 工程化（构建/发布/版本） | 7.0 / 10 | 统一版本号注入、Costura 内嵌、双形态发布 + SHA256；但无 CI、无 global.json、无 .editorconfig |
| **加权总分** | **≈ 4.9 / 10** | 演示级，尚未达交付级 |

---

## 2. P0 阻断级缺陷（数据可靠性）

### P0-1　正常退出必丢桌面布局 —— `IsOpen` 被覆写为 `false`（**已实测复现**）

**证据链**

| 位置 | 行为 |
|:---|:---|
| `WindowManager.cs:77-95` | 窗口 `Closed` 回调调用 `UpdateWindowBoundsAsync(..., isOpen: false)` |
| `NoteWindow.xaml.cs:56-59` | 窗口 `Closing` 回调调用 `ViewModel.FlushSaveAsync()` → `SaveAsync(Note)`，而内存中 `Note.IsOpen == true` |
| WPF 关闭顺序 | `Closing` → `Closed`（**Closed 在后**） |
| `App.xaml.cs:69` | 启动时 `foreach (var note in allActive.Where(n => n.IsOpen))` 恢复贴纸 |

**实测结果**（探针输出）

```
[PROBE] 关闭前 IsOpen=True   UpdatedAt=2026-10-02T02:45:28.6944841Z
[PROBE] 关闭后 IsOpen=False  UpdatedAt=2026-10-02T02:45:29.3634168Z
Assert.AreEqual(true, after.IsOpen) 失败：应为 True，实际为 False
```

**影响**　应用每次正常退出后，数据库中所有便签的 `IsOpen` 均为 `0`，下次启动 `App.xaml.cs:69` 的恢复循环**永远为空**，用户桌面上一张便签都不会出现。这废掉了产品文档 §3.1.2 第 3 条"坐标与状态记忆"、NFR 验收项 TC-05，以及整个"桌面布局"卖点。

**根因**　代码未区分「用户主动关闭这张便签」与「应用整体退出导致的连带关闭」两种语义。

**修复方案**

```csharp
// App.xaml.cs
public static bool IsShuttingDown { get; private set; }

// OnExit / SessionEnding 最先置位
protected override void OnExit(ExitEventArgs e)
{
    IsShuttingDown = true;
    // ... 同步 Flush
}

// WindowManager.cs —— Closed 回调
window.Closed += (_, _) =>
{
    _activeNoteWindows.Remove(note.Id);
    // 退出时仅同步坐标尺寸，IsOpen 保持 true 以便下次恢复
    if (App.IsShuttingDown)
    {
        _ = PersistBoundsAsync(note.Id, window);   // 不写 IsOpen
        return;
    }
    _ = PersistBoundsAsync(note.Id, window, isOpen: false);
};
```

更彻底的方案：删除 `Closed` 中的所有 DB 写入，改为 `App.OnExit` 中统一遍历 `WindowManager.ActiveWindows` 落盘（`IsOpen=true` + 坐标尺寸），单点写库、语义唯一、天然幂等。**推荐此方案。**

**回归测试（必须补）**
```csharp
[TestMethod] public void Shutdown_RestoresAllOpenStickyWindows()
{
    // 开 3 张贴纸 → 模拟 Application.Shutdown() → 重新初始化 App 启动路径
    // 断言：3 张全部恢复，且 Left/Top/Width/Height 与关闭前一致
}
```

---

### P0-2　导入 JSON 备份会静默回滚更新的便签（无 `UpdatedAt` 冲突裁决）

**证据**　`ExportImportService.cs:143` 直接 `await _repository.SaveAsync(note)`；`NoteRepository.cs:138` 的 `SaveAsync` 是 UPSERT（`ON CONFLICT(Id) DO UPDATE`），而导入项携带原始 `Id`（`ExportImportService.cs:128`）。

**影响**　用户导入一份两周前的备份 → 当前所有**更新的**便签内容被无条件回滚，**无预览、无冲突计数、无二次确认**。这是不可逆的数据丢失，且用户主观上以为自己在"恢复"。

**修复方案**

1. 导入改为**两阶段**：`AnalyzeAsync(path)` 返回 `{ NewCount, ConflictCount, SkipCount, Conflicts[] }`，UI 先弹预览对话框（列出前 N 条冲突的"本地 vs 备份"内容对比 + 时间）。
2. 逐条比较 `UpdatedAt`，默认策略 `Skip`（保留较新），用户可切换 `Overwrite` / `KeepBoth`（`KeepBoth` 时重新分配 Guid）。
3. 整批导入包裹在单个 SQLite 事务中（`SqliteDatabaseContext` 需暴露 `BeginTransaction()`），失败则整体回滚。
4. 导入前自动生成一次当前全量快照（复用 P0-3 修好的备份能力）。

---

### P0-3　WAL 模式下裸拷数据库，备份不可靠

**证据**　`BackupService.cs:25` `File.Copy(dbPath, targetBackupFile, overwrite: true)`；`SqliteDatabaseContext.cs:41` 启用 `journal_mode = WAL`。

**影响**
- WAL 模式下，**最后一次 checkpoint 之后的所有提交都存放在 `notes.db-wal` 中**。仅复制主库文件得到的备份会丢失这部分数据 —— 也就是说"每天的自动备份"很可能缺少用户当天写的内容。
- SQLite 官方明确禁止在存在活跃连接时直接拷贝 db 文件：文件增长过程中可能拷到半截数据页。

**修复方案**

```csharp
// 使用 SQLite 3.27+ 的 VACUUM INTO：单语句产出事务一致、已压缩的全新数据库
await using var cmd = connection.CreateCommand();
cmd.CommandText = "VACUUM INTO $target;";
cmd.Parameters.AddWithValue("$target", targetBackupFile);
await cmd.ExecuteNonQueryAsync();
```

同时：`BackupService.RunDailyBackupIfNeeded()` 当前在 `App.xaml.cs:62` 于窗口恢复**之前**同步执行（阻塞 UI）。应改为：先 Flush 全部脏数据 → `Task.Run` 后台执行 → 失败仅记日志，不影响启动。

---

### P0-4　`App.OnExit` 是 `async void`，退出刷盘不被等待

**证据**　`App.xaml.cs:80` `protected override async void OnExit(ExitEventArgs e)`，内部 `App.xaml.cs:88` `await coordinator.FlushAllAsync();`

**影响**　WPF **不会 await** `OnExit`。方法在第一个真正的挂起点返回，WPF 随即继续关闭窗口、运行终结器、退出进程。关机 / 任务管理器"结束任务"场景下，防抖窗口内的最后一批输入会丢失 —— 这恰好是 NFR 承诺的"最多丢 500ms"，而实际是"退出瞬间可能丢一批"。

**伴随缺陷**　`SessionEnding`（关机 / 注销）**完全未实现**（`rg SessionEnding` 全项目 0 命中），而 `APP-PRODUCT.md` §3.1.5 第 2 条明确承诺"应用程序捕获 `App.Exit`、`SessionEnding`（Windows 关机或注销）时，无条件 Flush 内存脏数据"。**承诺未落地。**

**修复方案**

```csharp
protected override void OnExit(ExitEventArgs e)   // 回到同步签名
{
    IsShuttingDown = true;
    _serviceProvider?.GetService<AutoSaveCoordinator>()?.FlushAllBlocking();
    _instanceMutex?.ReleaseMutex();
    _instanceMutex?.Dispose();
    _serviceProvider?.Dispose();
    base.OnExit(e);
}
```

`AutoSaveCoordinator` 增加 `FlushAllBlocking()`：内部 `GetAwaiter().GetResult()`。此处 UI 线程已无消息泵待处理，且 `Microsoft.Data.Sqlite` 为同步 I/O，不会死锁。

补充 `Microsoft.Win32.SystemEvents.SessionEnding` 处理器（需在 `OnExit` 前后正确 `Dispose` 订阅），至少覆盖 `SessionEndingReason.Shutdown` / `LogOff`。

**同时修复**　`App.xaml.cs:44` 在 `DispatcherUnhandledException` 中执行 `FlushAllAsync().GetAwaiter().GetResult()` —— 当前因 SQLite 同步 I/O 侥幸不死锁，但一旦任一 `SaveAction` 引入真实异步就会**死锁 UI**。应抽到上面同一个 `FlushAllBlocking()` 里，并加 `#if DEBUG` 断言避免在有消息泵的线程上调用。

---

### P0-5　Release 下所有异常处理被编译器优化掉，等于没有日志

**证据**　全项目 12 处错误处理**全部**是 `System.Diagnostics.Debug.WriteLine`：

```
BackupService.cs:44          SettingsService.cs:68
AutoSaveCoordinator.cs:70    WindowManager.cs:93
App.xaml.cs:48               NotesListWindow.xaml.cs:200, 219
```

`Debug.WriteLine` 带 `[Conditional("DEBUG")]`，**Release 编译后整段被移除**。而 `AppPaths.LogsDirectory`（`AppPaths.cs:155`）虽然定义了目录、测试也断言其被创建（`PortableModeAndPathTests.cs:83`），但**生产代码从不写入任何文件**（`rg LogsDirectory` 仅命中定义与测试）。

**雪上加霜**　`App.xaml.cs:39-50` 的 `DispatcherUnhandledException` 直接 `args.Handled = true` 静默吞掉异常，且 `AppDomain.CurrentDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException` 均未注册（0 命中）。

**影响**　用户遇到崩溃只会觉得"应用莫名其妙没反应"；开发者拿到用户反馈后**没有任何可诊断信息**。这与 `APP-ARCHITECTURE.md` §1.1 "可靠性高于一切"形成直接矛盾。

**修复方案**

1. 新增极简 `Infrastructure/AppLog.cs`（不引第三方库）：
   - 路径 `AppPaths.LogsDirectory/app-YYYYMMDD.log`
   - 单文件按日切分，> 2 MB 轮转保留 3 份
   - `lock` + 追加写；`Info/Warn/Error(string, Exception?)` 三级
   - 异步写队列（`BlockingCollection`）避免阻塞 UI
2. 全量替换 12 处 `Debug.WriteLine` → `AppLog.Warn/Error`。
3. 注册三层异常兜底：
   - `DispatcherUnhandledException` → 落盘 + 弹"程序遇到问题"对话框（含「复制详情」按钮）+ `Handled = true`（仅对可恢复异常）
   - `AppDomain.CurrentDomain.UnhandledException` → 落盘
   - `TaskScheduler.UnobservedTaskException` → 落盘 + `SetObserved(true)`
4. 日志中**禁止**记录便签正文（隐私）。

---

## 3. P1 严重问题（架构与性能）

### P1-1　搜索在后台线程遍历 UI 绑定的 `ObservableCollection`（竞态）

**证据**　`NotesListViewModel.cs:103-110`

```csharp
_ = Task.Run(async () =>
{
    await Task.Delay(300, token);
    var hits = _searchService.Search(Notes, value);   // ← Notes 是绑定到 ItemsControl 的集合
```

`Notes` 是绑定在 `NotesListWindow.xaml:184` `ItemsControl` 上的 `ObservableCollection<Note>`。后台线程 `foreach` 枚举的同时，UI 线程正在 `HandleNoteDeleted`(`:242`)、`HandleNoteCreated`(`:261`)、`HandleNoteUpdated`(`:216/:226`)、`LoadNotesAsync`(`:75-79`) 中做 `RemoveAt / Insert / Clear`。

**影响**　枚举期间集合被改 → 抛 `InvalidOperationException`（被 `catch (OperationCanceledException)` 之外的路径吞掉，表现为"搜索偶尔没反应"），或漏项 / 重复项。贴纸窗口越多、编辑越频繁越易触发。

**修复**　后台线程只读**不可变快照**：

```csharp
private IReadOnlyList<Note> _searchSnapshot = Array.Empty<Note>();
// 所有 UI 线程上的集合变更点结束后统一：
private void RefreshSearchSnapshot() => _searchSnapshot = Notes.ToArray();
// 搜索时：_searchService.Search(_searchSnapshot, value)
```

---

### P1-2　每次防抖保存都触发列表 `RemoveAt + Insert` 重排（列表抖动）

**证据链**

| 位置 | 行为 |
|:---|:---|
| `NoteViewModel.cs:68-72` | 每次防抖落盘后 `Send(new NoteUpdatedMessage(n))` |
| `NotesListViewModel.cs:213-226` | `Notes.ToList().FindIndex(...)` → `RemoveAt(idx)` → 线性找插入位 → `Insert(pos, updated)` |
| `NotesListViewModel.cs:223` | 判据 `updated.UpdatedAt >= cur.UpdatedAt`，而 `UpdatedAt` 刚被刷成 `DateTime.UtcNow` → **每次都插到最前** |
| `NotesListViewModel.cs:233` | 若正在搜索，额外再触发一次 `OnSearchTextChanged` → 300ms 后整体重建 `SearchResults` |

**影响**
- (a) 用户在贴纸上每停顿 500ms，主列表中该便签卡片就**跳到顶部并重建视觉树**（丢失 hover / 焦点态、可见闪烁）；
- (b) `Notes.ToList()` 每次分配一个 `List`，O(n) 起步；
- (c) 边打字边出结果时结果列表持续抖动。

**修复**　消息分类（关键设计）：

```csharp
// 内容变更：静默原地更新卡片，不重排、不重建
public sealed record NoteContentChangedMessage(Guid NoteId, int CharCount, DateTime UpdatedAt);
// 元数据变更：置顶 / 颜色 / 归档 —— 需要重排
public sealed record NoteMetaChangedMessage(Note Note);
```

`NoteUpdatedMessage` 拆分后，`HandleContentChanged` 只更新卡片的字数与时间戳两个绑定属性。

---

### P1-3　`Note` 实体不实现 `INotifyPropertyChanged`，且被列表 VM 与贴纸 VM 共享同一实例

**证据**　`Note.cs` 是纯 POCO；`WindowManager.cs:42-43` 把调用方传入的 `Note` 直接交给 `vm.Initialize(note)`，而这个 `Note` 正是 `NotesListViewModel.Notes` 集合中的同一对象（`NotesListViewModel.cs:142-143` 新建时也是同一个实例）。

**影响**　这是 P1-2 抖动的根因：由于无法做局部更新，只能整条 `Remove + Insert`。同时贴纸窗口的**每一次按键都在改列表 VM 正在渲染的对象**，形成跨窗口共享可变状态 —— 无法安全扩展（多窗口编辑同一便签、未来加同步都会立刻失控）。

**修复**　保留 `Note` 为纯领域模型（这是好设计，不要污染），新增投影层：

```csharp
public sealed partial class NoteRowViewModel : ObservableObject
{
    public Guid Id { get; }
    public string Preview { get; }        // 由 Content 派生，一次性计算
    public string DisplayTitle { get; }
    public int CharCount { get; }
    public DateTime UpdatedAt { get; }
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private NoteColor _color;
}
```

`Notes` 集合类型由 `ObservableCollection<Note>` 改为 `ObservableCollection<NoteRowViewModel>`。

---

### P1-4　"置顶"被合并为两个正交概念

**证据**　`NoteWindow.xaml:14` `Topmost="{Binding IsPinned}"`；`Note.cs:23` 注释为"是否在桌面置顶（Topmost）"；而 `NotesListViewModel.cs:29-32` 又用同一个 `IsPinned` 决定列表排序。

**影响**　用户想让一张便签排在列表最前，就必须让它**永远压在所有其他窗口之上**；反之亦然。Windows Sticky Notes 明确把这两个能力分开（*Pin to notes list* / *Always on top*）。这是概念模型缺陷，不是实现 bug。

**修复**　`PRAGMA user_version = 2` 拆列：

```sql
ALTER TABLE Notes ADD COLUMN IsPinnedInList INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Notes ADD COLUMN AlwaysOnTop    INTEGER NOT NULL DEFAULT 0;
-- 迁移时从旧 IsPinned 同时赋值两者，保证行为不回退
```

UI 映射：贴纸工具栏图钉 = `AlwaysOnTop`；卡片右键"置顶" = `IsPinnedInList`。

---

### P1-5　单实例互斥量名硬编码 + `Global` 作用域 + 唤醒目标错误

**证据**　`App.xaml.cs:25`

```csharp
const string mutexName = "Global\\StickyNotes_App_Instance_Mutex_mcxiaoke";
```

`NativeMethods.BringExistingInstanceToFront()`（`App.xaml.cs:140-148`）用 `FindWindow(null, "彩色便签")` 查找，而 `"彩色便签"` 是**贴纸窗口**的 `Title`（`NoteWindow.xaml:6`）—— 主窗口、4 个贴纸、归档、设置窗口**全部共用这个标题**。

**影响**

| 问题 | 后果 |
|:---|:---|
| 名字里写死作者名 `mcxiaoke` | 不可分发 |
| `Global\` 是跨会话全局 | 同一台机器的多用户 / 多 RDP 会话互相踢 |
| 名字不含数据目录 | 同时运行"安装版"与"便携版"（各写各的 `notes.db`）时第二个实例被无理由踢掉 |
| `FindWindow` 按标题查找 | 随机唤醒**某张便签**而非主窗口；标题重名 / 多语言环境下不可靠 |

**修复**　Mutex 名 `Local\` + 程序集名 + 数据目录路径哈希；唤醒改用 `RegisterWindowMessage("StickyNotes.Activate")` + `PostMessage(hwnd, msg, 0, 0)`，主窗口显式处理该消息。

---

### P1-6　`synchronous` PRAGMA 只在迁移连接上生效；无 `busy_timeout`

**证据**　`NoteRepository` 的**每个**方法都 `_context.CreateConnection()` 开新连接；而 `SqliteDatabaseContext.cs:41` 的 `PRAGMA synchronous = NORMAL` 只在 `InitializeAndMigrateAsync` 的那一个连接上执行过一次。

**事实**　`synchronous`（以及 `foreign_keys`、`busy_timeout`）是**连接级**设置，不是数据库级。只有 `journal_mode` 会持久化到 db 文件。

**影响**
- 全部业务连接实际跑在默认 `synchronous = FULL` 下 → 每次写都 fsync，NFR 里"防抖保存不阻塞 IO"的目标未达成，持续产生磁盘 IO 与 SSD 写入放大；
- 无 `busy_timeout` → 归档窗口 / 主窗口 / 多张贴纸并发写时可能直接抛 `SQLITE_BUSY`（虽有单实例保护，但单进程内多连接并发是真实场景）。

**修复**

```csharp
public SqliteConnection CreateConnection()
{
    var conn = new SqliteConnection(_connectionString);
    conn.Open();
    using var pragma = conn.CreateCommand();
    pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
    pragma.ExecuteNonQuery();
    return conn;
}
```

进一步可考虑长生命周期单连接 + 串行化访问队列（写入队列），减少连接建立开销。

---

### P1-7　`AllowsTransparency` + `DropShadowEffect` 强制软件渲染

**证据**
- `NoteWindow.xaml:11-13` `AllowsTransparency="True" ResizeMode="CanResize" Background="Transparent"`
- `NoteWindow.xaml:22` `Effect="{StaticResource Elevation.Window}"`（`BlurRadius=12` 的 `DropShadowEffect`）
- `NotesListWindow.xaml:198-222` **每张卡片**都挂 `DropShadowEffect`，且 hover 时**换成另一个 Effect 实例**（`:209-214`）→ 触发整棵视觉树重渲染

**影响**　`AllowsTransparency="True"` 使该 HWND 走分层窗口路径，WPF 对其内容基本走**软件渲染**；叠加 `DropShadowEffect`（强制离屏绘制 + 模糊）后，**拖动 / 缩放 / 滚动贴纸时 CPU 占用明显**，多张贴纸同开时更糟。这与 NFR"30~60MB、丝滑"的体验目标相悖，也是 WPF 社区公认的经典性能反模式。

**修复**

```xml
<Window WindowStyle="None" AllowsTransparency="False" ResizeMode="NoResize"
        WindowChrome.WindowChrome="{StaticResource WindowChromeTemplate}">
  <WindowChrome.WindowChrome>
    <WindowChrome CaptionHeight="34" ResizeBorderThickness="6"
                  GlassFrameThickness="0" CornerRadius="8" UseAeroCaptionButtons="False"/>
  </WindowChrome.WindowChrome>
```
去掉 `Effect`，改用 `WindowChrome` + 系统阴影。列表卡片阴影改为固定单例 Effect 或直接用 1px 边框 + hover 只改 `BorderBrush` / `Background`，**不要在 trigger 中替换 Effect 实例**。

---

### P1-8　无 UI 虚拟化

**证据**　`NotesListWindow.xaml:181-184`

```xml
<ScrollViewer VerticalScrollBarVisibility="Auto" ...>
  <StackPanel>
    <ItemsControl ItemsSource="{Binding FilteredNotes}">
```

`rg VirtualizingStackPanel` 全项目 **0 命中**。

**影响**　便签数上百后，每次重排（P1-2）都要为**所有**卡片创建完整视觉树。叠加 P1-2 的 `RemoveAt + Insert`，滚动与编辑都会明显卡顿。`APP-PRODUCT.md` §6 承诺的"UI 虚拟化卡片渲染"**未实现**。

**修复**

```xml
<ItemsControl ItemsSource="{Binding FilteredNotes}"
              VirtualizingStackPanel.IsVirtualizing="True"
              VirtualizingStackPanel.VirtualizationMode="Recycling"
              VirtualizingStackPanel.ScrollUnit="Item"
              ScrollViewer.CanContentScroll="True">
```
（`StackPanel` 需从 `ItemsControl` 与 ScrollViewer 之间移除。）

---

### P1-9　多显示器 / 混合 DPI 坐标处理不可靠

**证据**　`app.manifest:21-22` 声明 `PerMonitorV2`；而坐标计算使用了 **系统 DPI 空间**的 API：
- `WindowManager.cs:60, 178` → `SystemParameters.WorkArea`
- `NotesListWindow.xaml.cs:183-186` → `SystemParameters.VirtualScreenLeft / Width`
- 持久化直接存 `Left / Top`（`NoteWindow.xaml.cs:41-48`）
- `WindowManager.cs:48` 用 `WindowX <= 0 || (|X-150|<1 && |Y-150|<1)` 猜测"未初始化"

**影响**　在 100% + 150% 双屏下，`Window.Left/Top`（每显示器 DIP 空间）与 `SystemParameters.WorkArea`（系统 DPI 空间）混算，便签恢复后**位置偏移甚至跑到屏幕外**；拔掉副屏后的兜底判定依赖"50 像素容差"（`:61-62`），不可靠。

**修复**　统一走 `MonitorFromPoint` + `GetMonitorInfo`（Win32）获取目标显示器工作区；持久化时同时记录 `MonitorDeviceName` 与**相对该屏工作区的偏移**，恢复时先按设备名匹配，匹配不到再按比例回退到主屏。同时删除魔法数字 `150`，改用 `WindowManager.DefaultLeft/DefaultTop` 常量。

---

## 4. P2 中等问题

### P2-1　`SearchService` 行号计算是 O(命中数 × 正文长度)

```csharp
// SearchService.cs:30-34  每个命中都从 0 重新扫一遍
int lineNumber = 1;
for (int i = 0; i < matchIndex; i++)
    if (content[i] == '\n') lineNumber++;
```

**影响**　单条 10 万字长便签命中 50 次 → 500 万次字符比较。1000 条便签规模下搜索延迟会明显超过 NFR 的 30ms。
**修复**　单次线性扫描维护"换行符索引表"或"上一命中位置 → 行号"增量游标，整体降为 **O(n)**。

### P2-2　搜索结果卡片不做关键词高亮（与产品文档原型不符）

**证据**　`NotesListWindow.xaml:425` `<TextBlock Text="{Binding LineSnippet}" />` 是纯文本；`SearchHit.HighlightText`（`SearchHit.cs:14`）被算出来但**从未在 UI 使用**（`rg HighlightText` 仅命中定义与 `SearchService.cs:63`）。而 `APP-PRODUCT.md` §4.2 原型明确画了 `[SQLite]` 高亮。

**影响**　长行里找"哪个词命中"要靠肉眼扫 —— 而这是产品主打差异化功能（精准跳行）的**前置步骤**，弱化了核心卖点。

**修复**

```csharp
public sealed record SearchHit(..., IReadOnlyList<SnippetSegment> Segments);
public sealed record SnippetSegment(string Text, bool IsHit);
```
XAML 侧用 `ItemsControl` + `Run` 渲染 `IsHit` 段，配 `Fluent.AccentLightBgBrush` 底色 + `Fluent.AccentBrush` 前景。

### P2-3　已归档便签无保留期清理

**证据**　`INoteRepository` 只有 `ClearAllArchivedAsync`（用户手动清空）。`APP-PRODUCT.md` §5.2 承诺"在本地数据库保留 30 天后自动清理"。
**修复**　`user_version = 2` 加 `ArchivedAt` 列 + 启动时清理任务（启动后台执行，不阻塞）。

### P2-4　迁移无向下兼容保护、无 Schema 版本常量

**证据**　`SqliteDatabaseContext.cs:58` 只有 `if (currentVersion < 1)`；无 `currentVersion > SchemaVersion` 的降级检测；版本号 `1` 是散落的字面量。
**影响**　用户拿新版建的库去跑旧版 exe，会对着缺失列的表执行查询，报错晦涩。
**修复**　`private const int SchemaVersion = 2;`（随 P1-4 提升）；`if (currentVersion > SchemaVersion)` 弹"数据来自更高版本"提示；补"v0→v1→v2"升级路径集成测试。

### P2-5　`Note.Snippet` 是死代码，且与文档描述的卡片形态不符

**证据**　`Note.cs:100` 定义 `Snippet`，`rg Snippet` 全项目**仅命中定义处**（其余是 `SearchHit.LineSnippet`），无任何 XAML 绑定。卡片实际用 `PreviewText => Content.Trim()`（`Note.cs:70`）。
**文档漂移**　`APP-PRODUCT.md` §3.1.1 描述卡片为"标题 + 第 2~4 行摘要"两段式；`CHANGES-20261002.md` 已改为主流纯文本 1~4 行，但未回写产品文档。
**修复**　删除 `Snippet`；按最终设计统一产品文档。

### P2-6　View 层跨窗口依赖具体类型

**证据**　`NoteWindow.xaml.cs:116`
```csharp
var mainWindow = Application.Current?.MainWindow as NotesListWindow;
if (mainWindow?.DataContext is NotesListViewModel vm) vm.NewNoteCommand.Execute(null);
```
同文件 `:131-137` 直接操作 `Application.Current.MainWindow`。
**影响**　违反 `APP-ARCHITECTURE.md` §3.1 的隔离规范；测试被迫用 `null!` 绕过 DI（`UiRenderingAndScreenshotTests.cs:130, 173, 349, 399`），说明 `WindowManager` 与 `IServiceProvider` 强耦合不可测。
**修复**　改走 `WeakReferenceMessenger`（`NewNoteRequestedMessage` / `ShowNotesListRequestedMessage`），由主窗口 VM 订阅。

### P2-7　快捷键用裸 `KeyDown` 而非 `InputBindings`

**证据**　`NotesListWindow.xaml.cs:33-57`、`NoteWindow.xaml.cs:149-180` 均为手写 `KeyDown` 分发。
**影响**　与 XAML 中 `ToolTip` 承诺的 `(Ctrl+N)` / `(Ctrl+F)` / `(Ctrl+P)` / `(Ctrl+W)` / `(Ctrl+D)` / `(Ctrl+H)` 硬编码在两处，容易漂移；不支持按键重映射，不可禁用，不参与 WPF 命令系统。
**修复**　改用 `InputBindings` + `KeyBinding`，Tooltip 从 Command 自动生成。

### P2-8　`NavigateToHit` 在 UI 线程同步阻塞读库

```csharp
// WindowManager.cs:119
var note = _repository.GetByIdAsync(hit.NoteId).GetAwaiter().GetResult();
```
**影响**　UI 线程同步等 SQLite I/O。且此处**根本不需要读库** —— `OpenOrActivateNote` 已有 `Note`。
**修复**　`SearchHit` 携带足够的上下文；已打开的窗口直接 `JumpToSearchHit`，未打开时异步打开后回调。

### P2-9　空状态视图排版失效

**证据**　`NotesListWindow.xaml:341-360` 的空状态 `StackPanel` 是 `StackPanel`（`:183`）的子元素，设了 `VerticalAlignment="Center"` + `Margin="0,80,0,0"`。**`StackPanel` 的子元素按顺序纵向堆叠，不会垂直居中。**
**影响**　"还没有任何便签"提示贴在列表顶部而非居中。
**修复**　把空状态移出 `StackPanel`，作为 `Grid.Row="3"` 内的同级 `Grid` 覆盖层，用 `HorizontalAlignment/VerticalAlignment=Center` 居中。

### P2-10　缺少 Ctrl+滚轮缩放

行业标准（Windows Sticky Notes 支持）。当前只有设置中心 6 档下拉（`SettingsViewModel.cs:24-32`），且是**全局**设置，无法单张便签临时缩放。
**修复**　贴纸窗口 `PreviewMouseWheel` 拦截 Ctrl+滚轮，调整 `NoteViewModel.FontSize`（10~36 钳制），持久化到 `settings.json` 并广播。

### P2-11　冷启动在 UI 线程串行完成全部初始化

**证据**　`App.xaml.cs:22` `async void OnStartup` 的执行顺序：

| 行号 | 动作 | 是否阻塞主窗口出现 |
|:---|:---|:---|
| 59 | `InitializeAndMigrateAsync()` | ✅ |
| 62 | `BackupService.RunDailyBackupIfNeeded()`（同步 `File.Copy`） | ✅ |
| 67 | `GetAllActiveAsync()` 全量查库 | ✅ |
| 69-72 | **逐个** `OpenOrActivateNote`（每个 `new NoteWindow` + `Show()` + 完整 XAML 解析） | ✅ |
| 75-77 | `mainWindow.Show()` | — |

**影响**　主窗口在整个恢复过程结束前**完全不出现**。有 N 张贴纸时 NFR 的"冷启动 < 600ms"基本不可能达成。
**修复**　先 `mainWindow.Show()` 显示骨架 → `await Dispatcher.Yield(DispatcherPriority.Background)` → 后台加载数据并恢复窗口；备份移到 `Task.Run` 且排在 Flush 之后。

### P2-12　智能错开算法静默改写用户坐标且不落库

**证据**　`WindowManager.cs:51-56`
```csharp
if (isDefaultOrUnset || isOverlapping)
{
    var (newLeft, newTop) = CalculateSmartRightPlacement(note.WindowWidth, note.WindowHeight);
    note.WindowX = newLeft;   // ← 只改内存，不写库
    note.WindowY = newTop;
}
```
**影响**　用户精心拖拽的布局，在"与另一张便签重叠 >6px"时会被静默改写；因为不落库，下次打开又重算一遍，行为不可预期。
**修复**　新计算出的坐标落库；或对"用户手动移动过的窗口"（有 `UserPositioned` 标记）跳过自动错开。

---

## 5. 功能对标：与业界优秀同类 app 的差距

### 5.1 逐项对标表

对标对象：**Windows Sticky Notes**（原生标杆）、**Evernote / OneNote**（功能标杆）、**Joplin / Obsidian**（隐私 + Markdown 标杆）。

| # | 能力 | Win Sticky Notes | Evernote/OneNote | Joplin/Obsidian | **StickyNotes 现状** | 差距 |
|:--|:---|:--:|:--:|:--:|:---|:--:|
| 1 | **系统托盘常驻 + 托盘菜单** | ✅ | ✅ | ✅ | ❌ **完全缺失** | 🔴 命门 |
| 2 | **全局热键快速新建** | ✅ | ✅ | ✅ | ❌ `RegisterHotKey` 0 命中 | 🔴 命门 |
| 3 | **开机自启 + 启动最小化** | ✅ | ✅ | ✅ | ❌ `HKEY_CURRENT_USER` 0 命中 | 🔴 命门 |
| 4 | 退出/关闭行为可配 | ✅ | ✅ | ✅ | ❌ `ShutdownMode` 未设置，行为不确定 | 🔴 高 |
| 5 | **图片粘贴（贴截图进便签）** | ✅ | ✅ | ✅ | ❌ `Content` 是 `string`，**架构上就不支持** | 🔴 高 |
| 6 | **勾选清单 checklist** | ✅ 一等公民 | ✅ | ✅ | ❌ 纯文本无 toggle | 🟠 高 |
| 7 | 搜索结果内关键词高亮 | ✅ | ✅ | ✅ | ❌ 算了 `HighlightText` 但不用（P2-2） | 🟠 高 |
| 8 | 编辑器内 F3 上下跳命中 | ✅ | ✅ | ✅ | ❌ `APP-PRODUCT.md` §3.1 F3_4 承诺未实现 | 🟠 高 |
| 9 | 便签链接 / 关联 | ✅ | ✅ | ✅ | ❌ | 🟠 中 |
| 10 | 标签 / 分组 / 分类 | ✅ | ✅ | ✅ | ❌ 仅"全部 / 已置顶"两胶囊 | 🟠 中 |
| 11 | 排序维度（创建时间/颜色/手动） | ✅ | ✅ | ✅ | ❌ 仅 置顶+UpdatedAt，且逻辑散落 3 处 | 🟡 中 |
| 12 | 搜索选项（正则/全词/大小写/范围） | 部分 | ✅ | ✅ | ❌ 仅大小写不敏感子串匹配 | 🟡 中 |
| 13 | Ctrl+滚轮缩放 | ✅ | ✅ | ✅ | ❌（P2-10） | 🟡 中 |
| 14 | **深色模式 / 跟随系统主题** | ✅ | ✅ | ✅ | ❌ `App.xaml:11` 硬编码 `Light.xaml` | 🟠 高 |
| 15 | 版本历史 / 时间机器 | ❌ | ✅ | ✅ | ❌ | 🟠 差异化机会 |
| 16 | 文本格式（粗体/下划线/项目符号） | ✅ | ✅ | ✅ | ❌ v1 有意选纯文本（理由是字符定位） | 🟡 中 |
| 17 | 提醒 / 闹钟 | ✅ | ✅ | ✅ | ❌ | 🟢 低 |
| 18 | 缩略图网格视图切换 | ✅ | ✅ | ✅ | ❌ 仅卡片流 | 🟢 低 |
| 19 | 导入来源（OneNote / Evernote / 官方导出） | — | ✅ | ✅ | ❌ 仅自家 JSON | 🟡 中 |
| 20 | 导出格式（.txt / .md / 打印 / PDF） | ✅ | ✅ | ✅ | ❌ 仅 JSON | 🟡 中 |
| 21 | 本地加密（DPAPI） | ❌ | ✅ | ✅ | ❌ | 🟡 中 |
| 22 | 编辑器内光标 / 滚动位置记忆 | ✅ | ✅ | ✅ | ❌ | 🟢 低 |
| 23 | 多语言（zh-CN / en-US） | ✅ | ✅ | ✅ | ❌ 全部硬编码中文，无 resx | 🟡 中 |
| 24 | 便签折叠成小色块（collapse） | ✅ | ❌ | ❌ | ❌ | 🟢 低 |
| 25 | 撤销栈（删除/归档可撤销） | 部分 | ✅ | ✅ | ❌ 归档可恢复但用户不知道 | 🟢 低 |

### 5.2 三个"命门"必须最先补

#### 🔴 命门一：托盘常驻 —— 目前"关掉主窗口就找不到家"

`App.xaml.cs:75-77` 把 `NotesListWindow` 赋给 `MainWindow`；`App.xaml` **未设置 `ShutdownMode`**（默认 `OnLastWindowClose`）。后果：

- 关闭主窗口 → `Application.Current.MainWindow` 被释放；
- 贴纸窗口的 `ShowNotesList_Click`（`NoteWindow.xaml.cs:131-137`）执行 `Application.Current.MainWindow?.Activate()` —— 此时该引用已是 `null`（或已释放对象），**静默无效**；
- 用户只剩两条路：① 从任务栏图标右键恢复（`WPF-UI` 的 FluentWindow 默认有任务栏项，但贴纸窗口也会各自占一个任务栏项，用户分不清哪个是主窗口）；② 双击 exe 触发单实例唤醒 —— 但 `BringExistingInstanceToFront()` 找的是标题为"彩色便签"的窗口，而**全部 4 类窗口标题都一样**（P1-5），可能唤醒某张便签。

同时，关闭主窗口后如果用户把所有贴纸也关掉，`OnLastWindowClose` 会**直接结束进程** —— 便签数据虽安全，但"我只想收起"变成了"我退出了"。

**修复**：`App.xaml` 显式 `<Application ShutdownMode="OnExplicitShutdown">`；主窗口 `Closing` 时 `e.Cancel = true` + `Hide()`；引入 `NotifyIcon`（`Hardcodet.NotifyIcon.Wpf` 或 P/Invoke `Shell_NotifyIcon`）承载托盘菜单：新建便签 / 显示全部 / 显示管理中心 / 设置 / 退出。

#### 🔴 命门二：全局热键

`APP-PRODUCT.md` SC-01 场景写的是"用户突然收到验证码或灵感，**按下快捷键迅速呼出便签记录**"—— 但产品里唯一的 `Ctrl+N` 必须在**已有窗口获得焦点时**才生效（`NotesListWindow.xaml.cs:37-42`、`NoteWindow.xaml.cs:153-157`）。用户正在浏览器里，什么都唤不出来。

**修复**：`RegisterHotKey(hwnd, id, MOD_CONTROL|MOD_ALT|MOD_NOREPEAT, VK_N)`，默认 `Win+Alt+N` 新建空白贴纸并聚焦；`Win+Alt+H` 显示管理中心；设置中可改键位（`AppSettings` 加 `HotkeyNewNote` / `HotkeyShowList` 字段，`SettingsViewModel` 加录制 UI），退出时 `UnregisterHotKey`。

#### 🔴 命门三：开机自启 + 启动最小化

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入/删除（`asInvoker` 权限足够，无需管理员）；配合托盘，实现"开机静默驻留，只有用到时才浮现"。

### 5.3 关于图片粘贴的架构影响（必须现在就规划）

这是**本项目最大的一次架构演进**，需要在阶段 4 之前先在数据模型上留位：

当前 `Content TEXT` 是纯字符串，无法容纳二进制。推荐方案（**不破坏核心差异化功能**）：

```sql
-- user_version = 2
ALTER TABLE Notes ADD COLUMN DocKind       TEXT NOT NULL DEFAULT 'Plain';  -- Plain | Markdown
ALTER TABLE Notes ADD COLUMN ContentVersion INTEGER NOT NULL DEFAULT 1;
CREATE TABLE NoteAssets (
    Id TEXT PRIMARY KEY, NoteId TEXT NOT NULL REFERENCES Notes(Id) ON DELETE CASCADE,
    RelPath TEXT NOT NULL, MimeType TEXT NOT NULL, ByteSize INTEGER NOT NULL,
    Width INTEGER, Height INTEGER, CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_NoteAssets_NoteId ON NoteAssets(NoteId);
```

图片存 `%DataDir%\assets\<yyyyMM>\<guid>.<ext>`，便签正文以 Markdown 图片引用 `![](assets/202610/xxx.png)` 内联。

**关键点**：`SearchService` 仍在**纯文本层**做 `CharIndex` 定位，把图片引用当作一个普通 token 即可，`JumpToSearchHit` 的语义**完全不变**。这正是"纯文本优先"这一 v1 决策的复利 —— 现在做的架构债（绝对字符偏移而非行号）在这一步会得到回报。

---

## 6. UI/UX 差距

### 6.1 主题系统：只有浅色一套（**最高优先级 UI 问题**）

| 证据 | 数据 |
|:---|:---|
| `App.xaml:11` | `<ResourceDictionary Source=".../Wpf.Ui;component/Resources/Theme/Light.xaml" />` 硬编码 |
| `StickyColors.xaml:5-20` | `Fluent.*` 画刷全部为浅色值（`TextPrimaryBrush #202020`、`CardBgBrush #FFFFFF`…） |
| `TestEnvironment.cs:64` | 测试也硬编码 `Light.xaml` —— 佐证暗色从未存在 |
| 全项目 `StaticResource` | **227 处** |
| 全项目 `DynamicResource` | **3 处** |
| XAML 内硬编码 hex | **59 处** |

**后果**

1. 运行时换主题在架构上**不可行**（`StaticResource` 在加载时解析并冻结）。
2. 硬编码色在暗色下必然崩坏：
   - `NoteWindow.xaml:95` 弹层 `Background="#FFFFFFFF"` → 暗色系统下是一块刺眼纯白
   - `NoteWindow.xaml:255` `SelectionBrush="#3399FF"` → 命中高亮在浅色便签（`#FFF7D1`）上对比度不足
   - `DesignTokens.xaml:144/202/205` `#12000000` / `#1A000000` / `#30000000` → 暗色下 hover 反馈消失
   - `NoteWindow.xaml:72` 置顶底衬 `#26000000` → 在 `Sticky.Charcoal`（`#292929`）便签上几乎不可见
3. **Mica 材质被浪费**：`NotesListWindow.xaml:12` 设了 `WindowBackdropType="Mica"`，但 `:13` 的 `Background="{StaticResource Fluent.WindowBgBrush}"` 是不透明 `#F9F9FB`，把 Mica 完全遮住 —— 白白承担了 Mica 的合成开销却看不到任何效果。

**修复方案**

```
Resources/Themes/
  ├── Light.xaml        # Fluent.* 浅色画刷 + 语义别名
  ├── Dark.xaml         # Fluent.* 暗色画刷 + 语义别名
  └── HighContrast.xaml # SystemColors.* 高对比度画刷
Resources/
  ├── DesignTokens.xaml   # 间距/圆角/字体（与主题无关，保持不变）
  └── StickyColors.xaml   # 7 色便签调色板（与主题无关）
```

1. 全部引用改 `DynamicResource`（227 处），使运行期换肤成为可能；
2. 59 处硬编码 hex 收敛为 token（如 `Thickness.HoverOverlay`、`Brush.SelectionHighlight`）；
3. 合并顺序按 `AppSettings.ThemeMode`（`System` / `Light` / `Dark`）动态决定；`System` 模式监听 `SystemEvents.UserPreferenceChanged`；
4. `Fluent.WindowBgBrush` 在 Mica 模式下改为半透明（`#F9F9FB` + `Opacity 0.9`，或用 `MicaAlt`）。

### 6.2 布局与导航

| 问题 | 位置 | 影响 |
|:---|:---|:---|
| 空状态未垂直居中 | `NotesListWindow.xaml:341-360`（`StackPanel` 子元素） | 提示贴在顶部（P2-9） |
| **搜索结果无键盘导航** | `NotesListWindow.xaml.cs:146-151` 硬编码 `SearchResults[0]` | ↑/↓ 不能移动选择，Enter 永远跳第 1 条 → **键盘用户基本无法使用搜索** |
| 搜索结果无选中态 | `NotesListWindow.xaml:368-433` 只绑 `MouseLeftButtonDown` | 键盘操作缺少视觉反馈 |
| 便签卡片不可聚焦 | `NotesListWindow.xaml:194` 事件在 `Border` 上，无 `Focusable` / `ItemContainerStyle` | 无 Enter 打开路径 |
| 主窗口无底部状态栏 | `APP-PRODUCT.md` §4.1 原型有 | "共 N 条 / 数据库已安全同步"缺失 |
| `CreatedAt` 存了但从不展示 | `Note.cs:60` | 卡片只有"修改时间"和"字数"，信息维度偏少 |

**修复**　搜索结果改 `ListBox` + `IsKeyboardFocusWithin` 时接管 ↑/↓/Enter/Home/End；卡片包一层可聚焦容器并提供 Enter/Space 打开；补底部状态栏（便签总数 / 已归档数 / 数据库路径 / 保存状态）。

### 6.3 可访问性（**最严重的 UI 短板**）

| 问题 | 证据 | 违反 |
|:---|:---|:---|
| **键盘焦点完全不可见** | `DesignTokens.xaml:130, 162, 189, 222` 四处 `FocusVisualStyle="{x:Null}"` | WCAG 2.4.7 Focus Visible (A) |
| 图标按钮无可访问名 | `NoteWindow.xaml:64, 80, 232` 等纯 `SymbolIcon` 按钮只有 `ToolTip`，无 `AutomationProperties.Name` | WCAG 4.1.2 Name, Role, Value (A)；屏幕阅读器只会读"按钮" |
| 搜索结果数变化不播报 | `NotesListWindow.xaml:170-174` 静态 `TextBlock` | WCAG 4.1.3 Status Messages (AA) |
| 不支持高对比度（HC）主题 | 59 处硬编码色 + 7 色便签柔和底色 | Windows HC 用户基本不可用 |
| 无键盘-only 走查 | 归档、切换颜色、切筛选全为鼠标路径 | — |

**修复**

```xml
<!-- 恢复焦点可视化，跟随系统高对比度 -->
<Setter Property="FocusVisualStyle">
  <Setter.Value>
    <Style>
      <Setter Property="Control.Template">
        <Setter.Value>
          <ControlTemplate>
            <Rectangle Stroke="{DynamicResource SystemColors.HighlightBrush}"
                       StrokeThickness="2" StrokeDashArray="1 2" Margin="-2" SnapsToDevicePixels="True"/>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Setter.Value>
</Setter>
```

- 所有纯图标按钮补 `AutomationProperties.Name="置顶便签"` / `HelpText`；
- 搜索命中数所在 `TextBlock` 加 `AutomationProperties.LiveRegion="Polite"`；
- 新增 `Themes/HighContrast.xaml`，语义画刷全部指向 `SystemColors.*`；7 色便签的 `TextHex` 在 HC 下改用 `SystemColors.WindowText`；
- 用 `Accessibility Insights for Windows` + Narrator 做一轮完整走查。

### 6.4 交互细节

1. **底部"已同步"是硬编码字符串** —— `NoteWindow.xaml:275` `<TextBlock ... Text="已同步" />`，**不随真实保存状态变化**。用户无法判断是否真的落盘。这与"绝不丢字"的核心承诺**直接冲突**，是最伤信任的一处细节。
   **修复**：`NoteViewModel` 暴露 `SaveState` 枚举（`Idle / Saving / Saved / Failed` + `LastSavedAt`），Footer 绑定；`Failed` 时红色提示 + 「重试」按钮（并把异常写入 `AppLog`）。

2. **"更多"弹层信息架构混乱** —— `NoteWindow.xaml:207-226` 把"主题色彩"、"彩色便签 (Ctrl+H)"（导航）、"归档便签 (Ctrl+D)"（破坏性操作，用 `Fluent.DangerBrush`）混在同一层级，且破坏性项紧邻导航项、无确认、无二次防护。
   **修复**：导航项在上、Separator、`Delete note` 移至最底并加确认气泡；`Ctrl+D` 从"归档"改为"删除"语义一致性检查（现在快捷键是 D=Delete 但菜单文案是"归档"）。

3. **无"折叠成小色块"** —— Windows Sticky Notes 的 collapse 极实用（只留标题色条，不遮挡工作区）。当前最小尺寸 `MinWidth=280 MinHeight=240`（`NoteWindow.xaml:9`）仍占相当面积。

4. **无撤销栈** —— `Ctrl+Z` 超出 `TextBox` 内部缓冲即失效；删除只能去归档中心找，用户不知道有这回事。建议在便签操作栏提供"撤销上一次归档"。

5. **贴纸工具栏缺少"更多操作"承载** —— 4 个按钮（新建/置顶/更多/关闭）已占满，而"字号缩放""复制内容""新建同色便签""锁定位置"都无处安放。

### 6.5 错误反馈

`SettingsViewModel.cs:86, 95, 120, 153` 全部使用 `MessageBox.Show`：阻塞 UI、无技术细节、无「复制详情」。归档的"彻底删除"若也如此，误操作后用户无法自救。
**修复**：改用 `ContentDialog`（WPF-UI 自带）承载标题 + 原因 + 「复制详情」+ 「打开数据目录」。

---

## 7. 工程化与测试体系

### 7.1 缺失的基础设施

| 项 | 状态 | 说明 |
|:---|:---|:---|
| CI（`.github/` / `.gitlab-ci.yml` / `azure-pipelines.yml`） | ❌ 0 命中 | 30 个测试只能手动跑，无 PR 门禁 |
| `global.json` | ❌ | 实测 SDK 10.0.400 编译 net8.0 目标，可用但**不可复现** |
| `.editorconfig` | ❌ | （`obj/` 下的 `*.GeneratedMSBuildEditorConfig.editorconfig` 是 MSBuild 自动生成的临时产物，非仓库文件） |
| `TreatWarningsAsErrors` / `AnalysisLevel` 提升 | ❌ | `Directory.Build.props:24-26` 仅设 `LangVersion/Nullable/ImplicitUsings` |
| `README.md` / `LICENSE` | ❌ | 分发物料不完整 |
| `CONTRIBUTING` / Issue 模板 | ❌ | — |

### 7.2 测试覆盖的空白区（**恰好是 P0 缺陷所在区**）

现有 30 个用例分布：

| 文件 | 覆盖内容 | 评价 |
|:---|:---|:---|
| `SearchServiceTests.cs` | 搜索算法（中文/大小写/同行多命中/首尾行/长行截断） | ✅ 核心差异化有覆盖 |
| `NoteRepositoryTests.cs` | CRUD、软删除、恢复、窗口边界 | ✅ 基础扎实 |
| `SettingsAndBackupTests.cs` | 设置持久化、导入导出 | ⚠️ 未覆盖导入冲突 |
| `PortableModeAndPathTests.cs` | 四级路径决议 | ✅ |
| `UiRenderingAndScreenshotTests.cs` | 9 张真机 HiDPI 截图 + 部分交互 | ✅ 亮点 |

**但以下高危场景零覆盖，正是 P0 缺陷的藏身处**：

1. ❌ **退出/重启恢复路径** → P0-1 溜过
2. ❌ **并发与竞态**（搜索 vs 增删 vs 编辑）→ P1-1 溜过
3. ❌ **Schema 迁移升级路径**（v0→v1→v2）→ P2-4 溜过
4. ❌ **备份完整性**（备份能否恢复出最后一条笔记）→ P0-3 溜过
5. ❌ **导入冲突裁决**（导入旧备份是否覆盖新笔记）→ P0-2 溜过
6. ❌ **断电/强杀恢复**（NFR 声称 TC-04，但无自动化）
7. ❌ **性能基线**（1000 条便签搜索 <30ms）

`UiRenderingAndScreenshotTests.cs:130, 173, 349, 399` 使用 `new WindowManager(null!, repo)` —— 用 `null!` 绕过 DI，说明 `WindowManager` 与 `IServiceProvider` 的强耦合使其无法独立测试（P2-6）。

---

## 8. 修复路线图（分阶段）

> 建议每阶段独立提交、独立验证，阶段 0 完成前不建议对外分发。

### 阶段 0 · 止血（1~2 天）

**目标：把 5 个 P0 全部清零。** 顺序：P0-1 → P0-4 → P0-5 → P0-2 → P0-3。

| 任务 | 产出 |
|:---|:---|
| P0-1 单点化窗口状态落盘 | `App.IsShuttingDown` + `WindowManager` 退出分支；`ShutdownRestoreTests` |
| P0-4 `OnExit` 同步化 + `SessionEnding` | `AutoSaveCoordinator.FlushAllBlocking()`；`SessionEndingTests` |
| P0-5 `AppLog` + 三层异常兜底 | 替换 12 处 `Debug.WriteLine`；`LoggingTests` |
| P0-2 导入冲突裁决 | `AnalyzeAsync` + 预览对话框 + 事务化批量导入；`ImportConflictTests` |
| P0-3 `VACUUM INTO` 备份 | 后台化 + 排在 Flush 之后；`BackupIntegrityTests`（备份能恢复出最后一条笔记） |

**验收**：新增 ≥ 5 个测试类，覆盖上述 5 条；全部测试通过；手工走查"开 3 张贴纸 → 退出 → 重开 → 3 张都在原位"。

---

### 阶段 1 · 架构解耦（3~5 天）

| 任务 | 对应问题 |
|:---|:---|
| 搜索改读不可变快照 `_searchSnapshot` | P1-1 |
| 消息分类：`NoteContentChangedMessage`（不重排） / `NoteMetaChangedMessage`（重排） | P1-2 |
| 引入 `NoteRowViewModel : ObservableObject` 投影层，`Notes` 集合换类型 | P1-3 |
| 拆分 `IsPinnedInList` / `AlwaysOnTop`（`user_version = 2`） | P1-4 |
| `CreateConnection()` 内统一 PRAGMA（`busy_timeout` / `synchronous` / `foreign_keys`） | P1-6 |
| 修 Mutex 名与单实例唤醒（`Local\` + 数据目录哈希 + `RegisterWindowMessage`） | P1-5 |
| View 间改走 Messenger，去掉 `null!` 绕 DI | P2-6 |
| `SchemaVersion` 常量 + 降级保护 | P2-4 |

**新增测试**：`ConcurrencyTests`（搜索与增删并发 10k 次不抛异常）、`SortingStabilityTests`（连续编辑不改变列表顺序）、`MigrationTests`（v0→v1→v2）。

---

### 阶段 2 · 性能（2~3 天）

| 任务 | 对应问题 |
|:---|:---|
| 去掉 `AllowsTransparency`，改 `WindowChrome`；卡片阴影改固定单例 | P1-7, P2-10 |
| `ItemsControl` + `VirtualizingStackPanel(IsVirtualizing, Recycling)` | P1-8 |
| 搜索行号改增量游标，降为 O(n) | P2-1 |
| 空状态改 Grid 覆盖层居中 | P2-9 |
| 启动流程：先 `Show()` 骨架再后台加载 | P2-11 |
| 多显示器坐标改 `MonitorFromPoint` + 设备名锚定 | P1-9 |

**验收指标（需建立基线）**

| 指标 | 目标 |
|:---|:---|
| 贴纸拖动 / 缩放时进程 CPU | < 10% |
| 1000 条便签列表滚动 | ≥ 55 FPS |
| 1000 条便签（约 10 万字）搜索延迟 P95 | < 30ms |
| 冷启动（10 张贴纸）主窗口呈现 | < 800ms |

---

### 阶段 3 · 补齐命门功能（5~7 天）

| 任务 | 对应问题 |
|:---|:---|
| `ShutdownMode="OnExplicitShutdown"` + 主窗口关闭改为 Hide | 5.2 命门一 |
| `NotifyIcon` 托盘（新建 / 显示全部 / 管理中心 / 设置 / 退出），单击行为可配 | 5.2 命门一 |
| 退出行为设置项（关闭主窗口 = 最小化 / 退出） | 表 #4 |
| `RegisterHotKey` 全局热键（`Win+Alt+N` / `Win+Alt+H`），设置内可改键位 | 5.2 命门二 |
| 开机自启（`HKCU\...\Run` 读写开关） | 5.2 命门三 |
| 搜索结果关键词高亮（`SnippetSegment` + `Run` 渲染） | P2-2 |
| 搜索结果键盘导航（↑/↓/Enter/Home/End）+ 选中态 | 6.2 |
| 编辑器内 F3 / Shift+F3 上下跳命中（兑现 `APP-PRODUCT.md` F3_4） | 表 #8 |
| 保存状态真实反馈（`SaveState` 枚举 + 「已同步」动态化） | 6.4-1 |
| 补底部状态栏（便签数 / 归档数 / 保存状态） | 6.2 |
| 快捷键改 `InputBindings` + `KeyBinding` | P2-7 |

---

### 阶段 4 · 内容能力（10~15 天，工程量最大）

**前置**：`user_version = 2` 加 `DocKind` / `ContentVersion` / `ArchivedAt`，建 `NoteAssets` 表（见 5.3）。

| 任务 | 说明 |
|:---|:---|
| **图片粘贴** | `Ctrl+V` 识别 `Bitmap`/`FileDrop` → 落 `assets/` → 正文插入 Markdown 引用；`SearchService` 将引用视为单 token，**保持 CharIndex 语义不变** |
| **勾选清单** | `DocKind=Markdown`；`Tab` 前缀切换 `- [ ]` ↔ `- [x]`，渲染层行内着色 + 左侧复选框 |
| **便签链接** | `[[标题]]` 语法 + `Ctrl+Click` 跳转 —— 复用已验证的 `JumpToSearchHit` 链路 |
| **版本历史** | `NoteRevisions(note_id, content, created_at)`；每次防抖保存写一份，保留最近 N 版，提供"恢复此版本" + 版本 diff。**这是对"绝不丢字"定位最直接的加强** |
| **深色模式** | 6.1 的 token 重构 + `ThemeMode` 设置 + `UserPreferenceChanged` 监听 |
| **高对比度** | `Themes/HighContrast.xaml` + 恢复 `FocusVisualStyle`（6.3） |
| **可访问性** | `AutomationProperties.Name` 全量补齐 + LiveRegion + Narrator 走查 |
| **归档 30 天清理** | P2-3（`ArchivedAt` 已在本阶段引入） |
| **i18n** | 全部字符串抽 `Resources/Strings.zh-CN.resx` / `.en-US.resx`；先 zh-CN 保底，再 en-US |

**验收**　图片粘贴端到端测试；深色模式截图回归（现有 `TestEnvironment` 机制可直接扩展为多主题参数化）；高对比度主题截图。

---

### 阶段 5 · 检索与组织增强（3~5 天）

| 任务 | 对应问题 |
|:---|:---|
| SQLite FTS5（Trigram 分词）`ISearchService` 平滑升级（`APP-ARCHITECTURE.md` §9.4 已规划，契约不变） | 表 #12 |
| 搜索选项：正则 / 全词 / 区分大小写 / 搜索范围（标题 vs 正文） | 表 #12 |
| 标签 / 分组（`#tag` 语法 + 侧边栏筛选） | 表 #10 |
| 排序维度扩展（创建时间 / 颜色 / 手动拖拽） | 表 #11 |
| 导入来源扩展（`.txt` / `.md` / 官方 Sticky Notes 导出） | 表 #19 |
| 导出 `.md` / 打印 / PDF | 表 #20 |
| 本地加密（`ProtectedData` DPAPI，可选开关） | 表 #21 |
| 缩略图网格视图 | 表 #18 |
| 编辑器内光标 / 滚动位置记忆 | 表 #22 |
| 贴纸折叠成小色块（collapse） | 表 #24 |

---

## 9. 附录 A：文档与代码不一致清单

> 文档承诺与实现脱节会持续误导后续开发，且部分已被实测证伪。建议在阶段 0 完成后统一回写。

| # | 文档承诺 | 文档位置 | 代码现状 | 等级 |
|:--|:---|:---|:---|:--:|
| 1 | 捕获 `SessionEnding`（关机/注销）时无条件 Flush | `APP-PRODUCT.md` §3.1.5-2 | **完全未实现**（`rg` 0 命中） | 🔴 承诺未落地 |
| 2 | 退出时无条件 Flush 内存脏数据 | `APP-PRODUCT.md` §3.1.5-2 | `OnExit` 是 `async void`，WPF 不等待（P0-4） | 🔴 **已被证伪** |
| 3 | 归档便签保留 30 天后自动清理 | `APP-PRODUCT.md` §5.2 | 无清理逻辑 | 🟠 承诺未落地 |
| 4 | 搜索命中词高亮（原型 `[SQLite]`） | `APP-PRODUCT.md` §4.2 | `LineSnippet` 纯文本；`HighlightText` 算了不用 | 🟠 承诺未落地 |
| 5 | 多命中项按序切换（F3_4） | `APP-PRODUCT.md` §3.1 | 未实现 | 🟠 承诺未落地 |
| 6 | 卡片显示"标题 + 第 2~4 行摘要" | `APP-PRODUCT.md` §3.1.1 | 实际是 `PreviewText = Content.Trim()`；`Note.Snippet` 是死代码 | 🟠 实现漂移 |
| 7 | `Color TEXT NOT NULL DEFAULT 'Yellow'` | `APP-ARCHITECTURE.md` §5.2 | 实际 `Color INTEGER NOT NULL DEFAULT 0`（`SqliteDatabaseContext.cs:67`） | 🟡 文档过时 |
| 8 | 依赖仅 3 个 NuGet（CommunityToolkit / Sqlite / DI） | `APP-ARCHITECTURE.md` §7.1 | 实际 6 个（多 `WPF-UI 3.0.5`、`Costura.Fody 6.0.0`、`Fody 6.9.2`） | 🟡 文档过时 |
| 9 | 目录含 `NoteService.cs` / `SearchHitViewModel.cs` / `Views/Controls/ColorPickerPopup.xaml` | `APP-ARCHITECTURE.md` §7 | **三个文件均不存在**；调色盘是 `NoteWindow.xaml:90-229` 内联的 `Popup`；无 `NoteService`（逻辑在 `NoteRepository` + `NoteViewModel`） | 🟡 文档过时 |
| 10 | 1000 条便签搜索 < 30ms（"UI 虚拟化卡片渲染"） | `APP-PRODUCT.md` §6 | 无虚拟化 + 搜索 O(n·m) + 每次保存重建视觉树 | 🔴 **已被证伪** |
| 11 | 冷启动窗口呈现 < 600ms | `APP-PRODUCT.md` §6 | 迁移 + 备份 + 全量查库 + 逐个建窗口全在主窗口 `Show()` 之前串行 | 🟠 **已被证伪** |
| 12 | 便签列表"底部状态栏：共 12 条便签 \| 数据库已安全同步" | `APP-PRODUCT.md` §4.1 | 界面无底部状态栏 | 🟠 实现漂移 |
| 13 | 数据目录含 `logs/`（诊断与异常崩溃记录） | `APP-ARCHITECTURE.md` §9.3 | 目录被创建但**从不写入**（P0-5） | 🟠 实现漂移 |
| 14 | 主窗口标题栏含搜索框 + 筛选胶囊 + 底部状态栏的三段式原型 | `APP-PRODUCT.md` §4.1 | 结构基本一致（除状态栏） | 🟢 基本一致 |
| 15 | 贴纸 `MinWidth` 未定义 / "极窄顶部工具栏" | `APP-PRODUCT.md` §3.1.2 | 已实现（`RowDefinition Height="34"`） | 🟢 已实现 |

---

## 10. 附录 B：关键代码位置索引

### 缺陷定位速查

| 编号 | 文件:行 | 一句话 |
|:---|:---|:---|
| P0-1 | `WindowManager.cs:77-95` + `NoteWindow.xaml.cs:56-59` + `App.xaml.cs:69` | 退出时 `IsOpen` 被写 false |
| P0-2 | `ExportImportService.cs:128, 143` + `NoteRepository.cs:138` | 导入无条件 UPSERT 覆盖 |
| P0-3 | `BackupService.cs:25` + `SqliteDatabaseContext.cs:41` | WAL 下裸拷 db 文件 |
| P0-4 | `App.xaml.cs:80, 88` | `OnExit` 是 `async void` |
| P0-5 | `App.xaml.cs:39-50` + 12 处 `Debug.WriteLine` + `AppPaths.cs:155` | Release 下无日志，异常被静默吞掉 |
| P1-1 | `NotesListViewModel.cs:103-110` | 后台线程枚举 UI 绑定的 `ObservableCollection` |
| P1-2 | `NoteViewModel.cs:68-72` + `NotesListViewModel.cs:213-233` | 每次保存 `RemoveAt + Insert` 重排 |
| P1-3 | `Note.cs:8` + `WindowManager.cs:42-43` | 实体无 INPC 且跨窗口共享 |
| P1-4 | `NoteWindow.xaml:14` + `Note.cs:23` | `IsPinned` 兼表"置顶显示"与"置顶排序" |
| P1-5 | `App.xaml.cs:25, 142` | Mutex 硬编码 + `Global\` + `FindWindow` 目标错 |
| P1-6 | `SqliteDatabaseContext.cs:41` + `NoteRepository.cs:22` | `synchronous` 只在迁移连接生效，无 `busy_timeout` |
| P1-7 | `NoteWindow.xaml:11-13, 22` + `NotesListWindow.xaml:198-222` | `AllowsTransparency` + `DropShadow` 软件渲染 |
| P1-8 | `NotesListWindow.xaml:181-184` | `ScrollViewer > StackPanel > ItemsControl` 无虚拟化 |
| P1-9 | `app.manifest:21-22` + `WindowManager.cs:60, 178` | 系统 DPI 空间与每屏 DIP 空间混算 |
| P2-1 | `SearchService.cs:30-34` | 行号计算 O(命中数 × 正文长度) |
| P2-2 | `NotesListWindow.xaml:425` + `SearchHit.cs:14` | 结果不高亮，`HighlightText` 死字段 |
| P2-3 | `INoteRepository.cs` | 无归档保留期清理 |
| P2-4 | `SqliteDatabaseContext.cs:58` | 无 `SchemaVersion` 常量与降级保护 |
| P2-5 | `Note.cs:100` | `Snippet` 死代码 |
| P2-6 | `NoteWindow.xaml.cs:116, 131` | View 依赖具体 View 类型 |
| P2-7 | `NotesListWindow.xaml.cs:33-57` | 裸 `KeyDown` 而非 `InputBindings` |
| P2-8 | `WindowManager.cs:119` | UI 线程同步等 SQLite |
| P2-9 | `NotesListWindow.xaml:341-360` | 空状态在 `StackPanel` 内无法居中 |
| P2-10 | `SettingsViewModel.cs:24-32` | 无 Ctrl+滚轮缩放 |
| P2-11 | `App.xaml.cs:59-77` | 冷启动全串行，主窗口最后才显示 |
| P2-12 | `WindowManager.cs:51-56` | 自动错开静默改写坐标且不落库 |
| 6.1 | `App.xaml:11` + 227×StaticResource / 3×DynamicResource / 59×硬编码色 | 只有浅色主题 |
| 6.3 | `DesignTokens.xaml:130, 162, 189, 222` | 焦点可视化被禁用 |
| 6.4-1 | `NoteWindow.xaml:275` | "已同步"是硬编码字符串 |

### 关键能力缺失（`rg` 全项目 0 命中）

```
SessionEnding   NotifyIcon   TrayIcon   RegisterHotKey   HKEY_CURRENT_USER
busy_timeout   VACUUM       VirtualizingStackPanel      AutomationProperties
TaskScheduler.UnobservedTaskException   AppDomain.CurrentDomain.UnhandledException
FTS5   Regex   ShutdownMode   HighContrast   resx
```

---

## 总结

**工程底子**：分层清楚、依赖克制、核心差异化（精准跳行）的算法选型正确且有测试覆盖、UI 真机截图回归是超出常规的工程投入、便携模式与路径决议设计到位。这是一个认真做过的项目，不是 demo 拼装。

**但当前状态是"能演示、不能交付"。** 5 个 P0 缺陷全部命中用户最能感知的路径，且全部与项目自己写在文档第一页的"可靠性高于一切"直接冲突：

- 正常退出必丢桌面布局（P0-1，**已实测复现**）
- 导入备份静默回滚新数据（P0-2）
- 自动备份本身不可靠（P0-3）
- 关机时最后一批输入可能丢（P0-4，且 `SessionEnding` 承诺完全未实现）
- Release 下所有错误处理被编译器删光，崩溃不可诊断（P0-5）

**功能面上，缺的不是锦上添花，而是桌面便签的三个命门：托盘、全局热键、开机自启。** 补上这三个 + 搜索高亮/F3 + 真实保存状态反馈，产品竞争力就基本到位。图片粘贴与勾选清单是拉开"与记事本放大版"差距的关键；而**版本历史**是最能强化"绝不丢字"这一差异化定位的功能，且当前架构（绝对字符偏移、纯文本优先）恰好为它铺好了路。

**UI/UX 的短板高度集中：只有一个主题，且不可访问。** 227 处 `StaticResource` + 59 处硬编码色 + 4 处 `FocusVisualStyle={x:Null}`，说明设计系统只做了浅色一套、且完全没考虑键盘用户与高对比度用户。好消息是这部分工作量集中在 token 收敛，技术难度不高，但**必须先做** —— 它是深色模式、高对比度、i18n 三项的地基。

**建议立即启动阶段 0**（1~2 天，5 个 P0 + 5 个回归测试类），完成后项目才具备对外分发的资格。
