using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AbletonManager
{
    /// <summary>
    /// Журнал одного запуска: %APPDATA%\Alive\alive.log.
    ///
    /// Программа портативная и уезжает к людям одним exe-файлом, а всё интересное в ней
    /// зависит от чужой машины: какая стоит Live, куда она пишет настройки, есть ли у
    /// неё вообще база плагинов. Любая такая осечка тонула в catch { } и снаружи
    /// выглядела одинаково — «ничего не сканит», и разбираться было не с чем. Поэтому
    /// пишем короткий журнал: снимок окружения при старте, результат чтения базы
    /// плагинов и всякое пойманное исключение. Файл один и переписывается при каждом
    /// запуске — нужен ровно тот прогон, который сломался.
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

        /// <summary>Строка события — со временем, чтобы было видно порядок.</summary>
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

        // ------------------------------------------------------------------ окружение

        /// <summary>Всё, что нужно знать про чужую машину, одним куском.</summary>
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
        /// Версия рантайма, под которым нас реально запустили. Environment.Version у
        /// всей ветки 4.x одинаковый (4.0.30319), а разница между 4.6 и 4.8 нам важна —
        /// точное значение лежит только в реестре.
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
        /// Где на этой машине Live и её база плагинов. Именно здесь ломается чаще всего:
        /// база — это файл, который пишет сама Live, и до её первого запуска (а у старых
        /// версий и вовсе никогда) его просто нет.
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
