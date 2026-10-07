// 自检运行器（console）：
//   SelfTest --selftest  无界面逻辑自检（headless，无任何系统副作用）
//   SelfTest --uitest    无窗口 UI 冒烟测试（不显示窗体、模拟执行）
// 真实关机命令永远不会在自检中被调用（执行器一律为模拟实现）。

using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using YunTimerClone;
using YunTimerClone.Core;

namespace SelfTest
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool selftest = false, uitest = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--selftest" || s == "/selftest") selftest = true;
                if (s == "--uitest" || s == "/uitest") uitest = true;
            }
            if (selftest) return SelfTestSuite.Run();
            if (uitest) return UiTestSuite.Run();
            Console.WriteLine("用法: SelfTest --selftest | --uitest");
            return 2;
        }
    }

    // ---------- 无界面逻辑自检（headless，无任何系统副作用） ----------
    public static class SelfTestSuite
    {
        private static int _failed;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) Console.WriteLine("PASS  " + name);
            else { _failed++; Console.WriteLine("FAIL  " + name + (detail != null ? ("  -- " + detail) : "")); }
        }

        public static int Run()
        {
            Console.WriteLine("===== 定时关机助手 逻辑自检（模拟状态，不执行任何系统动作） =====");

            // 1. 执行命令映射（纯函数，不执行）
            RealExecutor.GetCommand(PowerAction.Shutdown, out string exe, out string args);
            Check("cmd.Shutdown", exe == "shutdown" && args == "-s -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Reboot, out exe, out args);
            Check("cmd.Reboot", exe == "shutdown" && args == "-r -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Logoff, out exe, out args);
            Check("cmd.Logoff", exe == "shutdown" && args == "-l", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Hibernate, out exe, out args);
            Check("cmd.Hibernate", exe == "rundll32.exe" && args.Contains("SetSuspendState"), exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Lock, out exe, out args);
            Check("cmd.Lock", exe == "rundll32.exe" && args.Contains("LockWorkStation"), exe + " " + args);

            // 2. 固定时间校验
            DateTime now = new DateTime(2026, 10, 8, 10, 0, 0);
            Check("validate.Fixed.ok", TaskValidator.ValidateFixed(10, 5, now) == ValidateResult.Ok, null);
            Check("validate.Fixed.past", TaskValidator.ValidateFixed(9, 0, now) == ValidateResult.TimeInPast, null);
            Check("validate.Fixed.equalNow", TaskValidator.ValidateFixed(10, 0, now) == ValidateResult.TimeInPast, null);
            Check("validate.Fixed.badHour", TaskValidator.ValidateFixed(24, 0, now) == ValidateResult.BadInput, null);
            Check("validate.Fixed.badMin", TaskValidator.ValidateFixed(10, 60, now) == ValidateResult.BadInput, null);

            // 3. 倒计时校验
            Check("validate.Count.ok", TaskValidator.ValidateCount(0, 1) == ValidateResult.Ok, null);
            Check("validate.Count.zero", TaskValidator.ValidateCount(0, 0) == ValidateResult.BadInput, null);
            Check("validate.Count.bad", TaskValidator.ValidateCount(-1, 0) == ValidateResult.BadInput, null);

            // 4. 固定任务跨午夜计算
            TimerTask t1 = new TimerTask { IsCountdown = false, Hour = 10, Minute = 5, CreatedAt = now };
            Check("nextFire.sameDay", t1.NextFire(now) == new DateTime(2026, 10, 8, 10, 5, 0), t1.NextFire(now).ToString());
            DateTime late = new DateTime(2026, 10, 8, 23, 0, 0);
            TimerTask t2 = new TimerTask { IsCountdown = false, Hour = 0, Minute = 30, CreatedAt = late };
            Check("nextFire.tomorrow", t2.NextFire(late) == new DateTime(2026, 10, 9, 0, 30, 0), t2.NextFire(late).ToString());

            // 5. 倒计时任务计算
            TimerTask t3 = new TimerTask { IsCountdown = true, Hour = 0, Minute = 90, CreatedAt = now };
            Check("nextFire.countdown90min", t3.NextFire(now) == now.AddMinutes(90), t3.NextFire(now).ToString());

            // 6. 引擎：未到点不触发、到点触发且只触发一次
            DateTime fake = now;
            TimerEngine eng = new TimerEngine(() => fake);
            int fired = 0;
            eng.FireRequested += (s, e) => fired++;
            TimerTask t4 = new TimerTask { IsCountdown = true, Hour = 0, Minute = 2, Action = PowerAction.Shutdown, CreatedAt = fake };
            eng.Arm(t4);
            for (int i = 0; i < 60; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.notFiredEarly", fired == 0 && eng.Armed, "fired=" + fired);
            for (int i = 0; i < 70; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.firedOnce", fired == 1 && !eng.Armed, "fired=" + fired);
            for (int i = 0; i < 5; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.noRefire", fired == 1, "fired=" + fired);

            // 7. 模拟执行器：记录动作、写日志，绝不真实执行
            string logPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_simulate.log");
            try { File.Delete(logPath); } catch { }
            SimulatedExecutor sim = new SimulatedExecutor(logPath);
            sim.Execute(PowerAction.Shutdown, "自检", out string err1);
            sim.Execute(PowerAction.Reboot, "自检2", out string err2);
            Check("sim.recorded", sim.Executed.Count == 2, sim.Executed.Count.ToString());
            Check("sim.noError", err1 == null && err2 == null, null);
            Check("sim.log", File.Exists(logPath) && File.ReadAllText(logPath).Contains("关机"), logPath);

            // 8. 延迟/取消决策 → 引擎状态（模拟主窗体行为）
            TimerEngine eng2 = new TimerEngine(() => DateTime.Now);
            eng2.Arm(new TimerTask { IsCountdown = true, Hour = 0, Minute = 1, Action = PowerAction.Shutdown, CreatedAt = DateTime.Now });
            // "延迟 10 分钟" = 重新武装一个 10 分钟倒计时
            eng2.Arm(new TimerTask { IsCountdown = true, Hour = 0, Minute = 10, Action = PowerAction.Shutdown, CreatedAt = DateTime.Now });
            TimeSpan rem = eng2.Remaining();
            Check("decision.delayRearms", eng2.Armed && rem.TotalMinutes > 9 && rem.TotalMinutes <= 10, rem.ToString());
            eng2.Disarm();
            Check("decision.cancelDisarms", !eng2.Armed, null);

            // 9. 配置读写往返
            string cfgPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_config.ini");
            try { File.Delete(cfgPath); } catch { }
            AppConfig c1 = new AppConfig { Action = PowerAction.Reboot, WarnSeconds = 120, Mode = 1, Hour = 5, Minute = 45 };
            c1.Save(cfgPath);
            AppConfig c2 = AppConfig.Load(cfgPath);
            Check("config.roundtrip", c2.Action == PowerAction.Reboot && c2.WarnSeconds == 120 && c2.Mode == 1 && c2.Hour == 5 && c2.Minute == 45,
                c2.Action + "/" + c2.WarnSeconds + "/" + c2.Mode + "/" + c2.Hour + ":" + c2.Minute);
            Check("config.missing", AppConfig.Load(Path.Combine(Path.GetTempPath(), "no_such_file.ini")).WarnSeconds == 60, null);

            // 10. 损坏配置不崩溃
            File.WriteAllText(cfgPath, "garbage [[ == bad", System.Text.Encoding.UTF8);
            AppConfig c3 = AppConfig.Load(cfgPath);
            Check("config.corrupt", c3.WarnSeconds == 60 && c3.Action == PowerAction.Shutdown, null);

            Console.WriteLine("===== 完成：失败 " + _failed + " 项 =====");
            return _failed == 0 ? 0 : 1;
        }
    }

    // ---------- 无窗口 UI 冒烟测试（不显示窗体、模拟执行） ----------
    public static class UiTestSuite
    {
        public static int Run()
        {
            Console.WriteLine("===== 定时关机助手 UI 冒烟测试（不显示窗口、模拟执行） =====");
            int failed = 0;

            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None);

            MainWindow.UiTestMode = true;
            try
            {
                AppConfig cfg = new AppConfig();
                SimulatedExecutor sim = new SimulatedExecutor(Path.Combine(Path.GetTempPath(), "yuntimer_uitest_simulate.log"));

                // 主窗体：强制创建句柄（不显示），验证控件解析
                MainWindow win = new MainWindow(cfg, sim, true);
                new WindowInteropHelper(win).EnsureHandle();
                bool mainOk = win.FindName("ClockText") != null
                    && win.FindName("NumHour") != null
                    && win.FindName("CmbAction") != null
                    && win.FindName("BtnOK") != null;
                Console.WriteLine((mainOk ? "PASS  " : "FAIL  ") + "main.controls");
                if (!mainOk) failed++;
                win.Close();

                // 确认弹窗：倒计时归零 → 决策为 Executed（直接驱动 TickOnce，不依赖消息循环）
                ConfirmDialog cf = new ConfirmDialog(PowerAction.Shutdown, 5, true);
                new WindowInteropHelper(cf).EnsureHandle();
                for (int i = 0; i < 10; i++) cf.TickOnce();
                bool cfOk = cf.Decision == Decision.Executed;
                Console.WriteLine((cfOk ? "PASS  " : "FAIL  ") + "confirm.countdownToExecute  (decision=" + cf.Decision + ")");
                if (!cfOk) failed++;
                cf.Close();

                // 确认弹窗：创建/销毁
                ConfirmDialog cf2 = new ConfirmDialog(PowerAction.Lock, 60, false);
                new WindowInteropHelper(cf2).EnsureHandle();
                cf2.Close();
                Console.WriteLine("PASS  confirm.createDispose");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL  uiexception  -- " + ex.GetType().Name + ": " + ex.Message);
            }

            MainWindow.UiTestMode = false;
            Console.WriteLine("===== 完成：失败 " + failed + " 项 =====");
            return failed == 0 ? 0 : 1;
        }
    }
}
