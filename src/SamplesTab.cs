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
        enum SampleLens { All, NeverUsed, MostUsed }

        SampleIndex _samples = SampleIndex.Empty;
        SampleLens _lens = SampleLens.All;
        string _sampleSortId;

        // The usage is worked out from two snapshots and remembered until either of them
        // changes — the same device as ProjectIndex.PluginUsage.
        SampleUsage _usage = SampleUsage.Empty;
        List<SetEntry> _usageSets;
        SampleIndex _usageIndex;

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

        sealed class SampleCol
        {
            public string Id = "", Title = "";
            public int Width;
            public bool Right, Path;
            public Font Font;
            public Color? Color;
        }

        List<SampleCol> _sampleCols = new List<SampleCol>();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        bool HasSampleRoots { get { return _settings.SampleRoots.Count > 0; } }

        bool SampleFlat { get { return _lens != SampleLens.All || _search.Box.Text.Trim().Length > 0; } }

        // ------------------------------------------------------------ building

        void BuildSamples()
        {
            _samplesEmpty.AddFromLive += delegate { EditSampleRoots(true); };
            _samplesEmpty.ChooseFolders += delegate { EditSampleRoots(false); };
            _samplesEmpty.Visible = false;
            Controls.Add(_samplesEmpty);
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

        /// <summary>
        /// The usage columns say "…" rather than "never" while the sets have not been read yet:
        /// right after an update the whole catalog is parsed anew, and for that half a minute
        /// everything would look unused.
        /// </summary>
        bool UsageUnknown { get { return _index.Sets.Count == 0 && _scanning; } }

        // -------------------------------------------------------------- folders

        /// <summary>
        /// The sample folders. fromLive — the "Add from Live" button of the empty tab: the
        /// dialog opens with every Place that is not a project folder already in the list, plus
        /// the User Library and the packs, so what is left is to look and press Scan.
        /// </summary>
        bool EditSampleRoots(bool fromLive)
        {
            RootsDialog.Kind kind = RootsDialog.Samples(LiveEnvironment.Detect(), _settings.Roots);
            List<string> start = new List<string>(_settings.SampleRoots);
            if (fromLive)
                foreach (RootsDialog.Suggestion s in kind.FromLive)
                    if (!s.Projects && !SampleIndex.ContainsPath(start, s.Path)) start.Add(s.Path);

            using (RootsDialog d = new RootsDialog(start, _settings.DisabledSampleRoots, kind))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                _settings.SampleRoots.Clear();
                _settings.SampleRoots.AddRange(d.Result);
                _settings.DisabledSampleRoots.Clear();
                _settings.DisabledSampleRoots.AddRange(d.DisabledRoots);
                _settings.Save();
            }
            // A folder taken out leaves the tree now rather than after the walk.
            _samples = _samples.Only(_settings.SampleRoots, _settings.DisabledSampleRoots);
            LayoutAll();
            Refill();
            RescanSamples();
            return true;
        }

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

        List<SampleCol> SampleColumns()
        {
            bool flat = SampleFlat;
            List<SampleCol> c = new List<SampleCol>();
            string first = _lens == SampleLens.NeverUsed ? "Folder"
                         : _lens == SampleLens.MostUsed ? "Sample"
                         : flat ? "Name" : "Folder";
            // The widths are the sets table's for the same kind of value (a date 150, a size 130):
            // the layout is in logical pixels while the type follows the screen's scale, and at
            // 125% narrower columns cut "2026-09-22" to "2026-0…".
            c.Add(new SampleCol { Id = "Name", Title = first, Width = 0, Font = Theme.FTitle, Color = Theme.Text });
            // Location stretches together with the name: the two share whatever is left, and a
            // path gets as much room as a name does.
            if (flat) c.Add(new SampleCol { Id = "Location", Title = "Location", Width = 0, Path = true });
            if (_lens != SampleLens.MostUsed) c.Add(new SampleCol { Id = "Samples", Title = "Samples", Width = 110, Right = true });
            if (!flat) c.Add(new SampleCol { Id = "Used", Title = "Used", Width = 90, Right = true });
            if (_lens != SampleLens.NeverUsed)
            {
                c.Add(new SampleCol { Id = "Projects", Title = "Projects", Width = 100, Right = true });
                c.Add(new SampleCol { Id = "LastUsed", Title = "Last used", Width = 150 });
            }
            c.Add(new SampleCol { Id = "Size", Title = "Size", Width = 130, Right = true });
            return c;
        }

        void FillSamples(bool animate)
        {
            SampleUsage use = Usage();
            _list.ColumnsConfigurable = false;
            _list.ShowPinIndicator = false;
            _list.ShowPlayButton = true;
            _list.IndentColumn = "Name";
            _list.DragFilePath = delegate (RowData r)
            {
                SampleFile f = r.Tag as SampleFile;
                return f != null ? f.Path : null;
            };
            _list.PlayingTag = null;
            _list.Playing = false;

            _sampleCols = SampleColumns();
            Column[] cols = new Column[_sampleCols.Count];
            int si = -1;
            for (int i = 0; i < _sampleCols.Count; i++)
            {
                SampleCol d = _sampleCols[i];
                cols[i] = new Column(d.Title, d.Width) { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color, PathEllipsis = d.Path };
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
            if (fi == null)
                fi = _lens == SampleLens.MostUsed
                   ? (Comparison<SampleFile>)use.CompareUse
                   : delegate (SampleFile a, SampleFile b) { return Natural(a.Name, b.Name); };
            folders.Sort(fo);
            files.Sort(fi);

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
                                  fu != null ? fu.LastUsed : default(DateTime), d.TotalBytes, true);
            r.Tag = d;
            r.Indent = depth;
            r.CanPlay = false;
            r.Dim = fu == null && !UsageUnknown;
            return r;
        }

        RowData FileRow(SampleFile f, int depth, SampleUsage use)
        {
            SampleUse u = use.Of(f);
            RowData r = new RowData();
            r.Cells = SampleCells(f.Name, SampleIndex.Location(f.Folder), 0, 0, u != null ? u.Projects : 0,
                                  u != null ? u.LastUsed : default(DateTime), f.Size, false);
            r.Tag = f;
            r.Indent = depth;
            r.CanPlay = f.CanPreview;
            r.Dim = u == null && !UsageUnknown;
            return r;
        }

        string[] SampleCells(string name, string location, int samples, int used, int projects,
                             DateTime last, long size, bool folder)
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
                }
            }
            return cells;
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

        /// <summary>The panel for the selected row. Folders and samples get their own look in
        /// the next step; until then — an empty panel with the right words.</summary>
        void ShowSampleDetails()
        {
            _detail.ShowEmpty("No folder selected", "Pick a folder or a sample");
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
        /// it is shown in the tree. A sample is shown in Explorer.</summary>
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
            if (f != null) RevealPath(f.Path, true);
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

        /// <summary>The Filters button on this tab: not a dialog but three lenses — the tree,
        /// the dead weight, the working sounds.</summary>
        void ShowSampleLens()
        {
            ContextMenuStrip m = DarkMenu.Create();
            m.ShowCheckMargin = true;
            AddLens(m, "All folders", SampleLens.All);
            AddLens(m, "Never used", SampleLens.NeverUsed);
            AddLens(m, "Most used", SampleLens.MostUsed);
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
