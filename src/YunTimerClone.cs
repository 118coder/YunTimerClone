// 定时关机助手（YunTimer 复刻版 · 零依赖 Fluent UI）
// 复刻自 www.yunguanji.com 的 YunTimer v2.0.1.9「定时关机助手」，原创实现。
//
// 技术形态：.NET Framework 4.8（Windows 10/11 系统自带）+ WPF，单 exe 零依赖、无运行时安装。
// 界面为手写 Fluent 设计语言（深色圆角卡片、分段选择器、开关、倒计时圆环、DWM 深色标题栏），
// 不使用任何第三方 UI 库；XAML 在运行时由 XamlReader 加载。
//
// 运行模式：
//   定时关机助手.exe            正常模式（定时到达后真实执行）
//   定时关机助手.exe --simulate 模拟模式（动作仅写入 simulate.log，绝不真正执行）
//   selftest.exe --selftest     无界面逻辑自检（headless，无任何系统副作用）
//   selftest.exe --uitest       无窗口 UI 冒烟测试（不显示窗体、不执行真实动作）

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

    // ---------- 界面 XAML（运行时解析，手写 Fluent 设计语言，无第三方库） ----------
    internal static class Ui
    {
        public const string Resources = @"
    <SolidColorBrush x:Key='AccentBrush' Color='#60CDFF'/>
    <SolidColorBrush x:Key='DangerBrush' Color='#DC2626'/>
    <SolidColorBrush x:Key='CardBrush' Color='#12FFFFFF'/>
    <SolidColorBrush x:Key='CardBorderBrush' Color='#1FFFFFFF'/>
    <SolidColorBrush x:Key='SegBrush' Color='#0FFFFFFF'/>
    <SolidColorBrush x:Key='TextSecondaryBrush' Color='#C9C9CE'/>
    <SolidColorBrush x:Key='TextTertiaryBrush' Color='#8B8B92'/>
    <Style x:Key='FieldLabel' TargetType='TextBlock'>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Foreground' Value='{StaticResource TextSecondaryBrush}'/>
      <Setter Property='VerticalAlignment' Value='Center'/>
    </Style>
    <Style x:Key='SegmentedRadio' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='#B3FFFFFF'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='0,9'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='{StaticResource AccentBrush}'/>
                <Setter Property='Foreground' Value='#1B1B1F'/>
                <Setter Property='FontWeight' Value='SemiBold'/>
              </Trigger>
              <MultiTrigger>
                <MultiTrigger.Conditions>
                  <Condition Property='IsChecked' Value='False'/>
                  <Condition Property='IsMouseOver' Value='True'/>
                </MultiTrigger.Conditions>
                <Setter TargetName='Bd' Property='Background' Value='#0FFFFFFF'/>
              </MultiTrigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='AccentButton' TargetType='Button'>
      <Setter Property='Foreground' Value='#1B1B1F'/>
      <Setter Property='Background' Value='{StaticResource AccentBrush}'/>
      <Setter Property='FontSize' Value='15'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='5'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.88'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.76'/></Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.35'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='SubtleButton' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='#14FFFFFF' BorderBrush='#1FFFFFFF' BorderThickness='1' CornerRadius='5'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='#0FFFFFFF'/></Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.35'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DangerButton' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='Background' Value='{StaticResource DangerBrush}'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='5'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.88'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.76'/></Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.35'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DarkTextBox' TargetType='TextBox'>
      <Setter Property='Background' Value='#0FFFFFFF'/>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='CaretBrush' Value='#FFFFFF'/>
      <Setter Property='FontSize' Value='14'/>
      <Setter Property='HorizontalContentAlignment' Value='Center'/>
      <Setter Property='VerticalContentAlignment' Value='Center'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='TextBox'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='#1FFFFFFF' BorderThickness='1' CornerRadius='5'>
              <ScrollViewer x:Name='PART_ContentHost' Margin='0'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Bd' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DarkComboItem' TargetType='ComboBoxItem'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ComboBoxItem'>
            <Border x:Name='Bd' Background='Transparent' Padding='12,8' CornerRadius='4' Margin='4,2'>
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#14FFFFFF'/></Trigger>
              <Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DarkCombo' TargetType='ComboBox'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='ItemContainerStyle' Value='{StaticResource DarkComboItem}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ComboBox'>
            <Grid>
              <Border x:Name='Bd' Background='#0FFFFFFF' BorderBrush='#1FFFFFFF' BorderThickness='1' CornerRadius='5'/>
              <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                                Margin='12,0,30,0' VerticalAlignment='Center' HorizontalAlignment='Left' IsHitTestVisible='False'/>
              <TextBlock Text='&#xE70D;' FontFamily='Segoe MDL2 Assets' FontSize='10' Foreground='#C9C9CE'
                         HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' IsHitTestVisible='False'/>
              <ToggleButton Focusable='False' ClickMode='Press'
                            IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
                <ToggleButton.Template>
                  <ControlTemplate TargetType='ToggleButton'>
                    <Border Background='Transparent'/>
                  </ControlTemplate>
                </ToggleButton.Template>
              </ToggleButton>
              <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' PopupAnimation='Fade'>
                <Border Background='#26262B' BorderBrush='#2FFFFFFF' BorderThickness='1' CornerRadius='6'
                        MinWidth='{TemplateBinding ActualWidth}' MaxHeight='210' Margin='0,4,0,0'>
                  <ScrollViewer>
                    <ItemsPresenter/>
                  </ScrollViewer>
                </Border>
              </Popup>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsDropDownOpen' Value='True'>
                <Setter TargetName='Bd' Property='BorderBrush' Value='{StaticResource AccentBrush}'/>
              </Trigger>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#14FFFFFF'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DarkSlider' TargetType='Slider'>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Slider'>
            <Grid Background='Transparent' VerticalAlignment='Center'>
              <Border Height='4' CornerRadius='2' Background='#1FFFFFFF' VerticalAlignment='Center'/>
              <Track x:Name='PART_Track' IsDirectionReversed='True'>
                <Track.DecreaseRepeatButton>
                  <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                    <RepeatButton.Template>
                      <ControlTemplate TargetType='RepeatButton'>
                        <Grid Background='Transparent'>
                          <Border Height='4' CornerRadius='2' Background='{StaticResource AccentBrush}' VerticalAlignment='Center'/>
                        </Grid>
                      </ControlTemplate>
                    </RepeatButton.Template>
                  </RepeatButton>
                </Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton>
                  <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                    <RepeatButton.Template>
                      <ControlTemplate TargetType='RepeatButton'>
                        <Border Background='Transparent'/>
                      </ControlTemplate>
                    </RepeatButton.Template>
                  </RepeatButton>
                </Track.IncreaseRepeatButton>
                <Track.Thumb>
                  <Thumb Width='14' Height='14' Focusable='False'>
                    <Thumb.Template>
                      <ControlTemplate TargetType='Thumb'>
                        <Ellipse Fill='#FFFFFF'/>
                      </ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='ToggleSwitch' TargetType='CheckBox'>
      <Setter Property='Foreground' Value='{StaticResource TextSecondaryBrush}'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <StackPanel Orientation='Horizontal'>
              <Border x:Name='Pill' Width='42' Height='22' CornerRadius='11' Background='#2FFFFFFF' VerticalAlignment='Center'>
                <Border x:Name='Thumb' Width='14' Height='14' CornerRadius='7' Background='#FFFFFF' HorizontalAlignment='Left' Margin='4,0,0,0'/>
              </Border>
              <ContentPresenter VerticalAlignment='Center' Margin='10,0,0,0'/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Pill' Property='Background' Value='{StaticResource AccentBrush}'/>
                <Setter TargetName='Thumb' Property='HorizontalAlignment' Value='Right'/>
                <Setter TargetName='Thumb' Property='Margin' Value='0,0,4,0'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>";

        public const string MainBody = @"
      <StackPanel x:Name='RootPanel' Margin='28,18,28,20'>
        <TextBlock x:Name='ClockText' Text='00:00:00' FontFamily='Segoe UI' FontSize='52' FontWeight='SemiBold' HorizontalAlignment='Center'/>
        <TextBlock x:Name='DateText' Text='' FontSize='13' Foreground='{StaticResource TextSecondaryBrush}' HorizontalAlignment='Center' Margin='0,2,0,18'/>
        <Border Background='{StaticResource SegBrush}' CornerRadius='6' Padding='3' Margin='0,0,0,14'>
          <UniformGrid Columns='2'>
            <RadioButton x:Name='RbFixed' GroupName='Mode' Content='固定时间定时' Style='{StaticResource SegmentedRadio}'/>
            <RadioButton x:Name='RbCount' GroupName='Mode' Content='倒计时定时' Style='{StaticResource SegmentedRadio}'/>
          </UniformGrid>
        </Border>
        <Border Background='{StaticResource CardBrush}' BorderBrush='{StaticResource CardBorderBrush}' BorderThickness='1' CornerRadius='8' Padding='18' Margin='0,0,0,14'>
          <StackPanel>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='44'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='96'/>
              </Grid.ColumnDefinitions>
              <TextBlock Grid.Column='0' Text='小时' Style='{StaticResource FieldLabel}'/>
              <Slider Grid.Column='1' x:Name='TrkHour' Minimum='0' Maximum='23' Value='23' IsSnapToTickEnabled='True' TickFrequency='1' IsMoveToPointEnabled='True' VerticalAlignment='Center' Margin='4,0,10,0' Style='{StaticResource DarkSlider}'/>
              <TextBox Grid.Column='2' x:Name='NumHour' Style='{StaticResource DarkTextBox}' Height='34' MaxLength='2' Text='23'/>
            </Grid>
            <Grid Margin='0,14,0,0'>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='44'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='96'/>
              </Grid.ColumnDefinitions>
              <TextBlock Grid.Column='0' Text='分钟' Style='{StaticResource FieldLabel}'/>
              <Slider Grid.Column='1' x:Name='TrkMin' Minimum='0' Maximum='59' Value='30' IsSnapToTickEnabled='True' TickFrequency='1' IsMoveToPointEnabled='True' VerticalAlignment='Center' Margin='4,0,10,0' Style='{StaticResource DarkSlider}'/>
              <TextBox Grid.Column='2' x:Name='NumMin' Style='{StaticResource DarkTextBox}' Height='34' MaxLength='2' Text='30'/>
            </Grid>
            <Grid Margin='0,14,0,0'>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='44'/>
                <ColumnDefinition Width='*'/>
              </Grid.ColumnDefinitions>
              <TextBlock Grid.Column='0' Text='执行' Style='{StaticResource FieldLabel}'/>
              <ComboBox Grid.Column='1' x:Name='CmbAction' Height='34' Style='{StaticResource DarkCombo}' VerticalContentAlignment='Center'/>
            </Grid>
          </StackPanel>
        </Border>
        <Button x:Name='BtnOK' Content='确  定' Style='{StaticResource AccentButton}' Height='44' HorizontalAlignment='Stretch' Margin='0,0,0,10'/>
        <Button x:Name='BtnStop' Content='取消定时' Style='{StaticResource SubtleButton}' Height='38' IsEnabled='False' HorizontalAlignment='Stretch' Margin='0,0,0,18'/>
        <TextBlock x:Name='StatusText' Text='未设置定时任务' FontSize='13' Foreground='{StaticResource TextSecondaryBrush}' HorizontalAlignment='Center'/>
        <TextBlock x:Name='RemainText' Text='' FontSize='15' FontWeight='SemiBold' Foreground='{StaticResource AccentBrush}' HorizontalAlignment='Center' Margin='0,4,0,18'/>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
          </Grid.ColumnDefinitions>
          <CheckBox Grid.Column='0' x:Name='ChkAuto' Content='开机自启动' Style='{StaticResource ToggleSwitch}'/>
          <Button Grid.Column='2' x:Name='BtnRunNow' Content='立即执行' Style='{StaticResource DangerButton}' Height='34' MinWidth='96'/>
        </Grid>
        <TextBlock HorizontalAlignment='Right' Margin='0,12,0,0' FontSize='12'>
          <Hyperlink x:Name='LinkHome' Foreground='{StaticResource TextTertiaryBrush}' TextDecorations='{x:Null}'>项目主页 v2.0.1.9</Hyperlink>
        </TextBlock>
      </StackPanel>";

        public const string DialogBody = @"
      <StackPanel x:Name='DialogRoot' Margin='28,10,28,24'>
        <StackPanel Orientation='Horizontal' HorizontalAlignment='Center'>
          <TextBlock Text='&#xE7BA;' FontFamily='Segoe MDL2 Assets' FontSize='20' Foreground='{StaticResource DangerBrush}' VerticalAlignment='Center' Margin='0,0,10,0'/>
          <TextBlock Text='关机提醒：请注意保存文件！' FontSize='17' FontWeight='SemiBold' Foreground='{StaticResource DangerBrush}' VerticalAlignment='Center'/>
        </StackPanel>
        <Grid Width='112' Height='112' Margin='0,18,0,6' HorizontalAlignment='Center'>
          <Ellipse Stroke='#1FFFFFFF' StrokeThickness='6'/>
          <Path x:Name='ArcPath' Stroke='{StaticResource AccentBrush}' StrokeThickness='6' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
          <TextBlock x:Name='RemainSeconds' Text='60' FontSize='26' FontWeight='SemiBold' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
        <TextBlock x:Name='BodyText' Text='' FontSize='14' Foreground='{StaticResource TextSecondaryBrush}' HorizontalAlignment='Center'/>
        <UniformGrid Columns='3' Margin='0,24,0,0'>
          <Button x:Name='BtnExecute' Content='执  行' Style='{StaticResource DangerButton}' Height='38' Margin='0,0,6,0'/>
          <Button x:Name='BtnDelay' Content='延迟 10 分钟' Style='{StaticResource SubtleButton}' Height='38' Margin='6,0'/>
          <Button x:Name='BtnCancel' Content='取  消' Style='{StaticResource SubtleButton}' Height='38' Margin='6,0,0,0'/>
        </UniformGrid>
      </StackPanel>";

        private static string Wrap(string body)
        {
            return "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                   "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                   "<Grid.Resources>" + Resources + "</Grid.Resources>" + body + "</Grid>";
        }

        public static Grid ParseMain()
        {
            return (Grid)System.Windows.Markup.XamlReader.Parse(Wrap(MainBody));
        }

        public static Grid ParseDialog()
        {
            return (Grid)System.Windows.Markup.XamlReader.Parse(Wrap(DialogBody));
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
        private readonly Grid _root;
        private DispatcherTimer _uiTimer;
        private DispatcherTimer _engineTimer;
        private WinForms.NotifyIcon _tray;
        private bool _updatingUi;
        private bool _forceExit;

        private TextBlock ClockText, DateText, StatusText, RemainText;
        private RadioButton RbFixed, RbCount;
        private Slider TrkHour, TrkMin;
        private TextBox NumHour, NumMin;
        private ComboBox CmbAction;
        private Button BtnOK, BtnStop, BtnRunNow;
        private CheckBox ChkAuto;
        private System.Windows.Documents.Hyperlink LinkHome;

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
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            try
            {
                Icon = Imaging.CreateBitmapSourceFromHIcon(TrayIconFactory.Create().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }

            _root = Ui.ParseMain();
            Content = _root;
            LocateFields();
            WireEvents();
            LoadConfigToUi();
            SetupTray();
            SetupTimers();
        }

        private void LocateFields()
        {
            ClockText = (TextBlock)_root.FindName("ClockText");
            DateText = (TextBlock)_root.FindName("DateText");
            StatusText = (TextBlock)_root.FindName("StatusText");
            RemainText = (TextBlock)_root.FindName("RemainText");
            RbFixed = (RadioButton)_root.FindName("RbFixed");
            RbCount = (RadioButton)_root.FindName("RbCount");
            TrkHour = (Slider)_root.FindName("TrkHour");
            TrkMin = (Slider)_root.FindName("TrkMin");
            NumHour = (TextBox)_root.FindName("NumHour");
            NumMin = (TextBox)_root.FindName("NumMin");
            CmbAction = (ComboBox)_root.FindName("CmbAction");
            BtnOK = (Button)_root.FindName("BtnOK");
            BtnStop = (Button)_root.FindName("BtnStop");
            BtnRunNow = (Button)_root.FindName("BtnRunNow");
            ChkAuto = (CheckBox)_root.FindName("ChkAuto");
            LinkHome = (System.Windows.Documents.Hyperlink)_root.FindName("LinkHome");
        }

        public FrameworkElement GetUiElement(string name)
        {
            return _root.FindName(name) as FrameworkElement;
        }

        private void WireEvents()
        {
            RbFixed.Checked += delegate { SetMode(0); };
            RbCount.Checked += delegate { SetMode(1); };
            BtnOK.Click += OnOK;
            BtnStop.Click += OnStop;
            BtnRunNow.Click += OnRunNow;

            TrkHour.ValueChanged += delegate
            {
                if (_updatingUi) return;
                _updatingUi = true;
                NumHour.Text = ((int)TrkHour.Value).ToString();
                _updatingUi = false;
            };
            NumHour.TextChanged += delegate
            {
                if (_updatingUi) return;
                int v;
                if (int.TryParse(NumHour.Text, out v))
                {
                    _updatingUi = true;
                    if (v < 0) v = 0;
                    if (v > (int)TrkHour.Maximum) v = (int)TrkHour.Maximum;
                    TrkHour.Value = v;
                    _updatingUi = false;
                }
            };
            TrkMin.ValueChanged += delegate
            {
                if (_updatingUi) return;
                _updatingUi = true;
                NumMin.Text = ((int)TrkMin.Value).ToString();
                _updatingUi = false;
            };
            NumMin.TextChanged += delegate
            {
                if (_updatingUi) return;
                int v;
                if (int.TryParse(NumMin.Text, out v))
                {
                    _updatingUi = true;
                    if (v < 0) v = 0;
                    if (v > 59) v = 59;
                    TrkMin.Value = v;
                    _updatingUi = false;
                }
            };

            ChkAuto.Checked += OnAutoStartChanged;
            ChkAuto.Unchecked += OnAutoStartChanged;

            LinkHome.RequestNavigate += delegate
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
            RbFixed.IsChecked = _cfg.Mode == 0;
            RbCount.IsChecked = _cfg.Mode == 1;
            ApplyModeMax(_cfg.Mode);
            NumHour.Text = Clamp(_cfg.Hour, 0, _cfg.Mode == 0 ? 23 : 99).ToString();
            NumMin.Text = Clamp(_cfg.Minute, 0, 59).ToString();
            TrkHour.Value = Clamp(_cfg.Hour, 0, _cfg.Mode == 0 ? 23 : 99);
            TrkMin.Value = Clamp(_cfg.Minute, 0, 59);
            CmbAction.ItemsSource = new string[] { "关机", "重启", "注销", "休眠", "锁定" };
            CmbAction.SelectedIndex = Clamp((int)_cfg.Action, 0, 4);
            ChkAuto.IsChecked = IsAutoStartSet() || _cfg.AutoStart;
            _updatingUi = false;
            UpdateClockLabels();
            UpdateStatusLabels();
        }

        private void SetupTray()
        {
            _tray = new WinForms.NotifyIcon();
            _tray.Icon = TrayIconFactory.Create();
            _tray.Text = "定时关机助手";
            _tray.Visible = !UiTestMode;
            _tray.DoubleClick += delegate { ShowMain(); };
            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, new EventHandler(delegate { ShowMain(); }));
            menu.Items.Add("取消定时并退出", null, new EventHandler(delegate { _engine.Disarm(); ExitApp(); }));
            menu.Items.Add("退出", null, new EventHandler(delegate { ExitApp(); }));
            _tray.ContextMenuStrip = menu;
        }

        private void SetupTimers()
        {
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiTimer.Tick += delegate { UpdateClockLabels(); UpdateStatusLabels(); };
            _uiTimer.Start();

            _engineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _engineTimer.Tick += delegate { _engine.Tick(); };
            _engineTimer.Start();
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
            TrkHour.Maximum = mode == 0 ? 23 : 99;
        }

        private void SetMode(int mode)
        {
            if (_updatingUi) return;
            _cfg.Mode = mode;
            int max = mode == 0 ? 23 : 99;
            ApplyModeMax(mode);
            int v;
            if (int.TryParse(NumHour.Text, out v) && v > max)
            {
                _updatingUi = true;
                NumHour.Text = max.ToString();
                TrkHour.Value = max;
                _updatingUi = false;
            }
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
            int hour, minute;
            if (!int.TryParse(NumHour.Text, out hour) || !int.TryParse(NumMin.Text, out minute))
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
        private readonly Grid _root;
        private TextBlock BodyText, RemainSeconds;
        private System.Windows.Shapes.Path ArcPath;

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
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;

            _root = Ui.ParseDialog();
            Content = _root;
            BodyText = (TextBlock)_root.FindName("BodyText");
            RemainSeconds = (TextBlock)_root.FindName("RemainSeconds");
            ArcPath = (System.Windows.Shapes.Path)_root.FindName("ArcPath");

            ((Button)_root.FindName("BtnExecute")).Click += delegate { Decide(Decision.Executed); };
            ((Button)_root.FindName("BtnDelay")).Click += delegate { Decide(Decision.Delayed); };
            ((Button)_root.FindName("BtnCancel")).Click += delegate { Decide(Decision.Cancelled); };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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
            BodyText.Text = string.Format("系统将在 {0} 秒后{1}。", _remaining, ActionInfo.Name(_action));
            RemainSeconds.Text = _remaining.ToString();
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
                ArcPath.Data = new EllipseGeometry(new Point(cx, cy), r, r);
                return;
            }
            if (fraction <= 0.001)
            {
                ArcPath.Data = null;
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
            ArcPath.Data = new PathGeometry(new System.Windows.Media.PathFigure[] { fig });
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

        public FrameworkElement GetUiElement(string name)
        {
            return _root.FindName(name) as FrameworkElement;
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
                bool mainOk = win.GetUiElement("ClockText") != null
                    && win.GetUiElement("NumHour") != null
                    && win.GetUiElement("CmbAction") != null
                    && win.GetUiElement("BtnOK") != null;
                Console.WriteLine((mainOk ? "PASS  " : "FAIL  ") + "main.controls");
                if (!mainOk) failed++;
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

    // ---------- 入口 ----------
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool simulate = false, selftest = false, uitest = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--simulate" || s == "/simulate" || s == "-s") simulate = true;
                if (s == "--selftest" || s == "/selftest") selftest = true;
                if (s == "--uitest" || s == "/uitest") uitest = true;
            }

            if (selftest) return SelfTestSuite.Run();
            if (uitest) return UiTestSuite.Run();

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
