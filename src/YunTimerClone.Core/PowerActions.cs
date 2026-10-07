// Core：动作类型与执行器抽象（真实执行与模拟执行的分界线）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace YunTimerClone.Core
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
            GetCommand(action, out string exe, out string args);
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
                Thread t = new Thread(delegate ()
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
                        // 强制关机也失败时不再提示
                    }
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

        public List<string> Executed { get { return _executed; } }
        private List<string> _executed = new List<string>();

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
            catch
            {
                // 日志写不进去也不影响模拟流程
            }
            return true;
        }
    }
}
