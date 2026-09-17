using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AbletonManager
{
    public sealed class SetEntry
    {
        public string Path = "";
        public string Name = "";
        public string ProjectName = "";
        public DateTime Modified;
        public DateTime Created;
        public long Size;
        public bool IsBackup;

        public string Creator = "";
        public double Tempo;
        public string Key = "";      // «C Major»; пусто у версий Live без общей тональности
        public int ScaleRoot = -1;   // 0..11; по нему фильтруем — «C#» и «Db» это одна нота
        public int ScaleIndex = -1;
        public int Tracks;
        public int MissingFiles;
        public int TotalRefs;
        public string[] Plugins = new string[0];
        public string[] PluginVendors = new string[0];        // параллельно Plugins, "" если неизвестен
        public bool[] PluginVendorConfident = new bool[0];    // вендор из VST3/AU, а не из папки браузера
        public string[] PluginUids = new string[0];           // «vst3:…» / «vst2:…», параллельно Plugins
        public string Error = "";

        /// <summary>
        /// Сколько плагинов сета не установлено. Считается не при сканировании, а после —
        /// набор установленного меняется без правки самих сетов, кешировать его нельзя.
        /// </summary>
        public int MissingPlugins;

        /// <summary>
        /// Есть ли рядом с проектом что слушать. Не кешируется на диск: рендеры
        /// появляются без правки самого сета, и запомненный ответ протух бы первым же
        /// экспортом. Считается заново при каждом сканировании.
        /// </summary>
        public bool HasRenders;

        /// <summary>
        /// Имена файлов, которые реально можно предпрослушать (та же выборка, что и
        /// у RenderScan.Find — Samples и служебные папки Live не в счёт), — чтобы
        /// искать сет по названию рендера, а не только по имени сета. Не кешируется на
        /// диск по той же причине, что и HasRenders.
        /// </summary>
        public string[] RenderNames = new string[0];

        /// <summary>
        /// Вес всей папки проекта и сколько в ней файлов. Не кешируется по той же
        /// причине, что и HasRenders: записал сэмпл — папка потяжелела, а .als не
        /// изменился, и запомненное число врало бы.
        /// </summary>
        public long ProjectSize;
        public int ProjectFiles;

        /// <summary>
        /// Сколько ещё сетов той же папки спрятано под этой строкой. Считается при
        /// каждом заполнении списка — зависит от фильтров, а не от самого сета.
        /// </summary>
        public int CollapsedCount;

        /// <summary>Короткая версия вида «12.4.3» из «Ableton Live 12.4.3».</summary>
        public string ShortVersion
        {
            get
            {
                if (Creator.Length == 0) return "";
                int i = Creator.LastIndexOf(' ');
                return i >= 0 && i + 1 < Creator.Length ? Creator.Substring(i + 1) : Creator;
            }
        }

        public string Directory { get { return System.IO.Path.GetDirectoryName(Path); } }

        string _projectDir;

        /// <summary>
        /// Папка проекта — та самая «… Project», которую заводит Live, со всеми
        /// Samples внутри. Если сет лежит сам по себе, вне такой папки, проектом
        /// считаем его собственную папку. Считается один раз: Path не меняется, а
        /// спрашивают её и вес, и теги, и группировка.
        /// </summary>
        public string ProjectDir
        {
            get
            {
                if (_projectDir == null) _projectDir = ComputeProjectDir();
                return _projectDir;
            }
        }

        string ComputeProjectDir()
        {
            string own = System.IO.Path.GetDirectoryName(Path);
            try
            {
                System.IO.DirectoryInfo d = new System.IO.DirectoryInfo(own);
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase))
                        return d.FullName;
                    d = d.Parent;
                }
            }
            catch { }
            return own;
        }

        string _place;

        /// <summary>
        /// Полка, на которой лежит проект: имя папки, содержащей папку «* Project».
        /// Для «…\Series 2\somnitelno\X Project\X.als» это «somnitelno» — по нему видно,
        /// в какой коллекции сет, не читая весь путь.
        ///
        /// Считается один раз и запоминается: Path у записи не меняется никогда, а вот
        /// саму «полку» спрашивают в горячих местах — по разу на каждую ячейку колонки
        /// и ДВАЖДЫ на каждое сравнение при сортировке по ней. На тысяче сетов это
        /// десятки тысяч обходов дерева DirectoryInfo вверх, и всё ради одной строки,
        /// которая всё это время одна и та же.
        /// </summary>
        public string Place
        {
            get
            {
                if (_place == null) _place = ComputePlace();
                return _place;
            }
        }

        string ComputePlace()
        {
            try
            {
                System.IO.DirectoryInfo d =
                    new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(Path));
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase))
                        return d.Parent != null ? d.Parent.Name : "";
                    d = d.Parent;
                }
                // Сет не в папке «* Project» — берём родителя папки, где лежит .als.
                System.IO.DirectoryInfo dir =
                    new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(Path));
                return dir.Parent != null ? dir.Parent.Name : dir.Name;
            }
            catch { return ""; }
        }
    }

    public sealed class PluginStat
    {
        public string Name = "";
        public string Vendor = "";
        public bool VendorConfident;
        public int Sets;

        public string Uid = "";
        public MatchKind Match = MatchKind.Missing;
        public InstalledPlugin Installed;      // null, если такого плагина на машине нет

        public bool IsInstalled { get { return Match != MatchKind.Missing; } }
        public bool IsUnused { get { return Sets == 0; } }

        /// <summary>«VST3» / «VST2» — формат берём у установленного, иначе у идентификатора.</summary>
        public string Format
        {
            get
            {
                if (Installed != null && Match == MatchKind.Exact) return Installed.Format;
                if (Uid.StartsWith("vst3:")) return "VST3";
                if (Uid.StartsWith("vst2:")) return "VST2";
                if (Uid.StartsWith("au:")) return "AU";
                return Installed != null ? Installed.Format : "";
            }
        }

        /// <summary>Категория плагина из VST3/AU (например, Mastering, Reverb, Synth, Dynamics).</summary>
        public string FxType
        {
            get
            {
                if (Installed == null || string.IsNullOrEmpty(Installed.Category))
                    return "";

                string cat = Installed.Category.Trim();
                if (cat.Length == 0) return "";

                if (cat.Contains("|"))
                {
                    string[] parts = cat.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        string last = parts[parts.Length - 1].Trim();
                        if ((last.Equals("Fx", StringComparison.OrdinalIgnoreCase) || last.Equals("FX", StringComparison.OrdinalIgnoreCase) || last.Equals("Instrument", StringComparison.OrdinalIgnoreCase)) && parts.Length > 1)
                        {
                            last = parts[parts.Length - 2].Trim();
                        }
                        if (!last.Equals("Fx", StringComparison.OrdinalIgnoreCase) && !last.Equals("FX", StringComparison.OrdinalIgnoreCase))
                            return last;
                    }
                }

                if (cat.Equals("Fx", StringComparison.OrdinalIgnoreCase) || cat.Equals("FX", StringComparison.OrdinalIgnoreCase))
                    return "";

                return cat;
            }
        }
    }

    /// <summary>Сводка по плагинам библиотеки — то, что показывается карточками.</summary>
    public sealed class PluginHealth
    {
        public int Used;             // разных плагинов встречается в сетах
        public int Installed;        // из них установлено (точное совпадение)
        public int OtherFormat;      // есть, но другого формата
        public int Missing;          // не установлено вовсе
        public int InstalledTotal;   // сколько всего стоит на машине
        public int InstalledUnused;  // из них не встречается ни в одном сете
        public int FilesGone;        // Live их помнит, но файла на диске уже нет
    }

    public delegate void ScanProgress(int done, int total, string current);

    /// <summary>
    /// Каталог сетов. Разбор одного сета — около 150 мс, а их больше тысячи, поэтому
    /// результат кешируется на диск и переиспользуется, пока файл не изменился.
    /// </summary>
    public sealed class ProjectIndex
    {
        const int CacheVersion = 10;  // 10: Files/Missing считаются по уникальным файлам

        volatile List<SetEntry> _sets = new List<SetEntry>();

        /// <summary>
        /// Каталог сетов. Список ПОДМЕНЯЕТСЯ целиком, а не правится на месте, и это
        /// не стилистика: читают его из потока интерфейса (список сетов, плитки
        /// главной, панель подробностей, диалог фильтров), а пишет фоновый поток
        /// сканирования. Прежняя правка на месте (Clear + Add по одному) роняла любой
        /// идущий в это время foreach на «Collection was modified» — достаточно было
        /// набрать что-нибудь в поиске, пока идёт скан.
        ///
        /// Присваивание ссылки атомарно, поэтому читатель всегда видит либо старый
        /// список целиком, либо новый целиком, но никогда полусобранный. Плата за это —
        /// уговор: опубликованный список не меняют. Нужен другой состав — собирают
        /// новый и присваивают его.
        /// </summary>
        public List<SetEntry> Sets { get { return _sets; } }

        public LiveEnvironment Env = new LiveEnvironment();

        /// <summary>
        /// Когда работали: история сохранений из папок Backup. Подменяется целиком, как
        /// и Sets, — читает её интерфейс, пишет фоновое сканирование.
        /// </summary>
        public Activity History = Activity.Empty;

        /// <summary>Что установлено на машине — по базе самой Live.</summary>
        public PluginInventory Inventory = new PluginInventory();

        static string CachePath { get { return Path.Combine(Settings.Dir, "index.cache"); } }

        // ------------------------------------------------------------- сканирование

        public bool LoadFromCache()
        {
            Dictionary<string, SetEntry> cache = LoadCache();
            if (cache == null || cache.Count == 0) return false;
            List<SetEntry> list = new List<SetEntry>(cache.Values);
            _sets = list;
            History = Activity.LoadCache();
            RefreshInstalled();
            return true;
        }

        public void Scan(Settings settings, ScanProgress progress, CancellationToken cancel)
        {
            // Кеш файловых проб живёт ровно одно сканирование — см. RefResolver.BeginScan.
            RefResolver.BeginScan();
            try { ScanCore(settings, progress, cancel); }
            finally { RefResolver.EndScan(); }
        }

        void ScanCore(Settings settings, ScanProgress progress, CancellationToken cancel)
        {
            Env = LiveEnvironment.Detect();

            Dictionary<string, SetEntry> cache = LoadCache();

            HashSet<string> disabled = new HashSet<string>(settings.DisabledRoots, StringComparer.OrdinalIgnoreCase);
            List<string> files = new List<string>();
            // Один и тот же .als легко попадается дважды: корни бывают вложены друг в
            // друга («…\Music» и «…\Music\Ableton» оба в списке). Без отсева сет потом
            // двоится в каталоге, и каждая копия разбирается заново.
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in settings.Roots)
            {
                if (disabled.Contains(root)) continue;   // временно выключена — папка остаётся в списке
                int before = files.Count;
                FolderScan.Result r = Collect(root, files, seen, progress, cancel);
                Diag.Line("scan: " + root + " -> " + (files.Count - before) + " sets in "
                        + r.Dirs + " folders"
                        + (r.Unreadable > 0 ? ", " + r.Unreadable + " folders unreadable" : "")
                        + (r.RootFailed ? "  ROOT NOT READABLE" : ""));
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            int total = files.Count, done = 0;
            SetEntry[] results = new SetEntry[total];

            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1));
            po.CancellationToken = cancel;

            try
            {
                Parallel.For(0, total, po, delegate (int i)
                {
                    string file = files[i];
                    SetEntry entry = null;
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        SetEntry cached;
                        if (cache.TryGetValue(file, out cached)
                            && cached.Size == fi.Length
                            && cached.Modified == fi.LastWriteTimeUtc)
                        {
                            entry = cached;          // файл не менялся — берём из кеша
                        }
                        else
                        {
                            entry = Build(file, fi);
                        }
                    }
                    catch (Exception ex)
                    {
                        entry = new SetEntry();
                        entry.Path = file;
                        entry.Name = Path.GetFileNameWithoutExtension(file);
                        entry.Error = ex.Message;
                    }

                    results[i] = entry;
                    int n = Interlocked.Increment(ref done);
                    if (progress != null && (n % 8 == 0 || n == total))
                        progress(n, total, entry != null ? entry.Name : "");
                });
            }
            catch (OperationCanceledException) { }

            // Собираем в свой список и публикуем его одним присваиванием в самом конце —
            // до этого момента поток интерфейса продолжает спокойно читать прежний
            // каталог. См. комментарий у Sets.
            List<SetEntry> fresh = new List<SetEntry>(total);
            foreach (SetEntry e in results) if (e != null) fresh.Add(e);

            // Рендеры — свойство папки, а не сета, поэтому идёт отдельным проходом и не
            // попадает в кеш: экспорт нового файла не меняет .als. Заодно запоминаем их
            // имена — RenderScan.Find уже даёт ровно ту выборку, что и предпрослушка
            // (без Samples и служебных папок), и по ней потом ищет MatchesSet.
            try
            {
                Parallel.ForEach(fresh, po, delegate (SetEntry e)
                {
                    List<RenderFile> renders = RenderScan.Find(e);
                    string[] names = new string[renders.Count];
                    for (int i = 0; i < renders.Count; i++) names[i] = renders[i].Name;
                    e.RenderNames = names;
                    e.HasRenders = names.Length > 0;
                });
            }
            catch (OperationCanceledException) { }

            // История копится поверх уже известной, а не собирается заново: Live
            // держит только десять последних копий на сет — см. Activity.
            Activity known = History.Total > 0 ? History : Activity.LoadCache();
            Activity activity = WeighProjects(fresh, po, cancel, known);

            _sets = fresh;                 // <- отсюда каталог виден интерфейсу целиком
            History = activity;
            activity.SaveCache();
            _knownVendors = null;          // состав вендоров зависит от набора сетов
            SaveCache();
            RefreshInstalled();
            if (progress != null) progress(fresh.Count, total, "");
        }

        /// <summary>
        /// Одна строка на папку. У проекта обычно лежит рядом с десяток .als — v1, v2,
        /// final, final2, — и в каталоге они занимают десять строк, хотя проект один.
        /// Оставляем самый свежий, остальные прячем под него, а сколько спрятано —
        /// пишем ему в CollapsedCount.
        ///
        /// Схлопываем ПОСЛЕ фильтров и поиска, а не до: иначе запрос по плагину,
        /// который остался только в старой версии, не нашёл бы вообще ничего.
        /// </summary>
        public static List<SetEntry> CollapseByFolder(List<SetEntry> matched)
        {
            foreach (SetEntry s in matched) s.CollapsedCount = 0;

            List<SetEntry> order = new List<SetEntry>(matched.Count);
            Dictionary<string, int> slot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (SetEntry s in matched)
            {
                string dir = s.Directory ?? "";
                int i;
                if (!slot.TryGetValue(dir, out i))
                {
                    slot[dir] = order.Count;
                    order.Add(s);
                    continue;
                }

                SetEntry was = order[i];
                if (s.Modified > was.Modified)
                {
                    s.CollapsedCount = was.CollapsedCount + 1;   // счётчик переезжает к новому главному
                    was.CollapsedCount = 0;
                    order[i] = s;
                }
                else was.CollapsedCount++;
            }
            return order;
        }

        /// <summary>
        /// Все сеты из той же папки, свежие сверху, — включая сам переданный. Нужно
        /// панели сведений: под схлопнутой строкой должно быть видно, что там спрятано.
        /// </summary>
        public List<SetEntry> InSameFolder(SetEntry s)
        {
            List<SetEntry> result = new List<SetEntry>();
            if (s == null) return result;
            string dir = s.Directory ?? "";
            foreach (SetEntry other in _sets)
                if (string.Equals(other.Directory ?? "", dir, StringComparison.OrdinalIgnoreCase))
                    result.Add(other);
            result.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });
            return result;
        }

        /// <summary>
        /// Вес папки каждого проекта. Считаем по РАЗНЫМ папкам, а не по сетам: в одной
        /// папке проекта обычно лежит с десяток версий .als, и обходить её ради каждой
        /// значило бы перечитать одно и то же дерево десять раз.
        /// </summary>
        static Activity WeighProjects(List<SetEntry> sets, ParallelOptions po,
                                      CancellationToken cancel, Activity known)
        {
            List<string> dirs = new List<string>();
            Dictionary<string, int> slot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SetEntry e in sets)
            {
                string d = e.ProjectDir;
                if (d.Length == 0 || slot.ContainsKey(d)) continue;
                slot[d] = dirs.Count;
                dirs.Add(d);
            }

            FolderScan.Weight[] weights = new FolderScan.Weight[dirs.Count];
            try
            {
                Parallel.For(0, dirs.Count, po, delegate (int i)
                {
                    weights[i] = FolderScan.Weigh(dirs[i], delegate { return cancel.IsCancellationRequested; });
                });
            }
            catch (OperationCanceledException) { }

            foreach (SetEntry e in sets)
            {
                int i;
                if (!slot.TryGetValue(e.ProjectDir, out i)) continue;
                e.ProjectSize = weights[i].Bytes;
                e.ProjectFiles = weights[i].Files;
            }

            // История сохранений приезжает тем же обходом: копии в Backup он и так
            // перечисляет, считая вес папки.
            return Activity.Build(dirs, weights, sets, known);
        }

        SetEntry Build(string file, FileInfo fi)
        {
            SetEntry e = new SetEntry();
            e.Path = file;
            e.Name = Path.GetFileNameWithoutExtension(file);
            e.Modified = fi.LastWriteTimeUtc;
            e.Created = fi.CreationTimeUtc;
            e.Size = fi.Length;
            e.IsBackup = file.IndexOf(@"\Backup\", StringComparison.OrdinalIgnoreCase) >= 0;
            e.ProjectName = ProjectNameOf(file);

            AlsInfo info = AlsFile.Read(file);
            if (info.Error != null) { e.Error = info.Error; return e; }

            e.Creator = info.Creator;
            e.Tempo = info.Tempo;
            e.Key = info.Key;
            e.ScaleRoot = info.ScaleRoot;
            e.ScaleIndex = info.ScaleIndex;
            e.Tracks = info.TotalTracks;

            // Один и тот же плагин встречается в сете многократно, и путь браузера есть
            // не у каждой копии. Берём первый непустой, но надёжный источник (VST3/AU)
            // всегда перебивает имя папки браузера.
            SortedDictionary<string, PluginRef> plugins =
                new SortedDictionary<string, PluginRef>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginRef p in info.Plugins)
            {
                if (p.Name.Length == 0) continue;
                PluginRef best;
                if (!plugins.TryGetValue(p.Name, out best))
                {
                    plugins[p.Name] = p;
                    continue;
                }
                bool better = (p.VendorConfident && !best.VendorConfident)
                           || (best.Uid.Length > 0 == false && p.Uid.Length > 0)
                           || (best.Manufacturer.Length == 0 && p.Manufacturer.Length > 0);
                if (better) plugins[p.Name] = p;
            }

            e.Plugins = new string[plugins.Count];
            e.PluginVendors = new string[plugins.Count];
            e.PluginVendorConfident = new bool[plugins.Count];
            e.PluginUids = new string[plugins.Count];
            int pi = 0;
            foreach (KeyValuePair<string, PluginRef> kv in plugins)
            {
                e.Plugins[pi] = kv.Key;
                e.PluginVendors[pi] = kv.Value.Manufacturer ?? "";
                e.PluginVendorConfident[pi] = kv.Value.VendorConfident;
                e.PluginUids[pi] = kv.Value.Uid ?? "";
                pi++;
            }

            string dir = Path.GetDirectoryName(file);
            // Считаем РАЗНЫЕ файлы, а не вхождения: один сэмпл, разрезанный на сотню
            // клипов, даёт сотню FileRef — и раньше колонка Files показывала именно их.
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0, real = 0;
            foreach (FileRefInfo fr in info.Files)
            {
                // Считаем только сэмплы клипов. Пресеты и рэки Ableton встраивает в сет,
                // их FileRef — лишь память о происхождении, файл для открытия не нужен.
                if (!fr.IsSampleDependency) continue;
                ResolvedRef rr = RefResolver.Resolve(fr, dir, Env);
                if (rr.Status == RefStatus.Empty) continue;
                if (!seen.Add(rr.ResolvedPath)) continue;
                real++;
                if (rr.Status == RefStatus.Missing || rr.Status == RefStatus.MissingPack) missing++;
            }
            e.TotalRefs = real;
            e.MissingFiles = missing;
            return e;
        }

        /// <summary>Ближайшая вверх папка «* Project», иначе просто имя родительской папки.</summary>
        static string ProjectNameOf(string file)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(Path.GetDirectoryName(file));
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase))
                        return d.Name.Substring(0, d.Name.Length - " Project".Length);
                    d = d.Parent;
                }
                return new DirectoryInfo(Path.GetDirectoryName(file)).Name;
            }
            catch { return ""; }
        }

        /// <summary>
        /// Собрать сеты одного корня. Один и тот же .als легко попадается дважды, если
        /// корни вложены друг в друга, — от этого спасает общий seen.
        ///
        /// По ходу дела дёргает progress: обход большой папки (D:\Music — двадцать тысяч
        /// каталогов) идёт секунды, а на холодном диске и минуты, и всё это время до
        /// разбора сетов дело ещё не дошло. Без этих отчётов окно молчало «Scanning 0 / 0»
        /// и выглядело так, будто добавленную папку оно просто не смотрит.
        /// </summary>
        static FolderScan.Result Collect(string dir, List<string> files, HashSet<string> seen,
                                         ScanProgress progress, CancellationToken cancel)
        {
            return FolderScan.Find(dir, ".als", false,
                delegate (string f)
                {
                    if (!seen.Add(f)) return;
                    files.Add(f);
                    // total = 0 — «сколько всего, ещё неизвестно»; см. MainForm.CountText.
                    if (progress != null && files.Count % 16 == 0) progress(files.Count, 0, f);
                },
                delegate { return cancel.IsCancellationRequested; });
        }

        // ------------------------------------------------------------------ кеш

        Dictionary<string, SetEntry> LoadCache()
        {
            Dictionary<string, SetEntry> map =
                new Dictionary<string, SetEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CachePath)) return map;
                using (FileStream fs = File.OpenRead(CachePath))
                using (BinaryReader r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadInt32() != CacheVersion) return map;
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        SetEntry e = new SetEntry();
                        e.Path = r.ReadString();
                        e.Name = r.ReadString();
                        e.ProjectName = r.ReadString();
                        e.Modified = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                        e.Created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                        e.Size = r.ReadInt64();
                        e.IsBackup = r.ReadBoolean();
                        e.Creator = r.ReadString();
                        e.Tempo = r.ReadDouble();
                        e.Key = r.ReadString();
                        e.ScaleRoot = r.ReadInt32();
                        e.ScaleIndex = r.ReadInt32();
                        e.Tracks = r.ReadInt32();
                        e.MissingFiles = r.ReadInt32();
                        e.TotalRefs = r.ReadInt32();
                        e.Error = r.ReadString();
                        int pc = r.ReadInt32();
                        string[] plugins = new string[pc];
                        string[] vendors = new string[pc];
                        bool[] confident = new bool[pc];
                        string[] uids = new string[pc];
                        for (int j = 0; j < pc; j++)
                        {
                            plugins[j] = r.ReadString();
                            vendors[j] = r.ReadString();
                            confident[j] = r.ReadBoolean();
                            uids[j] = r.ReadString();
                        }
                        e.Plugins = plugins;
                        e.PluginVendors = vendors;
                        e.PluginVendorConfident = confident;
                        e.PluginUids = uids;
                        map[e.Path] = e;
                    }
                }
            }
            catch { map.Clear(); }
            return map;
        }

        void SaveCache()
        {
            List<SetEntry> sets = _sets;
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                string tmp = CachePath + ".tmp";
                using (FileStream fs = File.Create(tmp))
                using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
                {
                    w.Write(CacheVersion);
                    w.Write(sets.Count);
                    foreach (SetEntry e in sets)
                    {
                        w.Write(e.Path); w.Write(e.Name); w.Write(e.ProjectName);
                        w.Write(e.Modified.Ticks); w.Write(e.Created.Ticks); w.Write(e.Size); w.Write(e.IsBackup);
                        w.Write(e.Creator); w.Write(e.Tempo); w.Write(e.Key ?? "");
                        w.Write(e.ScaleRoot); w.Write(e.ScaleIndex); w.Write(e.Tracks);
                        w.Write(e.MissingFiles); w.Write(e.TotalRefs); w.Write(e.Error ?? "");
                        w.Write(e.Plugins.Length);
                        for (int i = 0; i < e.Plugins.Length; i++)
                        {
                            w.Write(e.Plugins[i]);
                            w.Write(i < e.PluginVendors.Length ? (e.PluginVendors[i] ?? "") : "");
                            w.Write(i < e.PluginVendorConfident.Length && e.PluginVendorConfident[i]);
                            w.Write(i < e.PluginUids.Length ? (e.PluginUids[i] ?? "") : "");
                        }
                    }
                }
                if (File.Exists(CachePath)) File.Delete(CachePath);
                File.Move(tmp, CachePath);
            }
            catch { /* кеш — не критично, в худшем случае пересканируем */ }
        }

        // -------------------------------------------------------------- установленное

        /// <summary>
        /// Перечитывает базу установленных плагинов и проставляет каждому сету, скольких
        /// плагинов ему не хватает. Дёшево (один текстовый файл), поэтому вызывается и
        /// после сканирования, и по кнопке — набор плагинов меняется без правки сетов.
        /// </summary>
        public void RefreshInstalled()
        {
            Inventory = PluginInventory.Load();
            _knownVendors = null;      // список вендоров зависит и от установленного
            _usage = null;             // и сводка по плагинам: совпадения считаются по нему
            foreach (SetEntry e in _sets)
            {
                int missing = 0;
                for (int i = 0; i < e.Plugins.Length; i++)
                {
                    string uid = i < e.PluginUids.Length ? e.PluginUids[i] : "";
                    if (Inventory.Match(uid, e.Plugins[i]).Kind == MatchKind.Missing) missing++;
                }
                e.MissingPlugins = missing;
            }
        }

        public PluginHealth Health(List<PluginStat> usage)
        {
            PluginHealth h = new PluginHealth();
            h.InstalledTotal = Inventory.All.Count;
            foreach (InstalledPlugin p in Inventory.All) if (p.FileMissing) h.FilesGone++;

            // Считаем по тем же строкам, которые видно в списке: два разных подсчёта
            // одного и того же неизбежно разъезжаются.
            foreach (PluginStat st in usage)
            {
                if (st.IsUnused) { h.InstalledUnused++; continue; }
                h.Used++;
                if (st.Match == MatchKind.Exact) h.Installed++;
                else if (st.Match == MatchKind.OtherFormat) h.OtherFormat++;
                else h.Missing++;
            }

            return h;
        }

        HashSet<string> _knownVendors;

        /// <summary>
        /// Имена, которые хотя бы раз встретились как настоящий вендор (форма
        /// VST3:Вендор:Имя или поле Manufacturer у AU).
        /// </summary>
        public HashSet<string> KnownVendors
        {
            get
            {
                if (_knownVendors == null)
                {
                    _knownVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Вендоры из базы Live — источник вне подозрений: если «Arturia»
                    // там есть, то и папка браузера с таким именем не выдумка.
                    foreach (InstalledPlugin p in Inventory.All)
                        if (p.Vendor.Length > 0) _knownVendors.Add(p.Vendor);

                    foreach (SetEntry e in _sets)
                        for (int i = 0; i < e.Plugins.Length; i++)
                            if (i < e.PluginVendorConfident.Length && e.PluginVendorConfident[i]
                                && i < e.PluginVendors.Length && e.PluginVendors[i].Length > 0)
                                _knownVendors.Add(e.PluginVendors[i]);
                }
                return _knownVendors;
            }
        }

        /// <summary>
        /// У VST2 в пути браузера лежит папка, а не разработчик, и в коллекции,
        /// разложенной по папкам, оттуда прилетает «Eff» или «Gen». Поэтому ненадёжное
        /// имя принимаем, только если оно где-то подтверждено как настоящий вендор —
        /// «Arturia» пройдёт, «Eff» нет. Иначе честнее оставить пусто.
        /// </summary>
        public string AcceptVendor(string vendor, bool confident)
        {
            if (string.IsNullOrEmpty(vendor)) return "";
            if (confident) return vendor;
            return KnownVendors.Contains(vendor) ? vendor : "";
        }

        volatile List<PluginStat> _usage;
        volatile List<SetEntry> _usageOf;   // по какому снимку Sets собран _usage

        /// <summary>
        /// Сводка по плагинам: имя, разработчик, в скольких сетах встречается и стоит ли
        /// он на машине. В список попадает и то, что установлено, но не используется
        /// нигде — иначе такие плагины никак не найти.
        ///
        /// Результат запоминается. Обход тут — все сеты на все их плагины, плюс словари,
        /// сверка с базой Live и сортировка; на тысяче сетов это десятки тысяч шагов, а
        /// зовут его на КАЖДУЮ перерисовку вкладки плагинов, то есть на каждую букву,
        /// набранную в поиске. Сам ответ при этом меняется ровно от двух вещей: сменился
        /// набор сетов или перечитали установленное. Первое ловим по ссылке на список
        /// (Scan публикует новый), второе — сбросом из RefreshInstalled.
        /// </summary>
        public List<PluginStat> PluginUsage()
        {
            List<SetEntry> sets = _sets;
            List<PluginStat> cached = _usage;
            if (cached != null && ReferenceEquals(_usageOf, sets)) return cached;

            Dictionary<string, PluginStat> use =
                new Dictionary<string, PluginStat>(StringComparer.OrdinalIgnoreCase);

            foreach (SetEntry e in sets)
                for (int i = 0; i < e.Plugins.Length; i++)
                {
                    string name = e.Plugins[i];
                    bool rawConf = i < e.PluginVendorConfident.Length && e.PluginVendorConfident[i];
                    string vendor = AcceptVendor(
                        i < e.PluginVendors.Length ? e.PluginVendors[i] : "", rawConf);

                    PluginStat st;
                    if (!use.TryGetValue(name, out st))
                    {
                        st = new PluginStat();
                        st.Name = name;
                        use[name] = st;
                    }
                    st.Sets++;
                    if (st.Uid.Length == 0 && i < e.PluginUids.Length) st.Uid = e.PluginUids[i] ?? "";

                    // Разработчик известен не в каждом сете. Надёжный источник (VST3/AU)
                    // вытесняет ранее найденное имя папки браузера.
                    bool conf = rawConf;
                    if (!string.IsNullOrEmpty(vendor)
                        && (st.Vendor.Length == 0 || (conf && !st.VendorConfident)))
                    {
                        st.Vendor = vendor;
                        st.VendorConfident = conf;
                    }
                }

            // Сверяем с установленным и берём оттуда вендора: у Live он настоящий, а
            // путь браузера у VST2 подсовывает имя папки вроде «Eff».
            foreach (PluginStat st in use.Values)
            {
                PluginMatch m = Inventory.Match(st.Uid, st.Name);
                st.Match = m.Kind;
                st.Installed = m.Plugin;
                if (m.Kind == MatchKind.Exact && m.Plugin.Vendor.Length > 0)
                {
                    st.Vendor = m.Plugin.Vendor;
                    st.VendorConfident = true;
                }
            }

            // Установленное, но не встреченное ни в одном сете — кандидаты на снос.
            // «Встречено» считаем по тем же признакам, что и совпадение вообще: один и
            // тот же плагин у Live заведён и как VST2, и как VST3, и если сет просит
            // VST2-версию, то VST3-близнец тоже используется, а не простаивает.
            HashSet<string> usedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginStat st in use.Values)
            {
                if (st.Uid.Length > 0) usedUids.Add(st.Uid);
                usedNames.Add(PluginInventory.Normalize(st.Name));
            }

            foreach (InstalledPlugin p in Inventory.All)
            {
                if (usedUids.Contains(p.Uid)) continue;
                if (usedNames.Contains(PluginInventory.Normalize(p.Name))) continue;
                if (usedNames.Contains(PluginInventory.Normalize(p.Vendor + p.Name))) continue;

                string key = p.Name;
                while (use.ContainsKey(key)) key = key + " ";   // имя занято другим плагином
                PluginStat st2 = new PluginStat();
                st2.Name = p.Name;
                st2.Vendor = p.Vendor;
                st2.VendorConfident = true;
                st2.Uid = p.Uid;
                st2.Installed = p;
                st2.Match = MatchKind.Exact;
                st2.Sets = 0;
                use[key] = st2;
            }

            List<PluginStat> list = new List<PluginStat>(use.Values);
            list.Sort(delegate (PluginStat a, PluginStat b)
            {
                int c = b.Sets.CompareTo(a.Sets);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // Порядок важен: сперва «из чего собрано», потом сам ответ — иначе
            // читатель успеет увидеть новый список рядом со старой пометкой.
            _usageOf = sets;
            _usage = list;
            return list;
        }
    }
}
