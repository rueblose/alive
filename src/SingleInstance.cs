using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// One Alive at a time.
    ///
    /// A second copy is not a second window but a second owner of %APPDATA%\Alive. Settings,
    /// tags, pinned projects and the catalog cache are whole files, rewritten in one go —
    /// whoever saves last wins, and a tag added in one copy disappears the moment the other
    /// saves over it. Nothing warns anybody: both windows go on showing what they had in
    /// memory.
    ///
    /// So the second launch does what every other program does. It hands over whatever it was
    /// started with, brings the copy already running to the front, and leaves.
    ///
    /// The scope is the logon session (Local\): the settings folder belongs to a user, and
    /// two sessions of one user at once is a thing that happens over remote desktop and
    /// nowhere else.
    /// </summary>
    public static class SingleInstance
    {
        const string MutexName = @"Local\Alive.SingleInstance";

        /// <summary>
        /// The tag on our WM_COPYDATA. The message is a public door — anybody may knock on our
        /// window with one — so we read only what is marked as ours: 'ALIV'.
        /// </summary>
        const int Tag = 0x414C4956;

        const int WM_COPYDATA = 0x004A;

        [StructLayout(LayoutKind.Sequential)]
        struct CopyData
        {
            public IntPtr Kind;
            public int Size;
            public IntPtr Data;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

        const int SW_RESTORE = 9;

        // Held for the life of the process: a Mutex collected by the garbage collector
        // releases the name, and the next launch would think the place is free.
        static Mutex _held;

        /// <summary>
        /// True — we are the first copy and may carry on. False — somebody is already running,
        /// and the caller's business is to hand over and quit.
        /// </summary>
        public static bool Claim()
        {
            try
            {
                bool mine;
                _held = new Mutex(true, MutexName, out mine);
                if (!mine) { _held.Close(); _held = null; }
                return mine;
            }
            catch
            {
                // A mutex we are not allowed to open belongs to somebody else's session. Better
                // to start a second window than to refuse to start at all.
                return true;
            }
        }

        /// <summary>
        /// Give the copy already running what we were started with and bring it forward. An
        /// empty command line is handed over too — the message is what makes the other window
        /// come up, and "started it again" means "show it to me".
        /// </summary>
        public static void HandOver(string[] args)
        {
            IntPtr target = FindWindow();
            if (target == IntPtr.Zero) return;

            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);

            Send(target, args == null ? "" : string.Join("\n", args));
        }

        /// <summary>
        /// The main window of the copy already running. It may not exist yet — the other copy
        /// could be a second old — so we wait a little rather than dropping the path on the
        /// floor.
        /// </summary>
        static IntPtr FindWindow()
        {
            string me = null;
            try { me = Process.GetCurrentProcess().MainModule.FileName; }
            catch { }
            int mine = Process.GetCurrentProcess().Id;

            for (int attempt = 0; attempt < 30; attempt++)
            {
                foreach (Process p in Process.GetProcessesByName("Alive"))
                {
                    try
                    {
                        if (p.Id == mine) continue;
                        // The same name is not enough: somebody else's Alive.exe from another
                        // folder is another program as far as we are concerned.
                        if (me != null && !string.Equals(p.MainModule.FileName, me,
                                                        StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            AllowSetForegroundWindow(p.Id);
                            return p.MainWindowHandle;
                        }
                    }
                    catch { }
                }
                Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }

        static void Send(IntPtr target, string payload)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(payload + "\0");
            IntPtr text = Marshal.AllocHGlobal(bytes.Length);
            IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CopyData)));
            try
            {
                Marshal.Copy(bytes, 0, text, bytes.Length);

                CopyData cd;
                cd.Kind = (IntPtr)Tag;
                cd.Size = bytes.Length;
                cd.Data = text;
                Marshal.StructureToPtr(cd, block, false);

                // SendMessage rather than Post: WM_COPYDATA lends the other process our memory
                // for the length of the call, and freeing it before the call returns would hand
                // it a dangling pointer.
                SendMessage(target, WM_COPYDATA, IntPtr.Zero, block);
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(block);
                Marshal.FreeHGlobal(text);
            }
        }

        /// <summary>
        /// What a second copy has just handed us, split into paths. Null — the message is not
        /// ours and the window should go on handling it as usual. An empty array — ours, with
        /// nothing in it: somebody simply started the program again.
        /// </summary>
        public static string[] Received(ref System.Windows.Forms.Message m)
        {
            if (m.Msg != WM_COPYDATA) return null;
            try
            {
                CopyData cd = (CopyData)Marshal.PtrToStructure(m.LParam, typeof(CopyData));
                if (cd.Kind.ToInt64() != Tag) return null;

                m.Result = (IntPtr)1;
                if (cd.Size <= 0) return new string[0];

                string s = Marshal.PtrToStringUni(cd.Data, cd.Size / 2);
                s = s == null ? "" : s.TrimEnd('\0');
                return s.Length == 0
                     ? new string[0]
                     : s.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return null; }
        }
    }
}
