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
    /// Starts the program, waits for it to finish reading the plugin database and parsing a
    /// set, and captures its window to a PNG. It exists for exactly one reason: so a window can
    /// be looked at without sitting at the machine. A built exe that "compiles without errors"
    /// still says nothing about what got drawn.
    ///
    /// Build: see tools\build-rescue-test.cmd.
    /// </summary>
    internal static class Shot
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        // Clicking an element of a window without a real mouse: WindowFromPoint finds the child
        // control under the point, and the messages go straight to it. Touching SetCursorPos is
        // not an option — the cursor belongs to somebody else, and the click would land on
        // whichever window is on top rather than the one being checked.
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
        const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;

        /// <summary>
        /// The window draws itself into the given context. Capturing through CopyFromScreen
        /// will not do: Windows does not let a background process bring someone else's window
        /// to the front, and whatever was on top ends up in the frame instead. Flag 2 is
        /// PW_RENDERFULLCONTENT; without it windows with hardware composition come out blank.
        /// </summary>
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// A modal dialog is a separate top-level window and does not land in the parent's
        /// frame. We recognise it by the same sign a person sees it by: while the dialog is
        /// open, the owner window is disabled and the dialog itself is enabled.
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

        /// <summary>A click at a point in the window's client coordinates.</summary>
        static void Click(IntPtr window, int x, int y) { Click(window, x, y, false); }

        static void Click(IntPtr window, int x, int y, bool right)
        {
            POINT screen = new POINT(x, y);
            ClientToScreen(window, ref screen);

            IntPtr target = WindowFromPoint(screen);
            if (target == IntPtr.Zero) target = window;

            POINT local = screen;
            ScreenToClient(target, ref local);
            IntPtr lp = (IntPtr)((local.Y << 16) | (local.X & 0xFFFF));

            // The pause between press and release is mandatory: WinForms counts a click as a
            // pair of messages spread out in time, while a down+up stuck together in one queue
            // is handled by some controls as "the mouse was jerked", with no Click.
            PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(60);
            PostMessage(target, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), lp);
            Thread.Sleep(120);
            PostMessage(target, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, lp);

            Console.WriteLine((right ? "right-clicked " : "clicked ") + x + "," + y + " -> hwnd " + target.ToInt64()
                            + (target == window ? " (the window itself, not a control)" : " (child control)"));
        }

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Shot.exe <exe> <out.png> [--size W,H] [--click X,Y] [--rclick X,Y] [args...]");
                return 2;
            }

            try { SetProcessDPIAware(); } catch { }

            string exe = args[0];
            string png = args[1];
            string rest = "";
            List<Point> clicks = new List<Point>();
            List<bool> rightClick = new List<bool>();
            Size size = Size.Empty;

            for (int i = 2; i < args.Length; i++)
            {
                // --click X,Y — click a point in the window's client coordinates before
                // capturing
                if ((args[i] == "--click" || args[i] == "--rclick") && i + 1 < args.Length)
                {
                    bool right = args[i] == "--rclick";
                    string[] xy = args[++i].Split(',');
                    clicks.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1])));
                    rightClick.Add(right);
                    continue;
                }
                // --size W,H - resize the window before capturing, in physical pixels. A
                // list draws as many rows as fit and clips the last one; the frame looks
                // right only at a height that lands in the gap between two rows.
                if (args[i] == "--size" && i + 1 < args.Length)
                {
                    string[] wh = args[++i].Split(',');
                    size = new Size(int.Parse(wh[0]), int.Parse(wh[1]));
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

                // The plugin database and the set are read in the background — a shot taken
                // before that would show empty panels and "reading...".
                Thread.Sleep(6000);

                ShowWindow(hwnd, 5);
                Thread.Sleep(800);

                if (!size.IsEmpty)
                {
                    RECT cur;
                    GetWindowRect(hwnd, out cur);
                    MoveWindow(hwnd, cur.Left, cur.Top, size.Width, size.Height, true);
                    Thread.Sleep(900);      // relayout, then the lists settle
                }

                for (int i = 0; i < clicks.Count; i++)
                {
                    Click(hwnd, clicks[i].X, clicks[i].Y, rightClick[i]);
                    Thread.Sleep(900);      // the window gets time to rebuild the list
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
