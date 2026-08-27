using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AbletonOptions
{
    /// <summary>
    /// Матовое стекло: снимок рабочего стола, сильно уменьшенный (это уже сглаживание),
    /// пару раз размытый и растянутый обратно билинейно. Дёшево и выглядит как настоящий blur.
    /// Снимок делается один раз до показа окна, поэтому в кадр не попадаем мы сами.
    /// </summary>
    public static class Backdrop
    {
        const int Shrink = 8;      // во сколько раз ужимаем перед размытием
        const int BlurRadius = 3;  // радиус box-blur на уменьшенной картинке
        const int BlurPasses = 3;  // три прохода box-blur ≈ гауссово размытие

        // Готовая подложка во весь виртуальный экран: размытие и вуаль уже вплавлены.
        // Собирается один раз, дальше каждый контрол копирует свой кусок один-в-один,
        // без масштабирования — иначе интерфейс заметно подтормаживает при каждой
        // перерисовке и особенно при перетаскивании окна.
        static Bitmap _composited;
        static Rectangle _screen;

        public static bool Ready { get { return _composited != null; } }

        public static void Capture()
        {
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                if (vs.Width <= 0 || vs.Height <= 0) return;

                using (Bitmap full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);

                    int sw = Math.Max(1, vs.Width / Shrink);
                    int sh = Math.Max(1, vs.Height / Shrink);
                    Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(full, new Rectangle(0, 0, sw, sh));
                    }

                    for (int i = 0; i < BlurPasses; i++) BoxBlur(small, BlurRadius);

                    Bitmap composited = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(composited))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ia.SetWrapMode(WrapMode.TileFlipXY);   // без светлой каймы по краям
                            g.DrawImage(small, new Rectangle(0, 0, vs.Width, vs.Height),
                                        0, 0, small.Width, small.Height, GraphicsUnit.Pixel, ia);
                        }
                        // Затемняющая вуаль, чтобы белый текст читался на любых обоях.
                        using (SolidBrush veil = new SolidBrush(Color.FromArgb(0x5C, 0x0B, 0x0B, 0x0E)))
                            g.FillRectangle(veil, 0, 0, vs.Width, vs.Height);
                    }
                    small.Dispose();

                    Bitmap old = _composited;
                    _composited = composited;
                    _screen = vs;
                    if (old != null) old.Dispose();
                }
            }
            catch
            {
                // Захват экрана могут запретить (RDP, защищённый контент) — тогда просто
                // рисуем сплошную тёмную подложку.
                _composited = null;
            }
        }

        /// <summary>Рисует размытый фон под окном, расположенным в screenBounds.</summary>
        public static void Paint(Graphics g, Rectangle clientRect, Rectangle screenBounds)
        {
            if (_composited == null)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xFF, 0x14, 0x14, 0x17)))
                    g.FillRectangle(b, clientRect);
                return;
            }

            // Копирование один-в-один: размеры источника и приёмника совпадают,
            // GDI+ идёт быстрым путём без интерполяции.
            int sx = screenBounds.X - _screen.X;
            int sy = screenBounds.Y - _screen.Y;

            if (sx < 0 || sy < 0 ||
                sx + clientRect.Width > _composited.Width ||
                sy + clientRect.Height > _composited.Height)
            {
                // Окно частично за пределами снимка (экран отключили, разрешение сменилось).
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xFF, 0x14, 0x14, 0x17)))
                    g.FillRectangle(b, clientRect);

                sx = Math.Max(0, Math.Min(sx, _composited.Width - 1));
                sy = Math.Max(0, Math.Min(sy, _composited.Height - 1));
                int w = Math.Min(clientRect.Width, _composited.Width - sx);
                int h = Math.Min(clientRect.Height, _composited.Height - sy);
                if (w <= 0 || h <= 0) return;
                g.DrawImage(_composited, new Rectangle(clientRect.X, clientRect.Y, w, h),
                            sx, sy, w, h, GraphicsUnit.Pixel);
                return;
            }

            g.DrawImage(_composited, clientRect,
                        sx, sy, clientRect.Width, clientRect.Height, GraphicsUnit.Pixel);
        }

        // Разделимый box-blur по 32bpp-битмапу.
        static void BoxBlur(Bitmap bmp, int radius)
        {
            int w = bmp.Width, h = bmp.Height;
            if (w < 2 || h < 2) return;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                         ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = bd.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                byte[] tmp = new byte[buf.Length];

                BlurPass(buf, tmp, w, h, stride, radius, true);
                BlurPass(tmp, buf, w, h, stride, radius, false);

                System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
            }
            finally { bmp.UnlockBits(bd); }
        }

        static void BlurPass(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horizontal)
        {
            int len = horizontal ? w : h;
            int outer = horizontal ? h : w;
            int stepIn = horizontal ? 4 : stride;
            int stepOut = horizontal ? stride : 4;

            for (int o = 0; o < outer; o++)
            {
                int baseOff = o * stepOut;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (int i = 0; i <= r && i < len; i++) { sum += src[baseOff + i * stepIn + c]; count++; }

                    for (int i = 0; i < len; i++)
                    {
                        dst[baseOff + i * stepIn + c] = (byte)(sum / count);

                        int add = i + r + 1;
                        int rem = i - r;
                        if (add < len) { sum += src[baseOff + add * stepIn + c]; count++; }
                        if (rem >= 0) { sum -= src[baseOff + rem * stepIn + c]; count--; }
                    }
                }
            }
        }
    }
}
