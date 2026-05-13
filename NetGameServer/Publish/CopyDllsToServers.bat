@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "SRC=%~dp0DLL"
if not exist "%SRC%" (
  echo 错误：找不到 DLL 目录：%SRC%
  exit /b 1
)

for %%D in (DB Game Gateway Login Center Battle) do (
  if not exist "%%~D" mkdir "%%~D"
)

echo 正在将 "%SRC%" 下的所有文件复制到目标文件夹...
for %%f in ("%SRC%\*.*") do (
  for %%D in (DB Game Gateway Login Center Battle) do (
    copy /Y "%%~f" "%%~D\" >nul
  )
)

echo 复制完成！
exit /b 0
