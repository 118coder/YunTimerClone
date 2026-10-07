// 定时到达确认弹窗：倒计时圆环 + 执行/延迟/取消，倒计时归零自动执行
// 决策由主窗体处理；执行器统一走 IActionExecutor

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using YunTimerClone.Core;

namespace YunTimerClone
{
    public enum Decision { Executed = 0, Delayed = 1, Cancelled = 2 }

    public partial class ConfirmDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly PowerAction _action;
        private readonly int _total;
        private readonly bool _simulate;
        private int _remaining;
        private bool _decided;
        private readonly DispatcherTimer _timer;

        public Decision Decision { get; private set; } = Decision.Cancelled;

        public ConfirmDialog(PowerAction action, int warnSeconds, bool simulate)
        {
            InitializeComponent();

            _action = action;
            _total = Math.Max(1, warnSeconds);
            _simulate = simulate;
            _remaining = _total;

            Title = simulate ? "关机提示（模拟）" : "关机提示";

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => TickOnce();

            Loaded += (s, e) =>
            {
                UpdateBody();
                AnimateIn();
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                _timer.Start();
            };

            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
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
            double fraction = Math.Max(0.0, Math.Min(1.0, (double)_remaining / _total));
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
                new PathSegment[] { new ArcSegment(new Point(ex, ey), new Size(r, r), 0, large, SweepDirection.Clockwise, true) },
                false);
            ArcPath.Data = new PathGeometry(new[] { fig });
        }

        private void AnimateIn()
        {
            ScaleTransform scale = new ScaleTransform(0.94, 0.94);
            RenderTransform = scale;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Opacity = 0;

            System.Windows.Media.Animation.DoubleAnimation sa = new System.Windows.Media.Animation.DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(180));
            sa.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.Windows.Media.Animation.DoubleAnimation oa = new System.Windows.Media.Animation.DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(180));
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

        private void OnExecute(object sender, RoutedEventArgs e) { Decide(Decision.Executed); }
        private void OnDelay(object sender, RoutedEventArgs e) { Decide(Decision.Delayed); }
        private void OnCancel(object sender, RoutedEventArgs e) { Decide(Decision.Cancelled); }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _timer.Stop();
        }
    }
}
