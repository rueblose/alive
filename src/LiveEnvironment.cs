using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Microsoft.Win32;

namespace AbletonManager
{
    /// <summary>
    /// Где на ЭТОЙ машине лежат корни, от которых Live отсчитывает ссылки.
    /// В самих сетах абсолютные пути часто устаревшие: они указывают на прошлую версию
    /// Live, на другой диск или вообще в чужой профиль, если проект приехал из
    /// коллаборации. Поэтому единственный надёжный источник — локальная конфигурация.
    /// </summary>
    public sealed class LiveEnvironment
    {
        public string UserLibrary = "";
        public string Builtin = "";
        public string CoreLibrary = "";
        public string InstallDir = "";

        /// <summary>Имя пака -> папка пака, из Library.cfg.</summary>
        public readonly Dictionary<string, string> Packs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static LiveEnvironment Detect()
        {
            LiveEnvironment e = new LiveEnvironment();
            e.InstallDir = FindInstall();
            if (e.InstallDir.Length > 0)
            {
                e.Builtin = Path.Combine(e.InstallDir, @"Resources\Builtin");
                e.CoreLibrary = Path.Combine(e.InstallDir, @"Resources\Core Library");
            }

            e.UserLibrary = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                @"Ableton\User Library");

            foreach (string cfg in LibraryConfigs())
                e.ReadLibraryConfig(cfg);

            if (e.CoreLibrary.Length > 0 && !e.Packs.ContainsKey("Core Library"))
                e.Packs["Core Library"] = e.CoreLibrary;
            return e;
        }

        /// <summary>
        /// Путь к exe самой Live — тот, что запускается двойным щелчком по .als. Берём
        /// его из файловой ассоциации, а не угадываем по InstallDir: так это ровно та
        /// команда, которой Windows сама открыла бы сет, и она переживает любые
        /// изменения структуры папок между версиями и редакциями (Suite/Standard/Intro).
        /// </summary>
        public static string FindExecutable()
        {
            string exe = FromAssociation();
            if (exe.Length > 0 && File.Exists(exe)) return exe;

            // Ассоциации нет (Live ставили, но .als открывают чем-то другим) — ищем
            // сам исполняемый файл рядом с найденной установкой.
            string install = FindInstall();
            if (install.Length == 0) return "";
            string program = Path.Combine(install, "Program");
            if (!Directory.Exists(program)) return "";
            foreach (string f in Directory.GetFiles(program, "Ableton Live*.exe"))
                return f;
            return "";
        }

        static string FromAssociation()
        {
            try
            {
                string progId = ReadDefault(Registry.ClassesRoot, @".als");
                if (progId.Length == 0) return "";
                string cmd = ReadDefault(Registry.ClassesRoot, progId + @"\shell\open\command");
                return ParseCommand(cmd);
            }
            catch { return ""; }
        }

        static string ReadDefault(RegistryKey root, string subKey)
        {
            using (RegistryKey k = root.OpenSubKey(subKey))
            {
                object v = k != null ? k.GetValue("") : null;
                return v as string ?? "";
            }
        }

        /// <summary>«"C:\...\Live.exe" "%1"» → «C:\...\Live.exe» — берёт только путь,
        /// в кавычках или без, и отбрасывает аргумент подстановки файла.</summary>
        static string ParseCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int close = cmd.IndexOf('"', 1);
                return close > 0 ? cmd.Substring(1, close - 1) : "";
            }
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        /// <summary>
        /// Папка установки самой свежей Live. ProgramData спрашиваем у Windows, а не
        /// пишем «C:\ProgramData» строкой: система стоит не у всех на C:, и на чужой
        /// машине такой путь молча не находился — вместе с ним пропадали Core Library,
        /// паки и кнопка «новый проект».
        /// </summary>
        public static string FindInstallDir() { return FindInstall(); }

        static string FindInstall()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ableton");
            if (!Directory.Exists(root)) return "";
            string best = ""; long bestScore = -1;
            foreach (string d in Directory.GetDirectories(root, "Live *"))
            {
                if (!Directory.Exists(Path.Combine(d, "Resources"))) continue;
                long score = Version(Path.GetFileName(d));
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return best;
        }

        static long Version(string name)
        {
            long v = 0; int part = 0;
            foreach (char c in name)
            {
                if (char.IsDigit(c)) part = part * 10 + (c - '0');
                else if (c == '.') { v = v * 1000 + part; part = 0; }
            }
            return v * 1000 + part;
        }

        /// <summary>Library.cfg лежит в папке настроек каждой установленной версии Live.</summary>
        static IEnumerable<string> LibraryConfigs()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
            if (!Directory.Exists(root)) yield break;
            foreach (string d in Directory.GetDirectories(root, "Live *"))
            {
                string cfg = Path.Combine(d, @"Preferences\Library.cfg");
                if (File.Exists(cfg)) yield return cfg;
            }
        }

        void ReadLibraryConfig(string path)
        {
            try
            {
                XmlReaderSettings s = new XmlReaderSettings();
                s.DtdProcessing = DtdProcessing.Ignore;
                s.CheckCharacters = false;
                using (XmlReader r = XmlReader.Create(path, s))
                {
                    string projectName = null, projectPath = null;
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element) continue;

                        if (r.Name == "LibrarySliceInfo")
                        {
                            string p = r.GetAttribute("Path");
                            string n = r.GetAttribute("DisplayName");
                            if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(n) && Directory.Exists(p))
                                Packs[n] = p;
                        }
                        else if (r.Name == "ProjectName") projectName = r.GetAttribute("Value");
                        else if (r.Name == "ProjectPath") projectPath = r.GetAttribute("Value");
                    }

                    if (!string.IsNullOrEmpty(projectName) && !string.IsNullOrEmpty(projectPath))
                    {
                        string ul = Path.Combine(projectPath.Replace('/', '\\'), projectName);
                        if (Directory.Exists(ul)) UserLibrary = ul;
                    }
                }
            }
            catch { /* конфиг битый — обойдёмся тем, что нашли по умолчанию */ }
        }
    }
}
