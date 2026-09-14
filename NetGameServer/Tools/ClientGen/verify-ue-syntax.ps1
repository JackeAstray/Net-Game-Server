<#
.SYNOPSIS
  对 ClientGen 生成的 UE C++ 产物做真实编译检查（MSVC 语法/语义检查，不产出 .obj）。

.DESCRIPTION
  客户端 C++ 产物是入库交付物，但默认没有任何编译验证。实测它曾长期处于"完全编译不过"的状态：
  产物含中文注释且无 UTF-8 BOM，MSVC 在中文 Windows（936 代码页）下按 ANSI 解码，多字节错位吞掉
  后续代码行 → 报一片 `error C2039: "ReadCount": 不是 "mp::Reader" 的成员`，而 dotnet build 与
  全部 8 套测试仍然全绿。本脚本用 MSVC `cl /Zs` 真正编译一遍（覆盖 MemoryPack.h / Messages.h /
  NetClient.h），把这类"只存在于 C++ 侧"的问题暴露出来。

  找不到工具链时会明确提示并跳过（退出码 0）——不制造"必然失败的检查"。

.PARAMETER UeDir
  UE 产物目录，默认 <脚本目录>/Output/UE。

.PARAMETER VcVars
  vcvars64.bat 路径，默认自动定位（或读环境变量 UE_VCVARS）。

.EXAMPLE
  powershell -File NetGameServer/Tools/ClientGen/verify-ue-syntax.ps1

.EXAMPLE
  $env:UE_VCVARS = 'D:\Program Files\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvars64.bat'
  powershell -File NetGameServer/Tools/ClientGen/verify-ue-syntax.ps1
#>
[CmdletBinding()]
param(
    [string]$UeDir,
    [string]$VcVars
)

$ErrorActionPreference = 'Stop'

# ---------- 1. UE 产物目录 ----------
if (-not $UeDir) { $UeDir = Join-Path $PSScriptRoot 'Output\UE' }
if (-not (Test-Path $UeDir)) {
    Write-Host "找不到 UE 产物目录：$UeDir（先运行 README 里的 ClientGen 命令生成）" -ForegroundColor Red
    exit 2
}
$UeDir = (Resolve-Path $UeDir).Path

# ---------- 2. 定位 MSVC（vcvars64.bat）----------
function Find-VcVars {
    param([string]$Explicit)

    if ($Explicit -and (Test-Path $Explicit)) { return $Explicit }
    if ($env:UE_VCVARS -and (Test-Path $env:UE_VCVARS)) { return $env:UE_VCVARS }

    # 2a. vswhere（VS 官方定位器）
    $vswhere = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($vswhere) {
        $roots = & $vswhere -products * -property installationPath 2>$null
        foreach ($r in @($roots)) {
            if (-not $r) { continue }
            $c = Join-Path $r 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $c) { return $c }
        }
    }

    # 2b. 回退：扫各盘符下的常见 VS 安装根（vswhere 缺失 / VS 装在非 C: 盘）
    $patterns = @()
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue).Name) {
        $patterns += "$drive`:\Program Files\Microsoft Visual Studio\*\VC\Auxiliary\Build\vcvars64.bat"
    }
    $patterns += (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\*\VC\Auxiliary\Build\vcvars64.bat')
    $patterns += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\*\VC\Auxiliary\Build\vcvars64.bat')

    foreach ($p in $patterns) {
        $hit = Get-ChildItem -Path $p -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$vcvars = Find-VcVars -Explicit $VcVars
$clInPath = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $vcvars -and -not $clInPath) {
    Write-Host "未找到 MSVC 工具链（vcvars64.bat / cl.exe），跳过 UE 产物编译检查。" -ForegroundColor Yellow
    Write-Host "  安装 VS 时勾选“使用 C++ 的桌面开发”工作负载即可；或用 `$env:UE_VCVARS 指向 vcvars64.bat。"
    exit 0
}

# ---------- 3. 编译检查（临时 .bat，避开 cmd 引号地狱）----------
$sources = @()
foreach ($f in @('Demo.cpp', 'NetClient.cpp')) {
    if (Test-Path (Join-Path $UeDir $f)) { $sources += $f }
}
if ($sources.Count -eq 0) {
    Write-Host "UE 产物目录没有可编译的 .cpp：$UeDir" -ForegroundColor Red
    exit 2
}

$bat = Join-Path $env:TEMP ("ue_syntax_{0}.bat" -f $PID)
$lines = @('@echo off')
if ($vcvars) { $lines += "call `"$vcvars`" >nul 2>&1" }
$lines += "cd /d `"$UeDir`""
# /Zs = 仅语法与语义检查（不生成 .obj / 不链接）
$lines += "cl /nologo /Zs /std:c++17 /EHsc $($sources -join ' ')"
$lines += 'echo UE_SYNTAX_EXIT=%ERRORLEVEL%'
Set-Content -Path $bat -Value $lines -Encoding ASCII

try {
    $out = & cmd.exe /c $bat 2>&1
} finally {
    Remove-Item $bat -Force -ErrorAction SilentlyContinue
}

$exitLine = $out | Where-Object { $_ -match '^UE_SYNTAX_EXIT=(\d+)' } | Select-Object -Last 1
$code = 1
if ($exitLine -and $exitLine -match '^UE_SYNTAX_EXIT=(\d+)') { $code = [int]$matches[1] }

$c4819 = @($out | Where-Object { $_ -match 'C4819' }).Count
$errors = @($out | Where-Object { $_ -match ': error ' })

# 回显编译器输出（去掉自己的哨兵行）
$out | Where-Object { $_ -notmatch '^UE_SYNTAX_EXIT=' } | ForEach-Object { $_ }

if ($code -eq 0 -and $errors.Count -eq 0 -and $c4819 -eq 0) {
    Write-Host "OK：UE 产物编译检查通过（$($sources -join ', ')，无 error、无 C4819 编码警告）" -ForegroundColor Green
    exit 0
}

Write-Host "FAIL：UE 产物编译检查未通过（exit=$code, error=$($errors.Count), C4819=$c4819）" -ForegroundColor Red
if ($c4819 -gt 0) {
    Write-Host "  提示：C4819 = 源文件编码无法在当前代码页表示，产物必须带 UTF-8 BOM（见 Output/UE/README.md「编码要求」）。" -ForegroundColor Yellow
}
exit 1
