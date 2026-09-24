using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The Samples tab — the other half of MainForm: the library index, what the sets use of
    /// it, the folder tree and its two flat lenses. Kept apart so MainForm.cs does not grow by
    /// another few hundred lines; everything here is still MainForm and shares its list, panel
    /// and toolbar.
    /// </summary>
    public sealed partial class MainForm
    {
        enum SampleLens { All, NeverUsed, MostUsed, Duplicates }

        SampleIndex _samples = SampleIndex.Empty;
        SampleLens _lens = SampleLens.All;
        string _sampleSortId;

        // The usage is worked out from two snapshots and remembered until either of them
        // changes — the same device as ProjectIndex.PluginUsage.
        SampleUsage _usage = SampleUsage.Empty;
        List<SetEntry> _usageSets;
        SampleIndex _usageIndex;

        // The copies depend on the index alone and are found anew only when it changes.
        SampleCopies _copies = SampleCopies.Empty;
        SampleIndex _copiesIndex;

        // Open folders by path: a quiet re-walk makes new objects for the same folders.
        readonly HashSet<string> _openSampleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CancellationTokenSource _sampleCancel;
        volatile bool _sampleScanning;
        bool _sampleManual, _sampleRestart;
        volatile int _sampleFound;
        int _sampleEstimate;            // what the previous walk found: the progress bar's 100%
        bool _samplesWalked;            // one quiet walk per session, after the first scan of the sets
        int _lastSampleInvalidate;

        // A search is cut here: a query like "a" matches most of the library, and a table of
        // a hundred thousand rows helps nobody. The cut is said by the counter.
        const int SearchCap = 5000;
        int _sampleMatches;

        readonly SamplesEmpty _samplesEmpty = new SamplesEmpty();

        // The preview: a player of its own, not the render player — a sample is heard, not
        // listened to. Selecting a sample plays it (see AuditionSelection).
        readonly AudioPlayer _preview = new AudioPlayer();
        readonly System.Windows.Forms.Timer _previewTimer = new System.Windows.Forms.Timer();
        SampleFile _previewing;
        string _auditioned;         // the sample the selection last played
        int _previewStarted;        // when the preview last started — see OnSelectedRowClicked

        sealed class SampleCol
        {
            public string Id = "", Title = "";
            public int Width;
            public bool Right, Path;
            public Font Font;
            public Color? Color;
        }

        List<SampleCol> _sampleCols = new List<SampleCol>();

        // Which columns are on, in what order and width — configurable like the sets' and the
        // plugins' (see MainForm's column state). A view may still hide a column that means
        // nothing in it, or add its own: see SampleViewDecides.
        readonly List<string> _sampleOrder = new List<string>();
        readonly Dictionary<string, int> _sampleColW = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Every column the tab has, in the menu's order. The widths are the sets table's for
        // the same kind of value (a date 150, a size 130): the layout is in logical pixels while
        // the type follows the screen's scale, and at 125% narrower columns cut "2026-09-22" to
        // "2026-0…". A sorted column's header carries its arrow too: "Samples" needs 113 with
        // it and "Projects" 109, measured. The name and the location share whatever is left, and
        // are cut in the middle: what tells two samples apart is at the end — "(7).wav".
        static readonly List<SampleCol> SampleCatalog = new List<SampleCol>
        {
            new SampleCol { Id = "Name", Title = "Name", Width = 0, Font = Theme.FTitle, Color = Theme.Text, Path = true },
            new SampleCol { Id = "Location", Title = "Location", Width = 0, Path = true },
            new SampleCol { Id = "Samples", Title = "Samples", Width = 130, Right = true },
            new SampleCol { Id = "Used", Title = "Used", Width = 90, Right = true },
            new SampleCol { Id = "Copies", Title = "Copies", Width = 110, Right = true },
            new SampleCol { Id = "Projects", Title = "Projects", Width = 120, Right = true },
            new SampleCol { Id = "LastUsed", Title = "Last used", Width = 150 },
            new SampleCol { Id = "Created", Title = "Created", Width = 150 },
            new SampleCol { Id = "Modified", Title = "Modified", Width = 150 },
            new SampleCol { Id = "Size", Title = "Size", Width = 130, Right = true },
        };

        static readonly string[] DefaultSampleCols =
            { "Name", "Location", "Samples", "Used", "Projects", "LastUsed", "Size" };

        static SampleCol FindSampleCol(string id)
        {
            foreach (SampleCol d in SampleCatalog)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        bool HasSampleRoots { get { return _settings.SampleRoots.Count > 0; } }

        bool SampleFlat { get { return _lens != SampleLens.All || _search.Box.Text.Trim().Length > 0; } }

        // ------------------------------------------------------------ building

        void BuildSamples()
        {
            _samplesEmpty.AddFromLive += delegate { EditFolders(true, true); };
            _samplesEmpty.ChooseFolders += delegate { EditFolders(true, false); };
            _samplesEmpty.Visible = false;
            Controls.Add(_samplesEmpty);

            _previewTimer.Interval = 50;
            _previewTimer.Tick += delegate { PreviewTick(); };
        }

        void LoadSamplesFromCache()
        {
            _samples = SampleIndex.LoadCache().Only(_settings.SampleRoots, _settings.DisabledSampleRoots);
        }

        // ----------------------------------------------------------------- walk

        /// <summary>
        /// Walk the sample folders in the background. manual — F5 or the dialog: the progress
        /// bar and the "Indexing" counter. Otherwise quiet, and the view the person is looking at
        /// is put back afterwards.
        /// </summary>
        void StartSampleScan(bool manual)
        {
            if (_sampleScanning) return;
            List<string> roots = new List<string>(_settings.SampleRoots);
            List<string> off = new List<string>(_settings.DisabledSampleRoots);
            if (SampleIndex.Effective(roots, off).Count == 0)
            {
                _samples = SampleIndex.Empty;
                if (SamplesDomain) Refill();
                return;
            }

            _sampleScanning = true;
            _sampleManual = manual;
            _sampleFound = 0;
            _sampleEstimate = _samples.TotalSamples;
            _sampleCancel = new CancellationTokenSource();
            CancellationToken token = _sampleCancel.Token;
            SampleIndex previous = _samples;
            if (SamplesDomain) { LayoutReset(); Invalidate(); }

            Thread t = new Thread(delegate ()
            {
                SampleIndex fresh = null;
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    fresh = SampleIndex.Build(roots, off, delegate (int n)
                    {
                        _sampleFound = n;
                        int now = Environment.TickCount;
                        if (now - _lastSampleInvalidate < 150) return;
                        _lastSampleInvalidate = now;
                        try { BeginInvoke((MethodInvoker)delegate { if (SamplesDomain) Invalidate(); }); }
                        catch { }
                    }, token, previous);
                    if (!token.IsCancellationRequested) fresh.SaveCache();
                    Diag.Line("samples: " + fresh.TotalSamples + " in " + fresh.Folders.Count + " folders, "
                              + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex) { Diag.Fail("samples: walk", ex); fresh = null; }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _sampleScanning = false;
                        if (fresh != null && !token.IsCancellationRequested) _samples = fresh;
                        if (_sampleRestart) { _sampleRestart = false; StartSampleScan(true); return; }
                        if (SamplesDomain) { if (_sampleManual) Refill(); else RefillSamplesKeeping(SelectedSamplePath()); }
                        Invalidate();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        /// <summary>A walk already under way is called off and started again — it would have
        /// walked yesterday's list of folders.</summary>
        void RescanSamples()
        {
            if (_sampleScanning) { _sampleRestart = true; _sampleCancel.Cancel(); }
            else StartSampleScan(true);
        }

        // ---------------------------------------------------------------- usage

        /// <summary>What the sets use of the library — worked out anew only when the sets or the
        /// index changed. 74 ms on the development machine's 184 thousand samples.</summary>
        SampleUsage Usage()
        {
            List<SetEntry> sets = _index.Sets;
            if (ReferenceEquals(sets, _usageSets) && ReferenceEquals(_samples, _usageIndex)) return _usage;
            Stopwatch sw = Stopwatch.StartNew();
            _usage = SampleUsage.Compute(_samples, sets);
            _usageSets = sets;
            _usageIndex = _samples;
            if (sw.ElapsedMilliseconds > 50) Diag.Line("samples: usage in " + sw.ElapsedMilliseconds + " ms");
            return _usage;
        }

        /// <summary>The same sample in several places — found anew only for a new index.</summary>
        SampleCopies Copies()
        {
            if (ReferenceEquals(_samples, _copiesIndex)) return _copies;
            Stopwatch sw = Stopwatch.StartNew();
            _copies = SampleCopies.Find(_samples);
            _copiesIndex = _samples;
            if (sw.ElapsedMilliseconds > 50) Diag.Line("samples: copies in " + sw.ElapsedMilliseconds + " ms");
            return _copies;
        }

        /// <summary>
        /// The usage columns say "…" rather than "never" while the sets have not been read yet:
        /// right after an update the whole catalog is parsed anew, and for that half a minute
        /// everything would look unused.
        /// </summary>
        bool UsageUnknown { get { return _index.Sets.Count == 0 && _scanning; } }

        // -------------------------------------------------------------- folders

        /// <summary>Folders dropped onto the window while the Samples tab is open.</summary>
        void AddSampleRoots(string[] paths)
        {
            List<string> added = new List<string>();
            foreach (string path in paths)
            {
                string folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                if (SampleIndex.ContainsPath(_settings.SampleRoots, folder)) continue;
                _settings.SampleRoots.Add(folder);
                _settings.DisabledSampleRoots.Remove(folder);
                string name = Path.GetFileName(folder.TrimEnd('\\'));
                added.Add(name.Length > 0 ? name : folder);
            }
            if (added.Count == 0) { Notify("Already in the library"); return; }
            _settings.Save();
            Notify(added.Count == 1 ? "Added " + added[0] : "Added " + added.Count + " folders");
            LayoutAll();
            RescanSamples();
        }

        // ---------------------------------------------------------------- table

        /// <summary>
        /// Whether the current view decides a column by itself: null — it is the person's to
        /// turn on and off; false — never here (it would be the same in every row, or empty);
        /// true — always here, it is what the view is about.
        /// </summary>
        bool? SampleViewDecides(string id)
        {
            bool onlyFiles = _lens == SampleLens.MostUsed || _lens == SampleLens.Duplicates;
            switch (id)
            {
                case "Location": return SampleFlat ? (bool?)null : false;     // in the tree the tree is the location
                case "Samples": return onlyFiles ? (bool?)false : null;
                case "Used": return onlyFiles || _lens == SampleLens.NeverUsed ? (bool?)false : null;
                case "Projects":
                case "LastUsed": return _lens == SampleLens.NeverUsed ? (bool?)false : null;   // "never" in every row
                case "Copies": return _lens == SampleLens.Duplicates ? (bool?)true : null;
                case "Created": return _lens == SampleLens.NeverUsed ? (bool?)true : null;     // how long it has lain there
            }
            return null;
        }

        /// <summary>The columns of the current view: the person's, in their order, less what
        /// the view hides, plus what it adds — at its place in the catalog.</summary>
        List<SampleCol> SampleColumns()
        {
            List<SampleCol> c = new List<SampleCol>();
            foreach (string id in _sampleOrder)
            {
                SampleCol d = FindSampleCol(id);
                if (d != null && SampleViewDecides(d.Id) != false) c.Add(d);
            }
            for (int k = 0; k < SampleCatalog.Count; k++)
            {
                SampleCol d = SampleCatalog[k];
                if (SampleViewDecides(d.Id) != true || c.Contains(d)) continue;
                int at = c.Count;
                for (int i = 0; i < c.Count; i++)
                    if (SampleCatalog.IndexOf(c[i]) > k) { at = i; break; }
                c.Insert(at, d);
            }
            return c;
        }

        /// <summary>What the first column is called in this view.</summary>
        string SampleNameTitle
        {
            get
            {
                if (_lens == SampleLens.MostUsed || _lens == SampleLens.Duplicates) return "Sample";
                if (_lens == SampleLens.NeverUsed || !SampleFlat) return "Folder";
                return "Name";
            }
        }

        void FillSamples(bool animate)
        {
            SampleUsage use = Usage();
            _list.ColumnsConfigurable = true;
            _list.ShowPinIndicator = false;
            // No play button: a sample plays when it is selected (AuditionSelection).
            _list.ShowPlayButton = false;
            _list.IndentColumn = "Name";
            _list.DragFilePath = delegate (RowData r)
            {
                SampleFile f = r.Tag as SampleFile;
                return f != null ? f.Path : null;
            };
            _sampleCols = SampleColumns();
            Column[] cols = new Column[_sampleCols.Count];
            int si = -1;
            for (int i = 0; i < _sampleCols.Count; i++)
            {
                SampleCol d = _sampleCols[i];
                // A width the person dragged to stays — for the fixed columns; the name and the
                // location keep sharing whatever is left.
                int w = d.Width, ov;
                if (w != 0 && _sampleColW.TryGetValue(d.Id, out ov) && ov > 0) w = ov;
                string title = d.Id == "Name" ? SampleNameTitle : d.Title;
                cols[i] = new Column(title, w) { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color, PathEllipsis = d.Path };
                if (d.Id == _sampleSortId) si = i;
            }
            _list.SetColumns(cols);
            _list.SortColumn = si;
            _list.SortDescending = _sortDesc;

            List<RowData> rows = new List<RowData>();
            if (!SampleFlat)
            {
                _sampleMatches = 0;
                foreach (SampleFolder r in SortedFolders(_samples.Roots, true, use)) AddTree(rows, r, 0, use);
            }
            else rows = FlatRows(_search.Box.Text.Trim(), use);
            _total = _samples.TotalSamples;
            _list.SetRows(rows, animate);
            ShowSampleDetails();
        }

        void AddTree(List<RowData> rows, SampleFolder d, int depth, SampleUsage use)
        {
            rows.Add(FolderRow(d, depth, use, false));
            if (!_openSampleDirs.Contains(d.Path)) return;
            foreach (SampleFolder c in SortedFolders(d.Children, false, use)) AddTree(rows, c, depth + 1, use);
            foreach (SampleFile f in SortedFiles(d.Files, use)) rows.Add(FileRow(f, depth + 1, use));
        }

        List<RowData> FlatRows(string q, SampleUsage use)
        {
            List<SampleFolder> folders;
            List<SampleFile> files;
            if (_lens == SampleLens.NeverUsed) { folders = use.NeverUsed(_samples); files = new List<SampleFile>(); }
            else if (_lens == SampleLens.MostUsed) { folders = new List<SampleFolder>(); files = new List<SampleFile>(use.UsedFiles); }
            else if (_lens == SampleLens.Duplicates) { folders = new List<SampleFolder>(); files = Copies().Files; }
            else
            {
                // A search over the whole library; the roots are not matched — their "name" is a path.
                folders = new List<SampleFolder>();
                foreach (SampleFolder d in _samples.Folders) if (d.Parent != null) folders.Add(d);
                files = _samples.Files;
            }
            if (q.Length > 0)
            {
                folders = folders.FindAll(delegate (SampleFolder d) { return Has(d.Name, q); });
                files = files.FindAll(delegate (SampleFile f) { return Has(f.Name, q); });
            }

            _sampleMatches = folders.Count + files.Count;
            // The cut goes before the sort: ordering a hundred thousand names on every letter
            // typed would be the slow part, and whoever sees "first 5000" types another letter.
            if (folders.Count > SearchCap) folders = folders.GetRange(0, SearchCap);
            if (files.Count > SearchCap - folders.Count) files = files.GetRange(0, SearchCap - folders.Count);

            Comparison<SampleFolder> fo = FolderOrder(use);
            if (fo == null)
                fo = _lens == SampleLens.NeverUsed
                   ? (Comparison<SampleFolder>)delegate (SampleFolder a, SampleFolder b) { return b.TotalBytes.CompareTo(a.TotalBytes); }
                   : delegate (SampleFolder a, SampleFolder b) { return Natural(a.Name, b.Name); };
            Comparison<SampleFile> fi = FileOrder(use);
            if (fi == null && _lens == SampleLens.MostUsed) fi = use.CompareUse;
            else if (fi == null && _lens != SampleLens.Duplicates)
                fi = delegate (SampleFile a, SampleFile b) { return Natural(a.Name, b.Name); };
            folders.Sort(fo);
            // Duplicates keep SampleCopies' order: the most room wasted first, copies side by side.
            if (fi != null) files.Sort(fi);

            List<RowData> rows = new List<RowData>();
            foreach (SampleFolder d in folders) rows.Add(FolderRow(d, 0, use, true));
            foreach (SampleFile f in files) rows.Add(FileRow(f, 0, use));
            return rows;
        }

        static bool Has(string name, string q)
        {
            return name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        RowData FolderRow(SampleFolder d, int depth, SampleUsage use, bool flat)
        {
            FolderUse fu = use.Of(d);
            string name = d.Name;
            int kids = d.Children.Count + d.Files.Count;
            if (!flat && kids > 0)
                name += RowListView.CountSep
                      + (_openSampleDirs.Contains(d.Path) ? RowListView.CountClose : RowListView.CountOpen)
                      + kids.ToString("N0", Inv);

            RowData r = new RowData();
            r.Cells = SampleCells(name, d.Parent != null ? SampleIndex.Location(d.Parent) : "",
                                  d.TotalSamples, fu != null ? fu.Used : 0, fu != null ? fu.Projects : 0,
                                  fu != null ? fu.LastUsed : default(DateTime), d.TotalBytes, true,
                                  Copies().FilesIn(d), d.Created, d.Modified);
            r.Tag = d;
            r.Indent = depth;
            r.Icon = Glyph.Folder;
            r.Dim = fu == null && !UsageUnknown;
            return r;
        }

        RowData FileRow(SampleFile f, int depth, SampleUsage use)
        {
            SampleUse u = use.Of(f);
            RowData r = new RowData();
            r.Cells = SampleCells(f.Name, SampleIndex.Location(f.Folder), 0, 0, u != null ? u.Projects : 0,
                                  u != null ? u.LastUsed : default(DateTime), f.Size, false,
                                  Copies().CopiesOf(f), f.Created, f.Modified);
            r.Tag = f;
            r.Indent = depth;
            r.Icon = Glyph.Wave;
            r.NameFont = Theme.FBody;
            r.Dim = u == null && !UsageUnknown;
            return r;
        }

        /// <summary>copies — for a folder, how many of its samples lie elsewhere too; for a
        /// sample, in how many other places.</summary>
        string[] SampleCells(string name, string location, int samples, int used, int projects,
                             DateTime last, long size, bool folder, int copies, DateTime created, DateTime modified)
        {
            bool unknown = UsageUnknown;
            string[] cells = new string[_sampleCols.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                switch (_sampleCols[i].Id)
                {
                    case "Name": cells[i] = name; break;
                    case "Location": cells[i] = location; break;
                    case "Samples": cells[i] = folder ? samples.ToString("N0", Inv) : ""; break;
                    case "Used": cells[i] = !folder ? "" : unknown ? "…" : used > 0 ? used.ToString("N0", Inv) : "—"; break;
                    case "Projects": cells[i] = unknown ? "…" : projects > 0 ? projects.ToString("N0", Inv) : "—"; break;
                    case "LastUsed":
                        cells[i] = unknown ? "…" : last != default(DateTime) ? last.ToLocalTime().ToString("yyyy-MM-dd") : "never";
                        break;
                    case "Size": cells[i] = SampleSize(size); break;
                    // No copies is the ordinary case — an empty cell, not a dash in every row.
                    case "Copies": cells[i] = copies > 0 ? copies.ToString("N0", Inv) : ""; break;
                    case "Created": cells[i] = Day(created); break;
                    case "Modified": cells[i] = Day(modified); break;
                }
            }
            return cells;
        }

        static string Day(DateTime utc)
        {
            return utc == default(DateTime) ? "" : utc.ToLocalTime().ToString("yyyy-MM-dd");
        }

        /// <summary>Kilobytes for a single sample; the catalog's MB and GB for anything bigger.</summary>
        internal static string SampleSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024 * 1024) return Math.Max(1L, (long)Math.Round(bytes / 1024.0)).ToString(Inv) + " KB";
            return SizeMB(bytes);
        }

        // ---------------------------------------------------------------- order

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        /// <summary>Names the way Explorer orders them: "2. Drums" before "10. FX".</summary>
        static int Natural(string a, string b) { return StrCmpLogicalW(a ?? "", b ?? ""); }

        List<SampleFolder> SortedFolders(List<SampleFolder> list, bool roots, SampleUsage use)
        {
            List<SampleFolder> l = new List<SampleFolder>(list);
            Comparison<SampleFolder> cmp = FolderOrder(use);
            // With no column chosen the roots keep the order they were added in.
            if (cmp == null && roots) return l;
            if (cmp == null) cmp = delegate (SampleFolder a, SampleFolder b) { return Natural(a.Name, b.Name); };
            l.Sort(cmp);
            return l;
        }

        List<SampleFile> SortedFiles(List<SampleFile> list, SampleUsage use)
        {
            List<SampleFile> l = new List<SampleFile>(list);
            Comparison<SampleFile> cmp = FileOrder(use);
            if (cmp == null) cmp = delegate (SampleFile a, SampleFile b) { return Natural(a.Name, b.Name); };
            l.Sort(cmp);
            return l;
        }

        /// <summary>The chosen column's order for folders, null when none is chosen. Ties go by
        /// name, always A to Z — a reversed tie-break would only look like noise.</summary>
        Comparison<SampleFolder> FolderOrder(SampleUsage use)
        {
            if (_sampleSortId == null) return null;
            Comparison<SampleFolder> c;
            switch (_sampleSortId)
            {
                case "Name": c = delegate (SampleFolder a, SampleFolder b) { return Natural(a.Name, b.Name); }; break;
                case "Location": c = delegate (SampleFolder a, SampleFolder b) { return Natural(SampleIndex.Location(a.Parent), SampleIndex.Location(b.Parent)); }; break;
                case "Samples": c = delegate (SampleFolder a, SampleFolder b) { return a.TotalSamples.CompareTo(b.TotalSamples); }; break;
                case "Used": c = delegate (SampleFolder a, SampleFolder b) { return UsedOf(use, a).CompareTo(UsedOf(use, b)); }; break;
                case "Projects": c = delegate (SampleFolder a, SampleFolder b) { return ProjectsOf(use, a).CompareTo(ProjectsOf(use, b)); }; break;
                case "LastUsed": c = delegate (SampleFolder a, SampleFolder b) { return LastOf(use, a).CompareTo(LastOf(use, b)); }; break;
                case "Size": c = delegate (SampleFolder a, SampleFolder b) { return a.TotalBytes.CompareTo(b.TotalBytes); }; break;
                case "Copies": { SampleCopies cp = Copies(); c = delegate (SampleFolder a, SampleFolder b) { return cp.FilesIn(a).CompareTo(cp.FilesIn(b)); }; break; }
                case "Created": c = delegate (SampleFolder a, SampleFolder b) { return a.Created.CompareTo(b.Created); }; break;
                case "Modified": c = delegate (SampleFolder a, SampleFolder b) { return a.Modified.CompareTo(b.Modified); }; break;
                default: return null;
            }
            bool desc = _sortDesc;
            return delegate (SampleFolder a, SampleFolder b)
            {
                int r = desc ? c(b, a) : c(a, b);
                return r != 0 ? r : Natural(a.Name, b.Name);
            };
        }

        Comparison<SampleFile> FileOrder(SampleUsage use)
        {
            if (_sampleSortId == null) return null;
            Comparison<SampleFile> c;
            switch (_sampleSortId)
            {
                case "Location": c = delegate (SampleFile a, SampleFile b) { return Natural(SampleIndex.Location(a.Folder), SampleIndex.Location(b.Folder)); }; break;
                case "Projects": c = delegate (SampleFile a, SampleFile b) { return ProjectsOf(use, a).CompareTo(ProjectsOf(use, b)); }; break;
                case "LastUsed": c = delegate (SampleFile a, SampleFile b) { return LastOf(use, a).CompareTo(LastOf(use, b)); }; break;
                case "Size": c = delegate (SampleFile a, SampleFile b) { return a.Size.CompareTo(b.Size); }; break;
                case "Copies": { SampleCopies cp = Copies(); c = delegate (SampleFile a, SampleFile b) { return cp.CopiesOf(a).CompareTo(cp.CopiesOf(b)); }; break; }
                case "Created": c = delegate (SampleFile a, SampleFile b) { return a.Created.CompareTo(b.Created); }; break;
                case "Modified": c = delegate (SampleFile a, SampleFile b) { return a.Modified.CompareTo(b.Modified); }; break;
                default: c = delegate (SampleFile a, SampleFile b) { return Natural(a.Name, b.Name); }; break;
            }
            bool desc = _sortDesc && _sampleSortId != "Samples" && _sampleSortId != "Used";
            return delegate (SampleFile a, SampleFile b)
            {
                int r = desc ? c(b, a) : c(a, b);
                return r != 0 ? r : Natural(a.Name, b.Name);
            };
        }

        static int UsedOf(SampleUsage u, SampleFolder d) { FolderUse x = u.Of(d); return x != null ? x.Used : 0; }
        static int ProjectsOf(SampleUsage u, SampleFolder d) { FolderUse x = u.Of(d); return x != null ? x.Projects : 0; }
        static DateTime LastOf(SampleUsage u, SampleFolder d) { FolderUse x = u.Of(d); return x != null ? x.LastUsed : DateTime.MinValue; }
        static int ProjectsOf(SampleUsage u, SampleFile f) { SampleUse x = u.Of(f); return x != null ? x.Projects : 0; }
        static DateTime LastOf(SampleUsage u, SampleFile f) { SampleUse x = u.Of(f); return x != null ? x.LastUsed : DateTime.MinValue; }

        void SortSamplesBy(int column)
        {
            if (column < 0 || column >= _sampleCols.Count) return;
            string id = _sampleCols[column].Id;
            if (_sampleSortId == id) _sortDesc = !_sortDesc;
            else { _sampleSortId = id; _sortDesc = false; }
            RefillSamplesKeeping(SelectedSamplePath());
        }

        // ------------------------------------------------------------ selection

        static string PathOf(object tag)
        {
            SampleFolder d = tag as SampleFolder;
            if (d != null) return d.Path;
            SampleFile f = tag as SampleFile;
            return f != null ? f.Path : null;
        }

        static bool SamePath(object tag, string path)
        {
            string p = PathOf(tag);
            return p != null && path != null && string.Equals(p, path, StringComparison.OrdinalIgnoreCase);
        }

        string SelectedSamplePath()
        {
            RowData r = _list.Selected;
            return r != null ? PathOf(r.Tag) : null;
        }

        /// <summary>Rebuild without the entrance, then put the selection and the scroll back —
        /// one folder opened, or the index was quietly refreshed, and the rest of the list stays
        /// where it was.</summary>
        void RefillSamplesKeeping(string selectPath)
        {
            int scroll = _list.ScrollOffset, scrollX = _list.ScrollOffsetX;
            Refill(false);
            if (selectPath != null) _list.SelectRow(delegate (RowData r) { return SamePath(r.Tag, selectPath); });
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        /// <summary>The panel for the selected row: a folder with its numbers, most used samples
        /// and projects, or a sample with its wave and projects.</summary>
        void ShowSampleDetails()
        {
            RowData row = _list.Selected;
            SampleFile f = row != null ? row.Tag as SampleFile : null;
            if (f != null) _detail.ShowSample(f, Usage(), Copies(), UsageUnknown);
            else _detail.ShowFolder(row != null ? row.Tag as SampleFolder : null, Usage(), Copies(), UsageUnknown);
        }

        /// <summary>
        /// The library folders a set takes samples from, the most first — for its panel on the
        /// Sets tab. A sample counts under the folder right below its root: a pack, or a vendor's
        /// folder of packs, as the library is laid out.
        /// </summary>
        List<KeyValuePair<SampleFolder, int>> SampleFoldersOf(SetEntry s)
        {
            Dictionary<SampleFolder, int> n = new Dictionary<SampleFolder, int>();
            foreach (SampleFile f in Usage().FilesOf(s))
            {
                SampleFolder top = f.Folder;
                while (top.Parent != null && top.Parent.Parent != null) top = top.Parent;
                int c;
                n.TryGetValue(top, out c);
                n[top] = c + 1;
            }
            List<KeyValuePair<SampleFolder, int>> list = new List<KeyValuePair<SampleFolder, int>>(n);
            list.Sort(delegate (KeyValuePair<SampleFolder, int> a, KeyValuePair<SampleFolder, int> b)
            {
                return a.Value != b.Value ? b.Value.CompareTo(a.Value) : Natural(a.Key.Name, b.Key.Name);
            });
            return list;
        }

        /// <summary>A folder picked in a set's panel: the Samples tab, with the folder shown in
        /// the tree.</summary>
        void OnLibraryFolderRequested(SampleFolder d)
        {
            if (d == null) return;
            _mode.SelectedIndex = ModeSamples;
            ShowSampleInTree(d.Parent, d.Path);
        }

        /// <summary>A sample picked in the panel's "Most used": shown in the tree, its folders
        /// opened on the way.</summary>
        void OnSampleRequested(SampleFile f)
        {
            if (f != null) ShowSampleInTree(f.Folder, f.Path);
        }

        // -------------------------------------------------------------- preview

        void PlaySample(SampleFile f)
        {
            if (f == null || !f.CanPreview) return;
            // One sound at a time: a render that is playing makes way.
            bool player = _player != null && !_player.IsDisposed;
            if (player && _player.IsPlaying) _player.PlayPause();
            _preview.Volume = player ? _player.Volume : 0.8f;
            _preview.Open(f.Path, true);
            _previewing = f;
            _auditioned = f.Path;
            _previewStarted = Environment.TickCount;
            _previewTimer.Start();
            UpdatePlayerTransport();
        }

        void StopSample()
        {
            if (_previewing == null) return;
            _preview.Close();
            _previewing = null;
            _previewTimer.Stop();
            _detail.StopWave();
        }

        /// <summary>Space and the row menu: the playing one stops, any other starts. A file
        /// only Live plays silences whatever was playing.</summary>
        void ToggleSample(SampleFile f)
        {
            if (f == null) return;
            if (SamePath(_previewing, f.Path) || !f.CanPreview) { StopSample(); return; }
            PlaySample(f);
        }

        void ToggleSelectedSample()
        {
            RowData r = _list.Selected;
            SampleFile f = r != null ? r.Tag as SampleFile : null;
            if (f != null) ToggleSample(f);
            else StopSample();
        }

        void PreviewTick()
        {
            if (_previewing == null) { _previewTimer.Stop(); return; }
            if (_preview.OpenFailed)
            {
                string why = _preview.Error;
                StopSample();
                Notify(why == "File is gone" ? "File is gone" : "Can't play this file");
                return;
            }
            if (_preview.Finished) { StopSample(); return; }
            int len = _preview.Length > 0 ? _preview.Length : _detail.SampleDurationMs;
            if (len > 0 && _detail.ShowsSample(_previewing))
                _detail.SetWaveProgress(Math.Min(1f, _preview.Position / (float)len));
        }

        /// <summary>
        /// A sample plays the moment it is selected — by a click or an arrow, the way Live's
        /// browser previews; a folder stops it. The same sample put back by a refill (the list
        /// clears its selection for a moment and restores it) is not played again: _auditioned
        /// remembers what the selection already played.
        /// </summary>
        void AuditionSelection()
        {
            RowData r = _list.Selected;
            SampleFile f = r != null ? r.Tag as SampleFile : null;
            if (f == null) { StopSample(); _auditioned = null; return; }
            if (SamePath(f, _auditioned)) return;
            _auditioned = f.Path;
            if (f.CanPreview) PlaySample(f); else StopSample();
        }

        /// <summary>A click on the row that is already selected plays its sample again. The
        /// second click of a double click lands here as well, and must not restart what the
        /// first one has just started.</summary>
        void OnSelectedRowClicked(int idx)
        {
            SampleFile f = idx >= 0 && idx < _list.Rows.Count ? _list.Rows[idx].Tag as SampleFile : null;
            if (f == null || !f.CanPreview) return;
            if (SamePath(_previewing, f.Path)
                && Environment.TickCount - _previewStarted < SystemInformation.DoubleClickTime) return;
            PlaySample(f);
        }

        /// <summary>A click on the wave in the panel: play from there.</summary>
        void OnWaveClicked(float t)
        {
            RowData r = _list.Selected;
            SampleFile f = r != null ? r.Tag as SampleFile : null;
            if (f == null || !f.CanPreview) return;
            if (!SamePath(_previewing, f.Path)) PlaySample(f);
            int len = _detail.SampleDurationMs > 0 ? _detail.SampleDurationMs : _preview.Length;
            if (len > 0) _preview.Seek((int)(t * len));
        }

        /// <summary>The tab is left: the preview belongs to it, and coming back to the same
        /// sample plays it again.</summary>
        void LeaveSamples()
        {
            StopSample();
            _auditioned = null;
        }

        // ----------------------------------------------------------------- tree

        /// <summary>The "+N" tail, Enter or an arrow. The selection stays where it was — the tail
        /// is a button of its own, as on the versions of a set, and does not pick the row.</summary>
        void ToggleSampleFolder(SampleFolder d)
        {
            if (d == null || d.Children.Count + d.Files.Count == 0) return;
            if (!_openSampleDirs.Remove(d.Path)) _openSampleDirs.Add(d.Path);
            RefillSamplesKeeping(SelectedSamplePath());
        }

        /// <summary>
        /// Show something in the tree: the All lens, no search, every folder above it open, the
        /// row selected. From a flat list, and from the panel's "Most used".
        /// </summary>
        void ShowSampleInTree(SampleFolder container, string selectPath)
        {
            _lens = SampleLens.All;
            UpdateFiltersButton();
            for (SampleFolder x = container; x != null; x = x.Parent) _openSampleDirs.Add(x.Path);
            if (_search.Box.Text.Length > 0) _search.Box.Text = "";
            Refill(false);
            _list.SelectRow(delegate (RowData r) { return SamePath(r.Tag, selectPath); });
        }

        /// <summary>→ opens the selected folder, ← closes it — or, on a closed folder or a
        /// sample, steps to the parent. Only in the tree: a flat list has no levels.</summary>
        bool SampleTreeKey(bool open)
        {
            if (SampleFlat) return false;
            RowData row = _list.Selected;
            if (row == null) return false;
            SampleFolder d = row.Tag as SampleFolder;
            bool isOpen = d != null && _openSampleDirs.Contains(d.Path);
            if (open)
            {
                if (d != null && !isOpen) ToggleSampleFolder(d);
                return true;
            }
            if (d != null && isOpen) { ToggleSampleFolder(d); return true; }
            SampleFile f = row.Tag as SampleFile;
            SampleFolder parent = d != null ? d.Parent : (f != null ? f.Folder : null);
            if (parent != null)
            {
                string p = parent.Path;
                _list.SelectRow(delegate (RowData r) { return SamePath(r.Tag, p); });
            }
            return true;
        }

        /// <summary>Enter or a double click. A folder in the tree opens or closes; in a flat list
        /// it is shown in the tree. A sample plays — unless it already does: the first click of
        /// the double click has started it.</summary>
        void ActivateSample()
        {
            RowData row = _list.Selected;
            if (row == null) return;
            SampleFolder d = row.Tag as SampleFolder;
            if (d != null)
            {
                if (!SampleFlat) ToggleSampleFolder(d);
                else ShowSampleInTree(d.Parent, d.Path);
                return;
            }
            SampleFile f = row.Tag as SampleFile;
            if (f != null && !SamePath(_previewing, f.Path)) PlaySample(f);
        }

        void RevealSample()
        {
            RowData row = _list.Selected;
            string p = row != null ? PathOf(row.Tag) : null;
            if (p != null) RevealPath(p, row.Tag is SampleFile);
        }

        void RevealPath(string path, bool select)
        {
            try
            {
                if (select && File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else Notify("It is gone: " + path);
            }
            catch (Exception ex) { Diag.Line("samples: explorer: " + ex.Message); }
        }

        // ---------------------------------------------------------------- menus

        /// <summary>The Filters button on this tab: not a dialog but lenses — the tree, the
        /// dead weight, the working sounds, the same sound twice.</summary>
        void ShowSampleLens()
        {
            ContextMenuStrip m = DarkMenu.Create();
            m.ShowCheckMargin = true;
            AddLens(m, "All folders", SampleLens.All);
            AddLens(m, "Never used", SampleLens.NeverUsed);
            AddLens(m, "Most used", SampleLens.MostUsed);
            AddLens(m, "Duplicates", SampleLens.Duplicates);
            m.Show(_filtersBtn, new Point(0, _filtersBtn.Height + Sc(6)));
        }

        void AddLens(ContextMenuStrip m, string title, SampleLens lens)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(title);
            mi.Checked = _lens == lens;
            mi.Click += delegate
            {
                if (_lens == lens) return;
                _lens = lens;
                _sampleSortId = null;          // every lens has an order of its own
                _sortDesc = false;
                UpdateFiltersButton();
                Refill();
            };
            m.Items.Add(mi);
        }

        void SampleRowMenu(int idx, Point at)
        {
            if (idx < 0 || idx >= _list.Rows.Count) return;
            object tag = _list.Rows[idx].Tag;
            SampleFolder d = tag as SampleFolder;
            SampleFile f = tag as SampleFile;
            string path = PathOf(tag);
            if (path == null) return;

            ContextMenuStrip m = DarkMenu.Create();
            if (f != null && f.CanPreview)
            {
                ToolStripMenuItem play = new ToolStripMenuItem(SamePath(_previewing, f.Path) ? "Stop" : "Play");
                play.ShortcutKeyDisplayString = "Space";
                play.Click += delegate { ToggleSample(f); };
                m.Items.Add(play);
            }
            if (d != null && !SampleFlat && d.Children.Count + d.Files.Count > 0)
            {
                ToolStripMenuItem t = new ToolStripMenuItem(_openSampleDirs.Contains(d.Path) ? "Collapse" : "Expand");
                t.ShortcutKeyDisplayString = "Enter";
                t.Click += delegate { ToggleSampleFolder(d); };
                m.Items.Add(t);
            }
            if (SampleFlat)
            {
                ToolStripMenuItem t = new ToolStripMenuItem("Show in tree");
                if (d != null) t.ShortcutKeyDisplayString = "Enter";
                t.Click += delegate { ShowSampleInTree(d != null ? d.Parent : f.Folder, path); };
                m.Items.Add(t);
            }

            ToolStripMenuItem reveal = new ToolStripMenuItem("Show in Explorer");
            reveal.ShortcutKeyDisplayString = "Shift+Enter";
            reveal.Click += delegate { RevealPath(path, f != null); };
            m.Items.Add(reveal);

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy path");
            copy.Click += delegate { try { Clipboard.SetText(path); } catch { } };
            m.Items.Add(copy);

            m.Show(_list, at);
        }

        // -------------------------------------------------------------- counter

        string[] SampleCountVariants()
        {
            if (_sampleScanning && _sampleManual)
            {
                string n = _sampleFound.ToString("N0", Inv);
                return new string[] { "Indexing samples… " + n, "Indexing… " + n, n };
            }
            if (!SampleFlat)
            {
                int total = _samples.TotalSamples;
                string n = total.ToString("N0", Inv) + (total == 1 ? " sample" : " samples");
                string size = SizeMB(_samples.TotalBytes);
                return new string[] { size.Length > 0 ? n + " · " + size : n, n, total.ToString("N0", Inv) };
            }
            int rows = _list.Rows.Count;
            string r = rows.ToString("N0", Inv);
            if (_sampleMatches > rows)
            {
                string all = _sampleMatches.ToString("N0", Inv);
                return new string[] { "first " + r + " of " + all, r + " of " + all, r + "+" };
            }
            // The lens of copies says what they cost — unless a search narrows it.
            if (_lens == SampleLens.Duplicates && _search.Box.Text.Trim().Length == 0 && Copies().ExtraBytes > 0)
                return new string[] { r + " shown · " + SizeMB(Copies().ExtraBytes) + " extra", r + " shown", r };
            return new string[] { r + " shown", r };
        }

        /// <summary>How far the walk has got, 0..1, or -1 when there is nothing to measure it
        /// against — the very first walk does not know the size of the library.</summary>
        float SampleProgressShare
        {
            get
            {
                if (!_sampleScanning || !_sampleManual || _sampleEstimate <= 0) return -1f;
                return Math.Max(0.01f, Math.Min(0.99f, _sampleFound / (float)_sampleEstimate));
            }
        }
    }

    /// <summary>
    /// The Samples tab before any folder was chosen: what the tab is for and the two ways in.
    /// Not a dialog — the tab can simply be looked at.
    /// </summary>
    public sealed class SamplesEmpty : GlassControl
    {
        readonly GlassButton _fromLive = new GlassButton();
        readonly GlassButton _choose = new GlassButton();

        public event Action AddFromLive;
        public event Action ChooseFolders;

        const string Title = "No sample folders yet";
        const string Hint = "Point Alive at your sample folders, or take the ones Live already knows.";

        public SamplesEmpty()
        {
            Cursor = Cursors.Default;
            _fromLive.Text = "Add from Live";
            _fromLive.Primary = true;
            _fromLive.Click += delegate { if (AddFromLive != null) AddFromLive(); };
            Controls.Add(_fromLive);
            _choose.Text = "Choose folders…";
            _choose.Click += delegate { if (ChooseFolders != null) ChooseFolders(); };
            Controls.Add(_choose);
        }

        int TitleH { get { return TextRenderer.MeasureText("Ayg", Theme.FHead).Height; } }
        int HintH { get { return TextRenderer.MeasureText("Ayg", Theme.FLabel).Height; } }
        int BlockTop { get { return (Height - (TitleH + Sc(10) + HintH + Sc(24) + Sc(Theme.ControlH))) / 2; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _fromLive.FitToText(20);
            _choose.FitToText(16);
            _fromLive.Height = _choose.Height = Sc(Theme.ControlH);
            int gap = Sc(10);
            int left = (Width - (_fromLive.Width + gap + _choose.Width)) / 2;
            int y = BlockTop + TitleH + Sc(10) + HintH + Sc(24);
            _fromLive.Location = new Point(left, y);
            _choose.Location = new Point(_fromLive.Right + gap, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);
            int top = BlockTop;
            Chrome.DrawText(g, Title, Theme.FHead, new Rectangle(0, top, Width, TitleH), Theme.Text, Chrome.Center);
            Chrome.DrawText(g, Hint, Theme.FLabel, new Rectangle(0, top + TitleH + Sc(10), Width, HintH),
                            Theme.TextDim, Chrome.Center);
        }
    }
}
