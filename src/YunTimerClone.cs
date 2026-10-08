// 定时关机助手（YunTimer 复刻版 · 零依赖 Fluent UI · 秒开版）
// 复刻自 www.yunguanji.com 的 YunTimer v2.0.1.9「定时关机助手」，原创实现。
//
// 技术形态：.NET Framework 4.8（Windows 10/11 系统自带）+ WPF，单 exe 零依赖、无运行时安装。
// 界面为手写 Fluent 设计语言（深色圆角卡片、分段选择器、开关、倒计时圆环、DWM 深色标题栏）。
// 全部界面用纯 C# 对象构建（无运行时 XAML 解析），启动即建树，点击秒开。
//
// 运行模式：
//   定时关机助手.exe            正常模式（定时到达后真实执行）
//   定时关机助手.exe --simulate 模拟模式（动作仅写入 simulate.log，绝不真正执行）
//   selftest.exe --selftest     无界面逻辑自检（headless，无任何系统副作用）
//   selftest.exe --uitest       无窗口 UI 冒烟测试（不显示窗体、不执行真实动作）
//   selftest.exe --perftest     启动性能分阶段计时

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("定时关机助手")]
[assembly: System.Reflection.AssemblyProduct("定时关机助手（YunTimer 复刻版）")]
[assembly: System.Reflection.AssemblyVersion("2.0.1.9")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.1.9")]

namespace YunTimerCloneCore
{
    // ---------- 核心逻辑：动作与执行器 ----------
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

    public interface IActionExecutor
    {
        string ModeName { get; }
        bool Execute(PowerAction action, string reason, out string error);
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

        public bool Execute(PowerAction action, string reason, out string error)
        {
            error = null;
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
                error = ActionInfo.Name(action) + "失败：\r\n" + ex.Message;
                return false;
            }

            // 关机/重启后若系统仍未关闭（命令被拦截等原因），等待一段时间后强制执行
            if (action == PowerAction.Shutdown || action == PowerAction.Reboot)
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    System.Threading.Thread.Sleep(_forceWaitSeconds * 1000);
                    try
                    {
                        ProcessStartInfo psi2 = new ProcessStartInfo("shutdown", "-s -f -t 0");
                        psi2.CreateNoWindow = true;
                        psi2.UseShellExecute = false;
                        Process.Start(psi2);
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            return true;
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

        private System.Collections.Generic.List<string> _executed = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<string> Executed { get { return _executed; } }

        public bool Execute(PowerAction action, string reason, out string error)
        {
            error = null;
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] 【模拟模式】{1}（{2}）— 未真正执行",
                DateTime.Now, ActionInfo.Name(action), reason);
            _executed.Add(line);
            try
            {
                File.AppendAllText(_logPath, line + "\r\n", Encoding.UTF8);
            }
            catch { }
            return true;
        }
    }

    // ---------- 配置 ----------
    public class AppConfig
    {
        public PowerAction Action = PowerAction.Shutdown;
        public int WarnSeconds = 60;
        public int ForceWaitSeconds = 30;
        public bool AutoStart = false;
        public int Mode = 0;   // 0=固定时间 1=倒计时
        public int Hour = 23;
        public int Minute = 30;

        public static PowerAction ParseAction(string val, PowerAction fallback)
        {
            for (int i = 0; i <= 4; i++)
            {
                if (val == ((PowerAction)i).ToString() || val == i.ToString()) return (PowerAction)i;
                if (val == ActionInfo.Name((PowerAction)i)) return (PowerAction)i;
            }
            return fallback;
        }

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
                        else if (key == "AutoStart") c.AutoStart = val == "1" || val.ToLowerInvariant() == "true";
                        else if (key == "Mode") { int v; if (int.TryParse(val, out v)) c.Mode = (v == 1) ? 1 : 0; }
                    }
                    else if (section == "Task")
                    {
                        if (key == "Hour") { int v; if (int.TryParse(val, out v) && v >= 0) c.Hour = v; }
                        else if (key == "Minute") { int v; if (int.TryParse(val, out v) && v >= 0) c.Minute = v; }
                    }
                }
            }
            catch { }
            return c;
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
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
            catch { }
        }

        public static string DefaultPath()
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
                string dir = Path.Combine(appData, "定时关机助手");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                p = Path.Combine(dir, "config.ini");
            }
            return p;
        }
    }

    // ---------- 定时任务与引擎 ----------
    public class TimerTask
    {
        public bool IsCountdown;
        public int Hour;
        public int Minute;
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

        public TimerEngine(Func<DateTime> clock) { _clock = clock; }

        public TimerTask Task;
        public bool Armed;
        public event EventHandler FireRequested;

        public void Arm(TimerTask task) { Task = task; Armed = true; }
        public void Disarm() { Armed = false; Task = null; }

        public DateTime NextFireTime()
        {
            if (Task == null) return DateTime.MinValue;
            return Task.NextFire(_clock());
        }

        public TimeSpan Remaining()
        {
            if (!Armed || Task == null) return TimeSpan.Zero;
            TimeSpan r = Task.NextFire(_clock()) - _clock();
            if (r < TimeSpan.Zero) r = TimeSpan.Zero;
            return r;
        }

        public void Tick()
        {
            if (!Armed || Task == null) return;
            DateTime now = _clock();
            if (now >= Task.NextFire(now))
            {
                Armed = false;
                EventHandler h = FireRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
        }
    }
}

namespace YunTimerClone
{
    using YunTimerCloneCore;

    // ---------- Fluent 界面工厂：纯 C# 构建全部样式/模板/布局（无运行时 XAML，启动快） ----------
    internal static class UiFactory
    {
        // 调色板（Fluent 深色）
        public static readonly Brush WindowBg = Freeze(0x1B, 0x1B, 0x1F);
        public static readonly Brush Accent = Freeze(0x60, 0xCD, 0xFF);
        public static readonly Brush Danger = Freeze(0xDC, 0x26, 0x26);
        public static readonly Brush Card = FreezeArgb(0x12, 0xFF, 0xFF, 0xFF);
        public static readonly Brush CardBorder = FreezeArgb(0x1F, 0xFF, 0xFF, 0xFF);
        public static readonly Brush SegBg = FreezeArgb(0x0F, 0xFF, 0xFF, 0xFF);
        public static readonly Brush HoverOverlay = FreezeArgb(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly Brush PressedOverlay = FreezeArgb(0x0F, 0xFF, 0xFF, 0xFF);
        public static readonly Brush TextSecondary = Freeze(0xC9, 0xC9, 0xCE);
        public static readonly Brush TextTertiary = Freeze(0x8B, 0x8B, 0x92);
        public static readonly Brush SegText = FreezeArgb(0xB3, 0xFF, 0xFF, 0xFF);
        public static readonly Brush DarkText = Freeze(0x1B, 0x1B, 0x1F);
        public static readonly Brush PopupBg = Freeze(0x26, 0x26, 0x2B);
        public static readonly Brush PopupBorder = FreezeArgb(0x2F, 0xFF, 0xFF, 0xFF);

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static SolidColorBrush FreezeArgb(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        public static readonly Style FieldLabelStyle = BuildFieldLabel();
        public static readonly Style SegmentedStyle = BuildSegmented();
        public static readonly Style AccentButtonStyle = BuildFilledButton(Accent, DarkText, 15.0, FontWeights.SemiBold);
        public static readonly Style DangerButtonStyle = BuildFilledButton(Danger, Brushes.White, 13.0, FontWeights.SemiBold);
        public static readonly Style SubtleButtonStyle = BuildSubtleButton();
        public static readonly Style DarkTextBoxStyle = BuildDarkTextBox();
        public static readonly Style DarkComboItemStyle = BuildDarkComboItem();
        public static readonly Style DarkComboStyle = BuildDarkCombo();
        public static readonly Style ToggleSwitchStyle = BuildToggleSwitch();

        public static void ForceInit()
        {
            Brush b = Accent;
            Style s = FieldLabelStyle;
            GC.KeepAlive(b);
            GC.KeepAlive(s);
        }

        private static FrameworkElementFactory Presenter(bool center)
        {
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            if (center)
            {
                cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            }
            return cp;
        }

        private static Trigger ValueTrigger(DependencyProperty prop, object value, DependencyProperty targetProp, object targetValue, string targetName)
        {
            Trigger t = new Trigger();
            t.Property = prop;
            t.Value = value;
            t.Setters.Add(new Setter(targetProp, targetValue, targetName));
            return t;
        }

        private static Style BuildFieldLabel()
        {
            Style s = new Style(typeof(TextBlock));
            s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(TextBlock.ForegroundProperty, TextSecondary));
            s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            return s;
        }

        private static Style BuildSegmented()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(RadioButton));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, new Thickness(0, 9, 0, 9));
            bd.AppendChild(Presenter(true));
            tpl.VisualTree = bd;

            Trigger checkedTrg = new Trigger();
            checkedTrg.Property = RadioButton.IsCheckedProperty;
            checkedTrg.Value = true;
            checkedTrg.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "Bd"));
            checkedTrg.Setters.Add(new Setter(Control.ForegroundProperty, DarkText));
            checkedTrg.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            tpl.Triggers.Add(checkedTrg);

            MultiTrigger hover = new MultiTrigger();
            hover.Conditions.Add(new Condition(RadioButton.IsCheckedProperty, false));
            hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
            hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverOverlay, "Bd"));
            tpl.Triggers.Add(hover);

            Style s = new Style(typeof(RadioButton));
            s.Setters.Add(new Setter(Control.ForegroundProperty, SegText));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildFilledButton(Brush bg, Brush fg, double fontSize, FontWeight weight)
        {
            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, bg);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.AppendChild(Presenter(true));
            tpl.VisualTree = bd;
            tpl.Triggers.Add(ValueTrigger(UIElement.IsMouseOverProperty, true, Border.OpacityProperty, 0.88, "Bd"));
            tpl.Triggers.Add(ValueTrigger(Button.IsPressedProperty, true, Border.OpacityProperty, 0.76, "Bd"));
            tpl.Triggers.Add(ValueTrigger(UIElement.IsEnabledProperty, false, Border.OpacityProperty, 0.35, "Bd"));

            Style s = new Style(typeof(Button));
            s.Setters.Add(new Setter(Control.ForegroundProperty, fg));
            s.Setters.Add(new Setter(Control.FontSizeProperty, fontSize));
            s.Setters.Add(new Setter(Control.FontWeightProperty, weight));
            s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildSubtleButton()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, HoverOverlay);
            bd.SetValue(Border.BorderBrushProperty, CardBorder);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.AppendChild(Presenter(true));
            tpl.VisualTree = bd;
            tpl.Triggers.Add(ValueTrigger(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, CardBorder, "Bd"));
            tpl.Triggers.Add(ValueTrigger(Button.IsPressedProperty, true, Border.BackgroundProperty, PressedOverlay, "Bd"));
            tpl.Triggers.Add(ValueTrigger(UIElement.IsEnabledProperty, false, Border.OpacityProperty, 0.35, "Bd"));

            Style s = new Style(typeof(Button));
            s.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildDarkTextBox()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, SegBg);
            bd.SetValue(Border.BorderBrushProperty, CardBorder);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.AppendChild(new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"));
            tpl.VisualTree = bd;
            tpl.Triggers.Add(ValueTrigger(UIElement.IsKeyboardFocusedProperty, true, Border.BorderBrushProperty, Accent, "Bd"));

            Style s = new Style(typeof(TextBox));
            s.Setters.Add(new Setter(Control.BackgroundProperty, SegBg));
            s.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            s.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.White));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildDarkComboItem()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(ComboBoxItem));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
            bd.AppendChild(Presenter(false));
            tpl.VisualTree = bd;
            tpl.Triggers.Add(ValueTrigger(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, HoverOverlay, "Bd"));
            tpl.Triggers.Add(ValueTrigger(ComboBoxItem.IsSelectedProperty, true, Border.BackgroundProperty, CardBorder, "Bd"));

            Style s = new Style(typeof(ComboBoxItem));
            s.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildDarkCombo()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(ComboBox));
            FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));

            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, SegBg);
            bd.SetValue(Border.BorderBrushProperty, CardBorder);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            grid.AppendChild(bd);

            FrameworkElementFactory sel = new FrameworkElementFactory(typeof(ContentPresenter));
            sel.SetBinding(ContentPresenter.ContentProperty,
                new Binding("SelectionBoxItem") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            sel.SetBinding(ContentPresenter.ContentTemplateProperty,
                new Binding("SelectionBoxItemTemplate") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            sel.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 30, 0));
            sel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            sel.SetValue(UIElement.IsHitTestVisibleProperty, false);
            grid.AppendChild(sel);

            FrameworkElementFactory chevron = new FrameworkElementFactory(typeof(TextBlock));
            chevron.SetValue(TextBlock.TextProperty, "\uE70D");
            chevron.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
            chevron.SetValue(TextBlock.FontSizeProperty, 10.0);
            chevron.SetValue(TextBlock.ForegroundProperty, TextSecondary);
            chevron.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
            chevron.SetValue(UIElement.IsHitTestVisibleProperty, false);
            grid.AppendChild(chevron);

            FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton));
            ControlTemplate toggleTpl = new ControlTemplate(typeof(ToggleButton));
            FrameworkElementFactory toggleBd = new FrameworkElementFactory(typeof(Border));
            toggleBd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            toggleTpl.VisualTree = toggleBd;
            toggle.SetValue(Control.TemplateProperty, toggleTpl);
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            Binding openBnd = new Binding("IsDropDownOpen");
            openBnd.RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent);
            openBnd.Mode = BindingMode.TwoWay;
            toggle.SetBinding(ToggleButton.IsCheckedProperty, openBnd);
            grid.AppendChild(toggle);

            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
            Binding popupBnd = new Binding("IsDropDownOpen");
            popupBnd.RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent);
            popup.SetBinding(Popup.IsOpenProperty, popupBnd);
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);

            FrameworkElementFactory popupBd = new FrameworkElementFactory(typeof(Border));
            popupBd.SetValue(Border.BackgroundProperty, PopupBg);
            popupBd.SetValue(Border.BorderBrushProperty, PopupBorder);
            popupBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            popupBd.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
            popupBd.SetValue(FrameworkElement.MaxHeightProperty, 210.0);
            popupBd.SetBinding(FrameworkElement.MinWidthProperty,
                new Binding("ActualWidth") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            popupBd.AppendChild(sv);
            popup.AppendChild(popupBd);
            grid.AppendChild(popup);

            tpl.VisualTree = grid;
            tpl.Triggers.Add(ValueTrigger(ComboBox.IsDropDownOpenProperty, true, Border.BorderBrushProperty, Accent, "Bd"));
            tpl.Triggers.Add(ValueTrigger(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, HoverOverlay, "Bd"));

            Style s = new Style(typeof(ComboBox));
            s.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(ItemsControl.ItemContainerStyleProperty, DarkComboItemStyle));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        private static Style BuildToggleSwitch()
        {
            ControlTemplate tpl = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border), "Pill");
            pill.SetValue(FrameworkElement.WidthProperty, 42.0);
            pill.SetValue(FrameworkElement.HeightProperty, 22.0);
            pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            pill.SetValue(Border.BackgroundProperty, FreezeArgb(0x2F, 0xFF, 0xFF, 0xFF));
            pill.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            FrameworkElementFactory thumb = new FrameworkElementFactory(typeof(Border), "Thumb");
            thumb.SetValue(FrameworkElement.WidthProperty, 14.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 14.0);
            thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            thumb.SetValue(Border.BackgroundProperty, Brushes.White);
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
            pill.AppendChild(thumb);
            panel.AppendChild(pill);

            FrameworkElementFactory cp = Presenter(false);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            panel.AppendChild(cp);
            tpl.VisualTree = panel;

            Trigger checkedTrg = new Trigger();
            checkedTrg.Property = ToggleButton.IsCheckedProperty;
            checkedTrg.Value = true;
            checkedTrg.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "Pill"));
            checkedTrg.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
            checkedTrg.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0), "Thumb"));
            tpl.Triggers.Add(checkedTrg);

            Style s = new Style(typeof(CheckBox));
            s.Setters.Add(new Setter(Control.ForegroundProperty, TextSecondary));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return s;
        }

        // ---------- 主窗口内容 ----------
        public class MainUi
        {
            public Grid Root;
            public TextBlock ClockText, DateText, StatusText, RemainText;
            public RadioButton RbFixed, RbCount;
            public MiniSlider TrkHour, TrkMin;
            public TextBox NumHour, NumMin;
            public ComboBox CmbAction;
            public Button BtnOK, BtnStop, BtnRunNow;
            public CheckBox ChkAuto;
            public System.Windows.Documents.Hyperlink LinkHome;
        }

        internal static bool TraceBuild = false;

        public static MainUi BuildMain()
        {
            Stopwatch sw = TraceBuild ? Stopwatch.StartNew() : null;
            Action<string> mark = delegate(string n)
            {
                if (TraceBuild && sw != null)
                    Console.WriteLine("  build." + n + " " + sw.ElapsedMilliseconds + " ms");
            };

            MainUi ui = new MainUi();

            Grid root = new Grid();
            root.Background = WindowBg;
            ui.Root = root;
            mark("root");

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(28, 18, 28, 20);
            root.Children.Add(panel);
            mark("panel");

            ui.ClockText = new TextBlock();
            ui.ClockText.FontFamily = new FontFamily("Segoe UI");
            ui.ClockText.FontSize = 52;
            ui.ClockText.FontWeight = FontWeights.SemiBold;
            ui.ClockText.HorizontalAlignment = HorizontalAlignment.Center;
            ui.ClockText.Text = "00:00:00";
            panel.Children.Add(ui.ClockText);
            mark("clockText(文本引擎初始化)");

            ui.DateText = new TextBlock();
            ui.DateText.FontSize = 13;
            ui.DateText.Foreground = TextSecondary;
            ui.DateText.HorizontalAlignment = HorizontalAlignment.Center;
            ui.DateText.Margin = new Thickness(0, 2, 0, 18);
            panel.Children.Add(ui.DateText);

            Border seg = new Border();
            seg.Background = SegBg;
            seg.CornerRadius = new CornerRadius(6);
            seg.Padding = new Thickness(3);
            seg.Margin = new Thickness(0, 0, 0, 14);
            UniformGrid segGrid = new UniformGrid();
            segGrid.Columns = 2;
            ui.RbFixed = new RadioButton();
            ui.RbFixed.GroupName = "Mode";
            ui.RbFixed.Content = "固定时间定时";
            ui.RbFixed.Style = SegmentedStyle;
            ui.RbCount = new RadioButton();
            ui.RbCount.GroupName = "Mode";
            ui.RbCount.Content = "倒计时定时";
            ui.RbCount.Style = SegmentedStyle;
            segGrid.Children.Add(ui.RbFixed);
            segGrid.Children.Add(ui.RbCount);
            seg.Child = segGrid;
            panel.Children.Add(seg);
            mark("segmented");

            Border card = new Border();
            card.Background = Card;
            card.BorderBrush = CardBorder;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(8);
            card.Padding = new Thickness(18);
            card.Margin = new Thickness(0, 0, 0, 14);
            StackPanel cardPanel = new StackPanel();
            card.Child = cardPanel;
            panel.Children.Add(card);

            Grid row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            TextBlock lbHour = new TextBlock();
            lbHour.Text = "小时";
            lbHour.Style = FieldLabelStyle;
            Grid.SetColumn(lbHour, 0);
            row1.Children.Add(lbHour);
            ui.TrkHour = new MiniSlider();
            ui.TrkHour.Minimum = 0;
            ui.TrkHour.Maximum = 23;
            ui.TrkHour.Value = 23;
            ui.TrkHour.Margin = new Thickness(4, 0, 10, 0);
            ui.TrkHour.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ui.TrkHour, 1);
            row1.Children.Add(ui.TrkHour);
            ui.NumHour = new TextBox();
            ui.NumHour.Style = DarkTextBoxStyle;
            ui.NumHour.Height = 34;
            ui.NumHour.MaxLength = 2;
            ui.NumHour.Text = "23";
            Grid.SetColumn(ui.NumHour, 2);
            row1.Children.Add(ui.NumHour);
            cardPanel.Children.Add(row1);

            Grid row2 = new Grid();
            row2.Margin = new Thickness(0, 14, 0, 0);
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            TextBlock lbMin = new TextBlock();
            lbMin.Text = "分钟";
            lbMin.Style = FieldLabelStyle;
            Grid.SetColumn(lbMin, 0);
            row2.Children.Add(lbMin);
            ui.TrkMin = new MiniSlider();
            ui.TrkMin.Minimum = 0;
            ui.TrkMin.Maximum = 59;
            ui.TrkMin.Value = 30;
            ui.TrkMin.Margin = new Thickness(4, 0, 10, 0);
            ui.TrkMin.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ui.TrkMin, 1);
            row2.Children.Add(ui.TrkMin);
            ui.NumMin = new TextBox();
            ui.NumMin.Style = DarkTextBoxStyle;
            ui.NumMin.Height = 34;
            ui.NumMin.MaxLength = 2;
            ui.NumMin.Text = "30";
            Grid.SetColumn(ui.NumMin, 2);
            row2.Children.Add(ui.NumMin);
            cardPanel.Children.Add(row2);

            Grid row3 = new Grid();
            row3.Margin = new Thickness(0, 14, 0, 0);
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock lbAction = new TextBlock();
            lbAction.Text = "执行";
            lbAction.Style = FieldLabelStyle;
            Grid.SetColumn(lbAction, 0);
            row3.Children.Add(lbAction);
            ui.CmbAction = new ComboBox();
            ui.CmbAction.Height = 34;
            ui.CmbAction.Style = DarkComboStyle;
            ui.CmbAction.VerticalContentAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ui.CmbAction, 1);
            row3.Children.Add(ui.CmbAction);
            cardPanel.Children.Add(row3);
            mark("card(滑块/输入框/下拉框)");

            ui.BtnOK = new Button();
            ui.BtnOK.Content = "确  定";
            ui.BtnOK.Style = AccentButtonStyle;
            ui.BtnOK.Height = 44;
            ui.BtnOK.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(ui.BtnOK);

            ui.BtnStop = new Button();
            ui.BtnStop.Content = "取消定时";
            ui.BtnStop.Style = SubtleButtonStyle;
            ui.BtnStop.Height = 38;
            ui.BtnStop.IsEnabled = false;
            ui.BtnStop.Margin = new Thickness(0, 0, 0, 18);
            panel.Children.Add(ui.BtnStop);

            ui.StatusText = new TextBlock();
            ui.StatusText.Text = "未设置定时任务";
            ui.StatusText.FontSize = 13;
            ui.StatusText.Foreground = TextSecondary;
            ui.StatusText.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(ui.StatusText);

            ui.RemainText = new TextBlock();
            ui.RemainText.FontSize = 15;
            ui.RemainText.FontWeight = FontWeights.SemiBold;
            ui.RemainText.Foreground = Accent;
            ui.RemainText.HorizontalAlignment = HorizontalAlignment.Center;
            ui.RemainText.Margin = new Thickness(0, 4, 0, 18);
            panel.Children.Add(ui.RemainText);

            Grid footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ui.ChkAuto = new CheckBox();
            ui.ChkAuto.Content = "开机自启动";
            ui.ChkAuto.Style = ToggleSwitchStyle;
            footer.Children.Add(ui.ChkAuto);
            ui.BtnRunNow = new Button();
            ui.BtnRunNow.Content = "立即执行";
            ui.BtnRunNow.Style = DangerButtonStyle;
            ui.BtnRunNow.Height = 34;
            ui.BtnRunNow.MinWidth = 96;
            Grid.SetColumn(ui.BtnRunNow, 2);
            footer.Children.Add(ui.BtnRunNow);
            panel.Children.Add(footer);
            mark("buttons+footer");

            TextBlock linkWrap = new TextBlock();
            linkWrap.HorizontalAlignment = HorizontalAlignment.Right;
            linkWrap.Margin = new Thickness(0, 12, 0, 0);
            linkWrap.FontSize = 12;
            ui.LinkHome = new System.Windows.Documents.Hyperlink();
            ui.LinkHome.Foreground = TextTertiary;
            ui.LinkHome.TextDecorations = null;
            ui.LinkHome.Inlines.Add("项目主页 v2.0.1.9");
            linkWrap.Inlines.Add(ui.LinkHome);
            panel.Children.Add(linkWrap);
            mark("link");
            return ui;
        }

        // ---------- 确认弹窗内容 ----------
        public class DialogUi
        {
            public Grid Root;
            public TextBlock BodyText, RemainSeconds;
            public System.Windows.Shapes.Path ArcPath;
            public Button BtnExecute, BtnDelay, BtnCancel;
        }

        public static DialogUi BuildDialog()
        {
            DialogUi ui = new DialogUi();

            Grid root = new Grid();
            root.Background = WindowBg;
            ui.Root = root;

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(28, 10, 28, 24);
            root.Children.Add(panel);

            StackPanel warnRow = new StackPanel();
            warnRow.Orientation = Orientation.Horizontal;
            warnRow.HorizontalAlignment = HorizontalAlignment.Center;
            TextBlock warnIcon = new TextBlock();
            warnIcon.Text = "\uE7BA";
            warnIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            warnIcon.FontSize = 20;
            warnIcon.Foreground = Danger;
            warnIcon.VerticalAlignment = VerticalAlignment.Center;
            warnIcon.Margin = new Thickness(0, 0, 10, 0);
            warnRow.Children.Add(warnIcon);
            TextBlock warnText = new TextBlock();
            warnText.Text = "关机提醒：请注意保存文件！";
            warnText.FontSize = 17;
            warnText.FontWeight = FontWeights.SemiBold;
            warnText.Foreground = Danger;
            warnText.VerticalAlignment = VerticalAlignment.Center;
            warnRow.Children.Add(warnText);
            panel.Children.Add(warnRow);

            Grid ring = new Grid();
            ring.Width = 112;
            ring.Height = 112;
            ring.Margin = new Thickness(0, 18, 0, 6);
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            System.Windows.Shapes.Ellipse track = new System.Windows.Shapes.Ellipse();
            track.Stroke = CardBorder;
            track.StrokeThickness = 6;
            ring.Children.Add(track);
            ui.ArcPath = new System.Windows.Shapes.Path();
            ui.ArcPath.Stroke = Accent;
            ui.ArcPath.StrokeThickness = 6;
            ui.ArcPath.StrokeStartLineCap = PenLineCap.Round;
            ui.ArcPath.StrokeEndLineCap = PenLineCap.Round;
            ring.Children.Add(ui.ArcPath);
            ui.RemainSeconds = new TextBlock();
            ui.RemainSeconds.Text = "60";
            ui.RemainSeconds.FontSize = 26;
            ui.RemainSeconds.FontWeight = FontWeights.SemiBold;
            ui.RemainSeconds.HorizontalAlignment = HorizontalAlignment.Center;
            ui.RemainSeconds.VerticalAlignment = VerticalAlignment.Center;
            ring.Children.Add(ui.RemainSeconds);
            panel.Children.Add(ring);

            ui.BodyText = new TextBlock();
            ui.BodyText.FontSize = 14;
            ui.BodyText.Foreground = TextSecondary;
            ui.BodyText.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(ui.BodyText);

            UniformGrid btnRow = new UniformGrid();
            btnRow.Columns = 3;
            btnRow.Margin = new Thickness(0, 24, 0, 0);
            ui.BtnExecute = new Button();
            ui.BtnExecute.Content = "执  行";
            ui.BtnExecute.Style = DangerButtonStyle;
            ui.BtnExecute.Height = 38;
            ui.BtnExecute.Margin = new Thickness(0, 0, 6, 0);
            btnRow.Children.Add(ui.BtnExecute);
            ui.BtnDelay = new Button();
            ui.BtnDelay.Content = "延迟 10 分钟";
            ui.BtnDelay.Style = SubtleButtonStyle;
            ui.BtnDelay.Height = 38;
            ui.BtnDelay.Margin = new Thickness(6, 0, 6, 0);
            btnRow.Children.Add(ui.BtnDelay);
            ui.BtnCancel = new Button();
            ui.BtnCancel.Content = "取  消";
            ui.BtnCancel.Style = SubtleButtonStyle;
            ui.BtnCancel.Height = 38;
            ui.BtnCancel.Margin = new Thickness(6, 0, 0, 0);
            btnRow.Children.Add(ui.BtnCancel);
            panel.Children.Add(btnRow);

            return ui;
        }

        private static ControlTemplate _thumbTemplate;

        public static ControlTemplate WhiteThumbTemplate()
        {
            if (_thumbTemplate == null)
            {
                ControlTemplate tpl = new ControlTemplate(typeof(Thumb));
                FrameworkElementFactory el = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
                el.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.White);
                tpl.VisualTree = el;
                _thumbTemplate = tpl;
            }
            return _thumbTemplate;
        }
    }

    // ---------- 轻量滑块（自绘，替代 Slider 模板，启动更快、交互一致） ----------
    public class MiniSlider : Canvas
    {
        private readonly Border _track;
        private readonly Border _fill;
        private readonly Thumb _thumb;
        private double _fracAtDragStart;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(MiniSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange, OnDependencyChanged));
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            "Minimum", typeof(double), typeof(MiniSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange, OnDependencyChanged));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            "Maximum", typeof(double), typeof(MiniSlider),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsArrange, OnDependencyChanged));

        public event EventHandler ValueChanged;

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public double Minimum
        {
            get { return (double)GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, value); }
        }

        public double Maximum
        {
            get { return (double)GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); }
        }

        private bool _inChangeCallback;

        private static void OnDependencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MiniSlider s = (MiniSlider)d;
            if (s._inChangeCallback)
            {
                s.UpdateVisual();
                return;
            }
            s._inChangeCallback = true;
            try
            {
                double range = s.Maximum - s.Minimum;
                if (range < 0) { s.Maximum = s.Minimum; range = 0; }
                double before = (e.Property == ValueProperty) ? (double)e.OldValue : s.Value;
                if (s.Value < s.Minimum) s.Value = s.Minimum;   // 嵌套回调只刷新视觉，不重复发事件
                else if (s.Value > s.Maximum) s.Value = s.Maximum;
                s.UpdateVisual();
                if (s.Value != before)
                {
                    EventHandler h = s.ValueChanged;
                    if (h != null) h(s, EventArgs.Empty);
                }
            }
            finally
            {
                s._inChangeCallback = false;
            }
        }

        public MiniSlider()
        {
            _track = new Border();
            _track.Height = 4;
            _track.CornerRadius = new CornerRadius(2);
            _track.Background = UiFactory.CardBorder;
            _fill = new Border();
            _fill.Height = 4;
            _fill.CornerRadius = new CornerRadius(2);
            _fill.Background = UiFactory.Accent;
            _thumb = new Thumb();
            _thumb.Width = 14;
            _thumb.Height = 14;
            _thumb.Cursor = Cursors.Hand;
            _thumb.Template = UiFactory.WhiteThumbTemplate();
            Children.Add(_track);
            Children.Add(_fill);
            Children.Add(_thumb);
            MinHeight = 18;
            Cursor = Cursors.Hand;
            SetTop(_track, 7);
            SetTop(_fill, 7);
            SetTop(_thumb, 2);
            SizeChanged += delegate { UpdateVisual(); };
            MouseLeftButtonDown += OnTrackMouseDown;
            _thumb.DragStarted += OnThumbDragStarted;
            _thumb.DragDelta += OnThumbDragDelta;
            UpdateVisual();
        }

        private double CurrentFraction()
        {
            double range = Maximum - Minimum;
            if (range <= 0) return 0;
            double frac = (Value - Minimum) / range;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            return frac;
        }

        private void UpdateVisual()
        {
            double w = ActualWidth - 14;
            if (w < 0) w = 0;
            double frac = CurrentFraction();
            _track.Width = ActualWidth;
            _fill.Width = 7 + frac * w;
            SetLeft(_thumb, frac * w);
        }

        private void SetValueFromX(double x)
        {
            double w = ActualWidth - 14;
            if (w < 1) w = 1;
            double frac = (x - 7) / w;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            Value = Minimum + Math.Round(frac * (Maximum - Minimum));
        }

        private void OnTrackMouseDown(object sender, MouseButtonEventArgs e)
        {
            SetValueFromX(e.GetPosition(this).X);
            _fracAtDragStart = CurrentFraction();
            CaptureMouse();
            _dragging = true;
            e.Handled = true;
        }

        private bool _dragging;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                SetValueFromX(e.GetPosition(this).X);
                e.Handled = true;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_dragging)
            {
                _dragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
        {
            _fracAtDragStart = CurrentFraction();
        }

        private void OnThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double w = ActualWidth - 14;
            if (w < 1) w = 1;
            double frac = _fracAtDragStart + e.HorizontalChange / w;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            Value = Minimum + Math.Round(frac * (Maximum - Minimum));
        }
    }

    // ---------- 主窗体 ----------
    public class MainWindow : Window
    {
        public static bool UiTestMode = false;

        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "定时关机助手";
        public const string RepoUrl = "https://github.com/118coder/YunTimerClone";

        private readonly AppConfig _cfg;
        private readonly IActionExecutor _executor;
        private readonly bool _simulate;
        private readonly TimerEngine _engine;
        private UiFactory.MainUi _ui;
        private DispatcherTimer _uiTimer;
        private DispatcherTimer _engineTimer;
        private WinForms.NotifyIcon _tray;
        private System.Drawing.Icon _trayIcon;
        private bool _updatingUi;
        private bool _forceExit;

        internal UiFactory.MainUi Ui { get { return _ui; } }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public MainWindow(AppConfig cfg, IActionExecutor executor, bool simulate)
        {
            _cfg = cfg;
            _executor = executor;
            _simulate = simulate;
            _engine = new TimerEngine(delegate { return DateTime.Now; });
            _engine.FireRequested += OnFire;

            Title = simulate ? "定时关机助手（模拟模式）" : "定时关机助手";
            Width = 430; Height = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = UiFactory.WindowBg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;

            // 首帧只画深色外壳（窗口立即出现），内容树在窗口显示后马上填充
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AttachContent));
        }

        internal void AttachContent()
        {
            if (_ui != null) return;
            _ui = UiFactory.BuildMain();
            Content = _ui.Root;
            WireEvents();
            LoadConfigToUi();
            // 托盘/定时器在内容就位后再补齐
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(DeferredSetup));
        }

        private void DeferredSetup()
        {
            try
            {
                _trayIcon = TrayIconFactory.Create();
                try
                {
                    Icon = Imaging.CreateBitmapSourceFromHIcon(_trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                catch { }
                _tray = new WinForms.NotifyIcon();
                _tray.Icon = _trayIcon;
                _tray.Text = "定时关机助手";
                _tray.Visible = !UiTestMode;
                _tray.DoubleClick += delegate { ShowMain(); };
                WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("显示主界面", null, new EventHandler(delegate { ShowMain(); }));
                menu.Items.Add("取消定时并退出", null, new EventHandler(delegate { _engine.Disarm(); ExitApp(); }));
                menu.Items.Add("退出", null, new EventHandler(delegate { ExitApp(); }));
                _tray.ContextMenuStrip = menu;
            }
            catch { }

            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiTimer.Tick += delegate { UpdateClockLabels(); UpdateStatusLabels(); };
            _uiTimer.Start();

            _engineTimer = new DispatcherTimer();
            _engineTimer.Interval = TimeSpan.FromSeconds(1);
            _engineTimer.Tick += delegate { _engine.Tick(); };
            _engineTimer.Start();
        }

        private void WireEvents()
        {
            _ui.RbFixed.Checked += delegate { SetMode(0); };
            _ui.RbCount.Checked += delegate { SetMode(1); };
            _ui.BtnOK.Click += OnOK;
            _ui.BtnStop.Click += OnStop;
            _ui.BtnRunNow.Click += OnRunNow;

            _ui.TrkHour.ValueChanged += delegate
            {
                if (_updatingUi) return;
                _updatingUi = true;
                _ui.NumHour.Text = ((int)_ui.TrkHour.Value).ToString();
                _updatingUi = false;
            };
            _ui.NumHour.TextChanged += delegate
            {
                if (_updatingUi) return;
                int v;
                if (int.TryParse(_ui.NumHour.Text, out v))
                {
                    _updatingUi = true;
                    if (v < 0) v = 0;
                    if (v > (int)_ui.TrkHour.Maximum) v = (int)_ui.TrkHour.Maximum;
                    _ui.TrkHour.Value = v;
                    _updatingUi = false;
                }
            };
            _ui.TrkMin.ValueChanged += delegate
            {
                if (_updatingUi) return;
                _updatingUi = true;
                _ui.NumMin.Text = ((int)_ui.TrkMin.Value).ToString();
                _updatingUi = false;
            };
            _ui.NumMin.TextChanged += delegate
            {
                if (_updatingUi) return;
                int v;
                if (int.TryParse(_ui.NumMin.Text, out v))
                {
                    _updatingUi = true;
                    if (v < 0) v = 0;
                    if (v > 59) v = 59;
                    _ui.TrkMin.Value = v;
                    _updatingUi = false;
                }
            };

            _ui.ChkAuto.Checked += OnAutoStartChanged;
            _ui.ChkAuto.Unchecked += OnAutoStartChanged;

            _ui.LinkHome.RequestNavigate += delegate
            {
                try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
                catch { }
            };

            StateChanged += OnStateChanged;
            Closing += OnClosing;
        }

        private void LoadConfigToUi()
        {
            _updatingUi = true;
            _ui.RbFixed.IsChecked = _cfg.Mode == 0;
            _ui.RbCount.IsChecked = _cfg.Mode == 1;
            ApplyModeMax(_cfg.Mode);
            _ui.NumHour.Text = Clamp(_cfg.Hour, 0, _cfg.Mode == 0 ? 23 : 99).ToString();
            _ui.NumMin.Text = Clamp(_cfg.Minute, 0, 59).ToString();
            _ui.TrkHour.Value = Clamp(_cfg.Hour, 0, _cfg.Mode == 0 ? 23 : 99);
            _ui.TrkMin.Value = Clamp(_cfg.Minute, 0, 59);
            _ui.CmbAction.ItemsSource = new string[] { "关机", "重启", "注销", "休眠", "锁定" };
            _ui.CmbAction.SelectedIndex = Clamp((int)_cfg.Action, 0, 4);
            _ui.ChkAuto.IsChecked = IsAutoStartSet() || _cfg.AutoStart;
            _updatingUi = false;
            UpdateClockLabels();
            UpdateStatusLabels();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int chrome = 0x001F1B1B; // #1B1B1F（COLORREF 为 BGR）
                DwmSetWindowAttribute(hwnd, 35, ref chrome, 4); // 标题栏颜色（Win11）
                DwmSetWindowAttribute(hwnd, 34, ref chrome, 4); // 边框颜色（Win11）
            }
            catch { }
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void ApplyModeMax(int mode)
        {
            _ui.TrkHour.Maximum = mode == 0 ? 23 : 99;
        }

        private void SetMode(int mode)
        {
            if (_updatingUi) return;
            _cfg.Mode = mode;
            int max = mode == 0 ? 23 : 99;
            ApplyModeMax(mode);
            int v;
            if (int.TryParse(_ui.NumHour.Text, out v) && v > max)
            {
                _updatingUi = true;
                _ui.NumHour.Text = max.ToString();
                _ui.TrkHour.Value = max;
                _updatingUi = false;
            }
        }

        private PowerAction SelectedAction()
        {
            int i = _ui.CmbAction.SelectedIndex;
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
            _ui.ClockText.Text = string.Format("{0:00}:{1:00}:{2:00}", now.Hour, now.Minute, now.Second);
            _ui.DateText.Text = string.Format("{0}年{1}月{2}日 {3}", now.Year, now.Month, now.Day, weeks[(int)now.DayOfWeek]);
        }

        private void UpdateStatusLabels()
        {
            if (_engine.Armed)
            {
                _ui.StatusText.Text = "将于 " + ArmedDescription();
                _ui.RemainText.Text = "剩余 " + FormatSpan(_engine.Remaining());
                _ui.BtnStop.IsEnabled = true;
            }
            else
            {
                _ui.StatusText.Text = "未设置定时任务";
                _ui.RemainText.Text = "";
                _ui.BtnStop.IsEnabled = false;
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
            int hour, minute;
            if (!int.TryParse(_ui.NumHour.Text, out hour) || !int.TryParse(_ui.NumMin.Text, out minute))
            {
                MessageBox.Show("亲，这个是火星文嘛？看不懂啊", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
            string error;
            if (!_executor.Execute(action, reason, out error))
            {
                MessageBox.Show(error, "关机失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnFire(object sender, EventArgs e)
        {
            PowerAction action = _engine.Task != null ? _engine.Task.Action : SelectedAction();

            if (UiTestMode)
            {
                string ignored;
                _executor.Execute(action, "定时到达(UI测试)", out ignored);
                _ui.StatusText.Text = "已执行：" + ActionInfo.Name(action);
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
                _ui.StatusText.Text = "已执行：" + ActionInfo.Name(action);
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
            if (_tray != null) _tray.Visible = false;
            Application.Current.Shutdown();
        }

        private void OnAutoStartChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingUi) return;
            bool on = _ui.ChkAuto.IsChecked == true;
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
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (on) k.SetValue(RunValueName, "\"" + exePath + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_uiTimer != null) _uiTimer.Stop();
            if (_engineTimer != null) _engineTimer.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
    }

    // ---------- 定时到达确认弹窗 ----------
    public enum Decision { Executed = 0, Delayed = 1, Cancelled = 2 }

    public class ConfirmDialog : Window
    {
        private readonly PowerAction _action;
        private readonly int _total;
        private readonly bool _simulate;
        private int _remaining;
        private bool _decided;
        private readonly DispatcherTimer _timer;
        private readonly UiFactory.DialogUi _ui;

        public Decision Decision { get; private set; }

        public ConfirmDialog(PowerAction action, int warnSeconds, bool simulate)
        {
            _action = action;
            _total = Math.Max(1, warnSeconds);
            _simulate = simulate;
            _remaining = _total;
            Decision = Decision.Cancelled;

            Title = simulate ? "关机提示（模拟）" : "关机提示";
            Width = 400; Height = 330;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Background = UiFactory.WindowBg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;

            _ui = UiFactory.BuildDialog();
            Content = _ui.Root;

            _ui.BtnExecute.Click += delegate { Decide(Decision.Executed); };
            _ui.BtnDelay.Click += delegate { Decide(Decision.Delayed); };
            _ui.BtnCancel.Click += delegate { Decide(Decision.Cancelled); };

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += delegate { TickOnce(); };

            Loaded += delegate
            {
                UpdateBody();
                AnimateIn();
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                _timer.Start();
            };
            MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
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

        private void UpdateBody()
        {
            _ui.BodyText.Text = string.Format("系统将在 {0} 秒后{1}。", _remaining, ActionInfo.Name(_action));
            _ui.RemainSeconds.Text = _remaining.ToString();
            double fraction = (double)_remaining / _total;
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            UpdateArc(fraction);
        }

        private void UpdateArc(double fraction)
        {
            const double cx = 56, cy = 56, r = 48;
            if (fraction >= 0.999)
            {
                _ui.ArcPath.Data = new EllipseGeometry(new Point(cx, cy), r, r);
                return;
            }
            if (fraction <= 0.001)
            {
                _ui.ArcPath.Data = null;
                return;
            }
            double sweep = 2 * Math.PI * fraction;
            double ex = cx + r * Math.Sin(sweep);
            double ey = cy - r * Math.Cos(sweep);
            bool large = fraction > 0.5;
            PathFigure fig = new PathFigure(
                new Point(cx, cy - r),
                new System.Windows.Media.PathSegment[] {
                    new ArcSegment(new Point(ex, ey), new Size(r, r), 0, large, SweepDirection.Clockwise, true)
                },
                false);
            _ui.ArcPath.Data = new PathGeometry(new System.Windows.Media.PathFigure[] { fig });
        }

        private void AnimateIn()
        {
            ScaleTransform scale = new ScaleTransform(0.94, 0.94);
            RenderTransform = scale;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Opacity = 0;
            DoubleAnimation sa = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(180));
            CubicEase ease = new CubicEase();
            ease.EasingMode = EasingMode.EaseOut;
            sa.EasingFunction = ease;
            DoubleAnimation oa = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(180));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            BeginAnimation(OpacityProperty, oa);
        }

        private void Decide(Decision d)
        {
            if (_decided) return;
            _decided = true;
            _timer.Stop();
            Decision = d;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _timer.Stop();
        }
    }

    // ---------- 托盘图标（运行时绘制，无外部资源） ----------
    public static class TrayIconFactory
    {
        public static System.Drawing.Icon Create()
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(32, 32))
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (System.Drawing.SolidBrush bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(23, 27, 38)))
                        g.FillRectangle(bg, 0, 0, 32, 32);
                    using (System.Drawing.Pen ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(96, 205, 255), 2.4f))
                        g.DrawEllipse(ring, 4, 4, 24, 24);
                    using (System.Drawing.Pen hand = new System.Drawing.Pen(System.Drawing.Color.White, 2.4f))
                    {
                        hand.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        hand.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(hand, 16, 16, 16, 8);
                        g.DrawLine(hand, 16, 16, 22, 16);
                    }
                    using (System.Drawing.SolidBrush dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 38, 38)))
                        g.FillEllipse(dot, 14, 14, 4, 4);
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // ---------- 自检 ----------
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

            string exe, args;
            RealExecutor.GetCommand(PowerAction.Shutdown, out exe, out args);
            Check("cmd.Shutdown", exe == "shutdown" && args == "-s -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Reboot, out exe, out args);
            Check("cmd.Reboot", exe == "shutdown" && args == "-r -t 0", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Logoff, out exe, out args);
            Check("cmd.Logoff", exe == "shutdown" && args == "-l", exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Hibernate, out exe, out args);
            Check("cmd.Hibernate", exe == "rundll32.exe" && args.Contains("SetSuspendState"), exe + " " + args);
            RealExecutor.GetCommand(PowerAction.Lock, out exe, out args);
            Check("cmd.Lock", exe == "rundll32.exe" && args.Contains("LockWorkStation"), exe + " " + args);

            DateTime now = new DateTime(2026, 10, 8, 10, 0, 0);
            Check("validate.Fixed.ok", TaskValidator.ValidateFixed(10, 5, now) == ValidateResult.Ok, null);
            Check("validate.Fixed.past", TaskValidator.ValidateFixed(9, 0, now) == ValidateResult.TimeInPast, null);
            Check("validate.Fixed.equalNow", TaskValidator.ValidateFixed(10, 0, now) == ValidateResult.TimeInPast, null);
            Check("validate.Fixed.badHour", TaskValidator.ValidateFixed(24, 0, now) == ValidateResult.BadInput, null);
            Check("validate.Fixed.badMin", TaskValidator.ValidateFixed(10, 60, now) == ValidateResult.BadInput, null);

            Check("validate.Count.ok", TaskValidator.ValidateCount(0, 1) == ValidateResult.Ok, null);
            Check("validate.Count.zero", TaskValidator.ValidateCount(0, 0) == ValidateResult.BadInput, null);
            Check("validate.Count.bad", TaskValidator.ValidateCount(-1, 0) == ValidateResult.BadInput, null);

            TimerTask t1 = new TimerTask(); t1.IsCountdown = false; t1.Hour = 10; t1.Minute = 5; t1.CreatedAt = now;
            Check("nextFire.sameDay", t1.NextFire(now) == new DateTime(2026, 10, 8, 10, 5, 0), t1.NextFire(now).ToString());
            DateTime late = new DateTime(2026, 10, 8, 23, 0, 0);
            TimerTask t2 = new TimerTask(); t2.IsCountdown = false; t2.Hour = 0; t2.Minute = 30; t2.CreatedAt = late;
            Check("nextFire.tomorrow", t2.NextFire(late) == new DateTime(2026, 10, 9, 0, 30, 0), t2.NextFire(late).ToString());

            TimerTask t3 = new TimerTask(); t3.IsCountdown = true; t3.Hour = 0; t3.Minute = 90; t3.CreatedAt = now;
            Check("nextFire.countdown90min", t3.NextFire(now) == now.AddMinutes(90), t3.NextFire(now).ToString());

            DateTime fake = now;
            TimerEngine eng = new TimerEngine(delegate { return fake; });
            int fired = 0;
            eng.FireRequested += delegate { fired++; };
            TimerTask t4 = new TimerTask(); t4.IsCountdown = true; t4.Hour = 0; t4.Minute = 2; t4.Action = PowerAction.Shutdown; t4.CreatedAt = fake;
            eng.Arm(t4);
            for (int i = 0; i < 60; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.notFiredEarly", fired == 0 && eng.Armed, "fired=" + fired);
            for (int i = 0; i < 70; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.firedOnce", fired == 1 && !eng.Armed, "fired=" + fired);
            for (int i = 0; i < 5; i++) { fake = fake.AddSeconds(1); eng.Tick(); }
            Check("engine.noRefire", fired == 1, "fired=" + fired);

            // MiniSlider 数值钳制与事件
            MiniSlider ms = new MiniSlider();
            int msEvents = 0;
            ms.ValueChanged += delegate { msEvents++; };
            ms.Minimum = 0; ms.Maximum = 23;
            ms.Value = 30;
            Check("slider.clampHigh", Math.Abs(ms.Value - 23) < 0.001, ms.Value.ToString());
            ms.Value = -5;
            Check("slider.clampLow", Math.Abs(ms.Value) < 0.001, ms.Value.ToString());
            Check("slider.events", msEvents == 2, msEvents.ToString());

            string logPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_simulate.log");
            try { File.Delete(logPath); } catch { }
            SimulatedExecutor sim = new SimulatedExecutor(logPath);
            string err1, err2;
            sim.Execute(PowerAction.Shutdown, "自检", out err1);
            sim.Execute(PowerAction.Reboot, "自检2", out err2);
            Check("sim.recorded", sim.Executed.Count == 2, sim.Executed.Count.ToString());
            Check("sim.noError", err1 == null && err2 == null, null);
            Check("sim.log", File.Exists(logPath) && File.ReadAllText(logPath).Contains("关机"), logPath);

            TimerEngine eng2 = new TimerEngine(delegate { return DateTime.Now; });
            TimerTask t5 = new TimerTask(); t5.IsCountdown = true; t5.Hour = 0; t5.Minute = 1; t5.Action = PowerAction.Shutdown; t5.CreatedAt = DateTime.Now;
            eng2.Arm(t5);
            TimerTask t6 = new TimerTask(); t6.IsCountdown = true; t6.Hour = 0; t6.Minute = 10; t6.Action = PowerAction.Shutdown; t6.CreatedAt = DateTime.Now;
            eng2.Arm(t6);
            TimeSpan rem = eng2.Remaining();
            Check("decision.delayRearms", eng2.Armed && rem.TotalMinutes > 9 && rem.TotalMinutes <= 10, rem.ToString());
            eng2.Disarm();
            Check("decision.cancelDisarms", !eng2.Armed, null);

            string cfgPath = Path.Combine(Path.GetTempPath(), "yuntimer_selftest_config.ini");
            try { File.Delete(cfgPath); } catch { }
            AppConfig c1 = new AppConfig(); c1.Action = PowerAction.Reboot; c1.WarnSeconds = 120; c1.Mode = 1; c1.Hour = 5; c1.Minute = 45;
            c1.Save(cfgPath);
            AppConfig c2 = AppConfig.Load(cfgPath);
            Check("config.roundtrip", c2.Action == PowerAction.Reboot && c2.WarnSeconds == 120 && c2.Mode == 1 && c2.Hour == 5 && c2.Minute == 45,
                c2.Action + "/" + c2.WarnSeconds + "/" + c2.Mode + "/" + c2.Hour + ":" + c2.Minute);
            Check("config.missing", AppConfig.Load(Path.Combine(Path.GetTempPath(), "no_such_file.ini")).WarnSeconds == 60, null);

            File.WriteAllText(cfgPath, "garbage [[ == bad", Encoding.UTF8);
            AppConfig c3 = AppConfig.Load(cfgPath);
            Check("config.corrupt", c3.WarnSeconds == 60 && c3.Action == PowerAction.Shutdown, null);

            Console.WriteLine("===== 完成：失败 " + _failed + " 项 =====");
            return _failed == 0 ? 0 : 1;
        }
    }

    public static class UiTestSuite
    {
        public static int Run()
        {
            Console.WriteLine("===== 定时关机助手 UI 冒烟测试（不显示窗口、模拟执行） =====");
            int failed = 0;

            if (Application.Current == null)
            {
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            MainWindow.UiTestMode = true;
            try
            {
                AppConfig cfg = new AppConfig();
                SimulatedExecutor sim = new SimulatedExecutor(Path.Combine(Path.GetTempPath(), "yuntimer_uitest_simulate.log"));

                MainWindow win = new MainWindow(cfg, sim, true);
                new WindowInteropHelper(win).EnsureHandle();
                win.AttachContent();
                // 直接对内容树测量/布局（未显示的 Window 不会布局内容），强制模板应用
                win.Ui.Root.Measure(new Size(430, 660));
                win.Ui.Root.Arrange(new Rect(0, 0, 430, 660));
                win.Ui.Root.UpdateLayout();
                bool mainOk = win.Ui.ClockText != null
                    && win.Ui.NumHour != null
                    && win.Ui.CmbAction != null
                    && win.Ui.BtnOK != null
                    && System.Windows.Media.VisualTreeHelper.GetChildrenCount(win.Ui.BtnOK) > 0
                    && win.Ui.ClockText.ActualWidth > 0;
                if (!mainOk)
                {
                    Console.WriteLine("  诊断: clock=" + (win.Ui.ClockText == null ? "null" : win.Ui.ClockText.ActualWidth.ToString("0.#"))
                        + " numHour=" + (win.Ui.NumHour == null ? "null" : "ok")
                        + " combo=" + (win.Ui.CmbAction == null ? "null" : "ok")
                        + " btnOK=" + (win.Ui.BtnOK == null ? "null" : win.Ui.BtnOK.ActualWidth.ToString("0.#")));
                }
                Console.WriteLine((mainOk ? "PASS  " : "FAIL  ") + "main.controls");
                if (!mainOk) failed++;

                // 滑块 ↔ 数字框联动
                win.Ui.TrkHour.Value = 5;
                bool linkOk = win.Ui.NumHour.Text == "5";
                Console.WriteLine((linkOk ? "PASS  " : "FAIL  ") + "slider.textbox.sync");
                if (!linkOk) failed++;
                win.Close();

                ConfirmDialog cf = new ConfirmDialog(PowerAction.Shutdown, 5, true);
                new WindowInteropHelper(cf).EnsureHandle();
                for (int i = 0; i < 10; i++) cf.TickOnce();
                bool cfOk = cf.Decision == Decision.Executed;
                Console.WriteLine((cfOk ? "PASS  " : "FAIL  ") + "confirm.countdownToExecute  (decision=" + cf.Decision + ")");
                if (!cfOk) failed++;
                cf.Close();

                ConfirmDialog cf2 = new ConfirmDialog(PowerAction.Lock, 60, false);
                new WindowInteropHelper(cf2).EnsureHandle();
                cf2.UpdateLayout();
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

    // ---------- 启动性能分阶段计时（定位"点击到出画面"的耗时） ----------
    public static class PerfTest
    {
        public static int Run()
        {
            Console.WriteLine("===== 启动性能分阶段计时 =====");
            Stopwatch total = Stopwatch.StartNew();
            Action<string, Stopwatch, Action> stage = delegate(string name, Stopwatch sw, Action work)
            {
                Stopwatch s = Stopwatch.StartNew();
                work();
                s.Stop();
                Console.WriteLine(string.Format("{0,-28} {1,8} ms   (累计 {2} ms)", name, s.ElapsedMilliseconds, sw.ElapsedMilliseconds));
            };

            Application app = null;
            stage("Application 创建", total, delegate
            {
                app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            });

            Window bare = null;
            stage("裸 Window 创建+句柄", total, delegate
            {
                bare = new Window();
                bare.Width = 100; bare.Height = 100;
                new WindowInteropHelper(bare).EnsureHandle();
            });

            MainWindow win = null;
            MainWindow.UiTestMode = true;
            stage("MainWindow 构造(仅外壳)", total, delegate
            {
                win = new MainWindow(new AppConfig(), new SimulatedExecutor(Path.Combine(Path.GetTempPath(), "perf.log")), true);
            });

            stage("EnsureHandle", total, delegate
            {
                new WindowInteropHelper(win).EnsureHandle();
            });

            UiFactory.TraceBuild = true;
            stage("AttachContent(内容树+接线)", total, delegate
            {
                win.AttachContent();
            });
            UiFactory.TraceBuild = false;

            stage("UpdateLayout(布局)", total, delegate
            {
                win.UpdateLayout();
            });

            Console.WriteLine("总耗时: " + total.ElapsedMilliseconds + " ms");
            MainWindow.UiTestMode = false;
            return 0;
        }
    }

    // ---------- 入口 ----------
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool simulate = false, selftest = false, uitest = false, perftest = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--simulate" || s == "/simulate" || s == "-s") simulate = true;
                if (s == "--selftest" || s == "/selftest") selftest = true;
                if (s == "--uitest" || s == "/uitest") uitest = true;
                if (s == "--perftest" || s == "/perftest") perftest = true;
            }

            if (selftest) return SelfTestSuite.Run();
            if (uitest) return UiTestSuite.Run();
            if (perftest) return PerfTest.Run();

            AppConfig cfg = AppConfig.Load(AppConfig.DefaultPath());
            IActionExecutor executor;
            if (simulate)
                executor = new SimulatedExecutor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "simulate.log"));
            else
                executor = new RealExecutor(cfg.ForceWaitSeconds);

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnLastWindowClose;
            MainWindow win = new MainWindow(cfg, executor, simulate);
            app.Run(win);
            return 0;
        }
    }
}
