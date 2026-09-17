using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace AliveTools
{
    /// <summary>
    /// Запускает прототип, ждёт, пока он дочитает базу плагинов и разберёт сет, и
    /// снимает его окно в PNG. Нужен ровно затем, чтобы окно можно было увидеть, не
    /// сидя за машиной: собранный exe, который «компилируется без ошибок», ещё ничего
    /// не говорит о том, что нарисовалось.
    ///
    /// Сборка: см. proto\test\build-test.cmd.
    /// </summary>
    internal static class Shot
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Клик по элементу окна без настоящей мыши: WindowFromPoint находит тот дочерний
        // control, что лежит под точкой, а сообщения уходят прямо ему. Дёргать
        // SetCursorPos нельзя — курсор чужой, и клик достался бы тому окну, что сейчас
        // сверху, а не тому, которое проверяем.
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

        delegate bool EnumProc(IntPtr hWnd, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;

        /// <summary>
        /// Окно рисует себя само в переданный контекст. Через CopyFromScreen снимать
        /// нельзя: вывести чужое окно на передний план фоновому процессу Windows не
        /// даёт, и в кадр попадает то, что лежало сверху. Флаг 2 —
        /// PW_RENDERFULLCONTENT, без него окна с аппаратной композицией выходят пустыми.
        /// </summary>
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Модальный диалог — отдельное верхнеуровневое окно, в кадр родителя он не
        /// попадает. Узнаём его по тому же признаку, по которому его видит человек:
        /// пока диалог открыт, окно-владелец выключено, а он сам включён.
        /// </summary>
        static IntPtr ModalOf(Process p, IntPtr owner)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr param)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != (uint)p.Id) return true;
                if (h == owner || !IsWindowVisible(h) || !IsWindowEnabled(h)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Щелчок по точке в клиентских координатах окна.</summary>
        static void Click(IntPtr window, int x, int y)
        {
            POINT screen = new POINT(x, y);
            ClientToScreen(window, ref screen);

            IntPtr target = WindowFromPoint(screen);
            if (target == IntPtr.Zero) target = window;

            POINT local = screen;
            ScreenToClient(target, ref local);
            IntPtr lp = (IntPtr)((local.Y << 16) | (local.X & 0xFFFF));

            // Пауза между нажатием и отпусканием обязательна: WinForms считает щелчком
            // пару сообщений, разнесённую во времени, а слипшиеся в одну очередь
            // down+up часть контролов обрабатывает как «мышь дёрнули», без Click.
            PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(60);
            PostMessage(target, WM_LBUTTONDOWN, (IntPtr)1, lp);
            Thread.Sleep(120);
            PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lp);

            Console.WriteLine("clicked " + x + "," + y + " -> hwnd " + target.ToInt64()
                            + (target == window ? " (the window itself, not a control)" : " (child control)"));
        }

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Shot.exe <exe> <out.png> [args...]");
                return 2;
            }

            try { SetProcessDPIAware(); } catch { }

            string exe = args[0];
            string png = args[1];
            string rest = "";
            List<Point> clicks = new List<Point>();

            for (int i = 2; i < args.Length; i++)
            {
                // --click X,Y — щёлкнуть по точке в клиентских координатах окна перед съёмкой
                if (args[i] == "--click" && i + 1 < args.Length)
                {
                    string[] xy = args[++i].Split(',');
                    clicks.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1])));
                    continue;
                }
                rest += "\"" + args[i] + "\" ";
            }

            ProcessStartInfo psi = new ProcessStartInfo(exe, rest.Trim());
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);
            if (p == null) { Console.WriteLine("could not start"); return 1; }

            try
            {
                p.WaitForInputIdle(10000);

                IntPtr hwnd = IntPtr.Zero;
                for (int i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
                {
                    Thread.Sleep(250);
                    p.Refresh();
                    hwnd = p.MainWindowHandle;
                }
                if (hwnd == IntPtr.Zero) { Console.WriteLine("no window"); return 1; }

                // База плагинов и разбор сета читаются в фоне — снимок до этого показал
                // бы пустые панели и «reading...».
                Thread.Sleep(6000);

                ShowWindow(hwnd, 5);
                Thread.Sleep(800);

                foreach (Point c in clicks)
                {
                    Click(hwnd, c.X, c.Y);
                    Thread.Sleep(900);      // окно успевает перестроить список
                }

                if (clicks.Count > 0)
                {
                    IntPtr modal = ModalOf(p, hwnd);
                    if (modal != IntPtr.Zero)
                    {
                        Console.WriteLine("modal dialog opened, capturing it instead");
                        hwnd = modal;
                    }
                }

                RECT r;
                if (!GetWindowRect(hwnd, out r)) { Console.WriteLine("no rect"); return 1; }
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) { Console.WriteLine("empty rect"); return 1; }

                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try { ok = PrintWindow(hwnd, hdc, 2); }
                        finally { g.ReleaseHdc(hdc); }
                        if (!ok) { Console.WriteLine("PrintWindow failed"); return 1; }
                    }
                    bmp.Save(png, ImageFormat.Png);
                }
                Console.WriteLine("saved " + png + "  " + w + "x" + h);
                return 0;
            }
            finally
            {
                try
                {
                    if (!p.HasExited) { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); }
                }
                catch { }
            }
        }
    }
}
