@echo off
rem 定时关机助手（YunTimer 复刻版 · 零依赖 Fluent UI）构建脚本
rem 使用系统自带的 .NET Framework 4.x csc.exe，无需安装任何第三方依赖
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo 未找到 .NET Framework 4.x csc.exe
  exit /b 1
)
set WPFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set NETDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%WPFDIR%\PresentationFramework.dll" (
  set WPFDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF
  set NETDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
)
set REFS=/r:"%WPFDIR%\PresentationFramework.dll" /r:"%WPFDIR%\PresentationCore.dll" /r:"%WPFDIR%\WindowsBase.dll" /r:"%NETDIR%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
if not exist dist mkdir dist

rem GUI 主程序（winexe：双击运行，不显示控制台）
"%CSC%" /nologo /warn:0 /target:winexe /platform:anycpu /codepage:65001 %REFS% /out:dist\定时关机助手.exe src\YunTimerClone.cs
if errorlevel 1 goto :fail

rem 自检运行器（console：用于 --selftest / --uitest，日常使用不需要）
"%CSC%" /nologo /warn:0 /target:exe /platform:anycpu /codepage:65001 %REFS% /out:dist\selftest.exe src\YunTimerClone.cs
if errorlevel 1 goto :fail

echo.
echo 构建完成：
echo   dist\定时关机助手.exe   主程序（零依赖，Windows 10/11 自带 .NET Framework 4.8）
echo   dist\selftest.exe       逻辑/UI 自检运行器
exit /b 0

:fail
echo.
echo 构建失败！
exit /b 1
