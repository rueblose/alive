using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonManager
{
    public sealed class InstalledPlugin
    {
        public string Uid = "";        // «vst3:ed57bd72-…» / «vst2:2017543218»
        public string Name = "";
        public string Vendor = "";
        public string Version = "";
        public string Category = "";
        public string Path = "";       // the .vst3 / .dll file
        public PluginKind Kind;
        public bool Enabled = true;
        public bool FileMissing;       // Live knows it, but the file is no longer on disk

        public string Format
        {
            get { return Kind == PluginKind.Vst3 ? "VST3" : Kind == PluginKind.Vst2 ? "VST2" : "AU"; }
        }
    }

    public enum MatchKind
    {
        Exact,        // the very same plugin: the identifier matched
        OtherFormat,  // such a plugin exists but in another format — the set will still open, with a hole
        Missing       // nothing resembling it is installed
    }

    public struct PluginMatch
    {
        public readonly InstalledPlugin Plugin;
        public readonly MatchKind Kind;
        public PluginMatch(InstalledPlugin p, MatchKind k) { Plugin = p; Kind = k; }
        public bool Found { get { return Kind != MatchKind.Missing; } }
    }

    /// <summary>
    /// Which plugins are installed on this machine — according to Live itself.
    ///
    /// Live keeps them in %APPDATA%\Ableton\Live &lt;version&gt;\Preferences\PluginScanDb.txt
    /// and rewrites the file on every start. Inside are three tables: domains (search folders),
    /// modules (a file on disk) and plugins (name, vendor, version, device identifier).
    /// Scanning the folders ourselves makes no sense: that would mean parsing VST binaries, and
    /// that is precisely the work Live has already done — and done with the folder settings
    /// that are set in Live itself.
    ///
    /// We identify a plugin by its identifier rather than by name:
    ///     device:vst3:audiofx:ed57bd72-5c60-467e-a64d-d2f400758b6f  -> vst3:ed57bd72-…
    ///     device:vst:instr:2017543218?n=Addictive%20Drums%202       -> vst2:2017543218
    /// A set holds exactly these same numbers (see PluginRef.FinishUid).
    /// </summary>
    public sealed class PluginInventory
    {
        public readonly List<InstalledPlugin> All = new List<InstalledPlugin>();
        public string SourcePath = "";
        public string LiveVersion = "";
        public DateTime Scanned;
        public string Error;

        readonly Dictionary<string, InstalledPlugin> _byUid =
            new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, InstalledPlugin> _byName =
            new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty { get { return All.Count == 0; } }

        public InstalledPlugin ByUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            InstalledPlugin p;
            return _byUid.TryGetValue(uid, out p) ? p : null;
        }

        /// <summary>
        /// A fallback: a set may have been saved with the VST2 version of a plugin while the
        /// VST3 one is now installed — different identifiers, essentially the same plugin. It
        /// also catches the case where the set has the name with the vendor ("FabFilter Pro-L
        /// 2") while Live knows it in short ("Pro-L 2" by FabFilter).
        /// </summary>
        public InstalledPlugin ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            InstalledPlugin p;
            return _byName.TryGetValue(Normalize(name), out p) ? p : null;
        }

        /// <summary>How confidently a plugin from a set was identified among the installed
        /// ones.</summary>
        public PluginMatch Match(string uid, string name)
        {
            InstalledPlugin exact = ByUid(uid);
            if (exact != null) return new PluginMatch(exact, MatchKind.Exact);
            InstalledPlugin soft = ByName(name);
            if (soft != null) return new PluginMatch(soft, MatchKind.OtherFormat);
            return new PluginMatch(null, MatchKind.Missing);
        }

        /// <summary>Names like "Serum_x64" and "Serum (64 Bit)" are one and the same
        /// plugin.</summary>
        internal static string Normalize(string name)
        {
            if (name == null) return "";
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            string s = sb.ToString();
            foreach (string tail in new string[] { "x64", "64bit", "64", "x86", "win", "vst3", "vst" })
                if (s.Length > tail.Length + 2 && s.EndsWith(tail)) s = s.Substring(0, s.Length - tail.Length);
            return s;
        }

        // ------------------------------------------------------------------ loading

        /// <summary>Which Live installs the list was assembled from — for the settings
        /// window.</summary>
        public readonly List<string> Sources = new List<string>();

        /// <summary>
        /// Every Live install there is on the machine: folder names, newest first. The list is
        /// provisional — a folder gets into it merely by having a Preferences, with no parsing
        /// of the contents, and it may include installs without a single plugin (an empty beta
        /// next to the stable version that the plugins have not reached yet).
        ///
        /// Real filtering — "is there ACTUALLY at least one plugin here" — cannot be done
        /// cheaply: the scanner log is cumulative and remembers plugins that have been removed,
        /// and telling those from installed ones is only possible by checking each one's file
        /// on disk — that is, by the same full parse Load does. Whoever wants the exact list
        /// (SettingsDialog, for its dropdown) counts it in the background through Load and
        /// takes it from PluginInventory.Sources.
        /// </summary>
        public static List<string> Installs()
        {
            List<string> names = new List<string>();
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
                if (!Directory.Exists(root)) return names;
                foreach (string d in VersionFolders(root)) names.Add(Path.GetFileName(d));
            }
            catch { }
            return names;
        }

        public static PluginInventory Load() { return Load(Settings.Load()); }

        /// <summary>
        /// What is installed on this machine. By default, according to Live itself and across
        /// all its installs at once; by setting, by walking the folders (see LoadFromFolders).
        ///
        /// Why across ALL installs rather than the newest. It used to take the first install
        /// where plugins were found at all, and installs are sorted by the time of their last
        /// scan — which is enough to end up with a list of one plugin. A real case on this
        /// machine: next to the stable 12.4.3 stands a beta 12.4.5, which has no
        /// PluginScanDb.txt at all but does have a fresh scanner log — of the single plugin
        /// being developed in it. The beta came first, gave "found, 1 of them", and the search
        /// stopped there: the program reported exactly one plugin installed against 717 in the
        /// neighbouring version's database.
        ///
        /// Plugins are installed in the system rather than "in a Live version": each install is
        /// merely its own snapshot of what it managed to scan. So the snapshots are added
        /// together, newest to oldest, and for each identifier the freshest wins. From the
        /// NON-fresh snapshots everything whose file has already gone from disk is thrown out —
        /// otherwise a three-year-old database would resurrect what was removed long ago.
        /// </summary>
        public static PluginInventory Load(Settings settings)
        {
            PluginInventory inv = new PluginInventory();
            ResetPathCache();

            if (settings != null && settings.PluginsFromFolders)
            {
                inv.LoadFromFolders(settings);
                inv.Reindex();
                Diag.Line("plugins: " + inv.All.Count + " from folders");
                return inv;
            }

            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
                if (!Directory.Exists(root))
                {
                    inv.Error = "Ableton settings folder not found — run Ableton Live once";
                    Diag.Line("plugins: no " + root);
                    return inv;
                }

                List<string> versions = VersionFolders(root);

                // A manual choice of install in the settings: we then look at that one only.
                string only = settings != null ? settings.PluginSource : "";
                if (!string.IsNullOrEmpty(only))
                    versions.RemoveAll(delegate(string d)
                    {
                        return !string.Equals(Path.GetFileName(d), only, StringComparison.OrdinalIgnoreCase);
                    });

                if (versions.Count == 0)
                {
                    inv.Error = "No Live settings found in " + root;
                    Diag.Line("plugins: nothing that looks like Live inside " + root);
                    return inv;
                }

                List<string> tried = new List<string>();
                foreach (string dir in versions)
                {
                    PluginInventory one = new PluginInventory();
                    one.LoadFrom(dir);
                    tried.Add(Path.GetFileName(dir) + " -> " + one.All.Count);
                    if (one.All.Count == 0) continue;

                    // The first non-empty snapshot is the principal one: it is what answers for
                    // "Live knows the plugin but the file is gone". In the others such records
                    // are simply old.
                    if (inv.All.Count > 0)
                        one.All.RemoveAll(delegate(InstalledPlugin p) { return p.FileMissing; });

                    if (inv.SourcePath.Length == 0)
                    {
                        inv.SourcePath = one.SourcePath;
                        inv.LiveVersion = one.LiveVersion;
                        inv.Scanned = one.Scanned;
                    }
                    inv.Sources.Add(Path.GetFileName(dir) + " · " + one.All.Count);
                    inv.All.AddRange(one.All);
                }

                if (inv.All.Count == 0)
                {
                    // The Live folders are there but there are no plugins in them: either Live
                    // has never scanned plugins, or this is a version older than 11 — in that
                    // one PluginScanDb.txt is not written at all, and there is nowhere to take
                    // the data from.
                    inv.Error = "Live has no plugin database yet - open Live, Preferences > Plug-Ins > Rescan";
                    Diag.Line("plugins: nothing found (" + string.Join(", ", tried.ToArray()) + ")");
                }
                else Diag.Line("plugins: " + inv.All.Count + " from " + string.Join(", ", inv.Sources.ToArray()));
            }
            catch (Exception ex)
            {
                inv.Error = ex.Message;
                Diag.Fail("plugins", ex);
            }

            inv.Reindex();
            return inv;
        }

        // ------------------------------------------------------- walking the folders

        /// <summary>
        /// Walk the plugin folders ourselves — the same three switches Live has (Preferences →
        /// Plug-Ins). Needed when Live's database is empty or not to be trusted.
        ///
        /// Honestly about the ceiling: this shows files rather than plugins. One VST2 shell
        /// such as WaveShell is hundreds of plugins inside a single .dll, and here it stays one
        /// row; Live has 717 of them against a couple of dozen bundles on disk. Which is why
        /// the mode is not the default.
        ///
        /// For VST3 it does work out exactly: next to the binary lies moduleinfo.json, and in
        /// it that very CID Live writes into device-class-id — the identifiers match down to
        /// the placement of the hyphens, so sets tally by them rather than by name. A VST2's
        /// identifier lies inside the .dll itself, and climbing in there for it is not worth
        /// it: such records are identified by name.
        /// </summary>
        void LoadFromFolders(Settings s)
        {
            SourcePath = "";
            LiveVersion = "";
            Scanned = DateTime.UtcNow;

            if (s.Vst3SystemOn)
                foreach (string dir in SystemVst3Dirs())
                    ScanVst3(dir);

            if (s.Vst3CustomOn && s.Vst3CustomPath.Length > 0) ScanVst3(s.Vst3CustomPath);
            if (s.Vst2CustomOn && s.Vst2CustomPath.Length > 0) ScanVst2(s.Vst2CustomPath);

            if (All.Count == 0)
                Error = "Nothing found in the plug-in folders — check the paths in Settings";
        }

        static List<string> SystemVst3Dirs()
        {
            List<string> dirs = new List<string>();
            foreach (string var in new string[] { "CommonProgramFiles", "CommonProgramFiles(x86)" })
            {
                string common = Environment.GetEnvironmentVariable(var);
                if (string.IsNullOrEmpty(common)) continue;
                string d = Path.Combine(common, "VST3");
                if (Directory.Exists(d) && !dirs.Contains(d)) dirs.Add(d);
            }
            return dirs;
        }

        /// <summary>
        /// A .vst3 bundle is a folder, but for some plugins it is still simply a file, so we
        /// catch both. The walk does not go inside a bundle: a second .vst3 lies in there, this
        /// one a real binary, and it would arrive as a second plugin of the same name.
        /// </summary>
        void ScanVst3(string root)
        {
            FolderScan.Find(root, ".vst3", true, delegate(string file)
            {
                if (!InsideBundle(root, file)) AddBundle(file);
            }, null);

            try
            {
                foreach (string d in AllDirs(root))
                    if (d.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) AddBundle(d);
            }
            catch (Exception ex) { Diag.Fail("plugins: " + root, ex); }
        }

        void ScanVst2(string root)
        {
            FolderScan.Find(root, ".dll", true, delegate(string file)
            {
                InstalledPlugin p = new InstalledPlugin();
                p.Kind = PluginKind.Vst2;
                p.Name = Path.GetFileNameWithoutExtension(file);
                p.Path = file;
                p.Uid = "file:" + Normalize(p.Name);
                All.Add(p);
            }, null);
        }

        /// <summary>The folders of a tree — by a walk of our own, so as not to stumble over
        /// long paths.</summary>
        static List<string> AllDirs(string root)
        {
            List<string> res = new List<string>();
            Stack<string> todo = new Stack<string>();
            todo.Push(root);
            while (todo.Count > 0)
            {
                string dir = todo.Pop();
                string[] kids;
                try { kids = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string k in kids)
                {
                    res.Add(k);
                    // We do not descend into a bundle — see ScanVst3.
                    if (!k.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) todo.Push(k);
                }
            }
            return res;
        }

        static bool InsideBundle(string root, string file)
        {
            string dir = Path.GetDirectoryName(file);
            while (dir != null && dir.Length > root.Length)
            {
                if (dir.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }

        void AddBundle(string bundlePath)
        {
            string info = Path.Combine(Path.Combine(Path.Combine(bundlePath, "Contents"), "Resources"),
                                       "moduleinfo.json");
            int before = All.Count;
            if (File.Exists(info))
            {
                try { ReadModuleInfo(File.ReadAllText(info), bundlePath); }
                catch (Exception ex) { Diag.Fail("plugins: " + info, ex); }
            }
            if (All.Count > before) return;

            InstalledPlugin p = new InstalledPlugin();
            p.Kind = PluginKind.Vst3;
            p.Name = Path.GetFileNameWithoutExtension(bundlePath);
            p.Path = bundlePath;
            p.Uid = "file:" + Normalize(p.Name);
            All.Add(p);
        }

        /// <summary>
        /// moduleinfo.json without parsing the whole JSON: only the class list is needed, and
        /// that is laid out predictably — "CID", followed in the same object by "Category",
        /// "Name" and "Vendor". We cut the text by '"CID"' and look at each piece up to the
        /// next; we take only the classes that are the plugin itself rather than its editor.
        /// </summary>
        void ReadModuleInfo(string json, string bundlePath)
        {
            string[] parts = json.Split(new string[] { "\"CID\"" }, StringSplitOptions.None);
            for (int i = 1; i < parts.Length; i++)
            {
                string chunk = parts[i];
                if (chunk.IndexOf("Audio Module Class", StringComparison.Ordinal) < 0) continue;

                string cid = FirstString(chunk, 0);
                if (cid == null || cid.Length != 32) continue;

                InstalledPlugin p = new InstalledPlugin();
                p.Kind = PluginKind.Vst3;
                p.Uid = "vst3:" + Dashed(cid);
                p.Name = Field(chunk, "\"Name\"") ?? Path.GetFileNameWithoutExtension(bundlePath);
                p.Vendor = Field(chunk, "\"Vendor\"") ?? "";
                p.Version = Field(chunk, "\"Version\"") ?? "";
                p.Path = bundlePath;
                All.Add(p);
            }
        }

        /// <summary>«56534558667350736572756D20320000» -> «56534558-6673-5073-6572-756d20320000».</summary>
        static string Dashed(string cid)
        {
            cid = cid.ToLowerInvariant();
            return cid.Substring(0, 8) + "-" + cid.Substring(8, 4) + "-" + cid.Substring(12, 4)
                 + "-" + cid.Substring(16, 4) + "-" + cid.Substring(20);
        }

        static string Field(string chunk, string key)
        {
            int at = chunk.IndexOf(key, StringComparison.Ordinal);
            return at < 0 ? null : FirstString(chunk, at + key.Length);
        }

        /// <summary>The first quoted string starting from `from` — the value after the
        /// colon.</summary>
        static string FirstString(string s, int from)
        {
            int open = s.IndexOf('"', from);
            if (open < 0) return null;
            int close = s.IndexOf('"', open + 1);
            return close < 0 ? null : s.Substring(open + 1, close - open - 1);
        }

        /// <summary>
        /// The version folders (%APPDATA%\Ableton\Live 12.1.5) — from the one that wrote its
        /// settings last to the oldest. The "Live *" mask is deliberately not the only one:
        /// betas and localised builds name the folder differently while the Preferences inside
        /// is the same, so if the mask finds nothing we look at every subfolder.
        /// </summary>
        static List<string> VersionFolders(string root)
        {
            List<string> all = new List<string>();
            foreach (string d in Directory.GetDirectories(root, "Live *"))
                if (Directory.Exists(Path.Combine(d, "Preferences"))) all.Add(d);
            if (all.Count == 0)
                foreach (string d in Directory.GetDirectories(root))
                    if (Directory.Exists(Path.Combine(d, "Preferences"))) all.Add(d);

            all.Sort(delegate (string a, string b) { return Touched(b).CompareTo(Touched(a)); });
            return all;
        }

        static DateTime Touched(string versionDir)
        {
            DateTime t = DateTime.MinValue;
            foreach (string name in new string[] { "PluginScanDb.txt", "PluginScanner.txt" })
            {
                try
                {
                    string f = Path.Combine(versionDir, Path.Combine("Preferences", name));
                    if (!File.Exists(f)) continue;
                    DateTime w = File.GetLastWriteTimeUtc(f);
                    if (w > t) t = w;
                }
                catch { }
            }
            return t;
        }

        /// <summary>Read the database and the scanner log of one Live install.</summary>
        void LoadFrom(string versionDir)
        {
            LiveVersion = Path.GetFileName(versionDir);
            string prefs = Path.Combine(versionDir, "Preferences");
            string db = Path.Combine(prefs, "PluginScanDb.txt");
            string log = Path.Combine(prefs, "PluginScanner.txt");

            try
            {
                if (File.Exists(db))
                {
                    SourcePath = db;
                    Scanned = File.GetLastWriteTimeUtc(db);
                    Parse(File.ReadAllLines(db));
                }
            }
            catch (Exception ex) { Diag.Fail("plugins: " + db, ex); }

            // PluginScanDb.txt is a snapshot Live does not rewrite after every scan: a case was
            // observed where a plugin was found by the scanner half an hour after the
            // database's last entry and never got into it. So we read on into the scanner log —
            // it is written on every scan and holds the same device-class-ids as the database.
            // We read it when there is no database at all too: for some installs it never
            // appears while the log does — which is better than an empty list.
            try
            {
                if (File.Exists(log))
                {
                    ParseScannerLog(File.ReadAllLines(log));
                    DateTime logTime = File.GetLastWriteTimeUtc(log);
                    if (logTime > Scanned) Scanned = logTime;
                    if (SourcePath.Length == 0) SourcePath = log;
                }
            }
            catch (Exception ex) { Diag.Fail("plugins: " + log, ex); }
        }

        /// <summary>
        /// The scanner log: a "VST3: found: Name" block followed by indented fields. The log is
        /// cumulative and remembers plugins removed long ago, so we take only the records whose
        /// file is on disk right now — otherwise we resurrect what was deleted. What is already
        /// known from the database we leave alone: there it carries an "enabled" flag.
        /// </summary>
        void ParseScannerLog(string[] lines)
        {
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstalledPlugin p in All) known.Add(p.Uid);

            // The log is cumulative and one plugin occurs in it dozens of times — we take the
            // last record: the vendor and the version may have changed on an update.
            Dictionary<string, InstalledPlugin> found =
                new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);

            InstalledPlugin cur = null;
            string curDev = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                int hit = line.IndexOf("info: VST3: found: ", StringComparison.Ordinal);
                PluginKind kind = PluginKind.Vst3;
                if (hit < 0)
                {
                    hit = line.IndexOf("info: VST2: found: ", StringComparison.Ordinal);
                    kind = PluginKind.Vst2;
                }

                if (hit >= 0)
                {
                    Flush(found, cur, curDev);
                    cur = new InstalledPlugin();
                    cur.Kind = kind;
                    cur.Name = line.Substring(hit + "info: VST3: found: ".Length).Trim();
                    curDev = null;
                    continue;
                }

                // The fields of a block are indented; a line with no indent is the end of the
                // block.
                if (cur == null) continue;
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
                {
                    Flush(found, cur, curDev);
                    cur = null; curDev = null;
                    continue;
                }

                string t = line.Trim();
                if (t.StartsWith("vendor: ")) cur.Vendor = t.Substring(8).Trim();
                else if (t.StartsWith("version: ")) cur.Version = t.Substring(9).Trim();
                else if (t.StartsWith("subCategories: ")) cur.Category = t.Substring(15).Trim();
                else if (t.StartsWith("device-class-id: ")) curDev = t.Substring(17).Trim();
                else if (t.StartsWith("path: "))
                    cur.Path = t.Substring(6).Trim().Trim('"').Replace('/', '\\');
            }
            Flush(found, cur, curDev);

            foreach (KeyValuePair<string, InstalledPlugin> kv in found)
            {
                if (known.Contains(kv.Key)) continue;
                All.Add(kv.Value);
            }
        }

        // The scanner log is cumulative: one plugin occurs in it dozens of times, and each time
        // the disk would have to be asked about the same file. The cache lives for exactly one
        // load of the database (see Load) — otherwise a plugin installed while the program was
        // running would not appear even after a "rescan".
        static readonly HashSet<string> _validPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _invalidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void ResetPathCache()
        {
            _validPaths.Clear();
            _invalidPaths.Clear();
        }

        static bool FastPathExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (_validPaths.Contains(path)) return true;
            if (_invalidPaths.Contains(path)) return false;

            bool exists = File.Exists(path) || Directory.Exists(path);
            if (exists) _validPaths.Add(path);
            else _invalidPaths.Add(path);
            return exists;
        }

        static void Flush(Dictionary<string, InstalledPlugin> found, InstalledPlugin p, string dev)
        {
            if (p == null || string.IsNullOrEmpty(dev) || p.Name.Length == 0) return;
            p.Uid = UidOf(dev, p.Kind);
            if (p.Uid.Length == 0) return;

            // The log remembers what has been removed too — we trust only what is on disk right
            // now. For VST3 the "file" is a bundle folder, so we check both variants.
            if (p.Path.Length == 0 || !FastPathExists(p.Path)) return;

            found[p.Uid] = p;      // the last record wins
        }

        void Parse(string[] lines)
        {
            // ModuleId -> file path, from the modules table
            Dictionary<string, string> modulePath = new Dictionary<string, string>();
            Dictionary<string, bool> moduleOk = new Dictionary<string, bool>();

            int section = 0;   // 1 — modules, 2 — plugins
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("Logging plugins information about plugin modules start")) { section = 1; continue; }
                if (line.StartsWith("Logging plugins information about all plugins start")) { section = 2; continue; }
                if (line.StartsWith("Logging plugins information about") && line.Contains(" end ")) { section = 0; continue; }
                if (section == 0) continue;
                if (line.StartsWith("ModuleId,") || line.StartsWith("PluginId,")) continue;   // headers

                List<string> f = SplitCsv(line);

                if (section == 1 && f.Count >= 5)
                {
                    modulePath[f[0]] = f[1];
                    moduleOk[f[0]] = f[4].Equals("ok", StringComparison.OrdinalIgnoreCase);
                }
                else if (section == 2 && f.Count >= 11)
                {
                    InstalledPlugin p = new InstalledPlugin();
                    p.Name = f[3];
                    p.Vendor = f[4];
                    p.Version = f[5];
                    p.Category = f[9];
                    p.Enabled = f[10] == "1";

                    string dev = f[2];
                    p.Kind = dev.StartsWith("device:vst3:") ? PluginKind.Vst3
                           : dev.StartsWith("device:vst:") ? PluginKind.Vst2
                           : dev.StartsWith("device:au") ? PluginKind.AudioUnit
                           : PluginKind.Vst3;
                    p.Uid = UidOf(dev, p.Kind);

                    string path;
                    if (modulePath.TryGetValue(f[1], out path)) p.Path = path;
                    bool ok;
                    if (moduleOk.TryGetValue(f[1], out ok) && !ok) continue;   // «not-a-plugin»

                    if (p.Uid.Length == 0 || p.Name.Length == 0) continue;

                    // For VST3 the "file" routinely turns out to be a bundle folder
                    // (C:\...\VST3\Serum2.vst3 is a directory), so File.Exists alone is not
                    // enough: it honestly returns false for an installed plugin.
                    p.FileMissing = p.Path.Length > 0
                                 && !File.Exists(p.Path) && !Directory.Exists(p.Path);
                    All.Add(p);
                }
            }
        }

        /// <summary>"device:vst3:audiofx:&lt;guid&gt;" /
        /// "device:vst:instr:&lt;number&gt;?n=…".</summary>
        static string UidOf(string dev, PluginKind kind)
        {
            if (string.IsNullOrEmpty(dev)) return "";
            int last = dev.LastIndexOf(':');
            if (last < 0 || last + 1 >= dev.Length) return "";
            string id = dev.Substring(last + 1);
            int q = id.IndexOf('?');
            if (q >= 0) id = id.Substring(0, q);
            if (id.Length == 0) return "";
            return (kind == PluginKind.Vst2 ? "vst2:" : kind == PluginKind.AudioUnit ? "au:" : "vst3:")
                   + id.ToLowerInvariant();
        }

        void Reindex()
        {
            // One and the same plugin gets into the database several times when Live sees
            // several of its files (a Debug and a Release of one build, for instance). We count
            // such as one, keeping the file that is on disk.
            Dictionary<string, InstalledPlugin> unique =
                new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
            List<InstalledPlugin> order = new List<InstalledPlugin>();
            foreach (InstalledPlugin p in All)
            {
                InstalledPlugin was;
                if (!unique.TryGetValue(p.Uid, out was)) { unique[p.Uid] = p; order.Add(p); continue; }
                if (was.FileMissing && !p.FileMissing)
                {
                    order[order.IndexOf(was)] = p;
                    unique[p.Uid] = p;
                }
            }
            All.Clear();
            All.AddRange(order);

            _byUid.Clear();
            _byName.Clear();
            foreach (InstalledPlugin p in All)
            {
                if (!_byUid.ContainsKey(p.Uid)) _byUid[p.Uid] = p;
                Index(Normalize(p.Name), p);
                Index(Normalize(p.Vendor + p.Name), p);
            }
        }

        void Index(string key, InstalledPlugin p)
        {
            if (key.Length > 1 && !_byName.ContainsKey(key)) _byName[key] = p;
        }

        /// <summary>A line of the form '1,"C:\...\x.vst3",3,1,ok,"..."' — the quotes are
        /// stripped.</summary>
        internal static List<string> SplitCsv(string line)
        {
            List<string> res = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { quoted = !quoted; continue; }
                if (c == ',' && !quoted) { res.Add(cur.ToString()); cur.Length = 0; continue; }
                cur.Append(c);
            }
            res.Add(cur.ToString());
            return res;
        }

        public string Describe()
        {
            if (Error != null) return Error;
            return All.Count + " plugins · " + LiveVersion + " · "
                 + Scanned.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
