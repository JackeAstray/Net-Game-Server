@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "PROCS=redis-server.exe DB.exe Gateway.exe Login.exe Game.exe Center.exe Battle.exe"

echo 正在结束服务器进程（需要管理员权限以结束某些进程）...
for %%P in (%PROCS%) do (
  tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul
  if %ERRORLEVEL%==0 (
    echo 结束 %%P ...
    taskkill /F /IM "%%P" >nul 2>&1
    if %ERRORLEVEL%==0 (
      echo 已结束 %%P
    ) else (
      echo 无法结束 %%P（可能需要管理员权限）
    )
  ) else (
    echo 未找到 %%P
  )
)

echo 全部操作完成。
exit /b 0
