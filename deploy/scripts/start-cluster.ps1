# =====================================================================
# Net-Game-Server 一键启动集群（Windows 开发环境）
# 先决：已 dotnet build（或 publish 后改 -MachineConfig 指向对应拓扑）
# 流程：后台启动 Logger（UDP 31320 + HTTP 31321）→ 后台启动 Machine（读取
#       machine.json 按依赖拓扑拉起全部节点 + HTTP 控制台 31321）
# PID 记录到 .cluster/ 供 stop-cluster.ps1 使用。
# 生产环境建议：Docker compose（deploy/docker-compose.yml）或 systemd/NSSM。
# =====================================================================
param(
    [string]$MachineConfig = "machine.json",
    [int]$MachineHttpPort = 31321,
    [int]$LoggerHttpPort = 31321,
    [switch]$SkipLogger
)

$ErrorActionPreference = "Stop"
$runDir = ".cluster"
New-Item -ItemType Directory -Force $runDir | Out-Null

# 1. Logger（日志聚合）
if (-not $SkipLogger) {
    $loggerOut = Join-Path $runDir "logger.out.log"
    $loggerErr = Join-Path $runDir "logger.err.log"
    $logger = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", "NetGameServer/Logger", "--", "--http-port", "$LoggerHttpPort") `
        -WindowStyle Hidden -RedirectStandardOutput $loggerOut -RedirectStandardError $loggerErr -PassThru
    $logger.Id | Set-Content (Join-Path $runDir "logger.pid")
    Write-Host "[start] Logger PID=$($logger.Id) 日志查看: http://127.0.0.1:$LoggerHttpPort/"
}

# 2. Machine（按 topology 拉起全部节点）
if (-not (Test-Path $MachineConfig)) {
    Write-Warning "未找到 $MachineConfig（可用 -MachineConfig 指定拓扑）。请先准备 machine.json（参考 machine.sample.json）。"
} else {
    $machineOut = Join-Path $runDir "machine.out.log"
    $machineErr = Join-Path $runDir "machine.err.log"
    $machine = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", "NetGameServer/Tools/Machine", "--", "--config", $MachineConfig, "--http-port", "$MachineHttpPort") `
        -WindowStyle Hidden -RedirectStandardOutput $machineOut -RedirectStandardError $machineErr -PassThru
    $machine.Id | Set-Content (Join-Path $runDir "machine.pid")
    Write-Host "[start] Machine PID=$($machine.Id) 控制台: http://127.0.0.1:$MachineHttpPort/"
}

Write-Host ""
Write-Host "启动完成。状态巡检: powershell -File deploy/scripts/cluster-status.ps1"
Write-Host "停止集群  : powershell -File deploy/scripts/stop-cluster.ps1"
Write-Host "进程日志  : $runDir\*.out.log"
