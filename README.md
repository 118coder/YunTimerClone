# 定时关机助手（YunTimer 复刻版 · Fluent UI）

一个 Windows 桌面「定时关机助手」，功能与交互复刻自 [www.yunguanji.com](https://www.yunguanji.com) 的 **YunTimer v2.0.1.9「定时关机助手」**，界面采用 GitHub 高星 WPF UI 库 **WPF-UI** 的 Fluent 设计语言（Windows 11 原生观感）。代码为 100% 原创实现（未复制、未反编译原程序的任何代码与资源）。

## WPF UI 库选型依据（GitHub 星数实测，2026-10）

| 排名 | 库 | 星数 | 风格 | 结论 |
|---|---|---|---|---|
| 1 | [MaterialDesignInXamlToolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | 16,271 | Google Material | 观感偏 Google 系，取其**倒计时圆环**元素融入 |
| 2 | [MahApps.Metro](https://github.com/MahApps/MahApps.Metro) | 9,833 | Metro 现代风 | 未采用 |
| 3 | **[WPF-UI](https://github.com/lepoco/wpfui)** | **9,685** | **Windows 11 Fluent** | **✅ 采用**：系统工具类最契合，Mica 背板 + Fluent 控件 |
| 4 | [HandyControl](https://github.com/HandyOrg/HandyControl) | 7,206 | 国产全能控件库 | 未采用 |
| 5 | [ModernWpf](https://github.com/Kinnara/ModernWpf) | 4,963 | Fluent（旧） | 未采用 |

设计要点：FluentWindow 无边框圆角窗口 + Mica 背板（Win11，Win10 自动回落纯深色）、Fluent 分段选择器、Fluent 滑块/数字框/开关、Material 风倒计时圆环、深色调色板（强调色 #60CDFF、警示色 #DC2626）、Segoe UI Variable 字体、亚秒级微动效（缩放淡入 180ms）。

## 功能（对照原版）

- 当前时间大时钟 + 日期显示
- 两种定时模式：**固定时间定时 / 倒计时定时**（小时、分钟支持数字框 + 滑块双向调节）
- 执行动作：**关机 / 重启 / 注销 / 休眠 / 锁定**
- 定时到达前弹窗提醒「关机提醒：请注意保存文件！系统将在 N 秒后关闭」，倒计时结束自动执行
- 弹窗三选项：**执 行 / 延迟 10 分钟 / 取 消**
- 关机命令未生效时，等待 30 秒后自动转**强制关机**（复刻原版逻辑）
- 托盘图标：最小化到托盘、气泡提示「已设置在 XX点XX分XX秒关机，若要更改，请在此图标上单击右键」、右键菜单管理
- **开机自启动**（写入 HKCU `Software\Microsoft\Windows\CurrentVersion\Run`）
- `config.ini` 记忆上次的设置（exe 不可写时自动回落到 %APPDATA%）
- 保留原版趣味文案：「亲，这个是火星文嘛？看不懂啊」

## 使用

直接双击 `dist\定时关机助手.exe`（**需要 .NET 10 桌面运行时**；开发机装有 .NET 10 SDK 即可运行）。

| 启动方式 | 说明 |
|---|---|
| `定时关机助手.exe` | 正常模式，定时到达后真实执行 |
| `定时关机助手.exe --simulate` | **模拟模式**：所有动作只写入 `simulate.log`，绝不真正关机（安全预览用） |
| `SelfTest --selftest` | 无界面逻辑自检（27 项断言，headless，无任何系统副作用） |
| `SelfTest --uitest` | 无窗口 UI 冒烟测试（不显示窗体、不执行真实动作） |

## 构建

需要 .NET SDK（项目使用 `dotnet` 构建，UI 库为 NuGet 包 WPF-UI 4.3.0）：

```
build.cmd
  → dist\定时关机助手.exe   主程序（win-x64 框架依赖单文件）
  → tests\SelfTest          自检运行器（--selftest / --uitest）
```

## 项目结构

```
src/YunTimerClone.Core/    纯逻辑库：定时引擎、任务校验、执行器抽象（真实/模拟）、INI 配置
src/YunTimerClone/         WPF 应用：FluentWindow 主窗体、确认弹窗、托盘
tests/SelfTest/            无界面自检运行器（逻辑 27 项 + UI 冒烟 3 项）
dist/                      发布产物
```

## 与原版的关系

- 功能与交互向 YunTimer（定时关机助手）v2.0.1.9 致敬，文案与流程尽量还原
- 代码为本仓库作者原创实现；界面基于开源库 WPF-UI（MIT）
- 与 www.yunguanji.com 官方无关，非官方产品

## License

[MIT](LICENSE)
