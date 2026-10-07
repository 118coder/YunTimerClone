// Core：配置读写（INI，exe 不可写时回落 %APPDATA%）

using System;
using System.IO;
using System.Text;

namespace YunTimerClone.Core
{
    public class AppConfig
    {
        public PowerAction Action = PowerAction.Shutdown;
        public int WarnSeconds = 60;       // 定时到达前弹窗倒计时秒数
        public int ForceWaitSeconds = 30;  // 关机命令后多久转强制关机
        public bool AutoStart = false;
        public int Mode = 0;               // 0=固定时间 1=倒计时
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
            catch
            {
                // 配置损坏则使用默认值
            }
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
            try
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 目录只读时忽略
            }
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
}
