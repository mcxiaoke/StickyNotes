<#
.SYNOPSIS
    StickyNotes 便签发布脚本 — 生成 Windows 便携版发布产物 + SHA256 校验和

.DESCRIPTION
    构建产物（按版本号归档到子目录，每次发布只清空当前版本目录，历史版本保留）：
    1. 框架依赖版 zip：dist/{ver}/StickyNotes-{ver}-win-x64.zip（需 .NET 8+ runtime，体积仅约 20MB）
    2. Inno Setup 安装包：dist/{ver}/StickyNotes-{ver}-setup.exe（per-user 免提权安装；未安装 iscc 时自动跳过）
    3. SHA256 校验和清单：dist/{ver}/SHA256SUMS.txt

.PARAMETER Version
    可选：覆盖版本号（默认自动从 exe 读取）

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Version 1.0.0
#>

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"
$pubTemp = Join-Path $repoRoot "temp" "pub"
$srcProject = Join-Path $repoRoot "src" "StickyNotes" "StickyNotes.csproj"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  StickyNotes 便签构建与打包发布脚本" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ============================================================
# 1. 清理构建缓存
# ============================================================
Write-Host "[1/5] 清理构建缓存..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
if (Test-Path $pubTemp) { Remove-Item $pubTemp -Recurse -Force }
New-Item -ItemType Directory -Path $pubTemp -Force | Out-Null

$releaseFiles = @()
$releaseDir = $null

# ============================================================
# 2. 构建框架依赖便携版 (Framework-Dependent)
# ============================================================
Write-Host "[2/5] 构建框架依赖便携版 (win-x64)..." -ForegroundColor Yellow
$fdOut = Join-Path $pubTemp "fd"
& dotnet publish $srcProject -c Release -r win-x64 --self-contained false --nologo -v minimal -o $fdOut
if ($LASTEXITCODE -ne 0) { Write-Host "构建框架依赖版失败" -ForegroundColor Red; exit 1 }

# 读取实际注入的版本号
if ([string]::IsNullOrEmpty($Version)) {
    $exePath = Join-Path $fdOut "StickyNotes.exe"
    if (Test-Path $exePath) {
        $ver = (Get-Item $exePath).VersionInfo.ProductVersion
        if ($ver -match '^(\d+\.\d+\.\d+)') { $Version = $Matches[1] }
        else { $Version = "1.0.0" }
    } else {
        $Version = "1.0.0"
    }
}
Write-Host "  检测到产品版本号: $Version" -ForegroundColor Green

# 按版本号建立发布子目录，仅清空当前版本目录（历史版本目录原样保留）
$releaseDir = Join-Path $distDir $Version
if (Test-Path $releaseDir) {
    Write-Host "  清空当前版本目录: dist/$Version" -ForegroundColor DarkGray
    Remove-Item $releaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 清理 pdb 调试符号与开发态临时文件，确保便携样例配置文件存在
Get-ChildItem $fdOut -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $fdOut "portable.ini") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $fdOut "app_data") -Recurse -Force -ErrorAction SilentlyContinue
$sampleIniSrc = Join-Path $repoRoot "src" "StickyNotes" "Samples" "portable.sample.ini"
if (-not (Test-Path (Join-Path $fdOut "portable.sample.ini")) -and (Test-Path $sampleIniSrc)) {
    Copy-Item $sampleIniSrc (Join-Path $fdOut "portable.sample.ini") -Force
}

# ============================================================
# 3. 构建免安装独立单文件版 (Self-Contained Single File) - [已注释停用]
# ============================================================
<#
Write-Host "[3/5] 构建自包含单文件独立版 (无需安装 .NET 运行时)..." -ForegroundColor Yellow
$scOut = Join-Path $pubTemp "standalone"
& dotnet publish $srcProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v minimal -o $scOut
if ($LASTEXITCODE -ne 0) { Write-Host "构建独立单文件版失败" -ForegroundColor Red; exit 1 }
Get-ChildItem $scOut -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $scOut "portable.ini") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $scOut "app_data") -Recurse -Force -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $scOut "portable.sample.ini")) -and (Test-Path $sampleIniSrc)) {
    Copy-Item $sampleIniSrc (Join-Path $scOut "portable.sample.ini") -Force
}
#>

# ============================================================
# 3. 压缩打包为发布 zip
# ============================================================
Write-Host "[3/5] 压缩打包产物..." -ForegroundColor Yellow

$fdZip = Join-Path $releaseDir "StickyNotes-$Version-win-x64.zip"
Compress-Archive -Path "$fdOut\*" -DestinationPath $fdZip -Force
$releaseFiles += $fdZip
Write-Host "  便携版 zip: $([math]::Round((Get-Item $fdZip).Length / 1MB, 2)) MB" -ForegroundColor DarkGray

<#
$scZip = Join-Path $releaseDir "StickyNotes-Standalone-$Version-win-x64.zip"
Compress-Archive -Path "$scOut\*" -DestinationPath $scZip -Force
$releaseFiles += $scZip
Write-Host "  自包含单文件 zip: $([math]::Round((Get-Item $scZip).Length / 1MB, 2)) MB" -ForegroundColor DarkGray
#>

# ============================================================
# 4. 编译 Inno Setup 安装包（未安装 iscc 时自动跳过，不影响 zip 产出）
# ============================================================
Write-Host "[4/5] 编译 Inno Setup 安装包..." -ForegroundColor Yellow
$isccPath = $null
$isccCandidates = @(
    "iscc",
    "C:\Home\Develop\Scoop\shims\iscc.exe",
    "C:\Home\Develop\Scoop\apps\inno-setup\current\iscc.exe",
    "C:\Program Files (x86)\Inno Setup 6\iscc.exe",
    "C:\Program Files\Inno Setup 6\iscc.exe"
)
foreach ($cand in $isccCandidates) {
    if ($cand -eq "iscc") {
        $resolved = Get-Command iscc -ErrorAction SilentlyContinue
        if ($resolved) { $isccPath = $resolved.Source; break }
    } elseif (Test-Path $cand) {
        $isccPath = $cand; break
    }
}

if ($isccPath) {
    $issScript = Join-Path $repoRoot "scripts" "installer" "StickyNotes.iss"
    $iconFile = Join-Path $repoRoot "src" "StickyNotes" "Assets" "AppIcon.ico"
    $setupName = "StickyNotes-$Version-setup"
    $isccArgs = @('/Qp', "/DAppVersion=$Version", "/DPayloadDir=$fdOut")
    if (Test-Path $iconFile) { $isccArgs += "/DIconFile=$iconFile" }
    $isccArgs += @("/O$releaseDir", "/F$setupName", $issScript)
    & $isccPath @isccArgs
    if ($LASTEXITCODE -ne 0) { Write-Host "安装包编译失败" -ForegroundColor Red; exit 1 }
    $setupExe = Join-Path $releaseDir "$setupName.exe"
    if (-not (Test-Path $setupExe)) { Write-Host "安装包未生成：$setupExe" -ForegroundColor Red; exit 1 }
    $releaseFiles += $setupExe
    Write-Host "  安装包: $setupName.exe ($([math]::Round((Get-Item $setupExe).Length / 1MB, 2)) MB)" -ForegroundColor DarkGray
} else {
    Write-Host "  未找到 Inno Setup (iscc.exe)，跳过安装包生成（scoop install inno-setup 可启用）" -ForegroundColor DarkYellow
}

# ============================================================
# 5. 生成 SHA256 校验和并清理临时目录
# ============================================================
Write-Host "[5/5] 生成 SHA256 校验和清单..." -ForegroundColor Yellow
$checksumFile = Join-Path $releaseDir "SHA256SUMS.txt"
$lines = @()

foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    $fileName = Split-Path $file -Leaf
    $lines += "$hash  $fileName"
    $sizeMB = [math]::Round((Get-Item $file).Length / 1MB, 2)
    Write-Host "  $fileName ($sizeMB MB)" -ForegroundColor Green
    Write-Host "    SHA256: $hash" -ForegroundColor DarkGray
}

$content = ($lines -join "`n") + "`n"
[System.IO.File]::WriteAllText($checksumFile, $content, (New-Object System.Text.UTF8Encoding $false))

# 清理构建临时目录
Remove-Item $pubTemp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  StickyNotes 发布打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "输出目录: $releaseDir" -ForegroundColor Cyan
Get-ChildItem $releaseDir -File | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name)  ($sizeMB MB)" -ForegroundColor White
}
Write-Host ""
