// 定时关机助手（YunTimer 复刻版）
// 复刻自 www.yunguanji.com 的 YunTimer v2.0.1.9「定时关机助手」，原创实现，未复制任何原程序代码。
//
// 运行模式：
//   定时关机助手.exe            正常模式（定时到达后真实执行关机/重启/注销/休眠/锁定）
//   定时关机助手.exe --simulate 模拟模式（所有动作仅写入 simulate.log，绝不真正执行）
//   selftest.exe --selftest     无界面逻辑自检（headless，仅测试逻辑，无任何系统副作用）
//   selftest.exe --uitest       无窗口 UI 冒烟测试（不显示窗体、不执行真实动作）
//
// 构建见 build.cmd（使用系统自带的 .NET Framework 4.x csc.exe，无需任何第三方依赖）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("定时关机助手")]
[assembly: System.Reflection.AssemblyProduct("定时关机助手")]
[assembly: System.Reflection.AssemblyDescription("定时关机助手（YunTimer 复刻版）")]
[assembly: System.Reflection.AssemblyVersion("2.0.1.9")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.1.9")]

namespace YunTimerClone
{
    public enum PowerAction
    {
        Shutdown = 0,
        Reboot = 1,
        Logoff = 2,
        Hibernate = 3,
        Lock = 4
    }

    public static class ActionInfo
    {
        public static string Name(PowerAction a)
        {
            switch (a)
            {
                case PowerAction.Shutdown: return "关机";
                case PowerAction.Reboot: return "重启";
                case PowerAction.Logoff: return "注销";
                case PowerAction.Hibernate: return "休眠";
                case PowerAction.Lock: return "锁定";
            }
            return "关机";
        }
    }

    // ---------- 执行器抽象：真实执行与模拟执行的分界线 ----------
    public interface IActionExecutor
    {
        string ModeName { get; }
        void Execute(PowerAction action, string reason);
    }

    public class RealExecutor : IActionExecutor
    {
        private int _forceWaitSeconds;

        public RealExecutor(int forceWaitSeconds)
        {
            _forceWaitSeconds = forceWaitSeconds;
        }

        public string ModeName { get { return "真实"; } }

        // 纯函数：只生成命令行，不执行，便于测试断言
        public static void GetCommand(PowerAction action, out string exe, out string args)
        {
            switch (action)
            {
                case PowerAction.Shutdown: exe = "shutdown"; args = "-s -t 0"; return;
                case PowerAction.Reboot: exe = "shutdown"; args = "-r -t 0"; return;
                case PowerAction.Logoff: exe = "shutdown"; args = "-l"; return;
                case PowerAction.Hibernate: exe = "rundll32.exe"; args = "powrprof.dll,SetSuspendState 0,1,0"; return;
                case PowerAction.Lock: exe = "rundll32.exe"; args = "user32.dll,LockWorkStation"; return;
            }
            exe = "shutdown"; args = "-s -t 0";
        }

        public void Execute(PowerAction action, string reason)
        {
            string exe, args;
            GetCommand(action, out exe, out args);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("关机提醒：" + ActionInfo.Name(action) + "失败！\r\n" + ex.Message,
                    "关机失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 关机/重启后若系统仍未关闭（命令被拦截等原因），等待一段时间后强制执行
            if (action == PowerAction.Shutdown || action == PowerAction.Reboot)
            {
                Thread t = new Thread(delegate()
                {
                    Thread.Sleep(_forceWaitSeconds * 1000);
                    try
                    {
                        ProcessStartInfo psi2 = new ProcessStartInfo("shutdown", "-s -f -t 0");
                        psi2.CreateNoWindow = true;
                        psi2.UseShellExecute = false;
                        Process.Start(psi2);
                    }
                    catch
                    {
                        // 强制关机也失败时进程即将随系统状态而定，不再提示
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
        }
    }

    public class SimulatedExecutor : IActionExecutor
    {
        private string _logPath;

        public SimulatedExecutor(string logPath)
        {
            _logPath = logPath;
        }

        public string ModeName { get { return "模拟"; } }

        public List<string> Executed { get { return _executed; } }
        private List<string> _executed = new List<string>();

        public void Execute(PowerAction action, string reason)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] 【模拟模式】{1}（{2}）— 未真正执行",
                DateTime.Now, ActionInfo.Name(action), reason);
            _executed.Add(line);
            try
            {
                File.AppendAllText(_logPath, line + "\r\n", Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去也不影响模拟流程
            }
        }
    }

    // ---------- 配置 ----------
    public class AppConfig
    {
        public PowerAction Action = PowerAction.Shutdown;
        public int WarnSeconds = 60;       // 定时到达前弹窗倒计时秒数
        public int ForceWaitSeconds = 30;  // 关机命令后多久转强制关机
        public bool AutoStart = false;
        public int Mode = 0;               // 0=固定时间 1=倒计时
        public int Hour = 23;
        public int Minute = 30;

        public static AppConfig Load(string path)
        {
            AppConfig c = new AppConfig();
            try
            {
                if (!File.Exists(path)) return c;
                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (section == "Config")
                    {
                        if (key == "Action") c.Action = ParseAction(val, c.Action);
                        else if (key == "WarnSeconds") { int v; if (int.TryParse(val, out v) && v >= 10 && v <= 3600) c.WarnSeconds = v; }
                        else if (key == "ForceWaitSeconds") { int v; if (int.TryParse(val, out v) && v >= 5 && v <= 3600) c.ForceWaitSeconds = v; }
                        else if (key == "AutoStart") c.AutoStart = val == "1" || val.ToLower() == "true";
                        else if (key == "Mode") { int v; if (int.TryParse(val, out v)) c.Mode = (v == 1) ? 1 : 0; }
                    }
                    else if (section == "Task")
                    {
                        if (key == "Hour") { int v; if (int.TryParse(val, out v) && v >= 0) c.Hour = v; }
                        else if (key == "Minute") { int v; if (int.TryParse(val, out v) && v >= 0) c.Minute = v; }
                    }
                }
            }
            catch
            {
                // 配置损坏则使用默认值
            }
            return c;
        }

        public static PowerAction ParseAction(string val, PowerAction fallback)
        {
            for (int i = 0; i <= 4; i++)
            {
                if (val == ((PowerAction)i).ToString() || val == i.ToString()) return (PowerAction)i;
                if (val == ActionInfo.Name((PowerAction)i)) return (PowerAction)i;
            }
            return fallback;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Config]");
            sb.AppendLine("Action=" + Action);
            sb.AppendLine("WarnSeconds=" + WarnSeconds);
            sb.AppendLine("ForceWaitSeconds=" + ForceWaitSeconds);
            sb.AppendLine("AutoStart=" + (AutoStart ? "1" : "0"));
            sb.AppendLine("Mode=" + Mode);
            sb.AppendLine();
            sb.AppendLine("[Task]");
            sb.AppendLine("Hour=" + Hour);
            sb.AppendLine("Minute=" + Minute);
            try
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 目录只读时忽略
            }
        }
    }

    // ---------- 定时任务与引擎 ----------
    public class TimerTask
    {
        public bool IsCountdown;
        public int Hour;       // 固定模式：时(0-23)；倒计时模式：小时数(0-99)
        public int Minute;     // 固定模式：分(0-59)；倒计时模式：分钟数(0-59)
        public PowerAction Action;
        public DateTime CreatedAt;

        public DateTime NextFire(DateTime now)
        {
            if (IsCountdown) return CreatedAt.AddHours(Hour).AddMinutes(Minute);
            DateTime t = now.Date.AddHours(Hour).AddMinutes(Minute);
            if (t <= now) t = t.AddDays(1);
            return t;
        }
    }

    public enum ValidateResult { Ok = 0, BadInput = 1, TimeInPast = 2 }

    public static class TaskValidator
    {
        public static ValidateResult ValidateFixed(int hour, int minute, DateTime now)
        {
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return ValidateResult.BadInput;
            if (now.Date.AddHours(hour).AddMinutes(minute) <= now) return ValidateResult.TimeInPast;
            return ValidateResult.Ok;
        }

        public static ValidateResult ValidateCount(int hour, int minute)
        {
            if (hour < 0 || hour > 99 || minute < 0 || minute > 59) return ValidateResult.BadInput;
            if (hour == 0 && minute == 0) return ValidateResult.BadInput;
            return ValidateResult.Ok;
        }
    }

    public class TimerEngine
    {
        private Func<DateTime> _clock;

        public TimerEngine(Func<DateTime> clock)
        {
            _clock = clock;
        }

        public TimerTask Task;
        public bool Armed;

        public event EventHandler FireRequested;

        public void Arm(TimerTask task)
        {
            Task = task;
            Armed = true;
        }

        public void Disarm()
        {
            Armed = false;
            Task = null;
        }

        public DateTime NextFireTime()
        {
            if (Task == null) return DateTime.MinValue;
            return Task.NextFire(_clock());
        }

        public TimeSpan Remaining()
        {
            if (!Armed || Task == null) return TimeSpan.Zero;
            DateTime now = _clock();
            TimeSpan r = Task.NextFire(now) - now;
            if (r < TimeSpan.Zero) r = TimeSpan.Zero;
            return r;
        }

        // 每秒由 UI 定时器或测试代码调用
        public void Tick()
        {
            if (!Armed || Task == null) return;
            DateTime now = _clock();
            if (now >= Task.NextFire(now))
            {
                Armed = false;   // 触发一次即解除，避免重复弹窗
                EventHandler h = FireRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }
    }

    // ---------- 图标（运行时绘制，无外部资源文件） ----------
    public static class IconFactory
    {
        public static Icon Create()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(23, 27, 38)))
                        g.FillRectangle(bg, 0, 0, 32, 32);
                    using (Pen ring = new Pen(Color.FromArgb(79, 140, 255), 2.4f))
                        g.DrawEllipse(ring, 4, 4, 24, 24);
                    using (Pen hand = new Pen(Color.White, 2.4f))
                    {
                        hand.StartCap = LineCap.Round; hand.EndCap = LineCap.Round;
                        g.DrawLine(hand, 16, 16, 16, 8);   // 分针
                        g.DrawLine(hand, 16, 16, 22, 16);  // 时针
                    }
                    using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 92, 92)))
                        g.FillEllipse(dot, 14, 14, 4, 4);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // ---------- 主窗体 ----------
    public class MainForm : Form
    {
        public static bool UiTestMode = false;

        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "定时关机助手";
        public const string RepoUrl = "https://github.com/118coder/YunTimerClone";

        private AppConfig _cfg;
        private IActionExecutor _executor;
        private bool _simulate;

        private TimerEngine _engine;
        private System.Windows.Forms.Timer _uiTimer;
        private System.Windows.Forms.Timer _engineTimer;
        private bool _updatingUi;
        private bool _forceExit;

        private Label lblTime;
        private Label lblDate;
        private Button btnFixed;
        private Button btnCount;
        private Panel pnlMode;
        private Label lbHour;
        private NumericUpDown numHour;
        private TrackBar trkHour;
        private Label lbMin;
        private NumericUpDown numMin;
        private TrackBar trkMin;
        private Label lbAction;
        private ComboBox cmbAction;
        private Button btnOK;
        private Button btnStop;
        private Label lblStatus;
        private Label lblRemain;
        private Button btnRunNow;
        private CheckBox chkAutoStart;
        private LinkLabel linkHome;
        private NotifyIcon tray;

        protected override void Dispose(bool disposing)
        {
            if (disposing && tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            base.Dispose(disposing);
        }

        public MainForm(AppConfig cfg, IActionExecutor executor, bool simulate)
        {
            _cfg = cfg;
            _executor = executor;
            _simulate = simulate;

            _engine = new TimerEngine(delegate { return DateTime.Now; });

            BuildUi();
            LoadConfigToUi();
            SetupTimers();
        }

        private void BuildUi()
        {
            Color bg = Color.FromArgb(23, 27, 38);
            Color panel = Color.FromArgb(31, 36, 51);
            Color fg = Color.FromArgb(226, 232, 244);
            Color sub = Color.FromArgb(138, 147, 166);
            Color accent = Color.FromArgb(79, 140, 255);
            Color red = Color.FromArgb(255, 92, 92);
            Font uiFont = new Font("Microsoft YaHei UI", 9F);

            this.Text = _simulate ? "定时关机助手（模拟模式）" : "定时关机助手";
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = bg;
            this.ForeColor = fg;
            this.Font = uiFont;
            this.ClientSize = new Size(372, 500);
            this.Icon = IconFactory.Create();

            lblTime = new Label();
            lblTime.Bounds = new Rectangle(0, 16, 372, 66);
            lblTime.ForeColor = fg;
            lblTime.BackColor = bg;
            lblTime.TextAlign = ContentAlignment.MiddleCenter;
            lblTime.Font = new Font("Microsoft YaHei UI", 36F, FontStyle.Bold);

            lblDate = new Label();
            lblDate.Bounds = new Rectangle(0, 84, 372, 22);
            lblDate.ForeColor = sub;
            lblDate.BackColor = bg;
            lblDate.TextAlign = ContentAlignment.MiddleCenter;
            lblDate.Font = new Font("Microsoft YaHei UI", 9.5F);

            btnFixed = MakeModeButton("固定时间定时", 56, 116, accent, bg);
            btnCount = MakeModeButton("倒计时定时", 196, 116, sub, panel);

            pnlMode = new Panel();
            pnlMode.Bounds = new Rectangle(16, 160, 340, 152);
            pnlMode.BackColor = panel;

            lbHour = new Label();
            lbHour.Bounds = new Rectangle(16, 20, 40, 24);
            lbHour.Text = "小时";
            lbHour.ForeColor = sub;

            numHour = new NumericUpDown();
            numHour.Bounds = new Rectangle(66, 16, 60, 26);
            numHour.Minimum = 0; numHour.Maximum = 23;
            numHour.BackColor = bg; numHour.ForeColor = fg;
            numHour.BorderStyle = BorderStyle.FixedSingle;
            numHour.TextAlign = HorizontalAlignment.Center;

            trkHour = new TrackBar();
            trkHour.Bounds = new Rectangle(134, 10, 192, 34);
            trkHour.Minimum = 0; trkHour.Maximum = 23;
            trkHour.TickStyle = TickStyle.None;

            lbMin = new Label();
            lbMin.Bounds = new Rectangle(16, 66, 40, 24);
            lbMin.Text = "分钟";
            lbMin.ForeColor = sub;

            numMin = new NumericUpDown();
            numMin.Bounds = new Rectangle(66, 62, 60, 26);
            numMin.Minimum = 0; numMin.Maximum = 59;
            numMin.BackColor = bg; numMin.ForeColor = fg;
            numMin.BorderStyle = BorderStyle.FixedSingle;
            numMin.TextAlign = HorizontalAlignment.Center;

            trkMin = new TrackBar();
            trkMin.Bounds = new Rectangle(134, 56, 192, 34);
            trkMin.Minimum = 0; trkMin.Maximum = 59;
            trkMin.TickStyle = TickStyle.None;

            lbAction = new Label();
            lbAction.Bounds = new Rectangle(16, 112, 40, 24);
            lbAction.Text = "执行";
            lbAction.ForeColor = sub;

            cmbAction = new ComboBox();
            cmbAction.Bounds = new Rectangle(66, 108, 110, 26);
            cmbAction.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAction.BackColor = bg; cmbAction.ForeColor = fg;
            cmbAction.FlatStyle = FlatStyle.Flat;
            cmbAction.Items.AddRange(new object[] { "关机", "重启", "注销", "休眠", "锁定" });
            cmbAction.SelectedIndex = 0;

            pnlMode.Controls.AddRange(new Control[] {
                lbHour, numHour, trkHour, lbMin, numMin, trkMin, lbAction, cmbAction });

            btnOK = new Button();
            btnOK.Bounds = new Rectangle(16, 324, 340, 44);
            btnOK.Text = "确  定";
            btnOK.FlatStyle = FlatStyle.Flat;
            btnOK.FlatAppearance.BorderSize = 0;
            btnOK.BackColor = accent;
            btnOK.ForeColor = Color.White;
            btnOK.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            btnOK.Cursor = Cursors.Hand;
            btnOK.Click += new EventHandler(OnOK);

            btnStop = new Button();
            btnStop.Bounds = new Rectangle(16, 374, 340, 34);
            btnStop.Text = "取消定时";
            btnStop.FlatStyle = FlatStyle.Flat;
            btnStop.FlatAppearance.BorderSize = 0;
            btnStop.BackColor = Color.FromArgb(45, 51, 68);
            btnStop.ForeColor = sub;
            btnStop.Enabled = false;
            btnStop.Cursor = Cursors.Hand;
            btnStop.Click += new EventHandler(OnStop);

            lblStatus = new Label();
            lblStatus.Bounds = new Rectangle(16, 416, 340, 22);
            lblStatus.ForeColor = sub;
            lblStatus.BackColor = bg;
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.Text = "未设置定时任务";

            lblRemain = new Label();
            lblRemain.Bounds = new Rectangle(16, 438, 340, 22);
            lblRemain.ForeColor = accent;
            lblRemain.BackColor = bg;
            lblRemain.TextAlign = ContentAlignment.MiddleCenter;
            lblRemain.Text = "";

            btnRunNow = new Button();
            btnRunNow.Bounds = new Rectangle(16, 466, 104, 26);
            btnRunNow.Text = "立即执行";
            btnRunNow.FlatStyle = FlatStyle.Flat;
            btnRunNow.FlatAppearance.BorderSize = 0;
            btnRunNow.BackColor = Color.FromArgb(60, 34, 44);
            btnRunNow.ForeColor = red;
            btnRunNow.Cursor = Cursors.Hand;
            btnRunNow.Click += new EventHandler(OnRunNow);

            chkAutoStart = new CheckBox();
            chkAutoStart.Bounds = new Rectangle(132, 468, 110, 22);
            chkAutoStart.Text = "开机自启动";
            chkAutoStart.ForeColor = sub;
            chkAutoStart.BackColor = bg;
            chkAutoStart.CheckedChanged += new EventHandler(OnAutoStartChanged);

            linkHome = new LinkLabel();
            linkHome.Bounds = new Rectangle(244, 468, 112, 22);
            linkHome.Text = "项目主页 v2.0.1.9";
            linkHome.LinkColor = sub;
            linkHome.ActiveLinkColor = accent;
            linkHome.LinkBehavior = LinkBehavior.HoverUnderline;
            linkHome.LinkClicked += new LinkLabelLinkClickedEventHandler(OnHome);

            this.Controls.AddRange(new Control[] {
                lblTime, lblDate, btnFixed, btnCount, pnlMode,
                btnOK, btnStop, lblStatus, lblRemain, btnRunNow, chkAutoStart, linkHome });

            // 数值框与滑块双向同步
            numHour.ValueChanged += new EventHandler(delegate { if (!_updatingUi) { _updatingUi = true; trkHour.Value = Clamp((int)numHour.Value, trkHour.Minimum, trkHour.Maximum); _updatingUi = false; } });
            trkHour.ValueChanged += new EventHandler(delegate { if (!_updatingUi) { _updatingUi = true; numHour.Value = Clamp(trkHour.Value, (int)numHour.Minimum, (int)numHour.Maximum); _updatingUi = false; } });
            numMin.ValueChanged += new EventHandler(delegate { if (!_updatingUi) { _updatingUi = true; trkMin.Value = Clamp((int)numMin.Value, trkMin.Minimum, trkMin.Maximum); _updatingUi = false; } });
            trkMin.ValueChanged += new EventHandler(delegate { if (!_updatingUi) { _updatingUi = true; numMin.Value = Clamp(trkMin.Value, (int)numMin.Minimum, (int)numMin.Maximum); _updatingUi = false; } });

            btnFixed.Click += new EventHandler(delegate { SetMode(0); });
            btnCount.Click += new EventHandler(delegate { SetMode(1); });

            // 托盘
            tray = new NotifyIcon();
            tray.Icon = IconFactory.Create();
            tray.Text = "定时关机助手";
            tray.Visible = !UiTestMode;
            tray.DoubleClick += new EventHandler(ShowMain);
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("显示主界面", delegate { ShowMain(null, null); });
            menu.MenuItems.Add("取消定时并退出", delegate { _engine.Disarm(); _forceExit = true; tray.Visible = false; Application.Exit(); });
            menu.MenuItems.Add("退出", delegate { _forceExit = true; tray.Visible = false; Application.Exit(); });
            tray.ContextMenu = menu;

            this.Resize += new EventHandler(delegate
            {
                if (this.WindowState == FormWindowState.Minimized && _engine.Armed)
                {
                    this.Hide();
                    this.ShowInTaskbar = false;
                    Balloon("定时关机助手", "已设置在 " + ArmedDescription() + "，若要更改，请在此图标上单击右键。");
                    this.WindowState = FormWindowState.Normal;
                }
            });

            this.FormClosing += new FormClosingEventHandler(OnFormClosing);
        }

        private Button MakeModeButton(string text, int x, int y, Color fg, Color bg)
        {
            Button b = new Button();
            b.Bounds = new Rectangle(x, y, 120, 34);
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void LoadConfigToUi()
        {
            _updatingUi = true;
            SetMode(_cfg.Mode);
            cmbAction.SelectedIndex = Clamp((int)_cfg.Action, 0, 4);
            numHour.Value = Clamp(_cfg.Hour, (int)numHour.Minimum, (int)numHour.Maximum);
            numMin.Value = Clamp(_cfg.Minute, (int)numMin.Minimum, (int)numMin.Maximum);
            bool autoSet = IsAutoStartSet();
            chkAutoStart.Checked = autoSet || _cfg.AutoStart;
            _updatingUi = false;
            UpdateClockLabels();
            UpdateStatusLabels();
        }

        private void SetupTimers()
        {
            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 500;
            _uiTimer.Tick += new EventHandler(delegate { UpdateClockLabels(); UpdateStatusLabels(); });
            _uiTimer.Start();

            _engineTimer = new System.Windows.Forms.Timer();
            _engineTimer.Interval = 1000;
            _engineTimer.Tick += new EventHandler(delegate { _engine.Tick(); });
            _engineTimer.Start();

            _engine.FireRequested += new EventHandler(OnFire);
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void SetMode(int mode)
        {
            Color accent = Color.FromArgb(79, 140, 255);
            Color sub = Color.FromArgb(138, 147, 166);
            Color bg = Color.FromArgb(23, 27, 38);
            Color panel = Color.FromArgb(31, 36, 51);

            _cfg.Mode = mode;
            if (mode == 0)
            {
                btnFixed.BackColor = accent; btnFixed.ForeColor = Color.White;
                btnCount.BackColor = panel; btnCount.ForeColor = sub;
                numHour.Maximum = 23; trkHour.Maximum = 23;
            }
            else
            {
                btnCount.BackColor = accent; btnCount.ForeColor = Color.White;
                btnFixed.BackColor = panel; btnFixed.ForeColor = sub;
                numHour.Maximum = 99; trkHour.Maximum = 99;
            }
            numHour.Value = Clamp((int)numHour.Value, (int)numHour.Minimum, (int)numHour.Maximum);
            trkHour.Value = Clamp(trkHour.Value, trkHour.Minimum, trkHour.Maximum);
        }

        private PowerAction SelectedAction()
        {
            int i = cmbAction.SelectedIndex;
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
            string[] weeks = new string[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
            lblTime.Text = string.Format("{0:00}:{1:00}:{2:00}", now.Hour, now.Minute, now.Second);
            lblDate.Text = string.Format("{0}年{1}月{2}日 {3}", now.Year, now.Month, now.Day, weeks[(int)now.DayOfWeek]);
        }

        private void UpdateStatusLabels()
        {
            if (_engine.Armed)
            {
                lblStatus.Text = "将于 " + ArmedDescription();
                lblRemain.Text = "剩余 " + FormatSpan(_engine.Remaining());
                btnStop.Enabled = true;
            }
            else
            {
                lblStatus.Text = "未设置定时任务";
                lblRemain.Text = "";
                btnStop.Enabled = false;
            }
        }

        private static string FormatSpan(TimeSpan t)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private void Balloon(string title, string text)
        {
            try { tray.BalloonTipTitle = title; tray.BalloonTipText = text; tray.ShowBalloonTip(2000); }
            catch { }
        }

        // ---------- 事件 ----------
        private void OnOK(object sender, EventArgs e)
        {
            int hour = (int)numHour.Value;
            int minute = (int)numMin.Value;
            PowerAction action = SelectedAction();

            ValidateResult vr;
            if (_cfg.Mode == 0) vr = TaskValidator.ValidateFixed(hour, minute, DateTime.Now);
            else vr = TaskValidator.ValidateCount(hour, minute);

            if (vr == ValidateResult.BadInput)
            {
                MessageBox.Show("亲，这个是火星文嘛？看不懂啊", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (vr == ValidateResult.TimeInPast)
            {
                DateTime now = DateTime.Now;
                MessageBox.Show(string.Format("亲，现在已经{0}点{1}分了，想现在就关机的话，请点桌面左下角！", now.Hour, now.Minute),
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            TimerTask task = new TimerTask();
            task.IsCountdown = _cfg.Mode == 1;
            task.Hour = hour;
            task.Minute = minute;
            task.Action = action;
            task.CreatedAt = DateTime.Now;
            _engine.Arm(task);

            _cfg.Action = action;
            _cfg.Hour = hour;
            _cfg.Minute = minute;
            _cfg.Save(ConfigPath());

            Balloon("定时关机助手", "已设置在 " + ArmedDescription() + "，若要更改，请在此图标上单击右键。");
            UpdateStatusLabels();
            if (_cfg.Mode == 1) this.WindowState = FormWindowState.Minimized;
        }

        private void OnStop(object sender, EventArgs e)
        {
            _engine.Disarm();
            UpdateStatusLabels();
            Balloon("定时关机助手", "已取消定时任务。");
        }

        private void OnRunNow(object sender, EventArgs e)
        {
            PowerAction action = SelectedAction();
            string verb = ActionInfo.Name(action);
            if (MessageBox.Show("确定要立即" + verb + "吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            RunNow(action, "手动立即执行");
        }

        private void RunNow(PowerAction action, string reason)
        {
            if (_simulate)
            {
                _executor.Execute(action, reason);
                MessageBox.Show("【模拟模式】" + ActionInfo.Name(action) + " 已写入 simulate.log（未真正执行）",
                    "模拟模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _executor.Execute(action, reason);
            }
        }

        private void OnFire(object sender, EventArgs e)
        {
            PowerAction action = _engine.Task != null ? _engine.Task.Action : SelectedAction();

            if (UiTestMode)
            {
                _executor.Execute(action, "定时到达(UI测试)");
                lblStatus.Text = "已执行：" + ActionInfo.Name(action);
                return;
            }

            Decision dec = Decision.Cancelled;
            using (ConfirmForm f = new ConfirmForm(action, _cfg.WarnSeconds, _simulate))
            {
                f.ShowDialog(this);
                dec = f.Decision;
            }

            if (dec == Decision.Executed)
            {
                _executor.Execute(action, "定时到达");
                lblStatus.Text = "已执行：" + ActionInfo.Name(action);
                Balloon("定时关机助手", "正在" + ActionInfo.Name(action) + "！");
            }
            else if (dec == Decision.Delayed)
            {
                TimerTask task = new TimerTask();
                task.IsCountdown = true;
                task.Hour = 0;
                task.Minute = 10;
                task.Action = action;
                task.CreatedAt = DateTime.Now;
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

        private void ShowMain(object sender, EventArgs e)
        {
            this.Show();
            this.ShowInTaskbar = true;
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void OnHome(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try { Process.Start(RepoUrl); } catch { }
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            SetAutoStart(chkAutoStart.Checked);
            _cfg.AutoStart = chkAutoStart.Checked;
            _cfg.Save(ConfigPath());
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_forceExit || _engine.Armed == false) return;
            if (MessageBox.Show("已设置定时关机任务，确定取消任务并退出应用程序？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _engine.Disarm();
                _forceExit = true;
                tray.Visible = false;
                return;
            }
            e.Cancel = true;
            this.Hide();
            this.ShowInTaskbar = false;
            Balloon("定时关机助手", "定时任务仍在运行，请在此图标上单击右键管理。");
        }

        // ---------- 注册表自启动 ----------
        public static bool IsAutoStartSet()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
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
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        // ---------- 配置路径 ----------
        public static string ConfigPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string p = Path.Combine(baseDir, "config.ini");
            try
            {
                string probe = Path.Combine(baseDir, ".write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                p = Path.Combine(appData, "定时关机助手");
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                p = Path.Combine(p, "config.ini");
            }
            return p;
        }
    }

    // ---------- 定时到达确认弹窗 ----------
    public enum Decision { Executed = 0, Delayed = 1, Cancelled = 2 }

    public class ConfirmForm : Form
    {
        private PowerAction _action;
        private int _warnSeconds;
        private bool _simulate;
        private int _remaining;
        private bool _decided;

        private Label lblWarn;
        private Label lblBody;
        private TrackBar progress;
        private Button btnExecute;
        private Button btnDelay;
        private Button btnCancel;
        private System.Windows.Forms.Timer timer;

        public Decision Decision = Decision.Cancelled;

        public ConfirmForm(PowerAction action, int warnSeconds, bool simulate)
        {
            _action = action;
            _warnSeconds = warnSeconds;
            _simulate = simulate;
            _remaining = warnSeconds;
            _decided = false;

            Color bg = Color.FromArgb(23, 27, 38);
            Color red = Color.FromArgb(255, 92, 92);

            this.Text = _simulate ? "关机提示（模拟）" : "关机提示";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false; this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = bg;
            this.ForeColor = Color.FromArgb(226, 232, 244);
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.ClientSize = new Size(400, 224);
            this.Icon = IconFactory.Create();

            lblWarn = new Label();
            lblWarn.Bounds = new Rectangle(0, 20, 400, 32);
            lblWarn.Text = "关机提醒：请注意保存文件！";
            lblWarn.ForeColor = red;
            lblWarn.TextAlign = ContentAlignment.MiddleCenter;
            lblWarn.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);

            lblBody = new Label();
            lblBody.Bounds = new Rectangle(0, 60, 400, 26);
            lblBody.ForeColor = Color.FromArgb(226, 232, 244);
            lblBody.TextAlign = ContentAlignment.MiddleCenter;

            progress = new TrackBar();
            progress.Bounds = new Rectangle(40, 96, 320, 30);
            progress.Minimum = 0;
            progress.Maximum = _warnSeconds;
            progress.Value = _warnSeconds;
            progress.TickStyle = TickStyle.None;
            progress.Enabled = false;

            btnExecute = new Button();
            btnExecute.Bounds = new Rectangle(40, 154, 90, 36);
            btnExecute.Text = "执  行";
            btnExecute.FlatStyle = FlatStyle.Flat;
            btnExecute.FlatAppearance.BorderSize = 0;
            btnExecute.BackColor = red;
            btnExecute.ForeColor = Color.White;
            btnExecute.Cursor = Cursors.Hand;
            btnExecute.Click += new EventHandler(delegate { Decide(Decision.Executed); });

            btnDelay = new Button();
            btnDelay.Bounds = new Rectangle(155, 154, 90, 36);
            btnDelay.Text = "延迟 10 分钟";
            btnDelay.FlatStyle = FlatStyle.Flat;
            btnDelay.FlatAppearance.BorderSize = 0;
            btnDelay.BackColor = Color.FromArgb(79, 140, 255);
            btnDelay.ForeColor = Color.White;
            btnDelay.Cursor = Cursors.Hand;
            btnDelay.Click += new EventHandler(delegate { Decide(Decision.Delayed); });

            btnCancel = new Button();
            btnCancel.Bounds = new Rectangle(270, 154, 90, 36);
            btnCancel.Text = "取  消";
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.BackColor = Color.FromArgb(45, 51, 68);
            btnCancel.ForeColor = Color.FromArgb(138, 147, 166);
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.Click += new EventHandler(delegate { Decide(Decision.Cancelled); });

            this.Controls.AddRange(new Control[] { lblWarn, lblBody, progress, btnExecute, btnDelay, btnCancel });

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += new EventHandler(delegate { TickOnce(); });

            this.Load += new EventHandler(delegate
            {
                UpdateBody();
                try { SystemSounds.Exclamation.Play(); } catch { }
                timer.Start();
            });
        }

        private void UpdateBody()
        {
            lblBody.Text = string.Format("系统将在 {0} 秒后{1}。", _remaining, ActionInfo.Name(_action));
            try { progress.Value = ClampV(_remaining, progress.Minimum, progress.Maximum); } catch { }
        }

        private static int ClampV(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // 供 UI 冒烟测试直接驱动（不依赖消息循环）
        public void TickOnce()
        {
            if (_decided) return;
            _remaining--;
            if (_remaining <= 0)
            {
                Decide(Decision.Executed);
                return;
            }
            UpdateBody();
        }

        private void Decide(Decision d)
        {
            if (_decided) return;
            _decided = true;
            timer.Stop();
            Decision = d;
            this.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            timer.Stop();
            timer.Dispose();
        }
    }

    // ---------- 无界面逻辑自检（headless，无任何系统副作用） ----------
    public static class SelfTest
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
            string exe, args;
            RealExecutor.GetCommand(PowerAction.Shutdown, out exe, out args);
            Check("cmd.Shutdown", exe == "shutdown" && args == "-s -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Reboot, out exe, out args);
            Check("cmd.Reboot", exe == "shutdown" && args == "-r -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Logoff, out exe, out args);
            Check("cmd.Logoff", exe == "shutdown" && args == "-l", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Hibernate, out exe, out args);
            Check("cmd.Hibernate", exe == "rundll32.exe" && args.IndexOf("SetSuspendState") >= 0, exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Lock, out exe, out args);
            Check("cmd.Lock", exe == "rundll32.exe" && args.IndexOf("LockWorkStation") >= 0, exe + " " + args);

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
            TimerTask t1 = new TimerTask();
            t1.IsCountdown = false; t1.Hour = 10; t1.Minute = 5; t1.CreatedAt = now;
            Check("nextFire.sameDay", t1.NextFire(now) == new DateTime(2026, 10, 8, 10, 5, 0), t1.NextFire(now).ToString());
            TimerTask t2 = new TimerTask();
            t2.IsCountdown = false; t2.Hour = 0; t2.Minute = 30; t2.CreatedAt = new DateTime(2026, 10, 8, 23, 0, 0);
            Check("nextFire.tomorrow", t2.NextFire(new DateTime(2026, 10, 8, 23, 0, 0)) == new DateTime(2026, 10, 9, 0, 30, 0), t2.NextFire(new DateTime(2026, 10, 8, 23, 0, 0)).ToString());

            // 5. 倒计时任务计算
            TimerTask t3 = new TimerTask();
            t3.IsCountdown = true; t3.Hour = 0; t3.Minute = 90; t3.CreatedAt = now;
            Check("nextFire.countdown90min", t3.NextFire(now) == now.AddMinutes(90), t3.NextFire(now).ToString());

            // 6. 引擎：未到点不触发、到点触发且只触发一次
            DateTime fake = now;
            TimerEngine eng = new TimerEngine(delegate { return fake; });
            int fired = 0;
            eng.FireRequested += new EventHandler(delegate { fired++; });
            TimerTask t4 = new TimerTask();
            t4.IsCountdown = true; t4.Hour = 0; t4.Minute = 2; t4.Action = PowerAction.Shutdown; t4.CreatedAt = fake;
            eng.Arm(t4);
            for (int i = 0; i < 60; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.notFiredEarly", fired == 0 && eng.Armed, "fired=" + fired);
            for (int i = 0; i < 70; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.firedOnce", fired == 1 && eng.Armed == false, "fired=" + fired);
            for (int i = 0; i < 5; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.noRefire", fired == 1, "fired=" + fired);

            // 7. 模拟执行器：记录动作、写日志，绝不真实执行
            string logPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_simulate.log");
            try { File.Delete(logPath); } catch { }
            SimulatedExecutor sim = new SimulatedExecutor(logPath);
            sim.Execute(PowerAction.Shutdown, "自检");
            sim.Execute(PowerAction.Reboot, "自检2");
            Check("sim.recorded", sim.Executed.Count == 2, sim.Executed.Count.ToString());
            Check("sim.log", File.Exists(logPath) && File.ReadAllText(logPath).IndexOf("关机") >= 0, logPath);

            // 8. 延迟/取消决策 → 引擎状态
            TimerEngine eng2 = new TimerEngine(delegate { return DateTime.Now; });
            TimerTask t5 = new TimerTask();
            t5.IsCountdown = true; t5.Hour = 0; t5.Minute = 1; t5.Action = PowerAction.Shutdown; t5.CreatedAt = DateTime.Now;
            eng2.Arm(t5);
            // 模拟"延迟 10 分钟"决策：主窗体行为 = 重新武装一个 10 分钟倒计时
            eng2.Arm(new TimerTask { IsCountdown = true, Hour = 0, Minute = 10, Action = PowerAction.Shutdown, CreatedAt = DateTime.Now });
            Check("decision.delayRearms", eng2.Armed && eng2.Remaining().TotalMinutes > 9 && eng2.Remaining().TotalMinutes <= 10, eng2.Remaining().ToString());
            eng2.Disarm();
            Check("decision.cancelDisarms", eng2.Armed == false, null);

            // 9. 配置读写往返
            string cfgPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_config.ini");
            try { File.Delete(cfgPath); } catch { }
            AppConfig c1 = new AppConfig();
            c1.Action = PowerAction.Reboot; c1.WarnSeconds = 120; c1.Mode = 1; c1.Hour = 5; c1.Minute = 45;
            c1.Save(cfgPath);
            AppConfig c2 = AppConfig.Load(cfgPath);
            Check("config.roundtrip", c2.Action == PowerAction.Reboot && c2.WarnSeconds == 120 && c2.Mode == 1 && c2.Hour == 5 && c2.Minute == 45,
                c2.Action + "/" + c2.WarnSeconds + "/" + c2.Mode + "/" + c2.Hour + ":" + c2.Minute);
            Check("config.missing", AppConfig.Load(Path.Combine(Path.GetTempPath(), "no_such_file.ini")).WarnSeconds == 60, null);

            // 10. 损坏配置不崩溃
            File.WriteAllText(cfgPath, "garbage [[ == bad", Encoding.UTF8);
            AppConfig c3 = AppConfig.Load(cfgPath);
            Check("config.corrupt", c3.WarnSeconds == 60 && c3.Action == PowerAction.Shutdown, null);

            Console.WriteLine("===== 完成：失败 " + _failed + " 项 =====");
            return _failed == 0 ? 0 : 1;
        }
    }

    // ---------- 无窗口 UI 冒烟测试（不显示窗体、不执行真实动作） ----------
    public static class UiTest
    {
        public static int Run()
        {
            Console.WriteLine("===== 定时关机助手 UI 冒烟测试（不显示窗口、模拟执行） =====");
            int failed = 0;
            MainForm.UiTestMode = true;

            try
            {
                AppConfig cfg = new AppConfig();
                SimulatedExecutor sim = new SimulatedExecutor(Path.Combine(Path.GetTempPath(), "yuntimer_uitest_simulate.log"));
                MainForm form = new MainForm(cfg, sim, true);
                form.CreateControl();
                IntPtr h = form.Handle; // 强制创建句柄，暴露布局/资源问题

                bool ok = h != IntPtr.Zero;
                Console.WriteLine((ok ? "PASS  " : "FAIL  ") + "form.handle");
                if (!ok) failed++;

                // 确认弹窗：倒计时归零 → 决策为 Executed（不依赖消息循环，直接驱动 TickOnce）
                ConfirmForm cf = new ConfirmForm(PowerAction.Shutdown, 5, true);
                cf.CreateControl();
                IntPtr ch = cf.Handle;
                for (int i = 0; i < 10; i++) cf.TickOnce();
                bool cfOk = ch != IntPtr.Zero && cf.Decision == Decision.Executed;
                Console.WriteLine((cfOk ? "PASS  " : "FAIL  ") + "confirm.countdownToExecute  (decision=" + cf.Decision + ")");
                if (!cfOk) failed++;
                cf.Dispose();

                // 确认弹窗：手动取消
                ConfirmForm cf2 = new ConfirmForm(PowerAction.Lock, 60, false);
                cf2.CreateControl();
                cf2.Dispose();
                Console.WriteLine("PASS  confirm.createDispose");

                form.Dispose();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL  uiexception  -- " + ex.GetType().Name + ": " + ex.Message);
            }

            MainForm.UiTestMode = false;
            Console.WriteLine("===== 完成：失败 " + failed + " 项 =====");
            return failed == 0 ? 0 : 1;
        }
    }

    // ---------- 入口 ----------
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool simulate = false, selftest = false, uitest = false;
            foreach (string a in args)
            {
                string s = a.ToLower();
                if (s == "--simulate" || s == "/simulate" || s == "-s") simulate = true;
                if (s == "--selftest" || s == "/selftest") selftest = true;
                if (s == "--uitest" || s == "/uitest") uitest = true;
            }

            if (selftest) return SelfTest.Run();
            if (uitest) return UiTest.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppConfig cfg = AppConfig.Load(MainForm.ConfigPath());
            IActionExecutor executor;
            if (simulate) executor = new SimulatedExecutor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "simulate.log"));
            else executor = new RealExecutor(cfg.ForceWaitSeconds);

            Application.Run(new MainForm(cfg, executor, simulate));
            return 0;
        }
    }
}
