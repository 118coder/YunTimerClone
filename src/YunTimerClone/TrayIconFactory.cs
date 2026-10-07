// 托盘图标：运行时用 GDI+ 绘制（无外部资源文件）

using System.Drawing;
using System.Drawing.Drawing2D;

namespace YunTimerClone
{
    public static class TrayIconFactory
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
                    using (Pen ring = new Pen(Color.FromArgb(96, 205, 255), 2.4f))
                        g.DrawEllipse(ring, 4, 4, 24, 24);
                    using (Pen hand = new Pen(Color.White, 2.4f))
                    {
                        hand.StartCap = LineCap.Round;
                        hand.EndCap = LineCap.Round;
                        g.DrawLine(hand, 16, 16, 16, 8);
                        g.DrawLine(hand, 16, 16, 22, 16);
                    }
                    using (SolidBrush dot = new SolidBrush(Color.FromArgb(220, 38, 38)))
                        g.FillEllipse(dot, 14, 14, 4, 4);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
