using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AbletonManager
{
    /// <summary>
    /// Walking the folder tree in search of sets.
    ///
    /// Why not Directory.GetFiles, as it used to be: in .NET Framework a path longer than 260
    /// characters is an exception, and it takes down the whole folder along with everything
    /// beneath it. In a library of packs and presets there are plenty of such places (measured
    /// on D:\Music: 14 folders), and they used to drop out of the scan silently — together with
    /// every set inside. From outside that looked like "the program does not see some of the
    /// projects", with not a trace of it in the interface. FindFirstFile with the \\?\ prefix
    /// lifts the limit.
    ///
    /// It is also one pass instead of two (GetFiles + GetDirectories) and without building an
    /// array of strings per folder — and under a root like D:\Music there are twenty thousand
    /// folders.
    /// </summary>
    internal static class FolderScan
    {
        public sealed class Result
        {
            public int Dirs;         // folders visited
            public int Files;        // files found
            public int Unreadable;   // folders that would not open
            public bool RootFailed;  // the starting folder itself would not open
        }

        /// <summary>Return true to stop the walk.</summary>
        public delegate bool CancelCheck();

        /// <summary>
        /// Every file with the given extension in the tree. Backup folders are skipped by a
        /// flag — Live breeds those sets itself, and the catalog has no use for them.
        /// </summary>
        public static Result Find(string root, string extension, bool includeBackups,
                                  Action<string> onFile, CancelCheck cancel)
        {
            Result res = new Result();
            if (string.IsNullOrEmpty(root)) return res;
            try { root = System.IO.Path.GetFullPath(root); }
            catch { res.RootFailed = true; return res; }

            // A stack rather than recursion: a tree can be unexpectedly deep, and there is no
            // excusing a stack overflow in the middle of a scan.
            Stack<string> todo = new Stack<string>();
            todo.Push(root);
            bool atRoot = true;

            while (todo.Count > 0)
            {
                if (cancel != null && cancel()) break;

                string dir = todo.Pop();
                res.Dirs++;

                WIN32_FIND_DATA fd;
                IntPtr h = FindFirstFileW(SearchPattern(dir), out fd);
                if (h == InvalidHandle)
                {
                    res.Unreadable++;
                    if (atRoot) res.RootFailed = true;
                    atRoot = false;
                    continue;
                }
                atRoot = false;

                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;

                        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            // Symlinks and junctions are not followed: through them the walk
                            // goes either into a loop or a second time into the very same
                            // folders.
                            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            if (!includeBackups &&
                                string.Equals(name, "Backup", StringComparison.OrdinalIgnoreCase)) continue;
                            todo.Push(Combine(dir, name));
                        }
                        else if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        {
                            // A probe copy from the rescue helper is an .als inside a project
                            // folder, and without this line it would turn up in the catalog
                            // next to the real set. Such a file lives for seconds, but that is
                            // enough to be caught by a scan.
                            if (RescueProbe.IsProbe(name)) continue;

                            res.Files++;
                            if (onFile != null) onFile(Combine(dir, name));
                        }
                    }
                    while (FindNextFileW(h, out fd));
                }
                finally { FindClose(h); }
            }
            return res;
        }

        /// <summary>How much a folder weighs with everything inside it.</summary>
        public struct Weight
        {
            public long Bytes;
            public int Files;

            /// <summary>
            /// When a project was saved. On every save Live puts a copy of the old file into
            /// Backup and writes the moment of saving into its name: "angelcore [2026-05-22
            /// 012035].als". There is no other history of the work on disk — the .als itself
            /// remembers only the last time.
            ///
            /// We take the bracket rather than the file time: a copy's time is the moment of
            /// the PREVIOUS save, since what gets copied is the content from before. Verified:
            /// "try1 riddik [2026-08-28 140615].als" sits with a time of 14:00.
            ///
            /// null rather than an empty list: most folders have no Backup, and there is no
            /// reason to make an object for each.
            /// </summary>
            public List<Save> Saves;
        }

        /// <summary>One save: when, and of which set — the copy carries the name within
        /// itself.</summary>
        public struct Save
        {
            public DateTime When;
            public string Set;      // "angelcore" out of "angelcore [2026-05-22 012035].als"
        }

        /// <summary>
        /// The weight of the whole folder. We count everything, Samples and Backup included:
        /// the question "how much does the project take" is about space on disk, and they take
        /// it too.
        ///
        /// The size comes straight from the directory entry FindNextFile returned anyway —
        /// there is no need to open each file for its length.
        /// </summary>
        public static Weight Weigh(string root, CancelCheck cancel)
        {
            Weight w = new Weight();
            if (string.IsNullOrEmpty(root)) return w;
            try { root = System.IO.Path.GetFullPath(root); }
            catch { return w; }

            Stack<string> todo = new Stack<string>();
            todo.Push(root);

            while (todo.Count > 0)
            {
                if (cancel != null && cancel()) break;
                string dir = todo.Pop();

                // Once per folder rather than once per file: inside Backup there can be
                // hundreds of them.
                bool backups = dir.EndsWith(@"\Backup", StringComparison.OrdinalIgnoreCase);

                WIN32_FIND_DATA fd;
                IntPtr h = FindFirstFileW(SearchPattern(dir), out fd);
                if (h == InvalidHandle) continue;
                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;

                        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            todo.Push(Combine(dir, name));
                        }
                        else
                        {
                            w.Bytes += ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                            w.Files++;

                            Save saved;
                            if (backups && TryBackupStamp(name, out saved))
                            {
                                if (w.Saves == null) w.Saves = new List<Save>();
                                w.Saves.Add(saved);
                            }
                        }
                    }
                    while (FindNextFileW(h, out fd));
                }
                finally { FindClose(h); }
            }
            return w;
        }

        /// <summary>
        /// The moment of a save from a copy's name: "anything [2026-05-22 012035].als". We
        /// parse by position rather than with ParseExact — there are thousands of calls, and
        /// the format is written by Live itself and depends neither on the system language nor
        /// on its date settings.
        /// </summary>
        internal static bool TryBackupStamp(string name, out Save save)
        {
            save = default(Save);
            if (name == null || !name.EndsWith("].als", StringComparison.OrdinalIgnoreCase)) return false;

            int i = name.LastIndexOf('[');
            // "[" + "2026-05-22 012035" (17) + "]" + ".als" — exactly 23 characters from the
            // end.
            if (i < 0 || name.Length - i != 23) return false;
            if (name[i + 5] != '-' || name[i + 8] != '-' || name[i + 11] != ' ') return false;

            int y, mo, d, h, mi, se;
            if (!Num(name, i + 1, 4, out y) || !Num(name, i + 6, 2, out mo) || !Num(name, i + 9, 2, out d) ||
                !Num(name, i + 12, 2, out h) || !Num(name, i + 14, 2, out mi) || !Num(name, i + 16, 2, out se))
                return false;

            if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || se > 59) return false;
            try { save.When = new DateTime(y, mo, d, h, mi, se, DateTimeKind.Local); }
            catch { return false; }        // the 31st of February out of someone else's file name

            save.Set = name.Substring(0, i).TrimEnd();
            return true;
        }

        static bool Num(string s, int at, int len, out int value)
        {
            value = 0;
            for (int i = at; i < at + len; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        internal static string Combine(string dir, string name)
        {
            return dir.Length > 0 && dir[dir.Length - 1] == '\\' ? dir + name : dir + "\\" + name;
        }

        /// <summary>One entry of a folder, with the size straight from the directory entry.</summary>
        internal delegate void EntryFound(string name, bool isDir, long size);

        /// <summary>
        /// The entries of one folder — the sample walk makes its own decisions about what to
        /// descend into, so it takes the tree level by level rather than whole. Junctions and
        /// symlinks are not handed out at all, as in Find. False: the folder would not open.
        /// </summary>
        internal static bool List(string dir, EntryFound found)
        {
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileW(SearchPattern(dir), out fd);
            if (h == InvalidHandle) return false;
            try
            {
                do
                {
                    string name = fd.cFileName;
                    if (name == "." || name == "..") continue;
                    bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    if (isDir && (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                    found(name, isDir, isDir ? 0L : ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow);
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
            return true;
        }

        /// <summary>
        /// The path in the form FindFirstFile understands without a length limit. The \\?\
        /// prefix disables path normalisation, so only a full path with ordinary backslashes
        /// will do — which is exactly what GetFullPath above produces.
        /// </summary>
        static string SearchPattern(string dir)
        {
            string p = dir;
            if (!p.StartsWith(@"\\?\", StringComparison.Ordinal))
                p = p.StartsWith(@"\\", StringComparison.Ordinal)
                  ? @"\\?\UNC\" + p.Substring(2)      // a network path \\server\share
                  : @"\\?\" + p;
            if (p.Length > 0 && p[p.Length - 1] != '\\') p += "\\";
            return p + "*";
        }

        // ------------------------------------------------------------------ win32

        const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // Every field is 4 bytes: with FILETIME as a long the structure drifts out of alignment
        // on x64 and the file name is read out of garbage.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public int dwFileAttributes;
            public uint ftCreationLow, ftCreationHigh;
            public uint ftAccessLow, ftAccessHigh;
            public uint ftWriteLow, ftWriteHigh;
            public uint nFileSizeHigh, nFileSizeLow;
            public uint dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string pattern, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr handle);
    }
}
