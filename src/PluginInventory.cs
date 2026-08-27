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
        public string Path = "";       // файл .vst3 / .dll
        public PluginKind Kind;
        public bool Enabled = true;
        public bool FileMissing;       // Live его знает, но файла на диске уже нет

        public string Format
        {
            get { return Kind == PluginKind.Vst3 ? "VST3" : Kind == PluginKind.Vst2 ? "VST2" : "AU"; }
        }
    }

    public enum MatchKind
    {
        Exact,        // тот же самый плагин: совпал идентификатор
        OtherFormat,  // такой плагин есть, но другого формата — сет всё равно откроется с дырой
        Missing       // ничего похожего не установлено
    }

    public struct PluginMatch
    {
        public readonly InstalledPlugin Plugin;
        public readonly MatchKind Kind;
        public PluginMatch(InstalledPlugin p, MatchKind k) { Plugin = p; Kind = k; }
        public bool Found { get { return Kind != MatchKind.Missing; } }
    }

    /// <summary>
    /// Что за плагины стоят на этой машине — по данным самой Live.
    ///
    /// Live держит их в %APPDATA%\Ableton\Live &lt;версия&gt;\Preferences\PluginScanDb.txt и
    /// переписывает файл при каждом запуске. Внутри три таблицы: домены (папки поиска),
    /// модули (файл на диске) и плагины (имя, вендор, версия, идентификатор устройства).
    /// Сканировать папки самим смысла нет: там пришлось бы разбирать бинарники VST, а
    /// это ровно та работа, которую Live уже сделала — и сделала с теми настройками
    /// папок, которые стоят в самой Live.
    ///
    /// Опознаём плагин по идентификатору, а не по имени:
    ///     device:vst3:audiofx:ed57bd72-5c60-467e-a64d-d2f400758b6f  -> vst3:ed57bd72-…
    ///     device:vst:instr:2017543218?n=Addictive%20Drums%202       -> vst2:2017543218
    /// В сете лежат ровно эти же числа (см. PluginRef.FinishUid).
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
        /// Запасной путь: сет мог быть сохранён с VST2-версией плагина, а стоит теперь
        /// VST3 — идентификаторы разные, плагин по сути тот же. Заодно ловим случай,
        /// когда в сете имя с вендором («FabFilter Pro-L 2»), а Live знает его коротко
        /// («Pro-L 2» вендора FabFilter).
        /// </summary>
        public InstalledPlugin ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            InstalledPlugin p;
            return _byName.TryGetValue(Normalize(name), out p) ? p : null;
        }

        /// <summary>Насколько уверенно плагин из сета опознан среди установленных.</summary>
        public PluginMatch Match(string uid, string name)
        {
            InstalledPlugin exact = ByUid(uid);
            if (exact != null) return new PluginMatch(exact, MatchKind.Exact);
            InstalledPlugin soft = ByName(name);
            if (soft != null) return new PluginMatch(soft, MatchKind.OtherFormat);
            return new PluginMatch(null, MatchKind.Missing);
        }

        /// <summary>Имена вида «Serum_x64» и «Serum (64 Bit)» — это один и тот же плагин.</summary>
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

        // ------------------------------------------------------------------ загрузка

        /// <summary>Из каких установок Live собран список — для окна настроек.</summary>
        public readonly List<string> Sources = new List<string>();

        /// <summary>
        /// Все установки Live, что есть на машине: имена папок, свежая первой. Список
        /// провизорный — папка попадает в него по одному наличию Preferences, без
        /// разбора содержимого, и может включать установки без единого плагина (пустая
        /// бета рядом со стабильной версией, куда плагины ещё не долетели).
        ///
        /// Настоящую фильтрацию — «а есть ли тут хоть один плагин НА САМОМ ДЕЛЕ» —
        /// дёшево не сделать: журнал сканера накопительный и помнит в том числе
        /// снесённые плагины, отличить их от установленных можно только проверкой файла
        /// на диске для каждого — то есть тем же полным разбором, что делает Load. Кто
        /// хочет точный список (SettingsDialog, для выпадающего меню) — считает его в
        /// фоне через Load и берёт из PluginInventory.Sources.
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
        /// Что стоит на этой машине. По умолчанию — по данным самой Live, сразу по всем
        /// её установкам; по настройке — обходом папок (см. LoadFromFolders).
        ///
        /// Почему по ВСЕМ установкам, а не по самой свежей. Раньше бралась первая
        /// установка, где плагины вообще нашлись, а установки сортируются по времени
        /// последнего скана — и этого достаточно, чтобы получить список из одного
        /// плагина. Реальный случай на этой машине: рядом со стабильной 12.4.3 стоит
        /// бета 12.4.5, у неё нет PluginScanDb.txt вовсе, зато есть свежий журнал
        /// сканера — от одного-единственного плагина, который в ней и разрабатывают.
        /// Бета оказывалась первой, давала «нашлось, 1 штука», и на этом поиск
        /// останавливался: программа сообщала, что установлен ровно один плагин, при
        /// 717 в базе соседней версии.
        ///
        /// Плагины стоят в системе, а не «в версии Live»: каждая установка — это лишь
        /// её снимок того, что она успела просканировать. Поэтому снимки складываются,
        /// от свежего к старому, и по каждому идентификатору побеждает самый свежий.
        /// Из НЕсвежих снимков выбрасывается всё, чей файл с диска уже исчез, — иначе
        /// база трёхлетней давности воскресила бы давно снесённое.
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

                // Ручной выбор установки в настройках: тогда смотрим только её.
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

                    // Первый непустой снимок — главный: он и отвечает за «Live знает
                    // плагин, а файла уже нет». У остальных такие записи просто старые.
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
                    // Папки Live есть, а плагинов в них нет: либо Live ещё ни разу не
                    // сканировала плагины, либо это версия старше 11 — в ней
                    // PluginScanDb.txt не пишется вовсе, и брать данные неоткуда.
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

        // ------------------------------------------------------------ обход папок

        /// <summary>
        /// Обойти папки плагинов самим — те же три переключателя, что в Live
        /// (Preferences → Plug-Ins). Нужно, когда база Live пуста или ей не веришь.
        ///
        /// Честно про потолок: так видно файлы, а не плагины. Один VST2-шелл вроде
        /// WaveShell — это сотни плагинов внутри одного .dll, и здесь он останется
        /// одной строкой; у Live их 717 против двух десятков бандлов на диске. Поэтому
        /// режим не по умолчанию.
        ///
        /// У VST3 всё же получается точно: рядом с бинарником лежит moduleinfo.json, и
        /// в нём тот самый CID, который Live пишет в device-class-id, — идентификаторы
        /// совпадают с точностью до расстановки дефисов, так что сеты сходятся по ним,
        /// а не по имени. У VST2 идентификатор лежит внутри самого .dll, и лезть туда
        /// ради него не стоит: такие записи опознаются по имени.
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
        /// Бандл .vst3 — это папка, но у части плагинов до сих пор просто файл, поэтому
        /// ловим оба. Внутрь бандла обход не заходит: там лежит второй такой же .vst3,
        /// уже настоящий бинарник, и он приехал бы вторым плагином с тем же именем.
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

        /// <summary>Папки дерева — своим обходом, чтобы не спотыкаться о длинные пути.</summary>
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
                    // Внутрь бандла не спускаемся — см. ScanVst3.
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
        /// moduleinfo.json без разбора JSON целиком: нужен только список классов, а он
        /// устроен предсказуемо — «CID», за ним в том же объекте «Category», «Name» и
        /// «Vendor». Режем текст по «"CID"» и смотрим каждый кусок до следующего;
        /// берём только классы, которые сам плагин, а не его редактор.
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

        /// <summary>Первая строка в кавычках начиная с from — значение после двоеточия.</summary>
        static string FirstString(string s, int from)
        {
            int open = s.IndexOf('"', from);
            if (open < 0) return null;
            int close = s.IndexOf('"', open + 1);
            return close < 0 ? null : s.Substring(open + 1, close - open - 1);
        }

        /// <summary>
        /// Папки версий (%APPDATA%\Ableton\Live 12.1.5) — от той, что писала настройки
        /// последней, к самой старой. Маска «Live *» на всякий случай не единственная:
        /// у бет и локализованных сборок папка называется иначе, а Preferences внутри
        /// всё та же, так что если по маске не нашлось — смотрим на все подпапки.
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

        /// <summary>Прочитать базу и журнал сканера одной установки Live.</summary>
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

            // PluginScanDb.txt — снимок, который Live переписывает не после каждого
            // сканирования: наблюдался случай, когда плагин был найден сканером на
            // полчаса позже последней записи базы и в неё не попал вовсе. Поэтому
            // дочитываем журнал сканера — он пишется на каждый скан и содержит те же
            // device-class-id, что и база. Читаем его и когда базы нет совсем: у части
            // установок она не появляется, а журнал есть — это лучше, чем пустой список.
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
        /// Журнал сканера: блок «VST3: found: Имя» и дальше поля с отступом. Журнал
        /// накопительный и помнит в том числе давно снесённые плагины, поэтому берём
        /// только те записи, файл которых сейчас есть на диске, — иначе воскресим
        /// удалённое. Уже известное из базы не трогаем: там есть флаг «включен».
        /// </summary>
        void ParseScannerLog(string[] lines)
        {
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstalledPlugin p in All) known.Add(p.Uid);

            // Журнал накопительный, один плагин встречается в нём десятки раз — берём
            // последнюю запись: вендор и версия могли поменяться при обновлении.
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

                // Поля блока идут с отступом; строка без отступа — конец блока.
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

        // Журнал сканера накопительный: один плагин встречается в нём десятки раз, и
        // каждый раз пришлось бы спрашивать диск про один и тот же файл. Кеш живёт
        // ровно одну загрузку базы (см. Load) — иначе поставленный при работающей
        // программе плагин не появился бы и после «пересканировать».
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

            // Журнал помнит и снесённое — доверяем только тому, что сейчас лежит на диске.
            // У VST3 «файл» это папка-бандл, поэтому проверяем оба варианта.
            if (p.Path.Length == 0 || !FastPathExists(p.Path)) return;

            found[p.Uid] = p;      // последняя запись побеждает
        }

        void Parse(string[] lines)
        {
            // ModuleId -> путь к файлу, из таблицы модулей
            Dictionary<string, string> modulePath = new Dictionary<string, string>();
            Dictionary<string, bool> moduleOk = new Dictionary<string, bool>();

            int section = 0;   // 1 — модули, 2 — плагины
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("Logging plugins information about plugin modules start")) { section = 1; continue; }
                if (line.StartsWith("Logging plugins information about all plugins start")) { section = 2; continue; }
                if (line.StartsWith("Logging plugins information about") && line.Contains(" end ")) { section = 0; continue; }
                if (section == 0) continue;
                if (line.StartsWith("ModuleId,") || line.StartsWith("PluginId,")) continue;   // заголовки

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

                    // У VST3 «файл» сплошь и рядом оказывается папкой-бандлом
                    // (C:\...\VST3\Serum2.vst3 — это каталог), так что одного
                    // File.Exists мало: он честно вернёт false для установленного плагина.
                    p.FileMissing = p.Path.Length > 0
                                 && !File.Exists(p.Path) && !Directory.Exists(p.Path);
                    All.Add(p);
                }
            }
        }

        /// <summary>«device:vst3:audiofx:&lt;guid&gt;» / «device:vst:instr:&lt;число&gt;?n=…».</summary>
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
            // Один и тот же плагин попадает в базу несколько раз, если Live видит
            // несколько его файлов (например, Debug и Release одной сборки). Считаем
            // такие за один, оставляя тот файл, который на диске есть.
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

        /// <summary>Строка вида «1,"C:\...\x.vst3",3,1,ok,"..."» — кавычки снимаем.</summary>
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
