# ============================================================
# 联调面冒烟脚本（P2 修复验证用）
# 覆盖：
#   1) HTTP login -> 真实 Token
#   2) query-account 不带 X-Auth-Token        -> 期望 401（未授权）
#   3) query-account 带本人 Token 查本人       -> 期望 200 Exists=true
#   4) query-account 带 Token 查他人账户       -> 期望 401（越权拒绝）
#   5) change-nickname                         -> 期望 Success=false（未实现，不再是假成功）
#   6) Bots 登录（10002 LoginRes 正确解析，loginCompleted 出现、无假 40002）
#
# 前置条件：集群已启动（DB/Gateway/Login，Redis/MySQL 可用）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\SmokeTest.ps1
#   可选环境变量：ApiBase(默认 http://127.0.0.1:31303)、HttpApiKeys、BotsExe、BotsCount
# ============================================================
$ErrorActionPreference = "Stop"
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
  if ($ok) { Write-Host "[PASS] $name $detail" -ForegroundColor Green }
  else { Write-Host "[FAIL] $name $detail" -ForegroundColor Red; $script:failed++ }
}

$ApiBase  = $env:ApiBase  ?? "http://127.0.0.1:31303"
$BotsExe  = $env:BotsExe  ?? "..\Bots\bin\Debug\net10.0\Bots.exe"
$BotsCount = $env:BotsCount ?? "2"
$ApiKey    = $env:HttpApiKeys ?? ""

# 测试账号（冒烟用，不存在则自动注册）
$AccA = "smoke_a_" + [guid]::NewGuid().ToString("N").Substring(0,8)
$AccB = "smoke_b_" + [guid]::NewGuid().ToString("N").Substring(0,8)
$Pwd  = "Smoke@123"

function Invoke-Json([string]$method, [string]$path, $body, [string[]]$headers) {
  $params = @{ Method=$method; Uri="$ApiBase$path"; ContentType="application/json"; ErrorAction="SilentlyContinue" }
  if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Compress) }
  if ($headers) { foreach ($h in $headers) { $kv = $h -split ":",2; $params.Headers[$kv[0]] = $kv[1].Trim() } }
  return Invoke-RestMethod @params
}

Write-Host "== 1. 注册/登录 ==" -ForegroundColor Cyan
$null = Invoke-Json "POST" "/api/account/register" @{ Account=$AccA; Password=$Pwd }
$null = Invoke-Json "POST" "/api/account/register" @{ Account=$AccB; Password=$Pwd }
$loginA = Invoke-Json "POST" "/api/account/login" @{ Account=$AccA; Password=$Pwd }
Check "login 返回 Token" (-not [string]::IsNullOrEmpty($loginA.Token)) ("Token非空=" + (-not [string]::IsNullOrEmpty($loginA.Token)))

Write-Host "== 2. query-account 鉴权绑定 ==" -ForegroundColor Cyan
try {
  $null = Invoke-Json "POST" "/api/account/query-account" @{ Account=$AccA }
  Check "无 Token -> 401" $false "（竟然返回了 200/数据，鉴权缺失！）"
} catch {
  Check "无 Token -> 401" ($_.Exception.Response.StatusCode.value__ -eq 401) ("HTTP " + $_.Exception.Response.StatusCode.value__)
}

$qA = Invoke-Json "POST" "/api/account/query-account" @{ Account=$AccA } @("X-Api-Key:$ApiKey", "X-Auth-Token:$($loginA.Token)")
Check "本人 Token 查本人 -> Exists" ($qA.Exists -eq $true) ("Exists=" + $qA.Exists)

try {
  $null = Invoke-Json "POST" "/api/account/query-account" @{ Account=$AccB } @("X-Api-Key:$ApiKey", "X-Auth-Token:$($loginA.Token)")
  Check "Token 查他人 -> 401" $false "（竟然返回了数据，越权未拦截！）"
} catch {
  Check "Token 查他人 -> 401" ($_.Exception.Response.StatusCode.value__ -eq 401) ("HTTP " + $_.Exception.Response.StatusCode.value__)
}

Write-Host "== 3. change-nickname 不再是假成功 ==" -ForegroundColor Cyan
$nick = Invoke-Json "POST" "/api/account/change-nickname" @{ Account=$AccA; Nickname="冒烟新昵称" } @("X-Api-Key:$ApiKey", "X-Auth-Token:$($loginA.Token)")
Check "change-nickname 返回未实现" ($nick.Success -eq $false) ("Success=" + $nick.Success + " Message=" + $nick.Message)

Write-Host "== 4. Bots 登录（10002 解析）==" -ForegroundColor Cyan
if (Test-Path $BotsExe) {
  $out = & $BotsExe --count $BotsCount --host 127.0.0.1 --port 31300 --duration 8 --scene default 2>&1
  $text = $out -join "`n"
  Check "Bots 正常退出" ($LASTEXITCODE -eq 0) ("exit=" + $LASTEXITCODE)
  Check "登录完成统计出现" ($text -match "loginCompleted|登录完成") "（日志含登录完成统计）"
  Check "无假 40002" ($text -notmatch "40002") "（未出现伪造 40002 处理路径）"
} else {
  Write-Host "[SKIP] 未找到 Bots 可执行文件: $BotsExe （请先构建 Bots 或设置 BotsExe）" -ForegroundColor Yellow
}

Write-Host ""
if ($failed -eq 0) { Write-Host "===== 冒烟全部通过 =====" -ForegroundColor Green; exit 0 }
else { Write-Host "===== 冒烟存在 $failed 项失败 =====" -ForegroundColor Red; exit 1 }
