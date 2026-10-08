# 定时关机助手（YunTimer 复刻版 · 零依赖 Fluent UI）

一个 Windows 桌面「定时关机助手」，功能与交互复刻自 [www.yunguanji.com](https://www.yunguanji.com) 的 **YunTimer v2.0.1.9「定时关机助手」**，界面为手写 Fluent 设计语言（Windows 11 观感）。代码为 100% 原创实现（未复制、未反编译原程序的任何代码与资源）。

**零依赖单 exe（约 110KB，比原版 YunTimer 的 2MB 还小）**：目标 .NET Framework 4.8（Windows 10/11 系统自带），用系统自带 csc.exe 编译，不安装任何运行时、不引用任何第三方库。

## 关于界面设计

曾采用 GitHub 高星 WPF UI 库 [WPF-UI](https://github.com/lepoco/wpfui)（9.7k★，Fluent 风格）做界面，但其 4.x 强制要求 .NET 6+ 运行时（打包 6.8MB 且需另装运行环境），背离本工具「小巧精简、开箱即用」的定位，故改为**手写 Fluent 设计语言、零第三方库**实现，观感保留：

- 深色圆角卡片 + 1px 微边框 + Windows 11 原生圆角窗口
- **DWM 深色标题栏 + 标题栏/边框着色**（`DwmSetWindowAttribute`，Win11 生效，Win10 自动忽略）
- Fluent 分段选择器（固定时间定时 / 倒计时定时）
- Fluent 滑块、深色数字框、下拉框、开关（ToggleSwitch 样式的开机自启动）
- Material Design 风格**倒计时圆环**（定时到达弹窗，圆弧随秒数收缩）
- 调色板：强调色 #60CDFF、警示色 #DC2626、卡片 #FFFFFF12 系；Segoe UI 字体；弹窗缩放淡入微动效（180ms）

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

## 启动性能（点击秒开）

- **~0.7 秒窗口外壳出现**（深色圆角窗体 + 标题栏），内容随即填充，全程 ~1.8 秒完全就绪
- 无运行时 XAML 解析：全部界面由纯 C# 对象构建（Style/Template/布局均为代码构造），滑块为自绘轻量控件
- 托盘图标、定时器等非首屏资源在内容就位后延迟初始化
- csc `/optimize+` 编译
- 内置分阶段计时器：`selftest.exe --perftest` 可随时复测启动耗时

## 使用

直接双击 `dist\定时关机助手.exe`，无需安装任何东西。

| 启动方式 | 说明 |
|---|---|
| `定时关机助手.exe` | 正常模式，定时到达后真实执行 |
| `定时关机助手.exe --simulate` | **模拟模式**：所有动作只写入 `simulate.log`，绝不真正关机（安全预览用） |
| `selftest.exe --selftest` | 无界面逻辑自检（27 项断言，headless，无任何系统副作用） |
| `selftest.exe --uitest` | 无窗口 UI 冒烟测试（不显示窗体、不执行真实动作） |

## 构建

运行 `build.cmd` 即可，使用系统自带的 .NET Framework 4.x `csc.exe`（Windows 10/11 自带），无需安装任何第三方依赖：

```
build.cmd
  → dist\定时关机助手.exe   主程序（winexe，约 110KB，零依赖）
  → dist\selftest.exe       逻辑/UI 自检运行器
```

单源文件 `src\YunTimerClone.cs`：核心逻辑（定时引擎/校验/执行器抽象/INI 配置）与 Fluent 界面（运行时由 XamlReader 加载的手写 XAML）同文件组织，C# 5 语法兼容系统自带编译器。

## 与原版的关系

- 功能与交互向 YunTimer（定时关机助手）v2.0.1.9 致敬，文案与流程尽量还原
- 代码为本仓库作者原创实现，未使用任何第三方 UI 库
- 与 www.yunguanji.com 官方无关，非官方产品

## License

[MIT](LICENSE)
