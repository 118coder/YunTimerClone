@echo off
rem 定时关机助手（YunTimer 复刻版 · Fluent UI）构建脚本
rem 需要 .NET SDK（WPF + WPF-UI 4.3.0）
setlocal
cd /d "%~dp0"

dotnet publish src\YunTimerClone\YunTimerClone.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 goto :fail

if not exist dist mkdir dist
powershell -NoProfile -Command "if (Test-Path 'dist\定时关机助手.exe') { Remove-Item 'dist\定时关机助手.exe' }; Copy-Item 'publish\YunTimerClone.exe' 'dist\定时关机助手.exe' -Force"
if errorlevel 1 goto :fail

dotnet build tests\SelfTest\SelfTest.csproj -c Release
if errorlevel 1 goto :fail

echo.
echo 构建完成：
echo   dist\定时关机助手.exe    主程序（框架依赖单文件，需要 .NET 10 桌面运行时）
echo   自检运行：dotnet run --project tests\SelfTest -c Release -- --selftest
echo            dotnet run --project tests\SelfTest -c Release -- --uitest
exit /b 0

:fail
echo.
echo 构建失败！
exit /b 1
