using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AbletonManager
{
    /// <summary>
    /// A log of one run: %APPDATA%\Alive\alive.log.
    ///
    /// The program is portable and reaches people as a single exe, while everything interesting
    /// about it depends on someone else's machine: which Live is installed, where it writes its
    /// settings, whether it even has a plugin database. Every such slip used to drown in a
    /// catch { } and looked the same from outside — "it does not scan anything" — with nothing
    /// to go on. So we write a short log: a snapshot of the environment at startup, the result
    /// of reading the plugin database, and every exception caught. There is one file and it is
    /// overwritten on each run — what is needed is exactly the run that broke.
    /// </summary>
    public static class Diag
    {
        static readonly object Gate = new object();
        static bool _open;

        public static string LogPath { get { return Path.Combine(Settings.Dir, "alive.log"); } }

        public static void Start()
        {
            lock (Gate)
            {
                try
                {
                    if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                    File.WriteAllText(LogPath, "", new UTF8Encoding(false));
                    _open = true;
                }
                catch { _open = false; }
            }
            Block(Snapshot());
        }

        /// <summary>An event line — with a timestamp, so the order is visible.</summary>
        public static void Line(string text)
        {
            Block(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);
        }

        public static void Fail(string where, Exception ex)
        {
            Line(where + " FAILED: " + (ex == null ? "unknown error" : ex.ToString()));
        }

        static void Block(string text)
        {
            if (!_open) return;
            lock (Gate)
            {
                try { File.AppendAllText(LogPath, text + "\r\n", new UTF8Encoding(false)); }
                catch { }
            }
        }

        // ------------------------------------------------------------------ environment

        /// <summary>Everything worth knowing about someone else's machine, in one
        /// piece.</summary>
        public static string Snapshot()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.AppendLine("Alive " + Application.ProductVersion + "   "
                            + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("exe        " + Application.ExecutablePath);
                sb.AppendLine("Windows    " + Environment.OSVersion.Version
                            + (Environment.Is64BitOperatingSystem ? " x64" : " x86")
                            + (Environment.Is64BitProcess ? ", 64-bit process" : ", 32-bit process"));
                sb.AppendLine("CLR        " + Environment.Version
                            + "   .NET Framework " + FrameworkVersion());
                sb.AppendLine("Documents  " + Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                sb.Append(Ableton());
            }
            catch (Exception ex) { sb.AppendLine("snapshot failed: " + ex); }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The runtime version we were actually started under. Environment.Version is the same
        /// across the whole 4.x branch (4.0.30319), while the difference between 4.6 and 4.8
        /// matters to us — and the exact value lives only in the registry.
        /// </summary>
        static string FrameworkVersion()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k == null) return "v4 Full not registered";
                    object ver = k.GetValue("Version");
                    object rel = k.GetValue("Release");
                    return (ver == null ? "?" : ver.ToString())
                         + (rel == null ? "" : " (release " + rel + ")");
                }
            }
            catch (Exception ex) { return "unknown: " + ex.Message; }
        }

        /// <summary>
        /// Where Live and its plugin database are on this machine. This is exactly where things
        /// break most often: the database is a file Live writes itself, and before its first
        /// run — or, on older versions, ever — it simply does not exist.
        /// </summary>
        public static string Ableton()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                string install = LiveEnvironment.FindInstallDir();
                sb.AppendLine("install    " + (install.Length > 0 ? install : "not found"));

                string exe = LiveEnvironment.FindExecutable();
                sb.AppendLine("Live.exe   " + (exe.Length > 0 ? exe : "not found"));

                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
                sb.AppendLine("prefs      " + root + (Directory.Exists(root) ? "" : "   MISSING"));
                if (!Directory.Exists(root)) return sb.ToString();

                foreach (string d in Directory.GetDirectories(root))
                {
                    string prefs = Path.Combine(d, "Preferences");
                    sb.AppendLine("  " + Path.GetFileName(d)
                                + "   " + FileNote(Path.Combine(prefs, "PluginScanDb.txt"))
                                + " | " + FileNote(Path.Combine(prefs, "PluginScanner.txt"))
                                + " | " + FileNote(Path.Combine(prefs, "Library.cfg")));
                }
            }
            catch (Exception ex) { sb.AppendLine("ableton snapshot failed: " + ex.Message); }
            return sb.ToString();
        }

        static string FileNote(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return name + ": no";
                return name + ": " + fi.Length + " B, "
                     + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { return name + ": ?"; }
        }
    }
}
