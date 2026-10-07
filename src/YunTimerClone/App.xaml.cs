// 入口：解析 --simulate 参数、应用 Fluent 深色主题（WPF-UI）、启动主窗体

using System;
using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;
using YunTimerClone.Core;

namespace YunTimerClone
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool simulate = false;
            foreach (string a in e.Args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--simulate" || s == "/simulate" || s == "-s") simulate = true;
            }

            ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica);

            AppConfig cfg = AppConfig.Load(AppConfig.DefaultPath());
            IActionExecutor executor = simulate
                ? (IActionExecutor)new SimulatedExecutor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "simulate.log"))
                : new RealExecutor(cfg.ForceWaitSeconds);

            MainWindow win = new MainWindow(cfg, executor, simulate);
            MainWindow = win;
            win.Show();
        }
    }
}
