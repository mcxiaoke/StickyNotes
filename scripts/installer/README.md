# Inno Setup 安装脚本使用指南

本目录提供一套**应用无关**的通用 Inno Setup 安装脚本：`AppInstaller.Common.iss` 是核心模板，每个应用只需一个十几行的壳脚本（参考 [StickyNotes.iss](StickyNotes.iss)）即可接入，其它应用复用时复制壳文件、少量 define 即可。

## 目录结构

```
scripts/installer/
├── README.md                  本指南
├── AppInstaller.Common.iss    通用核心模板（一般不需要改动）
├── StickyNotes.iss            StickyNotes 应用壳（其它应用复制此文件修改）
├── verify.ps1                 配置预览与产物核验工具（见下文）
└── Languages/
    └── ChineseSimplified.isl  简体中文语言文件（新仓库需随模板一起复制）
```

## 依赖

- Inno Setup 6（`scoop install inno-setup`），需 6.3+（`skipifsourcedoesntexist` 对通配符的支持）。
- 可选：innounp（`scoop install innounp`），`verify.ps1` 产物核验时用于读取安装器内部元数据、包内文件清单与完整性测试。
- 未安装 iscc 时 `publish.ps1` 自动跳过安装包，不影响 zip 产出。

## 接入新应用（Checklist）

1. 复制 `StickyNotes.iss` 为 `<AppName>.iss`，连同 `Languages/` 一起放入新仓库 `scripts/installer/`。
2. **换 AppId**：用 `powershell -c "[guid]::NewGuid()"` 生成新 GUID，保留 `{{` 前缀（Inno 转义）。AppId 决定升级识别与卸载注册表键，**每个应用必须唯一**。
3. 按下文参数表修改 define，核对 `AppDataDirRoot` 与应用代码中数据目录实现一致（如 StickyNotes 见 `src/StickyNotes/Infrastructure/AppPaths.cs`）。
4. 应用有命令行工具则定义 `AppCliExeName`；有开机自启功能则定义 `AppAutoStartNote`。
5. 发布脚本调用 iscc 编译（传 `/DAppVersion`、`/DPayloadDir`，可选 `/DIconFile`）。

## 壳脚本 define 参数

| 参数 | 必填 | 默认值 | 说明 |
|------|------|--------|------|
| `AppId` | 是 | 无 | 唯一 GUID，形如 `{{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}` |
| `AppName` | 是 | 无 | 应用显示名，同时用于安装目录名、快捷方式、数据目录名 |
| `AppExeName` | 否 | `AppName + ".exe"` | 主程序文件名 |
| `AppPublisher` | 否 | `AppName` | 发布者 |
| `AppURL` | 否 | 不设置 | 主页 URL，设置后写入发布者/支持/更新三个 URL |
| `AppDataDirRoot` | 否 | `{userappdata}` | 漫游数据目录根。数据在 `%LOCALAPPDATA%` 的应用（如 StickyNotes）定义为 `{localappdata}` |
| `AppDataDirName` | 否 | `AppName` | 漫游数据目录名 |
| `SetupOutputBaseName` | 否 | `AppName + "-setup"` | 安装包默认输出文件名（iscc `/F` 可覆盖） |
| `AppPortableFlagName` | 否 | `portable.ini` | 便携模式标记文件名。**修改时必须同步应用代码的读取逻辑**，否则应用无法识别便携模式 |
| `AppPortableDataDirName` | 否 | `app_data` | 便携模式数据目录名。**修改时必须同步应用代码**（同上） |
| `AppPreserveDirs` | 否 | 无 | 保留目录名，**逗号分隔**（如 `plugins,themes`）。升级时不覆盖其中文件，卸载时自然保留（不进卸载记录）；目录本身会被打包进安装包外的部分不受影响 |
| `AppCliExeName` | 否 | 无 | 定义后：CLI 随主程序打包、参与安装/卸载前进程检测与结束、写入 README |
| `AppAutoStartNote` | 否 | 不设置 | 定义后 README.txt 追加开机自启说明行 |
| `AppLicenseFile` | 否 | 不设置 | 许可协议文本路径，定义后向导显示许可页 |

`AppPreserveDirs` 的语义：安装时通过 `Excludes` 排除（PayloadDir 中即使存在同名目录也不会装入/覆盖已存在的目录），Inno 卸载器只删除自己安装时记录的文件，因此这些目录在升级和卸载后都原样保留——适合放应用运行时生成的插件、用户配置等安装目录内容。

## 编译期 /D 参数（发布脚本传入）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `AppVersion` | `0.0.0` | 版本号，写入 AppVersion 与 README |
| `PayloadDir` | `..\..\dist\publish` | 发布产物目录，**必须包含主程序 exe**，编译期校验 |
| `IconFile` | Inno 默认图标 | 安装包/卸载图标 ico 绝对路径 |

手动编译示例（`/O` 输出目录、`/F` 输出文件名）：

```powershell
iscc /Qp /DAppVersion=1.2.0 `
  /DPayloadDir=C:\path\to\publish `
  /DIconFile=C:\path\to\icon.ico `
  /O<输出目录> /F<AppName>-1.2.0-setup `
  <AppName>.iss
```

`publish.ps1` 集成方式见其 [4/5] 步骤：从 exe 版本信息取版本号，编译产物纳入 SHA256SUMS。

## 安装器行为（与 CarroDesk 同源）

- **Per-user 安装**：`PrivilegesRequired=lowest`，默认装到 `%LOCALAPPDATA%\Programs\<AppName>`，全程免 UAC 提权。
- **路径智能纠偏**：安装包位于已有应用目录（便携升级）时优先就地安装；历史安装目录自动继承；`...\AppName\AppName` 嵌套目录自动纠正。
- **数据模式选择页**（向导）：
  - 漫游模式（推荐）：数据在 `AppDataDirRoot\AppDataDirName`，生成开始菜单/卸载项；
  - 便携模式：数据在安装目录 `app_data` 下 + `portable.ini` 标记，零系统残留（安装后自动删除卸载器与注册表项）；
  - 目标目录已有 `portable.ini` / `app_data` 时自动预选便携模式；切换模式时数据递归迁移（同名文件跳过不覆盖）。
- **进程检测**：安装/卸载前检测主程序（及 CLI）运行中，提示后 taskkill 结束进程树。
- **文件规则**：`PayloadDir` 整体安装（排除 `*.pdb`、`app_data`、`portable.ini`）；`*.sample.*` 样例仅当目标不存在时安装，升级不覆盖用户样例；程序文件升级覆盖。
- **README.txt**：安装后生成于安装目录，记录版本、目录、数据模式与卸载说明。
- **卸载**：清理自启注册表（仅当 Run 值指向本安装目录）、README 与样例；`app_data` 有用户数据则保留并提示，为空则一并清理；漫游数据目录永不删除。

## 配置预览与产物核验（verify.ps1）

发布前预览实际编译参数、发布后核验产物，两种模式可同时执行：

```powershell
# 预览编译参数（AppId/默认目录/数据目录/最终 Excludes 等）+ 校验 PayloadDir，不实际编译
.\verify.ps1 -PayloadDir temp\pub\fd

# 产物核验（默认自动找 dist 下最新 *-setup.exe）：PE 版本资源、SHA256、签名、与壳脚本对照
.\verify.ps1
.\verify.ps1 -SetupExe dist\StickyNotes-1.2.0-setup.exe
```

产物核验中 PE 版本资源（ProductName/ProductVersion）是编译时固化的**实际参数**，脚本会自动与壳脚本 define 对照并在不一致时给出警告；若安装了 `innounp`（`scoop install innounp`），还会：读取安装器内部元数据（应用名/版本/压缩方式/语言数/文件总大小）、核验包内文件清单（主程序与样例已打包、无 pdb/便携数据混入——即 Excludes 真实生效）、执行包完整性测试。用于其它应用时传 `-Iss <壳脚本路径>`。

## 已知限制

- **静默安装（`/SILENT`、`/VERYSILENT`）固定为漫游模式**：自定义数据模式页在静默下不显示，`portable.ini` 会按漫游收尾被改名 `.bak`。便携模式请使用向导交互安装，或直接在便携目录运行安装包（就地升级）。
- 两个 `.iss` 必须保持 **UTF-8 with BOM** 编码（含中文脚本），编辑时注意不要被编辑器改为无 BOM 或 GBK。
