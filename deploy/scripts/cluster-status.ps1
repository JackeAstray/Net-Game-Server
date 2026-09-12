# =====================================================================
# Net-Game-Server 集群状态巡检（Windows PowerShell）
#   用法:  powershell -ExecutionPolicy Bypass -File cluster-status.ps1
#   输出:  各节点 TCP 端口探测 + 健康检查摘要 + 管理工具地址
#   注意:  Battle 可多实例，默认探测 31307-31309；用 -BattleInstances 调整。
# =====================================================================
param(
    [int]$BattleInstances = 3,
    [int]$ProbeTimeoutMs = 800
)

$ErrorActionPreference = "SilentlyContinue"

function Test-TcpPort([string]$HostName, [int]$Port, [int]$TimeoutMs) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($HostName, $Port)
        if (-not $task.Wait($TimeoutMs)) { return $false }
        return $client.Connected
    } catch { return $false }
    finally { $client.Dispose() }
}

function Get-Health([int]$Port) {
    # 健康检查端口 = 业务端口 + 10000（/healthz 存活 /readyz 就绪，关服排空时 readyz 返回 503）
    $hp = $Port + 10000
    try {
        $h = Invoke-RestMethod -Uri "http://127.0.0.1:$hp/healthz" -TimeoutSec 2
        return "healthz=$($h.status)"
    } catch { return "healthz=unreachable" }
}

Write-Host "==== Net-Game-Server 集群状态巡检 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ====" -ForegroundColor Cyan

$nodes = @(
    @{ Name = "Gateway"; Port = 31300 },
    @{ Name = "Login";   Port = 31302 },
    @{ Name = "Game";    Port = 31304 },
    @{ Name = "DB";      Port = 31305 },
    @{ Name = "Center";  Port = 31306 }
)
for ($i = 0; $i -lt $BattleInstances; $i++) {
    $nodes += @{ Name = "Battle#$($i+1)"; Port = 31307 + $i }
}

$rows = foreach ($n in $nodes) {
    $open = Test-TcpPort "127.0.0.1" $n.Port $ProbeTimeoutMs
    $health = if ($n.Name -eq "Center" -or $n.Name -like "Battle*") { Get-Health $n.Port } else { "" }
    [PSCustomObject]@{
        Node    = $n.Name
        Port    = $n.Port
        Status  = if ($open) { "UP  " } else { "DOWN" }
        Health  = $health
    }
}

$rows | Format-Table -AutoSize

# Logger UDP 端口（UDP 无连接语义：只做端口监听探测）
$logger = Test-TcpPort "127.0.0.1" 31320 $ProbeTimeoutMs
Write-Host "Logger  (UDP 31320): $(if($logger){'UP'}else{'DOWN'})"

Write-Host ""
Write-Host "==== 管理工具 ====" -ForegroundColor Cyan
Write-Host "  Center 管理台 : http://127.0.0.1:41306/   (X-Api-Key 鉴权)"
Write-Host "  Machine 控制台 : http://127.0.0.1:31321/   (进程看护状态/启停)"
Write-Host "  Logger 日志查看: http://127.0.0.1:31321/   (Logger 进程内嵌，端口与其 --http-port 一致)"
Write-Host "  监控指标       : http://127.0.0.1:41306/metrics"

$down = @($rows | Where-Object { $_.Status -eq "DOWN" })
if ($down.Count -gt 0) {
    Write-Host ""
    Write-Warning "以下节点未就绪: $($down.Node -join ', ')。请先启动 Machine/Logger 或检查配置。"
}
