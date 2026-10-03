# StickyNotes（彩色便签）

Windows 桌面便签工具。纯 WPF + .NET 8，依赖克制，无遥测、无云同步。

- **桌面贴纸**：便签以独立窗口常驻桌面，可置顶、可记忆位置与尺寸
- **管理中心**：主列表提供搜索（字符绝对偏移精准跳行）、置顶排序、归档
- **托盘常驻**：全局热键（`Win+Alt+N` / `Win+Alt+H`）、开机自启、关闭到托盘
- **PIN 轻量防偷窥**：PBKDF2-SHA256 + 随机盐 + 恒定时间比较（定位为"防偷窥"，非加密保险箱）
- **数据保障**：SQLite（WAL）+ 每日 `VACUUM INTO` 冷备份 + 导入导出 JSON、原子写入

## 快速开始

```powershell
# 构建
dotnet build -c Release

# 运行测试
dotnet test

# 打包（可选，生成发布 zip 到 dist/）
.\publish.ps1
```

## 目录结构

```
src/StickyNotes/          # 应用主工程（WinExe）
  Converters/             # IValueConverter 集合
  Data/                   # SqliteDatabaseContext / INoteRepository / NoteRepository
  Infrastructure/         # AppPaths / AppLog / NativeMethods
  Messages/               # 弱引用消息总线消息定义
  Models/                 # Note / NoteColor / SearchHit
  Services/               # 窗口调度、自动保存、搜索、备份、导入导出、托盘、热键、PIN
  ViewModels/             # MVVM 视图模型
  Views/                  # XAML 窗口与控件
  Resources/              # 设计令牌、便签配色、主题
tests/StickyNotes.Tests/  # MSTest 测试（含真机 HiDPI 渲染截图）
docs/                     # 产品/架构文档与审查报告、变更记录
scripts/                  # 辅助脚本（真实截图抓取等）
```

## 数据与文件位置

四级路径决议（优先级从高到低）：

1. `AppPaths.DataDirOverride`（测试/宿主注入）
2. 环境变量覆盖
3. 程序目录下的 `portable.ini`（便携模式）
4. `%LOCALAPPDATA%\StickyNotes`

数据库 `notes.db`、设置 `settings.json`、日志 `logs\app-YYYYMMDD.log`（**保留 30 天**）、
备份 `backups\notes_YYYYMMDD.db`（**保留 14 份**）均位于上述数据目录内。

## 工程约定

- **行尾 / 编码**：仓库统一 LF、UTF-8；由 `.gitattributes` 与 `.editorconfig` 约束。
  新增文件请勿引入 CRLF 或 BOM，否则会产生整文件级 diff 噪音。
- **警告即错误**：`TreatWarningsAsErrors=true`（仅 `CS0618` 豁免）。构建必须保持 0 警告。
- **提交前**：`dotnet build -c Release` 与 `dotnet test` 全绿。
- **变更记录**：重要代码变动需在 `docs/CHANGES-YYYYMMDD.md` 顶部追加摘要（含真实时间 GMT+8）。

## 文档

| 文档 | 说明 |
|:--|:--|
| `docs/APP-PRODUCT.md` | 产品定义与验收标准（**部分内容已落后于实现**，见下） |
| `docs/APP-ARCHITECTURE.md` | 架构设计（**历史设计文档**，部分伪代码/文件清单与当前代码不一致） |
| `docs/APP-REVIEW-FINAL-20261003.md` | 四份独立审查的合并版，缺陷编号唯一权威来源 |
| `docs/CHANGES-*.md` | 按日变更记录 |

> 阅读产品/架构文档时请以代码为准；已知漂移点已在 `APP-REVIEW-FINAL-20261003.md` §6.2 逐条列出。
