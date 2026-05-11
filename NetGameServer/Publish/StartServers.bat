@echo off
chcp 65001 >nul
cd /d "%~dp0"

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

echo 所有服务启动指令已发送！
