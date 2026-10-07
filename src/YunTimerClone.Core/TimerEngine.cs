// Core：定时任务、校验与计时引擎（UI 无关，纯逻辑可测）

using System;

namespace YunTimerClone.Core
{
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
}
