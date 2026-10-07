# 定时关机助手（YunTimer 复刻版）

一个 Windows 桌面「定时关机助手」，功能与交互复刻自 [www.yunguanji.com](https://www.yunguanji.com) 的 **YunTimer v2.0.1.9「定时关机助手」**，代码为 100% 原创实现（未复制、未反编译原程序的任何代码与资源）。

纯 C# WinForms 实现，使用 Windows 自带的 .NET Framework 4.x 编译，**无需安装任何运行库，单 exe 绿色运行**。

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

直接双击 `dist\定时关机助手.exe`。

| 启动方式 | 说明 |
|---|---|
| `定时关机助手.exe` | 正常模式，定时到达后真实执行 |
| `定时关机助手.exe --simulate` | **模拟模式**：所有动作只写入 `simulate.log`，绝不真正关机（安全预览用） |
| `selftest.exe --selftest` | 无界面逻辑自检（26 项断言，headless，无任何系统副作用） |
| `selftest.exe --uitest` | 无窗口 UI 冒烟测试（不显示窗体、不执行真实动作） |

## 构建

运行 `build.cmd` 即可，使用系统自带的 .NET Framework 4.x `csc.exe`（Windows 10/11 自带），无需安装任何第三方依赖：

```
build.cmd
  → dist\定时关机助手.exe   主程序（winexe，双击无控制台）
  → dist\selftest.exe       逻辑/UI 自检运行器
```

## 与原版的关系

- 功能与交互向 YunTimer（定时关机助手）v2.0.1.9 致敬，文案与流程尽量还原
- 代码为本仓库作者原创实现
- 与 www.yunguanji.com 官方无关，非官方产品

## License

[MIT](LICENSE)
