@echo off
rem 定时关机助手（YunTimer 复刻版 · 原生 C 版）构建脚本
rem 使用 mingw-w64 gcc 编译原生 Win32 程序，零依赖、双击秒开
setlocal
set GCC=C:\mingw64\mingw64\bin\gcc.exe
if not exist "%GCC%" set GCC=C:\mingw64\bin\gcc.exe
if not exist "%GCC%" for /f "delims=" %%i in ('where gcc 2^>nul') do set GCC=%%i
if not exist "%GCC%" (
  echo 未找到 gcc，请安装 mingw-w64
  exit /b 1
)
if not exist dist mkdir dist

rem GUI 主程序（原生 Win32，无控制台窗口）
"%GCC%" -O2 -s -municode -mwindows -o dist\定时关机助手.exe src\yuntimer.c -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
if errorlevel 1 goto :fail

rem 自检运行器（console：--selftest / --uitest）
"%GCC%" -O2 -s -DTEST_BUILD -o dist\selftest.exe src\yuntimer.c -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
if errorlevel 1 goto :fail

echo.
echo 构建完成：
echo   dist\定时关机助手.exe   主程序（原生 C，零依赖，双击秒开）
echo   dist\selftest.exe       逻辑/UI 自检运行器
exit /b 0

:fail
echo.
echo 构建失败！
exit /b 1
