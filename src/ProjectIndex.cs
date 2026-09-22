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
        public string Key = "";      // "C Major"; empty on Live versions with no overall key
        public int ScaleRoot = -1;   // 0..11; we filter by it — "C#" and "Db" are one note
        public int ScaleIndex = -1;
        public int Tracks;
        public int MissingFiles;
        public int TotalRefs;
        public string[] Plugins = new string[0];
        public string[] PluginVendors = new string[0];        // parallel to Plugins, "" when unknown
        public bool[] PluginVendorConfident = new bool[0];    // the vendor from VST3/AU rather than from a browser folder
        public string[] PluginUids = new string[0];           // "vst3:…" / "vst2:…", parallel to Plugins
        public string Error = "";

        /// <summary>
        /// The samples the set plays: resolved paths of its SampleRef dependencies that were
        /// found, without repeats. The Samples tab counts usage from these. Lost ones are not
        /// here — a file that is not on disk belongs to no library.
        /// </summary>
        public string[] Samples = new string[0];

        /// <summary>
        /// Parallel to Samples: the size recorded in the set (OriginalFileSize), or the length on
        /// disk when the set did not record one. A copy Collect All put into the project is
        /// recognised by name and this size, without opening anything.
        /// </summary>
        public long[] SampleSizes = new long[0];

        /// <summary>
        /// How many of a set's plugins are not installed. Computed after the scan rather than
        /// during it — what is installed changes without the sets themselves being edited, so
        /// it cannot be cached.
        /// </summary>
        public int MissingPlugins;

        /// <summary>
        /// Whether there is anything to listen to next to the project. Not cached to disk:
        /// renders appear without the set itself being edited, and a remembered answer would go
        /// stale with the first export. Recomputed on every scan.
        /// </summary>
        public bool HasRenders;

        /// <summary>
        /// The names of the files that really can be previewed (the same selection as
        /// RenderScan.Find uses — Samples and Live's housekeeping folders do not count) — so
        /// that a set can be searched for by the name of a render rather than only by the set's
        /// own name. Not cached to disk, for the same reason as HasRenders.
        /// </summary>
        public string[] RenderNames = new string[0];

        /// <summary>
        /// The weight of the whole project folder and how many files are in it. Not cached, for
        /// the same reason as HasRenders: record a sample and the folder grows heavier while
        /// the .als has not changed, and the remembered number would be a lie.
        /// </summary>
        public long ProjectSize;
        public int ProjectFiles;

        /// <summary>
        /// How many more sets of the same folder are hidden under this row. Computed on every
        /// filling of the list — it depends on the filters rather than on the set itself.
        /// </summary>
        public int CollapsedCount;

        /// <summary>The short version, "12.4.3", out of "Ableton Live 12.4.3".</summary>
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

        /// <summary>
        /// Whether two row tags point at the same set. A set's identity is its path, not the
        /// object: a scan republishes the catalog with NEW SetEntry objects even for files that
        /// have not changed (Scan reads them back out of the cache on disk), so reference
        /// equality stops holding the moment the folder watcher or F5 fires. The playing row
        /// then quietly lost its pulse while the sound carried on — see RowListView.PlayingTag
        /// and HomeView.PlayingTag, which is what this is for.
        ///
        /// Tags that are not sets (the player's own list of render files) fall back to
        /// reference: those objects live as long as the window does.
        /// </summary>
        public static bool SameSet(object a, object b)
        {
            if (ReferenceEquals(a, b)) return a != null;

            SetEntry x = a as SetEntry, y = b as SetEntry;
            return x != null && y != null
                && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        }

        string _projectDir;

        /// <summary>
        /// The project folder — that very "… Project" Live creates, with all the Samples
        /// inside. If a set lies on its own, outside such a folder, we treat its own folder as
        /// the project. Computed once: Path does not change, while the weight, the tags and the
        /// grouping all ask for it.
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
        /// The shelf a project lies on: the name of the folder containing the "* Project"
        /// folder. For "…\Series 2\somnitelno\X Project\X.als" that is "somnitelno" — from it
        /// one can see which collection a set is in without reading the whole path.
        ///
        /// Computed once and remembered: a record's Path never changes, while the "shelf"
        /// itself is asked for in hot places — once per cell of the column and TWICE per
        /// comparison when sorting by it. On a thousand sets that is tens of thousands of walks
        /// up the DirectoryInfo tree, all for one string that has been the same the whole time.
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
                // The set is not in a "* Project" folder — we take the parent of the folder
                // holding the .als.
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
        public InstalledPlugin Installed;      // null if there is no such plugin on the machine

        public bool IsInstalled { get { return Match != MatchKind.Missing; } }
        public bool IsUnused { get { return Sets == 0; } }

        /// <summary>"VST3" / "VST2" — the format comes from the installed one, otherwise from
        /// the identifier.</summary>
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

        /// <summary>The plugin's category from VST3/AU (Mastering, Reverb, Synth, Dynamics and
        /// so on).</summary>
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

    /// <summary>A summary of the library's plugins — what the cards show.</summary>
    public sealed class PluginHealth
    {
        public int Used;             // distinct plugins occurring in the sets
        public int Installed;        // of those, installed (an exact match)
        public int OtherFormat;      // present, but in another format
        public int Missing;          // not installed at all
        public int InstalledTotal;   // how many are installed on the machine in total
        public int InstalledUnused;  // of those, occurring in no set at all
        public int FilesGone;        // Live remembers them, but the file is no longer on disk
    }

    public delegate void ScanProgress(int done, int total, string current);

    /// <summary>
    /// The catalog of sets. Parsing one set takes around 150 ms and there are over a thousand
    /// of them, so the result is cached to disk and reused until the file changes.
    /// </summary>
    public sealed class ProjectIndex
    {
        const int CacheVersion = 11;  // 11: the paths and sizes of the samples a set plays

        volatile List<SetEntry> _sets = new List<SetEntry>();

        /// <summary>
        /// The catalog of sets. The list is SWAPPED whole rather than edited in place, and that
        /// is not a matter of style: it is read from the UI thread (the sets list, the home
        /// tiles, the detail panel, the filters dialog) while the background scanning thread
        /// writes it. Editing in place as it used to be (Clear plus Add one by one) brought
        /// down any foreach running at that moment with "Collection was modified" — it was
        /// enough to type something into the search while a scan was going.
        ///
        /// Assigning a reference is atomic, so a reader always sees either the old list whole
        /// or the new list whole, and never a half-built one. The price is an agreement: a
        /// published list is not modified. If a different content is needed, a new one is built
        /// and assigned.
        /// </summary>
        public List<SetEntry> Sets { get { return _sets; } }

        public LiveEnvironment Env = new LiveEnvironment();

        /// <summary>
        /// When the work happened: the history of saves from the Backup folders. Swapped whole,
        /// like Sets — the interface reads it while background scanning writes it.
        /// </summary>
        public Activity History = Activity.Empty;

        /// <summary>What is installed on the machine — according to Live's own
        /// database.</summary>
        public PluginInventory Inventory = new PluginInventory();

        static string CachePath { get { return Path.Combine(Settings.Dir, "index.cache"); } }

        // ------------------------------------------------------------------ scanning

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
            // The file probe cache lives for exactly one scan — see RefResolver.BeginScan.
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
            // One and the same .als turns up twice easily: roots are sometimes nested inside
            // each other ("…\Music" and "…\Music\Ableton" both in the list). Without filtering,
            // the set then doubles in the catalog and each copy is parsed anew.
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in settings.Roots)
            {
                if (disabled.Contains(root)) continue;   // temporarily off — the folder stays in the list
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
                            entry = cached;          // the file has not changed — we take it from the cache
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

            // We assemble into a list of our own and publish it with a single assignment at the
            // very end — until that moment the UI thread carries on reading the previous
            // catalog in peace. See the comment on Sets.
            List<SetEntry> fresh = new List<SetEntry>(total);
            foreach (SetEntry e in results) if (e != null) fresh.Add(e);

            // Renders are a property of the folder rather than of the set, so they go in a
            // separate pass and do not get into the cache: exporting a new file does not change
            // the .als. We also remember their names — RenderScan.Find already gives exactly
            // the selection the preview uses (without Samples and the housekeeping folders),
            // and MatchesSet searches through it afterwards.
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

            // The history accumulates on top of what is already known rather than being
            // rebuilt: Live keeps only the last ten copies per set — see Activity.
            Activity known = History.Total > 0 ? History : Activity.LoadCache();
            Activity activity = WeighProjects(fresh, po, cancel, known);

            _sets = fresh;                 // <- from here the catalog is visible to the interface whole
            History = activity;
            activity.SaveCache();
            _knownVendors = null;          // the set of vendors depends on the set of sets
            SaveCache();
            RefreshInstalled();
            if (progress != null) progress(fresh.Count, total, "");
        }

        /// <summary>
        /// One row per folder. A project usually has a dozen .als files lying next to each
        /// other — v1, v2, final, final2 — and in the catalog they take ten rows although the
        /// project is one. We keep the newest and hide the rest under it, writing how many are
        /// hidden into its CollapsedCount.
        ///
        /// We collapse AFTER the filters and the search, not before: otherwise a query for a
        /// plugin that survives only in an older version would find nothing at all.
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
                    s.CollapsedCount = was.CollapsedCount + 1;   // the counter moves to the new principal one
                    was.CollapsedCount = 0;
                    order[i] = s;
                }
                else was.CollapsedCount++;
            }
            return order;
        }

        /// <summary>
        /// Every set from the same folder, newest first — including the one passed in. The
        /// details panel needs it: what is hidden under a collapsed row has to be visible.
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
        /// The folder weight of each project. We count by DISTINCT folders rather than by sets:
        /// one project folder usually holds a dozen .als versions, and walking it for each
        /// would mean re-reading the same tree ten times.
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

            // The save history arrives by the same walk: it enumerates the copies in Backup
            // anyway while counting the folder weight.
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

            // One and the same plugin occurs in a set many times over, and not every copy has a
            // browser path. We take the first non-empty one, but a reliable source (VST3/AU)
            // always beats a browser folder name.
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
            // We count DISTINCT files rather than occurrences: one sample chopped into a
            // hundred clips gives a hundred FileRefs — and the Files column used to show
            // exactly those.
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0, real = 0;
            List<string> found = new List<string>();
            List<long> sizes = new List<long>();
            foreach (FileRefInfo fr in info.Files)
            {
                // We count only clip samples. Ableton embeds presets and racks into the set,
                // and their FileRef is only a memory of provenance — no file is needed to open
                // it.
                if (!fr.IsSampleDependency) continue;
                ResolvedRef rr = RefResolver.Resolve(fr, dir, Env);
                if (rr.Status == RefStatus.Empty) continue;
                if (!seen.Add(rr.ResolvedPath)) continue;
                real++;
                if (rr.Status == RefStatus.Missing || rr.Status == RefStatus.MissingPack)
                {
                    missing++;
                    continue;
                }
                found.Add(rr.ResolvedPath);
                sizes.Add(fr.OriginalFileSize > 0 ? fr.OriginalFileSize : LengthOf(rr.ResolvedPath));
            }
            e.TotalRefs = real;
            e.MissingFiles = missing;
            e.Samples = found.ToArray();
            e.SampleSizes = sizes.ToArray();
            return e;
        }

        static long LengthOf(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        /// <summary>The nearest "* Project" folder above, otherwise simply the parent folder's
        /// name.</summary>
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
        /// Collect the sets of one root. One and the same .als turns up twice easily when roots
        /// are nested inside each other — the shared seen guards against that.
        ///
        /// Along the way it pulls progress: walking a large folder (D:\Music — twenty thousand
        /// directories) takes seconds, and on a cold disk minutes, and all that time parsing
        /// the sets has not even begun. Without those reports the window sat silent on
        /// "Scanning 0 / 0" and looked as though it simply was not looking at the folder that
        /// had been added.
        /// </summary>
        static FolderScan.Result Collect(string dir, List<string> files, HashSet<string> seen,
                                         ScanProgress progress, CancellationToken cancel)
        {
            return FolderScan.Find(dir, ".als", false,
                delegate (string f)
                {
                    if (!seen.Add(f)) return;
                    files.Add(f);
                    // total = 0 means "how many there are in total is not known yet"; see
                    // MainForm.CountText.
                    if (progress != null && files.Count % 16 == 0) progress(files.Count, 0, f);
                },
                delegate { return cancel.IsCancellationRequested; });
        }

        // ------------------------------------------------------------------- cache

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
                        int sc = r.ReadInt32();
                        string[] samples = new string[sc];
                        long[] sampleSizes = new long[sc];
                        for (int j = 0; j < sc; j++)
                        {
                            samples[j] = r.ReadString();
                            sampleSizes[j] = r.ReadInt64();
                        }
                        e.Samples = samples;
                        e.SampleSizes = sampleSizes;
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
                        w.Write(e.Samples.Length);
                        for (int i = 0; i < e.Samples.Length; i++)
                        {
                            w.Write(e.Samples[i]);
                            w.Write(i < e.SampleSizes.Length ? e.SampleSizes[i] : 0L);
                        }
                    }
                }
                if (File.Exists(CachePath)) File.Delete(CachePath);
                File.Move(tmp, CachePath);
            }
            catch { /* the cache is not critical - worst case we rescan */ }
        }

        // ------------------------------------------------------------- what is installed

        /// <summary>
        /// Re-reads the database of installed plugins and marks each set with how many plugins
        /// it is missing. Cheap (one text file), so it is called both after a scan and from the
        /// button — the set of plugins changes without the sets being edited.
        /// </summary>
        public void RefreshInstalled()
        {
            Inventory = PluginInventory.Load();
            _knownVendors = null;      // the vendor list depends on what is installed too
            _usage = null;             // and the plugin summary: matches are counted by it
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

            // We count by the same rows that are visible in the list: two separate counts of
            // one and the same thing inevitably drift apart.
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
        /// The names that have turned up at least once as a genuine vendor (the
        /// VST3:Vendor:Name form, or an AU's Manufacturer field).
        /// </summary>
        public HashSet<string> KnownVendors
        {
            get
            {
                if (_knownVendors == null)
                {
                    _knownVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Vendors from Live's database are a source above suspicion: if "Arturia"
                    // is there, then a browser folder by that name is no invention either.
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
        /// For VST2 the browser path holds a folder rather than a developer, and in a
        /// collection sorted into folders "Eff" or "Gen" comes flying out of it. So we accept
        /// an unreliable name only if it has been confirmed somewhere as a genuine vendor —
        /// "Arturia" passes, "Eff" does not. Otherwise leaving it empty is more honest.
        /// </summary>
        public string AcceptVendor(string vendor, bool confident)
        {
            if (string.IsNullOrEmpty(vendor)) return "";
            if (confident) return vendor;
            return KnownVendors.Contains(vendor) ? vendor : "";
        }

        volatile List<PluginStat> _usage;
        volatile List<SetEntry> _usageOf;   // which snapshot of Sets _usage was built from

        /// <summary>
        /// A summary of the plugins: the name, the developer, how many sets it occurs in and
        /// whether it is on the machine. What is installed but used nowhere gets into the list
        /// too — otherwise such plugins cannot be found at all.
        ///
        /// The result is remembered. The walk here is every set against every one of its
        /// plugins, plus dictionaries, a cross-check against Live's database and a sort; on a
        /// thousand sets that is tens of thousands of steps, and it is called on EVERY repaint
        /// of the plugins tab, that is, on every letter typed into the search. The answer
        /// itself, meanwhile, changes from exactly two things: the set of sets changed, or what
        /// is installed was re-read. The first we catch by the reference to the list (Scan
        /// publishes a new one), the second by a reset from RefreshInstalled.
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

                    // The developer is not known in every set. A reliable source (VST3/AU)
                    // displaces a browser folder name found earlier.
                    bool conf = rawConf;
                    if (!string.IsNullOrEmpty(vendor)
                        && (st.Vendor.Length == 0 || (conf && !st.VendorConfident)))
                    {
                        st.Vendor = vendor;
                        st.VendorConfident = conf;
                    }
                }

            // We cross-check against what is installed and take the vendor from there: Live's
            // is genuine, while a VST2's browser path slips in a folder name like "Eff".
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

            // Installed but met in no set at all — candidates for removal. "Met" is counted by
            // the same signs as a match in general: one and the same plugin is registered by
            // Live both as VST2 and as VST3, and if a set asks for the VST2 version then the
            // VST3 twin is in use too rather than idle.
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
                while (use.ContainsKey(key)) key = key + " ";   // the name is taken by another plugin
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

            // The order matters: first "what it was built from", then the answer itself —
            // otherwise a reader would manage to see the new list next to the old marker.
            _usageOf = sets;
            _usage = list;
            return list;
        }
    }
}
