@echo off
rem 定时关机助手（YunTimer 复刻版 · 原生 C 版）构建脚本
rem 使用 mingw-w64 gcc + windres 编译原生 Win32 程序，零依赖、双击秒开
setlocal
set GCC=C:\mingw64\mingw64\bin\gcc.exe
if not exist "%GCC%" set GCC=C:\mingw64\bin\gcc.exe
if not exist "%GCC%" for /f "delims=" %%i in ('where gcc 2^>nul') do set GCC=%%i
if not exist "%GCC%" (
  echo 未找到 gcc，请安装 mingw-w64
  exit /b 1
)
set WINDRES=C:\mingw64\mingw64\bin\windres.exe
if not exist "%WINDRES%" for /f "delims=" %%i in ('where windres 2^>nul') do set WINDRES=%%i
if not exist dist mkdir dist

rem 资源（图标 + 版本信息）
pushd src
"%WINDRES%" app.rc -O coff -o app_res.o
popd
if errorlevel 1 goto :fail

rem GUI 主程序（原生 Win32，无控制台窗口）
"%GCC%" -O2 -s -municode -mwindows -o dist\定时关机助手.exe src\yuntimer.c src\app_res.o -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
if errorlevel 1 goto :fail

rem 自检运行器（console：--selftest / --uitest）
"%GCC%" -O2 -s -DTEST_BUILD -o dist\selftest.exe src\yuntimer.c src\app_res.o -lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32
if errorlevel 1 goto :fail

echo.
echo 构建完成：
echo   dist\定时关机助手.exe   主程序（原生 C，零依赖，双击秒开，内嵌图标）
echo   dist\selftest.exe       逻辑/UI 自检运行器
exit /b 0

:fail
echo.
echo 构建失败！
exit /b 1
