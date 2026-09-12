# =====================================================================
# Net-Game-Server 一键停止集群（Windows 开发环境）
# 读取 .cluster/*.pid 停止 Machine 与 Logger，并清理被 Machine 托管的
# 残留子节点（命令行含 --supervised-by 的 dotnet 进程）。
# 注意：Stop-Process 为强制终止（控制台进程无法注入 Ctrl+C）；
#       优雅排空请用 Docker compose down 或 systemd stop。
# =====================================================================
param()

$ErrorActionPreference = "SilentlyContinue"
$runDir = ".cluster"

# 1. 停 Machine（先停看护，再清理其子进程）
if (Test-Path "$runDir\machine.pid") {
    $pid = [int](Get-Content "$runDir\machine.pid")
    Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
    Remove-Item "$runDir\machine.pid"
    Write-Host "[stop] Machine PID=$pid"
}

# 2. 清理托管子节点（命令行含 --supervised-by 的 dotnet 进程）
$children = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match '--supervised-by' }
foreach ($c in $children) {
    Stop-Process -Id $c.ProcessId -Force -ErrorAction SilentlyContinue
    Write-Host "[stop] 子节点 PID=$($c.ProcessId)"
}

# 3. 停 Logger
if (Test-Path "$runDir\logger.pid") {
    $lpid = [int](Get-Content "$runDir\logger.pid")
    Stop-Process -Id $lpid -Force -ErrorAction SilentlyContinue
    Remove-Item "$runDir\logger.pid"
    Write-Host "[stop] Logger PID=$lpid"
}

Write-Host "集群已停止。"
