@echo off
chcp 65001 >nul
cd /d "%~dp0"

:: ===== 集群内部认证共享密钥（CenterNodeSharedSecret）=====
:: 所有节点共用同一份密钥完成内部 HMAC 认证握手；密钥必须一致，否则节点间认证失败。
:: 若已通过环境变量/外部配置提供，则尊重运维配置；否则首次启动自动生成并持久化到 .cluster_secret。
if "%CenterNodeSharedSecret%"=="" (
  if not exist ".cluster_secret" (
    call :gen_secret
    if errorlevel 1 exit /b 1
  )
  set /p CenterNodeSharedSecret=< .cluster_secret
)
if "%CenterNodeSharedSecret%"=="" (
  echo [错误] 无法获取共享密钥 CenterNodeSharedSecret，请手动设置后重试。
  exit /b 1
)
echo 集群共享密钥 CenterNodeSharedSecret 已就绪（来源：环境变量或 .cluster_secret）

echo 正在启动 Redis...
:: 如果 redis-server 不在系统环境变量(Path)中，请将下面的 redis-server 替换为你本地 Redis 的完整路径 (例如: "C:\Redis\redis-server.exe")
start "Redis" /D "Redis" redis-server.exe

:: 稍微等待一下，确保 Redis 能够先启动完成
timeout /t 2 /nobreak >nul

echo 正在启动 DB...
:: 注意: 这里的路径假设你的 exe 直接在对应项目的根目录中。
:: 如果你是通过 Visual Studio 默认编译的，可将路径修改为相对路径，例如: /D "DB\bin\Debug\net10.0" DB.exe
start "DB" /D "DB" DB.exe

timeout /t 1 /nobreak >nul

echo 正在启动 Gateway...
start "Gateway" /D "Gateway" Gateway.exe

timeout /t 1 /nobreak >nul

echo 正在启动 Login...
start "Login" /D "Login" Login.exe

timeout /t 1 /nobreak >nul

echo 正在启动 Game...
start "Game" /D "Game" Game.exe

timeout /t 1 /nobreak >nul

echo 正在启动 Center...
start "Center" /D "Center" Center.exe

timeout /t 1 /nobreak >nul

echo 正在启动 Battle...
start "Battle" /D "Battle" Battle.exe

echo 所有服务启动指令已发送！
exit /b 0

:gen_secret
:: 用 PowerShell 生成 32 字节随机密钥（Base64 编码，44 字符）并写入 .cluster_secret
:: 注意：用 RandomNumberGenerator.Create().GetBytes()（PS 5.1/.NET Framework 与 PS 7/.NET 均可用），
:: 不能使用 RandomNumberGenerator.Fill（仅 .NET Core，Windows PowerShell 5.1 会报方法不存在 → 生成全零弱密钥）。
for /f "usebackq delims=" %%S in (`powershell -NoProfile -Command "$b = New-Object byte[] 32; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)"`) do set "SECRET=%%S"
if "%SECRET%"=="" (
  echo [错误] 自动生成共享密钥失败（PowerShell 不可用？），请手动设置 CenterNodeSharedSecret 环境变量。
  exit /b 1
)
> .cluster_secret echo %SECRET%
echo 已自动生成集群共享密钥并保存到 .cluster_secret（请妥善保管，勿提交到版本库）
exit /b 0
