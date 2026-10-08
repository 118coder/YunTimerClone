# AI 交接文档（HANDOFF）

> 本文档写给**下一个接手本项目的 AI/开发者**。目标：5 分钟内掌握项目全貌、跑通构建测试、避开已知坑。
> 最后更新：2026-10-08（v0.1 已发布）

## 1. 项目一句话

「定时关机助手」—— Windows 桌面定时关机/重启/注销/休眠/锁定小工具，功能复刻自 YunTimer v2.0.1.9（www.yunguanji.com），代码 100% 原创。**当前版本 0.1，已发布到 GitHub Releases**。

- 仓库：https://github.com/118coder/YunTimerClone
- 发行页：https://github.com/118coder/YunTimerClone/releases（tag: v0.1）
- 形态：纯 Win32 API + GDI 自绘的**原生 C 程序**，单文件 `src/yuntimer.c`，exe 约 70KB，零依赖，双击秒开（实测 62~208ms）

## 2. 项目演进史（为什么是现在这个样子）

1. **v1 WinForms (.NET)**：首个复刻版，能用但界面老旧
2. **v2 WPF + WPF-UI 库**：界面现代化，但空白窗口 bug + 依赖 .NET 10 运行时 + 6.8MB → 用户否决（"背离小巧精简初衷"）
3. **v3 纯手写 Fluent WPF（net48）**：113KB 零依赖，但启动 3.2s（XamlReader 运行时解析 1.4s）→ 用户嫌慢
4. **v3.5 WPF 优化版**：纯 C# 建树 + 外壳先行，0.7s 出窗 → 用户仍要"原版级秒开"
5. **v4 原生 C（当前）**：与原版同技术形态，62ms 秒开 ✅。版本号改为 **0.1**（与原版致敬版本号 2.0.1.9 解耦，表明这是独立新项目）

> 教训：用户极度看重"小巧精简 + 双击秒开 + 和原版一样"，任何增加运行时依赖的方案都会被否决。界面风格偏好：深色 Fluent 外观 + 原版的极简结构（时间盒/单滑块/执行按钮）。

## 3. 仓库结构

```
YunTimerClone/
├── src/yuntimer.c      全部源码（核心逻辑 + 自绘 UI + 托盘 + 入口，~1300 行 C）
├── src/app.rc          资源脚本（图标 + VERSIONINFO，版本号在这里改）
├── src/app.ico         多尺寸图标（16/24/32/48/256，由 tools/make_icon.py 生成）
├── tools/make_icon.py  图标生成器（纯标准库，SDF 抗锯齿，可随时重跑）
├── build.cmd           一键构建（gcc + windres）
├── dist/               构建产物（有意提交进仓库，方便用户直接下载）
│   ├── 定时关机助手.exe   主程序
│   ├── selftest.exe       自检运行器（--selftest / --uitest）
│   └── 使用说明.txt        大众用户说明（UTF-8 BOM）
├── README.md           面向大众用户（下载/使用/FAQ）
├── HANDOFF.md          本文档
└── LICENSE / .gitignore
```

## 4. 常用命令

```cmd
build.cmd                              一键构建（产物进 dist\）
dist\定时关机助手.exe                  正常运行
dist\定时关机助手.exe --simulate       模拟模式（动作只写 simulate.log，绝不真关机）
dist\selftest.exe --selftest           27 项逻辑自检（headless，无系统副作用）
dist\selftest.exe --uitest             3 项 UI 冒烟测试（不显示窗口）
python tools\make_icon.py              重新生成图标（改图标设计后）
taskkill /F /IM 定时关机助手.exe       重新构建前先杀进程（exe 被占用会导致链接失败）
```

发布流程见 §7。

## 5. 架构速览（src/yuntimer.c 单文件分区）

| 分区 | 内容 | 要点 |
|---|---|---|
| 核心逻辑 | `PowerAction`/`get_command`/`AppConfig`(INI)/`Task`/`Engine`/校验器 | 纯函数可测；`get_command` 只生成命令行不执行 |
| 时间 | `st_to_100ns`/`st_add_seconds`/`st_cmp` | FILETIME 算术，勿手写日期进位 |
| 引擎 | `engine_arm/disarm/tick` | **arm 时固定 fireAt**（修复过"每次重算→固定任务永不触发"的缺陷，回归项 `engine.fixedFiresOnce`，勿改回） |
| 执行器 | `execute_action` | `g_simulate` 分流：真实走 `CreateProcessW("shutdown …")` + 30s 后强制线程；模拟写 `simulate.log` |
| UI 布局 | 文件顶部 `RC_*` 常量矩形 | 客户区 **430x560**，所有控件是坐标矩形 + `hit_test()` 命中 |
| 绘制 | `paint_main` + `draw_round/draw_text_r/draw_slider/draw_toggle` | 双缓冲；配色宏在文件头 `COL_*` |
| 输入 | 时间盒用**真 EDIT 控件**（IDC_EDIT_H/M） | ES_NUMBER、失焦补零校验；父窗体画容器和冒号；`WM_CTLCOLOREDIT` 深色；滑块控制**焦点段** |
| 托盘 | `tray_add/balloon/remove` | 图标来自 exe 内嵌资源 `LoadImageW(MAKEINTRESOURCEW(1))` |
| 确认弹窗 | `dlg_proc`/`show_confirm` | WS_POPUP 自绘 + 手写模态消息循环，返回决策 0 执行/1 延迟 10 分钟/2 取消 |

## 6. 测试纪律（务必遵守）

- **所有测试只用模拟执行器**，绝不允许测试路径触达真实 `shutdown` 命令
- 逻辑改动后跑 `selftest --selftest`（27 项）+ `selftest --uitest`（3 项）；UI 测试不显示窗口
- GUI 手工验证用 `--simulate` 模式
- 测试断言里新增逻辑请同步补自检项

## 7. 发布流程（已跑通的完整路径）

1. 改版本号：**只改 `src/app.rc`**（FILEVERSION/字符串）+ 界面内链接文字（yuntimer.c 搜 `项目主页 v`）+ `dist/使用说明.txt` 头部
2. `build.cmd` 重建 → 跑两级自检
3. 打 zip：`PowerShell Compress-Archive dist\定时关机助手.exe, dist\使用说明.txt -DestinationPath dist\ShutdownAssistant-vX.Y.zip`
4. `git push`（网络见 §8）→ GitHub API 创建 Release 并上传资产（脚本思路见 §8）
5. 资产命名**必须 ASCII**（见 §8），推荐 `ShutdownAssistant-vX.Y.zip` + `ShutdownAssistant.exe`

## 8. 已知的坑（全部踩过，别再踩）

### 网络
- **github.com:443 会间歇性被墙**（api.github.com / uploads.github.com 通常仍可达）。push 失败时的替代法：用 **Git Data API 在服务端重建提交**——读 `git cat-file commit HEAD` 的精确元数据（tree/parent/author/committer/日期），逐 blob 上传（base64），建 tree（注意 `-c core.quotepath=off` 否则中文路径变转义串），建 commit（元数据一致 → SHA 与本地完全一致），PATCH ref。成功后本地与远端零分叉。
- **Release 资产名含中文会被 GitHub API 吞掉**（"定时关机助手.exe"→"default.exe"），资产名一律 ASCII；zip 内部文件名可以是中文。

### 编码
- `build.cmd` 含中文 → 必须存 **GBK + CRLF**（cmd 按 ANSI 解析批处理；UTF-8/LF 都会炸）
- 含中文的 `.ps1` → 必须 **UTF-8 带 BOM**（否则 PowerShell 5.1 按 GBK 误读，路径找不到）
- `dist/使用说明.txt` → UTF-8 带 BOM（老记事本兼容）
- C 源码 UTF-8 即可（gcc 宽字面量默认转 UTF-16）

### 构建/运行
- 链接报 `Permission denied` = **exe 还在运行**，先 taskkill
- GUI 和 TEST 两个目标**都要链接系统库**（-lgdi32 -lshell32 -ladvapi32 -ldwmapi -luser32），TEST 漏链接会 undefined reference
- windres 资源改动无需手动清理，`src/app_res.o` 每次构建重生成（已 gitignore `*.o`）
- 图标在资源管理器不刷新是壳缓存，文件本体已变

### 设计决策（勿轻易回退）
- **不用任何 .NET/WPF/第三方 UI 库**——用户三次否决托管方案，"秒开"是硬需求
- dist/ 提交进仓库是有意的（用户直接从仓库下载）
- 深色配色（`COL_*` 宏）与原版布局（时间盒/单滑块/执 行）是两条并行要求：外观现代、结构照抄原版

## 9. 建议的下一步（用户提过或顺理成章）

- **每日重复定时**（用户在 FAQ 问过"每天 23:30"，当前到点执行一次后需重设）——引擎已支持固定 fireAt，加 repeat 标记即可
- README 加运行截图
- 代码签名（消除杀毒误报）或提交 Defender 误报申诉
- 倒计时模式可考虑支持秒级

## 10. 环境备忘

- 编译器：`C:\mingw64\mingw64\bin\gcc.exe`（mingw-w64 14.2.0）+ windres 同目录
- 也可用 llvm-mingw（winget 装的 clang），未验证
- GitHub 账号：118coder（凭据在 Windows 凭据管理器，`git credential fill` 可取，勿打印/外泄）
- 本仓库 dist/ 的 exe 与 Releases 资产需保持同步更新
