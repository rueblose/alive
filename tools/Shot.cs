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
        const uint WM_LBUTTONDBLCLK = 0x0203;

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
        static void Click(IntPtr window, int x, int y) { Click(window, x, y, Kind.Left); }

        /// <summary>
        /// Double: the four messages Windows really sends for a quick double tap — DOWN, UP,
        /// DBLCLK, UP. The second press arrives as its own message, not as a second DOWN, and
        /// a control that miscounts it can only be caught by sending the genuine sequence.
        /// </summary>
        enum Kind { Left, Right, Double, Text }

        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wp, string lp);
        const uint WM_SETTEXT = 0x000C;

        /// <summary>
        /// Type into the window's text field without a keyboard: the first EDIT child gets the
        /// text by WM_SETTEXT, which a WinForms TextBox answers with TextChanged — the search in
        /// the catalog rebuilds exactly as it does under typing.
        /// </summary>
        static void SetText(IntPtr window, string text)
        {
            IntPtr edit = IntPtr.Zero;
            EnumChildWindows(window, delegate(IntPtr h, IntPtr param)
            {
                System.Text.StringBuilder name = new System.Text.StringBuilder(256);
                GetClassName(h, name, name.Capacity);
                if (name.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) < 0) return true;
                edit = h;
                return false;
            }, IntPtr.Zero);
            if (edit == IntPtr.Zero) { Console.WriteLine("no text field to type into"); return; }
            SendMessage(edit, WM_SETTEXT, IntPtr.Zero, text);
            Console.WriteLine("typed \"" + text + "\"");
        }

        static void Click(IntPtr window, int x, int y, Kind kind)
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
            bool right = kind == Kind.Right;
            PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(60);
            PostMessage(target, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), lp);
            Thread.Sleep(120);
            PostMessage(target, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, lp);
            if (kind == Kind.Double)
            {
                Thread.Sleep(60);
                PostMessage(target, WM_LBUTTONDBLCLK, (IntPtr)1, lp);
                Thread.Sleep(60);
                PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lp);
            }

            Console.WriteLine(kind.ToString().ToLowerInvariant() + "-clicked " + x + "," + y + " -> hwnd " + target.ToInt64()
                            + (target == window ? " (the window itself, not a control)" : " (child control)"));
        }

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Shot.exe <exe> <out.png> [--wait S] [--size W,H] [--click X,Y] [--rclick X,Y] [--dbl X,Y] [--settext TEXT] [args...]");
                return 2;
            }

            try { SetProcessDPIAware(); } catch { }

            string exe = args[0];
            string png = args[1];
            string rest = "";
            List<Point> clicks = new List<Point>();
            List<Kind> kinds = new List<Kind>();
            List<string> texts = new List<string>();
            Size size = Size.Empty;
            int settle = 6000;

            for (int i = 2; i < args.Length; i++)
            {
                // --click X,Y — click a point in the window's client coordinates before
                // capturing
                if ((args[i] == "--click" || args[i] == "--rclick" || args[i] == "--dbl")
                    && i + 1 < args.Length)
                {
                    Kind kind = args[i] == "--rclick" ? Kind.Right
                              : args[i] == "--dbl" ? Kind.Double : Kind.Left;
                    string[] xy = args[++i].Split(',');
                    clicks.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1])));
                    kinds.Add(kind);
                    texts.Add(null);
                    continue;
                }
                // --settext TEXT - put TEXT into the window's text field, in order with the clicks
                if (args[i] == "--settext" && i + 1 < args.Length)
                {
                    clicks.Add(Point.Empty);
                    kinds.Add(Kind.Text);
                    texts.Add(args[++i]);
                    continue;
                }
                // --size W,H - resize the window before capturing, in physical pixels. A
                // list draws as many rows as fit and clips the last one; the frame looks
                // right only at a height that lands in the gap between two rows.
                // --wait SECONDS - how long to let the program get itself together before
                // the shot. Six is enough to read a plugin database and a set; a full scan of
                // a real library, or anything that only happens after it, needs more.
                if (args[i] == "--wait" && i + 1 < args.Length)
                {
                    settle = (int)(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) * 1000);
                    continue;
                }
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
                Thread.Sleep(settle);

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
                    if (kinds[i] == Kind.Text) SetText(hwnd, texts[i]);
                    else Click(hwnd, clicks[i].X, clicks[i].Y, kinds[i]);
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
