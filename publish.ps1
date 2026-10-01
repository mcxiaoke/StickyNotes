<#
.SYNOPSIS
    StickyNotes 便签发布脚本 — 生成 Windows 便携版发布产物 + SHA256 校验和

.DESCRIPTION
    构建产物：
    1. 框架依赖版 zip：dist/StickyNotes-{ver}-win-x64.zip（需 .NET 8+ runtime，体积仅约 20MB）
    2. 免安装自包含单文件 zip：dist/StickyNotes-Standalone-{ver}-win-x64.zip（内嵌独立运行时，开箱即用）
    3. SHA256 校验和清单：dist/SHA256SUMS.txt

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
# 1. 清理旧产物
# ============================================================
Write-Host "[1/5] 清理旧产物与构建缓存..." -ForegroundColor Yellow
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
if (Test-Path $pubTemp) { Remove-Item $pubTemp -Recurse -Force }
New-Item -ItemType Directory -Path $pubTemp -Force | Out-Null

$releaseFiles = @()

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

# 清理 pdb 调试符号文件
Get-ChildItem $fdOut -Filter "*.pdb" | Remove-Item -Force

# ============================================================
# 3. 构建免安装独立单文件版 (Self-Contained Single File)
# ============================================================
Write-Host "[3/5] 构建自包含单文件独立版 (无需安装 .NET 运行时)..." -ForegroundColor Yellow
$scOut = Join-Path $pubTemp "standalone"
& dotnet publish $srcProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v minimal -o $scOut
if ($LASTEXITCODE -ne 0) { Write-Host "构建独立单文件版失败" -ForegroundColor Red; exit 1 }
Get-ChildItem $scOut -Filter "*.pdb" | Remove-Item -Force

# ============================================================
# 4. 打包压缩为发布 zip
# ============================================================
Write-Host "[4/5] 压缩打包产物..." -ForegroundColor Yellow

$fdZip = Join-Path $distDir "StickyNotes-$Version-win-x64.zip"
Compress-Archive -Path "$fdOut\*" -DestinationPath $fdZip -Force
$releaseFiles += $fdZip
Write-Host "  便携版 zip: $([math]::Round((Get-Item $fdZip).Length / 1MB, 2)) MB" -ForegroundColor DarkGray

$scZip = Join-Path $distDir "StickyNotes-Standalone-$Version-win-x64.zip"
Compress-Archive -Path "$scOut\*" -DestinationPath $scZip -Force
$releaseFiles += $scZip
Write-Host "  自包含单文件 zip: $([math]::Round((Get-Item $scZip).Length / 1MB, 2)) MB" -ForegroundColor DarkGray

# ============================================================
# 5. 生成 SHA256 校验和并清理临时目录
# ============================================================
Write-Host "[5/5] 生成 SHA256 校验和清单..." -ForegroundColor Yellow
$checksumFile = Join-Path $distDir "SHA256SUMS.txt"
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
Write-Host "输出目录: $distDir" -ForegroundColor Cyan
Get-ChildItem $distDir -File | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name)  ($sizeMB MB)" -ForegroundColor White
}
Write-Host ""
