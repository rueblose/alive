using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Microsoft.Win32;

namespace AbletonManager
{
    /// <summary>
    /// Where THIS machine keeps the roots Live measures its references from. Absolute paths
    /// inside the sets are often stale: they point at a previous Live version, at another
    /// drive, or into somebody else's profile entirely if the project came from a
    /// collaboration. So the only reliable source is the local configuration.
    /// </summary>
    public sealed class LiveEnvironment
    {
        public string UserLibrary = "";
        public string Builtin = "";
        public string CoreLibrary = "";
        public string InstallDir = "";

        /// <summary>Pack name -> pack folder, from Library.cfg.</summary>
        public readonly Dictionary<string, string> Packs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The Places of Live's browser — its name for the folder and the folder itself — over
        /// every installed Live, without repeats. They mix sample folders with project ones:
        /// the Samples tab offers them, and the person picks.
        /// </summary>
        public readonly List<KeyValuePair<string, string>> Places = new List<KeyValuePair<string, string>>();

        /// <summary>Where Live installs packs (Preferences → Library); empty if it was never set.</summary>
        public string PacksFolder = "";

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
        /// The path to Live's own exe — the one a double click on an .als starts. We take it
        /// from the file association rather than guessing from InstallDir: that way it is
        /// exactly the command Windows itself would open the set with, and it survives any
        /// folder-structure change between versions and editions (Suite/Standard/Intro).
        /// </summary>
        public static string FindExecutable()
        {
            string exe = FromAssociation();
            if (exe.Length > 0 && File.Exists(exe)) return exe;

            // No association (Live is installed, but .als files open with something else) —
            // look for the executable itself next to the installation we found.
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

        /// <summary>'"C:\...\Live.exe" "%1"' → 'C:\...\Live.exe' — takes only the path, quoted
        /// or not, and drops the file substitution argument.</summary>
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
        /// The installation folder of the newest Live. We ask Windows for ProgramData rather
        /// than writing "C:\ProgramData" as a string: not everyone has the system on C:, and on
        /// someone else's machine such a path silently failed to resolve — taking the Core
        /// Library, the packs and the "new project" button down with it.
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

        /// <summary>Library.cfg lives in the settings folder of every installed Live
        /// version.</summary>
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

        internal void ReadLibraryConfig(string path)
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
                        else if (r.Name == "UserFolderInfo")
                        {
                            string p = r.GetAttribute("Path");
                            string n = r.GetAttribute("DisplayName");
                            if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                                AddPlace(string.IsNullOrEmpty(n) ? Path.GetFileName(p.TrimEnd('\\')) : n, p);
                        }
                        else if (r.Name == "PreferredFactoryPacksInstallationPath")
                        {
                            string p = r.GetAttribute("Value");
                            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) PacksFolder = p;
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
            catch { /* broken config - we make do with what the defaults found */ }
        }

        /// <summary>A Place seen in several versions keeps the name the last read one gives it —
        /// configs are read oldest first, so the newest Live has the last word.</summary>
        void AddPlace(string name, string path)
        {
            for (int i = 0; i < Places.Count; i++)
                if (string.Equals(Places[i].Value.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    Places[i] = new KeyValuePair<string, string>(name, Places[i].Value);
                    return;
                }
            Places.Add(new KeyValuePair<string, string>(name, path));
        }
    }
}
