# =====================================================================
# Net-Game-Server Windows 服务化（NSSM）安装脚本
# 前提：安装 NSSM（choco install nssm / scoop install nssm）且 nssm 在 PATH
# 用法：
#   .\install-service-nssm.ps1 -Node center -Dir C:\netgame\Publish\Center
#   .\install-service-nssm.ps1 -Node gateway -Dir C:\netgame\Publish\Gateway -Port 31300
# 卸载：nssm remove NetGame-<Node> confirm
# 说明：每个节点注册为独立 Windows 服务，NSSM 负责崩溃自动重启（2s 延迟）。
#       Machine/拓扑编排仍用于开发；生产 Windows 场景可用本脚本替代。
# =====================================================================
param(
    [Parameter(Mandatory)][ValidateSet("gateway","login","game","db","center","battle","logger")]
    [string]$Node,
    [Parameter(Mandatory)][string]$Dir,
    [int]$Port = 0,
    [string]$CenterHost = "127.0.0.1",
    [int]$CenterPort = 31306
)

$ErrorActionPreference = "Stop"
$svc = "NetGame-$Node"
$exe = Join-Path $Dir "$Node.exe"

if (-not (Test-Path $exe)) { Write-Error "未找到 $exe（请先发布该节点）"; exit 1 }
if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) { Write-Error "nssm 不在 PATH，请先安装：choco install nssm"; exit 1 }

# 服务参数（与 Machine 注入的命令行保持一致）
$argList = @()
if ($Port -gt 0) { $argList += "--port $Port" }
if (@("gateway","login","game","battle") -contains $Node) {
    $argList += "--center-host $CenterHost --center-port $CenterPort"
}

Write-Host "安装服务 $svc：$exe $($argList -join ' ')"
nssm install $svc $exe ($argList -join " ") | Out-Null
nssm set $svc AppDirectory $Dir | Out-Null
nssm set $svc Start SERVICE_AUTO_START | Out-Null
nssm set $svc AppRestartDelay 2000 | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Dir "logs") | Out-Null
nssm set $svc AppStdout (Join-Path $Dir "logs\$svc.out.log") | Out-Null
nssm set $svc AppStderr (Join-Path $Dir "logs\$svc.err.log") | Out-Null
# 集群共享密钥（与部署 .env 一致）；生产务必替换
nssm set $svc AppEnvironmentExtra "CenterNodeSharedSecret=replace-with-real-secret" | Out-Null

Write-Host ""
Write-Host "已安装。常用命令："
Write-Host "  启动:   nssm start $svc   （或 sc start $svc）"
Write-Host "  停止:   nssm stop $svc"
Write-Host "  状态:   sc query $svc"
Write-Host "  日志:   $Dir\logs\$svc.out.log"
