<#
.SYNOPSIS
    Inno Setup 安装包配置预览与产物核验脚本

.DESCRIPTION
    两种模式（可同时执行）：

    模式一 产物核验（默认）：分析 dist 下最新的 *-setup.exe（或 -SetupExe 指定的产物），
    展示 PE 版本资源、大小、SHA256、数字签名状态，并与壳脚本 define 对照；
    若安装了 innounp（scoop install innounp）则额外输出安装器内部元数据、
    包内文件清单核验（验证 Excludes 真实生效）与包完整性测试。

    模式二 编译参数预览（传入 -PayloadDir 时启用）：解析壳脚本 define（按核心模板
    同一套默认值规则补全），预览将要编译进安装包的实际参数——AppId、默认安装目录、
    数据目录、便携标记/数据目录名、保留目录与最终 Excludes、卸载注册表键等，
    并校验 PayloadDir 与图标。不实际编译。

.PARAMETER SetupExe
    指定 setup.exe 产物路径（默认取 dist 下最新的 *-setup.exe）

.PARAMETER Iss
    壳脚本路径（默认本目录的 StickyNotes.iss）

.PARAMETER PayloadDir
    启用编译参数预览模式，指定发布产物目录

.PARAMETER AppVersion
    预览模式使用的版本号（默认从 PayloadDir 中主程序 exe 的 ProductVersion 读取）

.PARAMETER IconFile
    预览模式使用的图标路径（默认壳脚本未提供时自动探测 <repo>/src/<主项目>/Assets/AppIcon.ico）

.EXAMPLE
    .\verify.ps1
    .\verify.ps1 -SetupExe dist\StickyNotes-1.2.0-setup.exe
    .\verify.ps1 -PayloadDir temp\pub\fd
#>

param(
    [string]$SetupExe = "",
    [string]$Iss = "",
    [string]$PayloadDir = "",
    [string]$AppVersion = "",
    [string]$IconFile = ""
)

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path $scriptDir -Parent | Split-Path -Parent

$script:warnCount = 0
function Write-Section([string]$title) {
    Write-Host ""
    Write-Host ("── " + $title + " " + ("─" * [Math]::Max(0, 56 - $title.Length))) -ForegroundColor Cyan
}
function Write-Item([string]$name, [string]$value) {
    Write-Host ("  " + $name.PadRight(22) + " ") -NoNewline
    Write-Host $value
}
function Write-Warn([string]$msg) {
    $script:warnCount++
    Write-Host ("  [警告] " + $msg) -ForegroundColor DarkYellow
}
function Write-Ok([string]$msg) {
    Write-Host ("  [通过] " + $msg) -ForegroundColor DarkGreen
}

# ============================================================
# 解析壳脚本 define（字面值），并按核心模板默认值规则补全
# ============================================================
function Get-IssDefines([string]$issPath) {
    if (-not (Test-Path $issPath)) { throw "壳脚本不存在：$issPath" }
    $text = [IO.File]::ReadAllText($issPath)
    $d = @{}
    foreach ($m in [regex]::Matches($text, '(?m)^\s*#define\s+([A-Za-z_]\w*)\s+"([^"]*)"')) {
        $d[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    # 与 AppInstaller.Common.iss 的 #ifndef 默认值保持同步
    if (-not $d.ContainsKey('AppExeName'))          { $d['AppExeName'] = $d['AppName'] + ".exe" }
    if (-not $d.ContainsKey('AppPublisher'))        { $d['AppPublisher'] = $d['AppName'] }
    if (-not $d.ContainsKey('AppDataDirName'))      { $d['AppDataDirName'] = $d['AppName'] }
    if (-not $d.ContainsKey('AppDataDirRoot'))      { $d['AppDataDirRoot'] = '{userappdata}' }
    if (-not $d.ContainsKey('AppPortableFlagName')) { $d['AppPortableFlagName'] = 'portable.ini' }
    if (-not $d.ContainsKey('AppPortableDataDirName')) { $d['AppPortableDataDirName'] = 'app_data' }
    if (-not $d.ContainsKey('SetupOutputBaseName')) { $d['SetupOutputBaseName'] = $d['AppName'] + "-setup" }
    return $d
}

# 展开 Inno 路径常量为本机实际路径（仅用于展示）
function Expand-ShellDir([string]$constant, [string]$tail) {
    $base = switch ($constant) {
        '{userappdata}'  { [Environment]::GetFolderPath('ApplicationData') }
        '{localappdata}' { [Environment]::GetFolderPath('LocalApplicationData') }
        default          { $constant }
    }
    return (Join-Path $base $tail)
}

# 构造主打包行 Excludes（与 AppInstaller.Common.iss 的 MainExcludes 保持同步）
function Get-MainExcludes($d) {
    $ex = "*.pdb,*.sample.*,$($d['AppPortableDataDirName'])\*,$($d['AppPortableFlagName']),$($d['AppPortableFlagName']).bak"
    if ($d.ContainsKey('AppPreserveDirs')) {
        $ex += "," + (($d['AppPreserveDirs'] -replace ',', '\*,') + '\*')
    }
    return $ex
}

# ============================================================
# 解析壳脚本
# ============================================================
if ([string]::IsNullOrEmpty($Iss)) { $Iss = Join-Path $scriptDir "StickyNotes.iss" }
$d = Get-IssDefines $Iss

if ([string]::IsNullOrEmpty($d['AppName'])) { throw "壳脚本未定义 AppName：$Iss" }
if ([string]::IsNullOrEmpty($d['AppId']))   { throw "壳脚本未定义 AppId：$Iss" }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Inno Setup 安装包核验工具" -ForegroundColor Cyan
Write-Host ("  壳脚本: " + $Iss) -ForegroundColor DarkGray
Write-Host "========================================" -ForegroundColor Cyan

# ============================================================
# 模式二：编译参数预览
# ============================================================
if (-not [string]::IsNullOrEmpty($PayloadDir)) {

    Write-Section "编译参数预览（不实际编译）"

    # AppId 校验
    $guidRaw = $d['AppId'].TrimStart('{').TrimEnd('}')
    $guidOk = [Guid]::TryParse($guidRaw, [ref][Guid]::Empty)
    Write-Item "AppId" $d['AppId']
    if ($guidOk) { Write-Ok "AppId 是合法 GUID" }
    else { Write-Warn "AppId 不是合法 GUID（去掉 {{ }} 前后缀后应可被 [guid]::Parse 解析）" }

    Write-Item "AppName" $d['AppName']
    Write-Item "AppExeName" $d['AppExeName']
    Write-Item "AppPublisher" $d['AppPublisher']
    if ($d.ContainsKey('AppURL')) { Write-Item "AppURL" $d['AppURL'] }
    if ($d.ContainsKey('AppCliExeName')) { Write-Item "AppCliExeName" $d['AppCliExeName'] }

    Write-Item "默认安装目录" (Expand-ShellDir '{localappdata}' ("Programs\" + $d['AppName']))
    Write-Item "卸载注册表键" ("HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + $guidRaw + "}_is1")
    Write-Item "漫游数据目录" (Expand-ShellDir $d['AppDataDirRoot'] $d['AppDataDirName'])
    Write-Item "便携标记文件" $d['AppPortableFlagName']
    Write-Item "便携数据目录" $d['AppPortableDataDirName']
    if ($d.ContainsKey('AppPreserveDirs')) { Write-Item "保留目录" ($d['AppPreserveDirs'] + "（升级不覆盖，卸载保留）") }
    Write-Item "最终 Excludes" (Get-MainExcludes $d)

    # 版本号
    if ([string]::IsNullOrEmpty($AppVersion)) {
        $exeInPayload = Join-Path $PayloadDir $d['AppExeName']
        if (Test-Path $exeInPayload) {
            $AppVersion = (Get-Item $exeInPayload).VersionInfo.ProductVersion
            if ($AppVersion -match '^(\d+\.\d+\.\d+)') { $AppVersion = $Matches[1] } else { $AppVersion = "1.0.0" }
        } else {
            $AppVersion = "0.0.0"
        }
    }
    Write-Item "AppVersion" $AppVersion

    # PayloadDir 校验
    Write-Section "PayloadDir 校验"
    Write-Item "路径" $PayloadDir
    if (-not (Test-Path $PayloadDir)) {
        Write-Warn "目录不存在，无法统计内容"
    } else {
        $files = Get-ChildItem $PayloadDir -Recurse -File
        $totalMB = [Math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
        Write-Item "文件数 / 总大小" ("{0} 个 / {1} MB" -f $files.Count, $totalMB)

        $exeInPayload = Join-Path $PayloadDir $d['AppExeName']
        if (Test-Path $exeInPayload) { Write-Ok ("主程序存在：" + $d['AppExeName']) }
        else { Write-Warn ("主程序缺失：" + $d['AppExeName'] + "（iscc 编译期会直接报错）") }

        if ($files | Where-Object Name -like "*.pdb") { Write-Warn "payload 中含 .pdb 调试符号（Excludes 会排除，建议发布前清理）" }
        if (Test-Path (Join-Path $PayloadDir $d['AppPortableDataDirName'])) { Write-Warn ("payload 中含 " + $d['AppPortableDataDirName'] + "（Excludes 会排除，建议发布前清理）") }
        if (Test-Path (Join-Path $PayloadDir $d['AppPortableFlagName'])) { Write-Warn ("payload 中含 " + $d['AppPortableFlagName'] + "（Excludes 会排除，建议发布前清理）") }
    }

    # IconFile 校验
    Write-Section "图标校验"
    if ([string]::IsNullOrEmpty($IconFile)) {
        $guess = Join-Path $repoRoot ("src\" + $d['AppName'] + "\Assets\AppIcon.ico")
        if (Test-Path $guess) { $IconFile = $guess }
    }
    if ([string]::IsNullOrEmpty($IconFile)) {
        Write-Item "IconFile" "（未提供，安装包将使用 Inno 默认图标）"
    } elseif (Test-Path $IconFile) {
        Write-Ok ("图标存在：" + $IconFile)
    } else {
        Write-Warn "图标文件不存在：" + $IconFile
    }
}

# ============================================================
# 模式一：产物核验
# ============================================================
if ([string]::IsNullOrEmpty($SetupExe)) {
    $distDir = Join-Path $repoRoot "dist"
    $latest = Get-ChildItem $distDir -Recurse -Filter "*-setup.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { $SetupExe = $latest.FullName }
}

Write-Section "产物核验"
if ([string]::IsNullOrEmpty($SetupExe)) {
    Write-Warn "未指定产物且 dist 下没有 *-setup.exe（先运行 publish.ps1 生成）"
}
elseif (-not (Test-Path $SetupExe)) {
    Write-Warn "产物不存在：$SetupExe"
}
else {
    $item = Get-Item $SetupExe
    Write-Item "文件" $item.Name
    Write-Item "大小" ([Math]::Round($item.Length / 1MB, 2).ToString() + " MB")
    Write-Item "修改时间" $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    Write-Item "SHA256" (Get-FileHash $SetupExe -Algorithm SHA256).Hash.ToLower()

    $vi = $item.VersionInfo
    Write-Section "PE 版本资源（编译时固化的实际参数）"
    Write-Item "ProductName" ($vi.ProductName.Trim())
    Write-Item "ProductVersion" ($vi.ProductVersion.Trim())
    Write-Item "FileDescription" ($vi.FileDescription.Trim())
    $fv = $vi.FileVersion.Trim()
    Write-Item "FileVersion" $(if ($fv) { $fv } else { "（空）" })
    Write-Item "CompanyName" ($vi.CompanyName.Trim())

    if (-not [string]::IsNullOrEmpty($vi.ProductName)) {
        if ($vi.ProductName.Trim() -eq $d['AppName']) { Write-Ok ("ProductName 与壳脚本 AppName 一致：" + $d['AppName']) }
        else { Write-Warn ("ProductName 与壳脚本 AppName 不一致：'" + $vi.ProductName.Trim() + "' != '" + $d['AppName'] + "'") }
    }
    if (-not [string]::IsNullOrEmpty($AppVersion)) {
        if ($vi.ProductVersion.Trim() -eq $AppVersion) { Write-Ok ("ProductVersion 与预览版本一致：" + $AppVersion) }
        else { Write-Warn ("ProductVersion 与预览版本不一致：'" + $vi.ProductVersion.Trim() + "' != '" + $AppVersion + "'") }
    }

    $sig = Get-AuthenticodeSignature $SetupExe
    Write-Item "数字签名" $sig.Status
    if ($sig.Status -eq 'NotSigned') { Write-Warn "安装包未签名（分发时 SmartScreen 可能拦截）" }

    # innounp 可用时：安装器内部元数据 + 包内文件清单核验 + 完整性测试
    $innounpCmd = Get-Command innounp -ErrorAction SilentlyContinue
    $innounp = if ($innounpCmd) { $innounpCmd.Source } elseif (Test-Path "C:\Home\Develop\Scoop\shims\innounp.exe") { "C:\Home\Develop\Scoop\shims\innounp.exe" } else { "" }
    if ($innounp) {
        # 1. 内部元数据（编译时固化的 header 信息）
        Write-Section "安装器内部元数据（innounp）"
        $metaLines = & $innounp -o -b $SetupExe 2>&1
        $meta = @{ name = ""; ver = ""; comp = ""; langs = ""; files = ""; size = ""; innoVer = "" }
        foreach ($line in $metaLines) {
            if ($line -match 'Inno Setup version detected:\s+(.+)$')     { $meta.innoVer = $Matches[1].Trim() }
            elseif ($line -match 'Application name:\s+(.+)$')            { $meta.name = $Matches[1].Trim() }
            elseif ($line -match 'Application version:\s+(.+)$')         { $meta.ver = $Matches[1].Trim() }
            elseif ($line -match 'Compression used:\s+(.+)$')            { $meta.comp = $Matches[1].Trim() }
            elseif ($line -match 'Supported languages:\s+(\d+)$')        { $meta.langs = $Matches[1] }
            elseif ($line -match 'Number of files:\s+(\d+)$')            { $meta.files = $Matches[1] }
            elseif ($line -match 'Total size of files:\s+(.+)$') { $meta.size = ($Matches[1] -replace '\D', '') }
        }
        if (-not $meta.name) {
            Write-Warn "无法解析 innounp 元数据（安装包版本可能过新，尝试升级 innounp：scoop update innounp）"
        } else {
            Write-Item "应用名 / 版本" ($meta.name + " / " + $meta.ver)
            Write-Item "Inno Setup 版本" $meta.innoVer
            Write-Item "压缩方式" $meta.comp
            Write-Item "语言数 / 文件数" ($meta.langs + " / " + $meta.files)
            Write-Item "文件总大小" ([Math]::Round([long]$meta.size / 1MB, 2).ToString() + " MB")

            if ($meta.name -eq $d['AppName']) { Write-Ok ("内部应用名与壳脚本一致：" + $meta.name) }
            else { Write-Warn ("内部应用名与壳脚本不一致：'" + $meta.name + "' != '" + $d['AppName'] + "'") }
            if (-not [string]::IsNullOrEmpty($AppVersion)) {
                if ($meta.ver -eq $AppVersion) { Write-Ok ("内部版本与预览版本一致：" + $AppVersion) }
                else { Write-Warn ("内部版本与预览版本不一致：'" + $meta.ver + "' != '" + $AppVersion + "'") }
            }
        }

        # 2. 包内文件清单核验：Excludes 是否真实生效
        Write-Section "包内文件清单核验（innounp -v）"
        $listing = & $innounp -o -b -v $SetupExe 2>&1
        $pkgFiles = @()
        foreach ($line in $listing) {
            if ($line -match '\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(.+)$') {
                $pkgFiles += $Matches[1].Trim()
            }
        }
        # install_script.iss 是编译后的内部脚本，不是安装文件
        $pkgFiles = @($pkgFiles | Where-Object { $_ -ne 'install_script.iss' })
        if ($pkgFiles.Count -eq 0) {
            Write-Warn "无法解析包内文件清单"
        } else {
            $dataDirName = $d['AppPortableDataDirName']
            $flagName = $d['AppPortableFlagName']

            if ($pkgFiles | Where-Object { $_.EndsWith('\' + $d['AppExeName'], [StringComparison]::OrdinalIgnoreCase) }) { Write-Ok ("主程序已打包：" + $d['AppExeName']) }
            else { Write-Warn ("包内未找到主程序：" + $d['AppExeName']) }

            $bad = @()
            if ($pkgFiles | Where-Object { $_ -match '\.pdb$' }) { $bad += ".pdb 调试符号" }
            if ($pkgFiles | Where-Object { $_ -match '(^|\\)' + [regex]::Escape($dataDirName) + '(\\|$)' }) { $bad += "便携数据目录 $dataDirName" }
            if ($pkgFiles | Where-Object { $_.EndsWith('\' + $flagName, [StringComparison]::OrdinalIgnoreCase) }) { $bad += "便携标记 $flagName" }
            if ($bad) { Write-Warn ("Excludes 未生效，包内含：" + ($bad -join "、")) }
            else { Write-Ok "Excludes 生效：无 pdb / 便携数据 / 便携标记混入" }

            $samples = @($pkgFiles | Where-Object { $_ -match '\.sample\.' })
            if ($samples.Count -gt 0) { Write-Ok ("样例文件已打包：" + (($samples | ForEach-Object { $_ -replace '^{app}\\', '' }) -join "、")) }

            Write-Item ("包内文件（" + $pkgFiles.Count + "）") (($pkgFiles | ForEach-Object { $_ -replace '^{app}\\', '' }) -join ", ")
        }

        # 3. 完整性测试
        Write-Section "包完整性测试（innounp -t）"
        & $innounp -o -b -q -t $SetupExe 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Ok "全部文件校验通过" }
        else { Write-Warn ("完整性测试失败（exit " + $LASTEXITCODE + "），安装包可能损坏") }
    } else {
        Write-Item "innounp" "未安装（scoop install innounp 可启用安装器内部元数据与文件清单核验）"
    }
}

# ============================================================
# 汇总
# ============================================================
Write-Host ""
if ($script:warnCount -eq 0) {
    Write-Host "核验完成，未发现警告。" -ForegroundColor Green
} else {
    Write-Host ("核验完成，共 " + $script:warnCount + " 条警告，请逐条确认。") -ForegroundColor Yellow
}
