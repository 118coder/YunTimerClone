// 主窗体：大时钟、模式分段选择、定时设置、托盘、开机自启动
// 逻辑与原 YunTimer 一致；执行统一走 IActionExecutor（真实/模拟）

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YunTimerClone.Core;
using WinForms = System.Windows.Forms;

namespace YunTimerClone
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        public static bool UiTestMode = false;

        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "定时关机助手";
        public const string RepoUrl = "https://github.com/118coder/YunTimerClone";

        private readonly AppConfig _cfg;
        private readonly IActionExecutor _executor;
        private readonly bool _simulate;
        private readonly TimerEngine _engine;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _engineTimer;
        private readonly WinForms.NotifyIcon _tray;
        private bool _updatingUi;
        private bool _forceExit;

        public MainWindow(AppConfig cfg, IActionExecutor executor, bool simulate)
        {
            InitializeComponent();

            _cfg = cfg;
            _executor = executor;
            _simulate = simulate;
            _engine = new TimerEngine(() => DateTime.Now);
            _engine.FireRequested += OnFire;

            Title = simulate ? "定时关机助手（模拟模式）" : "定时关机助手";
            TitleBar.Title = Title;
            try
            {
                using (var ico = TrayIconFactory.Create())
                {
                    Icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { }

            CmbAction.ItemsSource = new[] { "关机", "重启", "注销", "休眠", "锁定" };

            LoadConfigToUi();

            _tray = new WinForms.NotifyIcon();
            _tray.Icon = TrayIconFactory.Create();
            _tray.Text = "定时关机助手";
            _tray.Visible = !UiTestMode;
            _tray.DoubleClick += (s, e) => ShowMain();
            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowMain());
            menu.Items.Add("取消定时并退出", null, (s, e) => { _engine.Disarm(); ExitApp(); });
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            _tray.ContextMenuStrip = menu;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += (s, e) => { UpdateClockLabels(); UpdateStatusLabels(); };
            _uiTimer.Start();

            _engineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _engineTimer.Tick += (s, e) => _engine.Tick();
            _engineTimer.Start();
        }

        private void LoadConfigToUi()
        {
            _updatingUi = true;
            RbFixed.Checked += (s, e) => SetMode(0);
            RbCount.Checked += (s, e) => SetMode(1);
            RbFixed.IsChecked = _cfg.Mode == 0;
            RbCount.IsChecked = _cfg.Mode == 1;

            CmbAction.SelectedIndex = Clamp((int)_cfg.Action, 0, 4);
            NumHour.Value = Clamp(_cfg.Hour, (int)NumHour.Minimum, (int)NumHour.Maximum);
            NumMin.Value = Clamp(_cfg.Minute, (int)NumMin.Minimum, (int)NumMin.Maximum);

            // 数值框与滑块双向同步
            TrkHour.ValueChanged += (s, e) =>
            {
                if (_updatingUi) return;
                _updatingUi = true;
                NumHour.Value = TrkHour.Value;
                _updatingUi = false;
            };
            NumHour.TextChanged += (s, e) =>
            {
                if (_updatingUi) return;
                _updatingUi = true;
                TrkHour.Value = Math.Min(NumHour.Value.GetValueOrDefault(), TrkHour.Maximum);
                _updatingUi = false;
            };
            TrkMin.ValueChanged += (s, e) =>
            {
                if (_updatingUi) return;
                _updatingUi = true;
                NumMin.Value = TrkMin.Value;
                _updatingUi = false;
            };
            NumMin.TextChanged += (s, e) =>
            {
                if (_updatingUi) return;
                _updatingUi = true;
                TrkMin.Value = Math.Min(NumMin.Value.GetValueOrDefault(), TrkMin.Maximum);
                _updatingUi = false;
            };

            ChkAuto.Checked += OnAutoStartChanged;
            ChkAuto.Unchecked += OnAutoStartChanged;
            ChkAuto.IsChecked = IsAutoStartSet() || _cfg.AutoStart;

            LinkHome.RequestNavigate += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); } catch { }
            };

            StateChanged += OnStateChanged;
            Closing += OnClosing;

            _updatingUi = false;
            UpdateClockLabels();
            UpdateStatusLabels();
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void SetMode(int mode)
        {
            if (_updatingUi) return;
            _cfg.Mode = mode;
            double max = mode == 0 ? 23 : 99;
            NumHour.Maximum = max;
            TrkHour.Maximum = max;
            if (NumHour.Value.GetValueOrDefault() > max) NumHour.Value = max;
            if (TrkHour.Value > max) TrkHour.Value = max;
        }

        private PowerAction SelectedAction()
        {
            int i = CmbAction.SelectedIndex;
            if (i < 0) i = 0;
            return (PowerAction)i;
        }

        private string ArmedDescription()
        {
            if (_engine.Task == null) return "";
            DateTime fire = _engine.NextFireTime();
            return string.Format("{0}点{1}分{2}秒{3}", fire.Hour, fire.Minute, fire.Second, ActionInfo.Name(_engine.Task.Action));
        }

        private void UpdateClockLabels()
        {
            DateTime now = DateTime.Now;
            string[] weeks = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
            ClockText.Text = string.Format("{0:00}:{1:00}:{2:00}", now.Hour, now.Minute, now.Second);
            DateText.Text = string.Format("{0}年{1}月{2}日 {3}", now.Year, now.Month, now.Day, weeks[(int)now.DayOfWeek]);
        }

        private void UpdateStatusLabels()
        {
            if (_engine.Armed)
            {
                StatusText.Text = "将于 " + ArmedDescription();
                RemainText.Text = "剩余 " + FormatSpan(_engine.Remaining());
                BtnStop.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "未设置定时任务";
                RemainText.Text = "";
                BtnStop.IsEnabled = false;
            }
        }

        private static string FormatSpan(TimeSpan t)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private void Balloon(string title, string text)
        {
            try { _tray.ShowBalloonTip(2000, title, text, WinForms.ToolTipIcon.None); }
            catch { }
        }

        // ---------- 事件 ----------
        private void OnOK(object sender, RoutedEventArgs e)
        {
            int hour = (int)NumHour.Value.GetValueOrDefault();
            int minute = (int)NumMin.Value.GetValueOrDefault();
            PowerAction action = SelectedAction();

            ValidateResult vr = _cfg.Mode == 0
                ? TaskValidator.ValidateFixed(hour, minute, DateTime.Now)
                : TaskValidator.ValidateCount(hour, minute);

            if (vr == ValidateResult.BadInput)
            {
                MessageBox.Show("亲，这个是火星文嘛？看不懂啊", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (vr == ValidateResult.TimeInPast)
            {
                DateTime now = DateTime.Now;
                MessageBox.Show(string.Format("亲，现在已经{0}点{1}分了，想现在就关机的话，请点右下角“立即执行”！",
                    now.Hour, now.Minute), "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TimerTask task = new TimerTask
            {
                IsCountdown = _cfg.Mode == 1,
                Hour = hour,
                Minute = minute,
                Action = action,
                CreatedAt = DateTime.Now
            };
            _engine.Arm(task);

            _cfg.Action = action;
            _cfg.Hour = hour;
            _cfg.Minute = minute;
            _cfg.Save(AppConfig.DefaultPath());

            Balloon("定时关机助手", "已设置在 " + ArmedDescription() + "，若要更改，请在此图标上单击右键。");
            UpdateStatusLabels();
            if (_cfg.Mode == 1) WindowState = WindowState.Minimized;
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            _engine.Disarm();
            UpdateStatusLabels();
            Balloon("定时关机助手", "已取消定时任务。");
        }

        private void OnRunNow(object sender, RoutedEventArgs e)
        {
            PowerAction action = SelectedAction();
            string verb = ActionInfo.Name(action);
            if (MessageBox.Show("确定要立即" + verb + "吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            DoExecute(action, "手动立即执行");
            if (_simulate)
                MessageBox.Show("【模拟模式】" + verb + " 已写入 simulate.log（未真正执行）", "模拟模式",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DoExecute(PowerAction action, string reason)
        {
            if (!_executor.Execute(action, reason, out string error))
            {
                MessageBox.Show(error, "关机失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnFire(object sender, EventArgs e)
        {
            PowerAction action = _engine.Task != null ? _engine.Task.Action : SelectedAction();

            if (UiTestMode)
            {
                _executor.Execute(action, "定时到达(UI测试)", out _);
                StatusText.Text = "已执行：" + ActionInfo.Name(action);
                return;
            }

            Decision dec;
            ConfirmDialog f = new ConfirmDialog(action, _cfg.WarnSeconds, _simulate);
            f.Owner = this;
            try
            {
                f.ShowDialog();
                dec = f.Decision;
            }
            finally
            {
                f.Close();
            }

            if (dec == Decision.Executed)
            {
                DoExecute(action, "定时到达");
                StatusText.Text = "已执行：" + ActionInfo.Name(action);
                Balloon("定时关机助手", "正在" + ActionInfo.Name(action) + "！");
            }
            else if (dec == Decision.Delayed)
            {
                TimerTask task = new TimerTask
                {
                    IsCountdown = true,
                    Hour = 0,
                    Minute = 10,
                    Action = action,
                    CreatedAt = DateTime.Now
                };
                _engine.Arm(task);
                Balloon("定时关机助手", "已延迟 10 分钟执行。");
                UpdateStatusLabels();
            }
            else
            {
                Balloon("定时关机助手", "已取消定时任务。");
                UpdateStatusLabels();
            }
        }

        private void ShowMain()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _forceExit = true;
            _tray.Visible = false;
            Application.Current.Shutdown();
        }

        private void OnAutoStartChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingUi) return;
            bool on = ChkAuto.IsChecked == true;
            SetAutoStart(on);
            _cfg.AutoStart = on;
            _cfg.Save(AppConfig.DefaultPath());
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _engine.Armed)
            {
                Hide();
                ShowInTaskbar = false;
                Balloon("定时关机助手", "已设置在 " + ArmedDescription() + "，若要更改，请在此图标上单击右键。");
                WindowState = WindowState.Normal;
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceExit || !_engine.Armed) return;
            if (MessageBox.Show("已设置定时关机任务，确定取消任务并退出应用程序？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _engine.Disarm();
                ExitApp();
                return;
            }
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            Balloon("定时关机助手", "定时任务仍在运行，请在此图标上单击右键管理。");
        }

        // ---------- 注册表自启动 ----------
        public static bool IsAutoStartSet()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return k != null && k.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (on) k.SetValue(RunValueName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _uiTimer?.Stop();
            _engineTimer?.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
    }
}
