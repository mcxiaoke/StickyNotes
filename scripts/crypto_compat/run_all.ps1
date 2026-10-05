# ==============================================================================
# 跨平台端到端加密一致性全量自动化测试与交叉环形校验脚本
# 支持语言：C# (.NET), Node.js (JavaScript), Kotlin (Android), Python, Dart (Flutter)
# ==============================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = (Resolve-Path "$ScriptDir/../..").Path
$KotlincPath = "C:\Home\Develop\android-studio\plugins\Kotlin\kotlinc\bin\kotlinc.bat"

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "    StickyNotes 跨平台防偷窥加密：多语言纯函数 & NIST 向量一致性验证    " -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "工作目录: $ScriptDir`n" -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 阶段 1：各语言独立运行自测套件（验证 NIST KAT 向量与 vectors.json 测试用例）
# ------------------------------------------------------------------------------

Write-Host ">>> [Phase 1/2] 运行各语言单元测试与标准测试向量验证..." -ForegroundColor Yellow

# 1. C#
Write-Host "`n--- 1. C# (.NET 8) ---" -ForegroundColor Green
& dotnet run --project "$ScriptDir/csharp/CryptoCompat.csproj"
if ($LASTEXITCODE -ne 0) { throw "C# 测试失败！" }

# 2. Node.js
Write-Host "--- 2. Node.js (node:crypto) ---" -ForegroundColor Green
& node "$ScriptDir/nodejs/crypto_helper.js"
if ($LASTEXITCODE -ne 0) { throw "Node.js 测试失败！" }

# 3. Python
Write-Host "--- 3. Python (cryptography via uv) ---" -ForegroundColor Green
& uv run --with cryptography python "$ScriptDir/python/crypto_helper.py"
if ($LASTEXITCODE -ne 0) { throw "Python 测试失败！" }

# 4. Kotlin
Write-Host "--- 4. Kotlin (javax.crypto / Android) ---" -ForegroundColor Green
$TempJar = "$RootDir/temp/kotlin_crypto_compat.jar"
if (Test-Path $TempJar) { Remove-Item $TempJar -Force }
& $KotlincPath "$ScriptDir/kotlin/CryptoHelper.kt" "$ScriptDir/kotlin/Main.kt" -include-runtime -d $TempJar
if ($LASTEXITCODE -ne 0) { throw "Kotlin 编译失败！" }

& java "-Dfile.encoding=UTF-8" -jar $TempJar
$ktResult = $LASTEXITCODE
if ($ktResult -ne 0) { throw "Kotlin 测试运行失败！" }

# 5. Dart
Write-Host "--- 5. Dart (encrypt/pointycastle / Flutter) ---" -ForegroundColor Green
Push-Location "$ScriptDir/dart"
try {
    & dart run bin/crypto_helper.dart
    if ($LASTEXITCODE -ne 0) { throw "Dart 测试失败！" }
} finally {
    Pop-Location
}

# ------------------------------------------------------------------------------
# 阶段 2：跨语言闭环交叉加解密测试（Cross-Platform Ring Interop）
# 流程：C# 加密 -> Node 解密 -> Node 加密 -> Kotlin 解密 -> Kotlin 加密 -> Dart 解密 -> Dart 加密 -> Python 解密 -> Python 加密 -> C# 解密
# ------------------------------------------------------------------------------

Write-Host "`n>>> [Phase 2/2] 运行跨语言环形互通交叉测试 (Cross-Platform Ring)..." -ForegroundColor Yellow

$Password = "StickyNotes#CrossPlatformRingPass!2026"
$TestPlaintext = @"
【环形跨平台互通实测】
中文便签内容：这是一段包含特殊字符的加密便签！
Special Characters: ~!@#$%^&*()_+`-={}|[]\:";'<>?,./
Emoji 测试: 🚀🎉🔐📝💡🔥
换行与制表符:
	缩进第二行
末尾空白空格测试   
"@

# 辅助函数：CLI 调用各语言加密（通过 Base64 传递，避免 Windows 命令行拆分换行与特殊字符）
function Invoke-LangEncrypt($lang, $text, $pass) {
    $textB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text))
    switch ($lang) {
        "C#"     { return (& dotnet run --project "$ScriptDir/csharp/CryptoCompat.csproj" -- "encrypt-b64" $textB64 $pass) | ConvertFrom-Json }
        "Node"   { return (& node "$ScriptDir/nodejs/crypto_helper.js" "encrypt-b64" $textB64 $pass) | ConvertFrom-Json }
        "Kotlin" { return (& java "-Dfile.encoding=UTF-8" -jar $TempJar "encrypt-b64" $textB64 $pass) | ConvertFrom-Json }
        "Dart"   {
            Push-Location "$ScriptDir/dart"
            try {
                return (& dart run bin/crypto_helper.dart "encrypt-b64" $textB64 $pass) | ConvertFrom-Json
            } finally { Pop-Location }
        }
        "Python" { return (& uv run --with cryptography python "$ScriptDir/python/crypto_helper.py" "encrypt-b64" $textB64 $pass) | ConvertFrom-Json }
        default  { throw "Unknown language $lang" }
    }
}

# 辅助函数：CLI 调用各语言解密（通过 Base64 返回，确保换行与 Emoji 100% 精确接收）
function Invoke-LangDecrypt($lang, $iv, $data, $pass) {
    $decB64 = $null
    switch ($lang) {
        "C#"     { $decB64 = (& dotnet run --project "$ScriptDir/csharp/CryptoCompat.csproj" -- "decrypt-b64" $iv $data $pass) }
        "Node"   { $decB64 = (& node "$ScriptDir/nodejs/crypto_helper.js" "decrypt-b64" $iv $data $pass) }
        "Kotlin" { $decB64 = (& java "-Dfile.encoding=UTF-8" -jar $TempJar "decrypt-b64" $iv $data $pass) }
        "Dart"   {
            Push-Location "$ScriptDir/dart"
            try {
                $decB64 = (& dart run bin/crypto_helper.dart "decrypt-b64" $iv $data $pass)
            } finally { Pop-Location }
        }
        "Python" { $decB64 = (& uv run --with cryptography python "$ScriptDir/python/crypto_helper.py" "decrypt-b64" $iv $data $pass) }
        default  { throw "Unknown language $lang" }
    }
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($decB64.Trim()))
}

$RingPairs = @(
    @{ Enc = "C#";     Dec = "Node" },
    @{ Enc = "Node";   Dec = "Kotlin" },
    @{ Enc = "Kotlin"; Dec = "Dart" },
    @{ Enc = "Dart";   Dec = "Python" },
    @{ Enc = "Python"; Dec = "C#" }
)

foreach ($pair in $RingPairs) {
    $encLang = $pair.Enc
    $decLang = $pair.Dec
    Write-Host "  测试环节: [$encLang 加密] ---> [$decLang 解密] ... " -NoNewline -ForegroundColor Cyan

    $encRes = Invoke-LangEncrypt $encLang $TestPlaintext $Password
    $decText = Invoke-LangDecrypt $decLang $encRes.iv $encRes.data $Password

    if ($decText -ceq $TestPlaintext) {
        Write-Host "PASS (100% 精确还原)" -ForegroundColor Green
    } else {
        Write-Host "FAIL" -ForegroundColor Red
        Write-Host "  期望明文: `n$TestPlaintext" -ForegroundColor DarkRed
        Write-Host "  解密明文: `n$decText" -ForegroundColor DarkRed
        throw "环节 [$encLang -> $decLang] 解密结果不一致！"
    }
}

# 清理临时产物
if (Test-Path $TempJar) { Remove-Item $TempJar -Force }

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "  恭喜！全部 5 种语言纯函数与 NIST 标准向量全部通过，跨语言环形互通 100% 一致！" -ForegroundColor Green
Write-Host "======================================================================`n" -ForegroundColor Green
