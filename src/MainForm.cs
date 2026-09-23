using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    public sealed partial class MainForm : Form
    {
        readonly Segmented _mode = new Segmented();
        readonly FieldBox _search = new FieldBox();
        readonly FiltersButton _filtersBtn = new FiltersButton();
        readonly GlassButton _resetBtn = new GlassButton();

        // Actions as round glyphs: folder, settings, new project.
        readonly IconButton _folders = new IconButton();
        readonly IconButton _settingsBtn = new IconButton();
        readonly IconButton _helpBtn = new IconButton();
        readonly GlassButton _newProject = new GlassButton();

        // The player's mini transport in the footer — switch set and play/pause without raising
        // the player window. The layout from left to right: close, prev, play, next, seek +
        // time + track, volume, expand, set name.
        readonly IconButton _playerClose = new IconButton();
        readonly IconButton _playerPrev = new IconButton();
        readonly IconButton _playerPlayPause = new IconButton();
        readonly IconButton _playerNext = new IconButton();
        readonly SeekSlider _playerSeek = new SeekSlider();
        readonly IconButton _playerVolBtn = new IconButton();
        readonly VolumePopupControl _playerVolPopup = new VolumePopupControl();
        readonly System.Windows.Forms.Timer _volPopupTimer = new System.Windows.Forms.Timer();
        float _preMuteVolume = 0.5f;
        readonly IconButton _playerExpand = new IconButton();
        readonly PlayerSetLink _playerSetLink = new PlayerSetLink();
        readonly System.Windows.Forms.Timer _playerTimer = new System.Windows.Forms.Timer();
        string _playerTimeLeftStr = "", _playerTimeRightStr = "";
        Rectangle _rPlayerTimeLeft, _rPlayerTimeRight, _rPlayerTrack;

        // The window's own buttons.
        readonly IconButton _min = new IconButton();
        readonly IconButton _max = new IconButton();
        readonly IconButton _close = new IconButton();

        readonly RowListView _list = new RowListView();
        readonly DetailPanel _detail = new DetailPanel();
        readonly PluginSummary _summary = new PluginSummary();
        readonly HomeView _home = new HomeView();

        readonly IconButton _dice = new IconButton();
        readonly Random _rng = new Random();

        // A popup message in the bottom-left corner of the content — "Live is starting" and
        // other things worth saying but not worth asking about.
        readonly Toast _toast = new Toast();

        const int ModeHome = 0, ModeSets = 1, ModePlugins = 2, ModeSamples = 3;

        /// <summary>
        /// The tabs are two questions rather than one: which catalog we are looking at (sets,
        /// plugins or samples) and in what form (tiles or a table). Home and Sets are one and
        /// the same catalog; they share the filters, the search and the hotkeys, and only the
        /// layout and the selection are their own. So the code asks not for the tab number but
        /// for the catalog — SetsDomain, PluginsDomain, SamplesDomain — and for the view where
        /// the view matters (Tiles, TableView). There used to be only "sets, or else plugins",
        /// and every "else" had to be looked at again when the samples came.
        /// </summary>
        int _lastDomain;     // which catalog we came from — see _mode.SelectedChanged

        bool SetsDomain    { get { return _mode.SelectedIndex == ModeHome || _mode.SelectedIndex == ModeSets; } }
        bool PluginsDomain { get { return _mode.SelectedIndex == ModePlugins; } }
        bool SamplesDomain { get { return _mode.SelectedIndex == ModeSamples; } }

        /// <summary>0 — sets (Home and Sets), 1 — plugins, 2 — samples.</summary>
        int Domain { get { return SetsDomain ? 0 : PluginsDomain ? 1 : 2; } }

        bool Tiles      { get { return _mode.SelectedIndex == ModeHome; } }
        bool TableView  { get { return _mode.SelectedIndex == ModeSets; } }

        readonly Settings _settings;
        readonly ProjectIndex _index = new ProjectIndex();
        ArrangementLoader _arrangements;

        Thread _scanThread;
        CancellationTokenSource _cancel;
        volatile bool _scanning;
        bool _manualScan;
        int _scanDone, _scanTotal;
        int _lastScanInvalidate;

        string _status = "";

        // The path of the last selected set — going to the Plugins tab (a click on a plugin in
        // the details panel, for instance) rebuilds the sets list and clears the selection; on
        // returning to Sets we find the same set by this path and select it again.
        string _lastSetPath;

        readonly SetFilter _filter = new SetFilter();
        readonly PluginFilter _pluginFilterObj = new PluginFilter();
        readonly List<string> _versions = new List<string>();   // which Live versions there are at all

        // A quick filter on the plugins tab: -1 — everything, then the indices of the summary
        // cards.
        int _pluginView = -1;

        // The list sort: the column index (-1 — the default sort) and the direction. Its own
        // for each mode, so it is reset when switching Sets/Plugins — the column indices there
        // do not correspond in meaning.
        bool _sortDesc;

        // The configurable columns of the sets list: which are visible, in what order and at
        // what width. A LIST specifically, not a set: the order is chosen by the user by
        // dragging the headings, and it can no longer be recovered from the canonical Catalog.
        readonly List<string> _setOrder = new List<string>();
        readonly Dictionary<string, int> _setColW =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // id -> logical width

        // The same for the plugins table — it is configurable on equal terms with the sets.
        readonly List<string> _pluginOrder = new List<string>();
        readonly Dictionary<string, int> _pluginColW =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static bool HasCol(List<string> order, string id)
        {
            foreach (string s in order)
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A column's place in the canonical catalog — which is also the order of the
        /// menu items.</summary>
        static int CatalogPos(bool sets, string id)
        {
            if (sets)
            {
                for (int i = 0; i < Catalog.Count; i++)
                    if (string.Equals(Catalog[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            else
            {
                for (int i = 0; i < PluginCatalog.Count; i++)
                    if (string.Equals(PluginCatalog[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return int.MaxValue;
        }

        static void SortByCatalog(List<string> order, bool sets)
        {
            order.Sort(delegate (string a, string b)
                { return CatalogPos(sets, a).CompareTo(CatalogPos(sets, b)); });
        }

        string _setSortId;
        string _pluginSortId;
        List<ColDef> _setVisible = new List<ColDef>();
        List<PluginColDef> _pluginVisible = new List<PluginColDef>();

        Rectangle _rCount, _rStatus;
        int _total;                  // how many there are in the catalog in total — the denominator of "N / M shown"

        readonly HelpOverlay _help = new HelpOverlay();

        // Where the plugin data came from — shown in the status bar on the right, on the
        // plugins tab only, so that the numbers on the cards can be trusted.

        // The render listening window — one for the whole application, living beside the main
        // one.
        PlayerDialog _player;

        public MainForm()
        {
            Text = "Alive " + Application.ProductVersion + " — Ableton Live Manager";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            // The window sizes come from the mockup too, and therefore also through the screen
            // scale. 1475 and 1320 used to stand here in physical pixels: on a screen at 125%
            // the window came out 1180 mockup points instead of 1475, and the toolbar no longer
            // fitted into it — the counter ran into the buttons on the right. It is too early
            // to ask DeviceDpi here (there is no window yet), so we take the scale from the
            // screen DC.
            float k;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;

            ClientSize = new Size(Math.Min((int)(1475 * k), work.Width),
                                  Math.Min((int)(950 * k), work.Height));

            // The width is not plucked from the air: at it the toolbar still fits whole — the
            // tabs, the die, the filters with their counter, the search, the "N / N" with its
            // reset and the panel buttons on the right. On a screen narrower than that the
            // window takes its full width, but no wider.
            MinimumSize = new Size(Math.Min((int)(1340 * k), work.Width),
                                   Math.Min((int)(740 * k), work.Height));
            BackColor = Theme.Bg;
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;
            KeyPreview = true;
            AllowDrop = true;          // a folder can be dropped straight onto the window — see OnDragDrop
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _settings = Settings.Load();
            // After the defaults above and before anything is built: the sizes computed there
            // stay as the fallback for a first run, or for a window left on a screen that is no
            // longer plugged in.
            RestoreGeometry();
            Settings.RootsChanged += OnGlobalRootsChanged;
            LoadColumns();

            Build();
            ApplyTexts();
        }

        // ------------------------------------------------------- the column catalog

        delegate string CellText(SetEntry s);

        /// <summary>The description of one possible column of the sets list.</summary>
        sealed class ColDef
        {
            public string Id;
            public string En;
            public int Width;          // the logical default; 0 — the column stretches
            public bool Right;
            public bool Chips;          // draw the cell as tag pills rather than as text
            public Font Font;
            public Color? Color;
            public CellText Text;
            public Comparison<SetEntry> Sort;
        }

        // The order here is both the order of the columns on screen and the order of the items
        // in the menu.
        static readonly List<ColDef> Catalog = BuildCatalog();

        // What we show on first run and on "reset". The order is as in Catalog, or a column
        // switched on later would land in the wrong place: ToggleColumn looks for the first
        // neighbour that comes later in the catalog, and in a jumbled list such a one is found
        // too early.
        static readonly string[] DefaultSetCols =
            { "Set", "Modified", "BPM", "PluginCount", "FileCount", "Tags", "Size"};

        static List<ColDef> BuildCatalog()
        {
            List<ColDef> c = new List<ColDef>();

            c.Add(new ColDef {
                Id = "Set", En = "Set", Width = 0, Font = Theme.FTitle, Color = Theme.Text,
                // "+3" — that many versions of the same folder are hidden under this row.
                Text = delegate (SetEntry s)
                    { return s.CollapsedCount > 0 ? s.Name + "   +" + s.CollapsedCount : s.Name; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Place", En = "Place", Width = 150,
                Text = delegate (SetEntry s) { return s.Place; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Place, b.Place, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Modified", En = "Modified", Width = 150,
                Text = delegate (SetEntry s) { return s.Modified.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Modified.CompareTo(b.Modified); } });

            c.Add(new ColDef {
                Id = "Created", En = "Created", Width = 150,
                Text = delegate (SetEntry s)
                    { return s.Created == default(DateTime) ? "" : s.Created.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Created.CompareTo(b.Created); } });

            c.Add(new ColDef {
                Id = "Live", En = "Live", Width = 87,
                Text = delegate (SetEntry s) { return s.ShortVersion; },
                Sort = delegate (SetEntry a, SetEntry b) { return CompareVersion(a.ShortVersion, b.ShortVersion); } });

            c.Add(new ColDef {
                Id = "BPM", En = "BPM", Width = 81, Right = true,
                Text = delegate (SetEntry s)
                    { return s.Tempo > 0 ? s.Tempo.ToString("0.##", CultureInfo.InvariantCulture) : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tempo.CompareTo(b.Tempo); } });

            c.Add(new ColDef {
                Id = "Key", En = "Key", Width = 143,
                Text = delegate (SetEntry s) { return s.Key; },
                Sort = delegate (SetEntry a, SetEntry b)
                    {
                        // Sets with no key always go at the end rather than being mixed in.
                        bool ea = a.Key.Length == 0, eb = b.Key.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
                    } });

            c.Add(new ColDef {
                Id = "Tracks", En = "Tracks", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.Tracks > 0 ? s.Tracks.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tracks.CompareTo(b.Tracks); } });

            // Simply "how many there are in the project" — with no judgement and no colour.
            // Both are hidden by default and stand before their "Missed" neighbours: first how
            // many there are in total, then how many of them are lost.
            c.Add(new ColDef {
                Id = "PluginCount", En = "Plugins", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.Plugins.Length > 0 ? s.Plugins.Length.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Plugins.Length.CompareTo(b.Plugins.Length); } });

            c.Add(new ColDef {
                Id = "FileCount", En = "Files", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.TotalRefs > 0 ? s.TotalRefs.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.TotalRefs.CompareTo(b.TotalRefs); } });

            // Plugins and Files carry a coloured mark only; the cell text is empty. The sort is
            // specifically by the LOST ones: that is what the column is called, while it used
            // to sort silently by the total number of plugins, that is, by something other than
            // what it shows.
            c.Add(new ColDef {
                Id = "Plugins", En = "Plugins Missed", Width = 165, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingPlugins.CompareTo(b.MissingPlugins); } });

            c.Add(new ColDef {
                Id = "Files", En = "Files Missed", Width = 130, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingFiles.CompareTo(b.MissingFiles); } });

            c.Add(new ColDef {
                Id = "Tags", En = "Tags", Width = 180, Chips = true,
                Text = delegate (SetEntry s) { return ProjectMeta.JoinTags(ProjectMeta.TagsOf(s.ProjectDir)); },
                Sort = delegate (SetEntry a, SetEntry b)
                    {
                        // Unmarked ones to the end: the tags column exists to show what is
                        // marked rather than to admire the emptiness at the top of the list.
                        string ta = ProjectMeta.JoinTags(ProjectMeta.TagsOf(a.ProjectDir));
                        string tb = ProjectMeta.JoinTags(ProjectMeta.TagsOf(b.ProjectDir));
                        bool ea = ta.Length == 0, eb = tb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(ta, tb, StringComparison.CurrentCultureIgnoreCase);
                    } });

            // The weight of the whole project folder rather than of one .als: the set itself
            // always weighs a hundred or two kilobytes, while the question "what is taking up
            // space here" is about the samples and renders beside it. The size of the file
            // itself stayed in the details panel.
            c.Add(new ColDef {
                Id = "Size", En = "Project size", Width = 130, Right = true,
                Text = delegate (SetEntry s) { return SizeMB(s.ProjectSize); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.ProjectSize.CompareTo(b.ProjectSize); } });

            return c;
        }

        static ColDef FindCol(string id)
        {
            foreach (ColDef d in Catalog) if (d.Id == id) return d;
            return null;
        }

        // ------------------------------------------- the plugin column catalog

        delegate string PluginCellText(PluginStat p);

        /// <summary>The description of one possible column of the plugins table — ColDef's
        /// twin, only about a different entity.</summary>
        sealed class PluginColDef
        {
            public string Id;
            public string En;
            public int Width;          // the logical default; 0 — the column stretches
            public bool Right;
            public Font Font;
            public Color? Color;
            public PluginCellText Text;
            public Comparison<PluginStat> Sort;
        }

        static readonly List<PluginColDef> PluginCatalog = BuildPluginCatalog();

        static readonly string[] DefaultPluginCols =
            { "Plugin", "Developer", "FxType", "Format", "Sets", "Status" };

        static List<PluginColDef> BuildPluginCatalog()
        {
            List<PluginColDef> c = new List<PluginColDef>();

            c.Add(new PluginColDef {
                Id = "Plugin", En = "Plugin", Width = 0,
                Font = Theme.FTitle, Color = Theme.Text,
                Text = delegate (PluginStat p) { return p.Name; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Developer", En = "Developer", Width = 180,
                Text = delegate (PluginStat p) { return p.Vendor; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        // Nameless ones to the end: the column exists to find things by author
                        // rather than to admire the emptiness at the top of the list.
                        bool ea = a.Vendor.Length == 0, eb = b.Vendor.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        int r = string.Compare(a.Vendor, b.Vendor, StringComparison.CurrentCultureIgnoreCase);
                        return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    } });

            c.Add(new PluginColDef {
                Id = "FxType", En = "Type", Width = 150,
                Text = delegate (PluginStat p) { return p.FxType; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.FxType, b.FxType, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Format", En = "Format", Width = 120,
                Text = delegate (PluginStat p) { return p.Format; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Format, b.Format, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Sets", En = "Sets", Width = 80, Right = true,
                Text = delegate (PluginStat p) { return p.Sets > 0 ? p.Sets.ToString() : "—"; },
                Sort = delegate (PluginStat a, PluginStat b) { return a.Sets.CompareTo(b.Sets); } });

            // The version and the file come from Live's own database, so only installed ones
            // have them. Hidden by default: they are wanted when working out a specific plugin,
            // not when browsing the list.
            c.Add(new PluginColDef {
                Id = "Version", En = "Version", Width = 110,
                Text = delegate (PluginStat p) { return p.Installed != null ? p.Installed.Version : ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        string va = a.Installed != null ? a.Installed.Version : "";
                        string vb = b.Installed != null ? b.Installed.Version : "";
                        bool ea = va.Length == 0, eb = vb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return CompareVersion(va, vb);
                    } });

            c.Add(new PluginColDef {
                Id = "File", En = "File", Width = 240,
                Text = delegate (PluginStat p) { return p.Installed != null ? p.Installed.Path : ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        string pa = a.Installed != null ? a.Installed.Path : "";
                        string pb = b.Installed != null ? b.Installed.Path : "";
                        bool ea = pa.Length == 0, eb = pb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                    } });

            // A coloured mark only; the cell text is empty — like Plugins/Files on sets.
            c.Add(new PluginColDef {
                Id = "Status", En = "Status", Width = 160, Right = true,
                Text = delegate (PluginStat p) { return ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        int sa = PluginStatusRank(a), sb = PluginStatusRank(b);
                        return sa != sb ? sa.CompareTo(sb) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    } });

            return c;
        }

        static int PluginStatusRank(PluginStat p)
        {
            if (p.Match == MatchKind.Missing || (p.Installed != null && p.Installed.FileMissing)) return 2;
            if (p.Match == MatchKind.OtherFormat) return 1;
            return 0;
        }

        static PluginColDef FindPluginCol(string id)
        {
            foreach (PluginColDef d in PluginCatalog) if (d.Id == id) return d;
            return null;
        }

        /// <summary>
        /// A project folder easily runs to gigabytes, and "3841 MB" reads worse than "3.75 GB"
        /// — so past a thousand megabytes we switch to gigabytes.
        /// </summary>
        internal static string SizeMB(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / 1024.0 / 1024.0;
            if (mb >= 1024)
            {
                double gb = mb / 1024.0;
                return gb.ToString(gb >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture) + " GB";
            }
            return mb.ToString(mb >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ---------------------------------------------------- the column state

        void LoadColumns()
        {
            LoadColumnSpec(_settings.SetColumns, _setOrder, _setColW, DefaultSetCols, "Set", true);
            LoadColumnSpec(_settings.PluginColumns, _pluginOrder, _pluginColW, DefaultPluginCols, "Plugin", false);
            foreach (string tok in _settings.SampleColumns.Split(','))
            {
                int colon = tok.IndexOf(':'), w;
                if (colon > 0 && int.TryParse(tok.Substring(colon + 1), out w) && w > 0)
                    _sampleColW[tok.Substring(0, colon).Trim()] = w;
            }

            // A one-off repair of old settings: before this a column switched on fell to the
            // end, and the saved order is merely a history of presses rather than anybody's
            // intent. From here on the order is the user's again and is not touched.
            if (!_settings.ColumnsSorted)
            {
                SortByCatalog(_setOrder, true);
                SortByCatalog(_pluginOrder, false);
                _settings.ColumnsSorted = true;
                SaveColumns();
            }
        }

        /// <summary>
        /// Parsing a string of the form "Set,Modified:150,BPM:81". The order of the tokens IS
        /// the order of the columns on screen: ever since the headings became draggable it can
        /// no longer be recovered from the canonical catalog.
        ///
        /// mandatory is the column that cannot be removed (the set name, the plugin name):
        /// without it nothing is left in the row to recognise it by at all.
        /// </summary>
        void LoadColumnSpec(string spec, List<string> order, Dictionary<string, int> widths,
                            string[] defaults, string mandatory, bool sets)
        {
            order.Clear();
            widths.Clear();

            if (!string.IsNullOrEmpty(spec))
                foreach (string tok in spec.Split(','))
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;
                    string id = t;
                    int w = -1;
                    int colon = t.IndexOf(':');
                    if (colon > 0)
                    {
                        id = t.Substring(0, colon);
                        int.TryParse(t.Substring(colon + 1), out w);
                    }
                    // A column from another version of the program, or one already listed —
                    // skip it.
                    if (sets ? FindCol(id) == null : FindPluginCol(id) == null) continue;
                    if (HasCol(order, id)) continue;
                    order.Add(id);
                    if (w > 0) widths[id] = w;
                }

            if (!HasCol(order, mandatory)) order.Insert(0, mandatory);
            if (order.Count <= 1) { order.Clear(); order.AddRange(defaults); }
        }

        void SaveColumns()
        {
            _settings.SetColumns = ColumnSpec(_setOrder, _setColW, true);
            _settings.PluginColumns = ColumnSpec(_pluginOrder, _pluginColW, false);
            _settings.Save();
        }

        string ColumnSpec(List<string> order, Dictionary<string, int> widths, bool sets)
        {
            List<string> toks = new List<string>();
            foreach (string id in order)
            {
                // We do not write a width for a stretching column (Width == 0): it always takes
                // the remainder, and a remembered number would have no effect on anything
                // anyway.
                int def = sets ? WidthOf(FindCol(id)) : WidthOf(FindPluginCol(id));
                int w;
                if (def != 0 && widths.TryGetValue(id, out w) && w > 0) toks.Add(id + ":" + w);
                else toks.Add(id);
            }
            return string.Join(",", toks.ToArray());
        }

        static int WidthOf(ColDef d) { return d != null ? d.Width : 0; }
        static int WidthOf(PluginColDef d) { return d != null ? d.Width : 0; }

        // ------------------------------------------------------------ construction

        void Build()
        {
            _folders.Icon = Glyph.Folder;
            _settingsBtn.Icon = Glyph.Settings;
            _helpBtn.Icon = Glyph.Keyboard;
            _helpBtn.Click += delegate { ShowHelp(); };
            _min.Icon = Glyph.Minimize;
            _max.Icon = Glyph.Maximize;
            _close.Icon = Glyph.Close;
            _close.Danger = true;

            _settingsBtn.Click += delegate { ShowSettings(); };
            _min.Click += delegate { WindowState = FormWindowState.Minimized; };
            _max.Click += delegate { ToggleMaximize(); };
            _close.Click += delegate { Close(); };
            foreach (IconButton b in new IconButton[] { _folders, _settingsBtn, _helpBtn, _min, _max, _close })
                Controls.Add(b);

            // The new set button moved into the footer and gained a caption — getting a new
            // empty project out of a single icon was not obvious.
            Controls.Add(_newProject);

            _playerClose.Icon = Glyph.Close;
            _playerClose.Quiet = true;
            _playerClose.Visible = false;
            _playerClose.Click += delegate { if (_player != null && !_player.IsDisposed) _player.ShutDown(); };
            Controls.Add(_playerClose);

            _playerPrev.Icon = Glyph.PrevSet;
            _playerPlayPause.Icon = Glyph.Play;
            _playerNext.Icon = Glyph.NextSet;
            _playerExpand.Icon = Glyph.OpenPlaylist;
            _playerPrev.Click += delegate { if (_player != null && !_player.IsDisposed) _player.PrevSet(); };
            _playerNext.Click += delegate { if (_player != null && !_player.IsDisposed) _player.NextSet(); };
            _playerPlayPause.Click += delegate
            {
                if (_player != null && !_player.IsDisposed) _player.PlayPause();
            };
            _playerExpand.Click += delegate { ExpandPlayer(); };

            _playerSeek.Seeked += delegate (float pct)
            {
                if (_player != null && !_player.IsDisposed && _player.Audio != null && _player.Audio.IsOpen)
                {
                    int len = _player.Audio.Length;
                    if (len > 0) _player.Seek((int)(pct * len));
                }
            };

            _playerVolBtn.Icon = Glyph.VolumeHigh;
            _playerVolBtn.Click += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    if (_player.Volume > 0.001f)
                    {
                        _preMuteVolume = _player.Volume;
                        _player.Volume = 0f;
                    }
                    else
                    {
                        _player.Volume = _preMuteVolume > 0.05f ? _preMuteVolume : 0.5f;
                    }
                    _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                }
            };
            _playerVolBtn.MouseEnter += delegate
            {
                _volPopupTimer.Stop();
                if (_player != null && !_player.IsDisposed)
                {
                    _playerVolPopup.Value = _player.Volume;
                    _playerVolPopup.Visible = true;
                    _playerVolPopup.BringToFront();
                }
            };
            _playerVolBtn.MouseLeave += delegate
            {
                StartVolPopupCloseTimer();
            };
            _playerVolBtn.MouseWheel += delegate (object sender, MouseEventArgs e)
            {
                if (_player != null && !_player.IsDisposed && e.Delta != 0)
                {
                    int steps = e.Delta / 120;
                    if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                    float newVal = (float)Math.Round((_player.Volume + steps * 0.05f) / 0.05f) * 0.05f;
                    _player.Volume = Math.Max(0f, Math.Min(1f, newVal));
                    _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                }
            };

            _playerVolPopup.MouseEnter += delegate
            {
                _volPopupTimer.Stop();
            };
            _playerVolPopup.MouseLeave += delegate
            {
                StartVolPopupCloseTimer();
            };
            _playerVolPopup.ValueChanged += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    _player.Volume = _playerVolPopup.Value;
                    UpdatePlayerVolumeIcon();
                }
            };

            _volPopupTimer.Interval = 300;
            _volPopupTimer.Tick += delegate
            {
                Point pt = PointToClient(Cursor.Position);
                if (!_playerVolBtn.Bounds.Contains(pt) && !_playerVolPopup.Bounds.Contains(pt))
                {
                    _playerVolPopup.Visible = false;
                    _volPopupTimer.Stop();
                }
            };

            _playerSetLink.Click += delegate
            {
                if (_player != null && !_player.IsDisposed && _player.CurrentSet != null)
                {
                    NavigateToSet(_player.CurrentSet);
                }
            };

            _playerTimer.Interval = 50;
            _playerTimer.Tick += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    AudioPlayer audio = _player.Audio;
                    if (audio != null && audio.IsOpen)
                    {
                        int len = audio.Length, pos = audio.Position;
                        _playerTimeLeftStr = TimeStr(pos);
                        _playerTimeRightStr = len > 0 ? TimeStr(len) : "";
                        if (len > 0) _playerSeek.Progress = Math.Min(1f, pos / (float)len);
                        else _playerSeek.Progress = 0f;
                    }
                    else
                    {
                        _playerTimeLeftStr = "";
                        _playerTimeRightStr = "";
                        _playerSeek.Progress = 0f;
                    }
                    if (!_playerVolPopup.Visible)
                        _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                    UpdatePlayerTransport();
                    string sname = _player.CurrentSet != null ? _player.CurrentSet.Name : "";
                    if (_playerSetLink.SetName != sname)
                    {
                        _playerSetLink.SetName = sname;
                        LayoutAll();
                    }
                    if (!_rPlayerTimeLeft.IsEmpty) Invalidate(_rPlayerTimeLeft);
                    if (!_rPlayerTimeRight.IsEmpty) Invalidate(_rPlayerTimeRight);
                    if (!_rPlayerTrack.IsEmpty) Invalidate(_rPlayerTrack);
                }
            };

            _playerClose.Visible = _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible =
                _playerExpand.Visible = _playerSeek.Visible = _playerVolBtn.Visible = _playerVolPopup.Visible =
                _playerSetLink.Visible = false;
            Controls.Add(_playerPrev); Controls.Add(_playerPlayPause);
            Controls.Add(_playerNext); Controls.Add(_playerSeek);
            Controls.Add(_playerVolBtn); Controls.Add(_playerExpand);
            Controls.Add(_playerSetLink); Controls.Add(_playerVolPopup);

            _mode.SelectedChanged += delegate
            {
                _list.ScrollOffsetX = 0;
                _list.ScrollOffset = 0;

                // The sort and the set of columns differ between sets and plugins, and they
                // have to be reset when moving between them. Home and Sets, though, are one
                // catalog in two views: there is nothing to knock the sort down for, and the
                // person will come back to it.
                bool domainChanged = _lastDomain != Domain;
                int leaving = _lastDomain;
                _lastDomain = Domain;
                if (domainChanged)
                {
                    if (leaving == 2) LeaveSamples();
                    _pluginView = -1;
                    _summary.Selected = -1;
                    _setSortId = null; _pluginSortId = null; _sortDesc = false;
                    _list.SortColumn = -1;
                }

                // The selected set travels from the tiles to the table and back: the tabs show
                // one and the same thing, and there is no reason to lose one's place in the
                // transition.
                SetEntry setFromTiles = _home.Selected;
                RowData listRowBefore = _list.Selected;
                SetEntry setFromList = listRowBefore != null ? listRowBefore.Tag as SetEntry : null;

                UpdateFiltersButton();
                LayoutAll();
                Refill();

                if (Tiles)
                {
                    SetEntry target = setFromList ?? setFromTiles;
                    if (target != null) _home.Selected = target;
                    else RestoreLastSetSelection();
                }
                else if (TableView)
                {
                    SetEntry target = setFromTiles ?? setFromList;
                    if (target != null) _list.SelectRow(r => ReferenceEquals(r.Tag, target));
                    else RestoreLastSetSelection();
                }
                OnSelectionChanged();
            };
            Controls.Add(_mode);

            _dice.Icon = Glyph.Dice;
            _dice.SpinOnClick = true;
            _dice.Click += delegate { RollDiceFace(); RollRandomSet(); };
            Controls.Add(_dice);

            _summary.CardClicked += OnSummaryCard;
            Controls.Add(_summary);

            _search.ShowClear = true;
            // Rows appearing anew on every letter is a twitch rather than an animation: the
            // content did not "arrive", it was merely filtered.
            _search.Box.TextChanged += delegate { Refill(false); };
            Controls.Add(_search);

            _filtersBtn.Click += delegate
            {
                if (SetsDomain) EditFilters();
                else if (PluginsDomain) EditPluginFilters();
                else ShowSampleLens();
            };
            Controls.Add(_filtersBtn);

            // A word rather than a glyph: the circling arrow read as "rescan", not as "take the
            // filters off". Quiet — dim text that only lights up under the cursor.
            _resetBtn.Text = "reset";
            _resetBtn.Font = Theme.FButton;
            _resetBtn.Quiet = true;
            _resetBtn.FitToText(10);
            _resetBtn.Visible = false;
            _resetBtn.Click += delegate { ResetSearchAndFilters(); };
            Controls.Add(_resetBtn);

            _newProject.Click += delegate { NewProject(); };
            _folders.Click += delegate { EditFolders(SamplesDomain, false); };

            _list.SelectionChanged += delegate { OnSelectionChanged(); };
            _list.ItemActivated += delegate { ActivateSelected(); };
            _list.HeaderClicked += OnHeaderClicked;
            _list.HeaderRightClicked += OnHeaderRightClick;
            _list.ColumnsResized += OnColumnsResized;
            _list.RowPlayClicked += OnRowPlay;
            _list.RowRightClicked += OnListRowRightClick;
            _list.RowCountClicked += OnRowCountClicked;
            _list.SelectedRowClicked += delegate (int idx) { if (SamplesDomain) OnSelectedRowClicked(idx); };
            _list.RowTagsClicked += delegate (int idx)
            {
                if (idx >= 0 && idx < _list.Rows.Count) EditNotes(_list.Rows[idx].Tag as SetEntry);
            };
            _list.ColumnsReordered += OnColumnsReordered;
            _list.RowPinClicked += delegate (int idx)
            {
                if (idx >= 0 && idx < _list.Rows.Count) TogglePinAndRefresh(_list.Rows[idx].Tag as SetEntry);
            };
            _list.PinnedFirst = _settings.PinnedFirst;
            _list.PinnedFirstToggled += delegate { PinnedFirstChanged(_list.PinnedFirst); };
            Controls.Add(_list);

            _detail.Index = _index;
            _arrangements = new ArrangementLoader(this);
            _arrangements.Ready += _detail.OnArrangement;
            _detail.Loader = _arrangements;
            _detail.PreviewRequested += OpenPreview;
            _detail.RevealRequested += RevealSelected;
            _detail.OpenRequested += OpenSelected;
            _detail.RescueRequested += RescueSelected;
            _detail.CollectRequested += CollectSelected;
            _detail.SetRequested += OnSetRequested;
            _detail.PluginRequested += OnPluginRequested;
            _detail.SampleRequested += OnSampleRequested;
            _detail.WaveClicked += OnWaveClicked;
            _detail.NotesRequested += EditNotes;
            Controls.Add(_detail);

            _home.Overview.Open = _settings.OverviewOpen;
            _home.OverviewStateChanged += delegate
            {
                _settings.OverviewOpen = _home.Overview.Open;
                _settings.Save();
            };
            _home.PinnedFirst = _settings.PinnedFirst;
            _home.PinnedFirstToggled += delegate { PinnedFirstChanged(_home.PinnedFirst); };
            _home.Index = _index;
            _home.SetFilter = _filter;
            _home.GroupByFolder = _settings.GroupByFolder;
            _home.Loader = _arrangements;
            _arrangements.Ready += _home.OnArrangement;
            _home.Activated += OnHomeActivated;
            _home.PlayRequested += OpenPlayer;
            _home.PlayerRequested += OpenPlayerWindow;
            _home.RevealRequested += RevealSet;
            _home.DetailsRequested += OnSetRequested;   // "Show details" — go to the set in Sets
            _home.RescueRequested += RescueSet;
            _home.NotesRequested += EditNotes;
            _home.SelectionChanged += delegate { OnSelectionChanged(); };
            _home.NewProjectRequested += delegate { NewProject(); };

            _help.CloseRequested += delegate { HideHelp(); };
            Controls.Add(_help);
            Controls.Add(_home);

            BuildSamples();

            // A message over the content — we add it last and keep it in front, so that neither
            // the list nor the tiles cover it.
            Controls.Add(_toast);
            _toast.BringToFront();

        }

        /// <summary>A tile opened by a double click is the same "Open in Live" as in the
        /// list.</summary>
        void OnHomeActivated(SetEntry s)
        {
            OpenSet(s);
        }

        void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
            _max.Icon = WindowState == FormWindowState.Maximized ? Glyph.CloseFullscreen : Glyph.Maximize;
        }

        void ApplyTexts()
        {
            _mode.SetItems("Home", "Sets", "Plugins", "Samples");
            _filtersBtn.Text = "Filters";
            _filtersBtn.Count = _filter.ActiveCount;
            _newProject.Text = "New Live Set";
            _newProject.FitToText(20);
            SetSearchCue();
        }

        void UpdateFiltersButton()
        {
            if (SetsDomain)
                _filtersBtn.Count = _filter.ActiveCount;
            else if (PluginsDomain)
                _filtersBtn.Count = _pluginFilterObj.ActiveCount;
            else
                _filtersBtn.Count = _lens != SampleLens.All ? 1 : 0;
            // With a counter the pill is wider — and behind it stands the whole right half of
            // the panel.
            if (_filtersBtn.Width != Math.Max(Sc(140), _filtersBtn.PreferredWidth)) LayoutAll();
            _filtersBtn.Invalidate();
        }

        /// <summary>What is shown right now is a selection rather than the whole
        /// catalog.</summary>
        bool Filtering
        {
            get
            {
                if (_search.Box.Text.Trim().Length > 0) return true;
                return SetsDomain ? !_filter.IsEmpty
                     : PluginsDomain ? !_pluginFilterObj.IsEmpty
                     : _lens != SampleLens.All;
            }
        }

        void ResetSearchAndFilters()
        {
            if (SetsDomain) _filter.Clear();
            else if (PluginsDomain) _pluginFilterObj.Clear();
            else { _lens = SampleLens.All; _sampleSortId = null; }
            UpdateFiltersButton();
            // The field's text calls Refill itself through TextChanged — but only if it
            // actually changed.
            if (_search.Box.Text.Length > 0) _search.Box.Text = "";
            else Refill(false);
        }

        void SetSearchCue()
        {
            string cue;
            if (SamplesDomain)
                cue = "Search in samples…";
            else if (PluginsDomain)
                cue = "Search in plugins…";
            else
                cue = "Search in sets…";
            // Our own placeholder rather than the system EM_SETCUEBANNER — see the comment on
            // FieldBox.Cue.
            if (_search.Cue == cue) return;
            _search.Cue = cue;
            _search.Invalidate();
        }

        // ------------------------------------------------------------------ layout

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        /// <summary>
        /// The layout is built entirely on quantities from the mockup: a window margin of 30, a
        /// control height of 35, a round-button pitch of 45, the content from the 98 mark. The
        /// details panel sets the right boundary of the table, so the columns and the action
        /// glyphs line up on one vertical.
        /// </summary>
        void LayoutAll()
        {
            int pad = Sc(Theme.Pad);
            int h = Sc(Theme.ControlH);
            int icon = Sc(Theme.IconSize);
            int step = icon + Sc(Theme.IconGap);
            int left = pad, right = ClientSize.Width - pad;
            int y = pad;

            // The window buttons are pushed to the right edge, the actions to the left edge of
            // the panel.
            _close.SetBounds(right - icon, y, icon, icon);
            _max.SetBounds(_close.Left - step, y, icon, icon);
            _min.SetBounds(_max.Left - step, y, icon, icon);

            int panelW = Sc(Theme.PanelW);
            int panelX = right - panelW;

            _folders.SetBounds(panelX + step, y, icon, icon);
            _settingsBtn.SetBounds(panelX + step * 2, y, icon, icon);
            _helpBtn.SetBounds(panelX + step * 3, y, icon, icon);

            _mode.Height = h;
            _mode.Location = new Point(left, y);
            bool setsMode = SetsDomain;

            // The "New Live Set" button was taken out of the footer: the bottom strip is now
            // the player only. A set can still be created from the first tile in Recent.
            _newProject.Visible = false;

            _dice.SetBounds(_mode.Right + Sc(16), y, icon, icon);
            _dice.Visible = setsMode;

            _filtersBtn.SetBounds(_dice.Right + Sc(16), y,
                                  Math.Max(Sc(140), _filtersBtn.PreferredWidth), h);
            _filtersBtn.Visible = true;
            int searchX = _filtersBtn.Right + Sc(15);
            // The search does not climb onto the panel buttons even when the window is already
            // at its minimum (the screen is small and the minimum has run into its width) — it
            // then simply squeezes. And it gives way before the counter does: the counter keeps
            // room for a short caption with its reset button ("368 / 599", "150,212 samples"),
            // or with four tabs the counter went off into an ellipsis in the middle of a number.
            int countMin = Sc(150);
            int searchW = Math.Max(Sc(120), Math.Min(Sc(315), panelX - Sc(10) - searchX - Sc(12) - countMin));
            _search.SetBounds(searchX, y, searchW, h);
            // The counter's strip takes the whole gap between the search and the panel buttons:
            // "368 / 599 shown" with the reset button did not fit into the former fields of 16
            // and went off into an ellipsis at an ordinary window size already.
            _rCount = new Rectangle(_search.Right + Sc(12), y,
                                    Math.Max(0, panelX - Sc(10) - _search.Right - Sc(12)), h);
            LayoutReset();

            // The content
            int top = Sc(Theme.ContentY);
            int statusH = Sc(28);

            int footerBottom = ClientSize.Height - Sc(Theme.Pad);
            int panelBottom = footerBottom + Sc(Theme.PanelPad);
            int controlH = Sc(Theme.ControlH);
            int footerControlY = footerBottom - controlH;
            int listGap = Sc(20);

            // The bottom strip is the player only. No player, no strip: the content takes its
            // height for itself rather than leaving an empty gap across the full width.
            bool playerOpen = _player != null && !_player.IsDisposed;
            bool showTransport = playerOpen;
            bool scanLine = SetsDomain && _status.Length > 0;
            int listBottom = showTransport || scanLine ? footerControlY - Sc(10) : footerBottom;

            _newProject.SetBounds(panelX - listGap - _newProject.Width, footerControlY, _newProject.Width, controlH);

            int statusY = footerControlY + (controlH - statusH) / 2;
            _rStatus = new Rectangle(left + Sc(2), statusY, Sc(700), statusH);

            int listTop = top;

            // The mini transport goes everywhere there is something to play, the plugins tab
            // included: the caption about the plugin source has been taken out of there and the
            // strip is free.
            _playerClose.Visible = _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible
                = _playerExpand.Visible = _playerSeek.Visible = _playerVolBtn.Visible
                = _playerSetLink.Visible = showTransport;
            if (!showTransport)
            {
                _playerVolPopup.Visible = false;
                _rPlayerTimeLeft = _rPlayerTimeRight = _rPlayerTrack = Rectangle.Empty;
            }
            if (showTransport)
            {
                int trIcon = controlH;
                int trStep = trIcon + Sc(Theme.IconGap);
                // The new set button has been taken out of the footer, and its place is the
                // name's place too: there is no reason to run into an invisible rectangle and
                // cut the name off with an ellipsis.
                int contentRight = (_newProject.Visible ? _newProject.Left : panelX - listGap) - Sc(16);

                // We centre the row in the strip left under the table: below it is the window
                // margin Sc(Pad), above it only Sc(10) to the table — a row pushed against the
                // bottom margin sat noticeably above the middle of that strip.
                int playerY = listBottom + ((ClientSize.Height - listBottom) - controlH) / 2;

                int startX = left + Sc(7);

                // 1. The close cross on the left
                _playerClose.SetBounds(startX, playerY, trIcon, trIcon);
                int curX = _playerClose.Right + Sc(20);

                // 2. The Prev, Play/Pause and Next buttons
                _playerPrev.SetBounds(curX, playerY, trIcon, trIcon);
                _playerPlayPause.SetBounds(curX + trStep, playerY, trIcon, trIcon);
                _playerNext.SetBounds(curX + trStep * 2, playerY, trIcon, trIcon);
                curX = _playerNext.Right + Sc(24);

                // 3. The progress bar and the captions above it: the times at the ends of the
                // trough,
                //    the file name large and centred between them.
                int seekW = Sc(348);
                int seekPad = Sc(4);              // the trough's internal inset in SeekSlider
                int timeW = Sc(38);

                // We take the height of the caption line from the largest font in it: a number
                // set by eye shaves the tails of the letters (p, y, g) off the file name.
                int textH = TextRenderer.MeasureText("Agjpq", Theme.FTitle).Height;
                int seekH = Sc(14);
                int textY = playerY + (controlH - (textH + Sc(1) + seekH)) / 2;

                // The time is smaller than the track name, and by the top of their boxes they
                // would stand on different lines — we seat the small line on the large one's
                // baseline.
                int timeH = TextRenderer.MeasureText("Agjpq", Theme.FMini).Height;
                int timeY = textY + Theme.Baseline(Theme.FTitle) - Theme.Baseline(Theme.FMini);

                _rPlayerTimeLeft = new Rectangle(curX + seekPad, timeY, timeW, timeH);
                _rPlayerTimeRight = new Rectangle(curX + seekW - seekPad - timeW, timeY, timeW, timeH);

                int trackLeft = _rPlayerTimeLeft.Right + Sc(6);
                int trackW = Math.Max(0, (_rPlayerTimeRight.Left - Sc(6)) - trackLeft);
                _rPlayerTrack = new Rectangle(trackLeft, textY, trackW, textH);

                _playerSeek.SetBounds(curX, textY + textH + Sc(1), seekW, seekH);
                curX += seekW + Sc(24);

                // 4. The volume button and the slider popping up above it
                _playerVolBtn.SetBounds(curX, playerY, trIcon, trIcon);
                int popupW = Sc(34);
                int popupH = Sc(140);
                int popupX = _playerVolBtn.Left + (trIcon - popupW) / 2;
                int popupY = _playerVolBtn.Top - popupH - Sc(12);
                _playerVolPopup.SetBounds(popupX, popupY, popupW, popupH);
                curX += trIcon + Sc(Theme.IconGap);

                // 5. The button that expands the player (OpenPlaylist)
                _playerExpand.SetBounds(curX, playerY, trIcon, trIcon);
                curX += trIcon + Sc(16);

                // 6. The clickable set name
                string sname = (_player != null && !_player.IsDisposed && _player.CurrentSet != null) ? _player.CurrentSet.Name : "";
                _playerSetLink.SetName = sname;
                int maxNameW = Math.Max(Sc(100), contentRight - curX);
                int nameW = maxNameW;
                if (!string.IsNullOrEmpty(sname))
                {
                    int measured = TextRenderer.MeasureText(sname, Theme.FTitle).Width + Sc(8);
                    nameW = Math.Min(maxNameW, measured);
                }
                _playerSetLink.SetBounds(curX, playerY, nameW, controlH);
            }

            _summary.Visible = false;

            // The Samples tab with no folder chosen yet shows what it is for instead of an
            // empty table.
            bool emptySamples = SamplesDomain && !HasSampleRoots;
            _home.Visible = Tiles;
            _list.Visible = !Tiles && !emptySamples;
            _detail.Visible = !Tiles && !emptySamples;
            _samplesEmpty.Visible = emptySamples;
            if (emptySamples)
                _samplesEmpty.SetBounds(left, top, Math.Max(Sc(200), right - left), Math.Max(Sc(80), listBottom - top));

            if (Tiles)
                _home.SetBounds(left, top, Math.Max(Sc(200), right - left),
                                Math.Max(Sc(80), listBottom - top));

            if (!Tiles)
            {
                _list.PillRightGap = listGap;
                _list.SetBounds(left, listTop, Math.Max(Sc(200), panelX - left),
                                Math.Max(Sc(80), listBottom - listTop));
                _detail.SetBounds(panelX, top, panelW, Math.Max(Sc(120), panelBottom - top));
            }

            _toastArea = new Rectangle(left, top, Math.Max(Sc(200), right - left),
                                       Math.Max(Sc(80), listBottom - top));
            if (_toast.Visible) _toast.PlaceIn(_toastArea);
        }

        // Where a popup message sits — the bottom-left corner of the content. Computed in the
        // layout rather than at the moment of showing: the window may have been maximized or
        // stretched while there was no message, and by that point the coordinates are
        // different.
        Rectangle _toastArea;

        /// <summary>Show a message in the corner of the content for a couple of
        /// seconds.</summary>
        void Notify(string msg)
        {
            _toast.Post(msg, 2200);
            _toast.PlaceIn(_toastArea);
            _toast.BringToFront();
        }

        FormWindowState _lastWindowState;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // On a maximized window the frame matches the screen bounds exactly, yet DWM still
            // draws its rounded corners over it — the desktop shows through in the corners. We
            // kill the rounding precisely on the transition to Maximized and bring it back on
            // Normal, rather than always: an ordinary window has to stay rounded.
            if (_lastWindowState != WindowState)
            {
                _lastWindowState = WindowState;
                Glass.SetCornerRounding(this, WindowState != FormWindowState.Maximized);
                _max.Icon = WindowState == FormWindowState.Maximized ? Glyph.CloseFullscreen : Glyph.Maximize;
            }
            LayoutAll();
            if (_help.Visible) _help.Bounds = ClientRectangle;

            // While the window is being dragged by an edge we make do with a cheap repaint of
            // ourselves: Invalidate(true) walks over every child control as well, and there are
            // nearly two dozen of them here, so doing that on every resize message is pointless
            // — they repaint from their own size change anyway. We do the full repaint once,
            // when the edge is released (WM_EXITSIZEMOVE).
            if (_inSizeMove) Invalidate(); else Invalidate(true);
        }

        /// <summary>
        /// A repaint on a move is only needed when the window is moved by something other than
        /// the mouse (a keyboard snap, a move to another monitor). While a drag is under way we
        /// skip it: the content does not change from the window shifting, and extra work on
        /// every WM_MOVE is exactly what made the window trail behind the cursor.
        /// </summary>
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_inSizeMove) Invalidate(false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetSearchCue();
            LayoutAll();

            if (_index.LoadFromCache())
            {
                RefreshVersions();
                Refill();
            }
            LoadSamplesFromCache();

            Invalidate(true);
            _shown = true;

            if (_settings.IsFirstRun) { if (EditFolders(false, false)) { FlushPending(); return; } }
            StartScan(false);
            // No project folders means no scan of the sets to wait for — the library is walked
            // right away.
            if (!_scanning && !_samplesWalked) { _samplesWalked = true; StartSampleScan(false); }
            Rewatch();
            // If there was nothing to scan, the paths we were started with are all we have to
            // go on — otherwise they wait for the scan to finish.
            FlushPending();
        }

        /// <summary>
        /// We turn the glass and the rounded corners on when the handle is created rather than
        /// when the window is shown: otherwise the first frame gets drawn opaque and that is
        /// visible as a flash.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Glass.Apply(this);
            RegisterMinimizeHotkey();
        }

        // ------------------------------------------------- Win+M: minimize the window

        const int WM_HOTKEY = 0x0312;
        const int HotkeyMinimize = 0xA11E;
        const uint MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

        bool _minimizeHotkey;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Win+M is a system combination held by Explorer ("minimize all windows"), and
        /// RegisterHotKey on a taken combination honestly returns false. What is left then is
        /// the Windows behaviour: the window will minimize, but together with all the others.
        /// We write the result to the log — otherwise there is nothing to work out "why it does
        /// not minimize just mine" from.
        /// </summary>
        void RegisterMinimizeHotkey()
        {
            if (_minimizeHotkey) return;
            try
            {
                _minimizeHotkey = RegisterHotKey(Handle, HotkeyMinimize,
                                                 MOD_WIN | MOD_NOREPEAT, (uint)Keys.M);
                Diag.Line("hotkey Win+M: " + (_minimizeHotkey
                        ? "ours"
                        : "taken by Windows, falling back to the system 'minimize all'"));
            }
            catch (Exception ex) { Diag.Fail("hotkey Win+M", ex); }
        }

        /// <summary>
        /// The repeat on show is not belt and braces: a backdrop set before the first show is
        /// sometimes not picked up by DWM, and the glass appeared only after the window had
        /// been dragged to another desktop (where the window's visual is recreated).
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Glass.Apply(this);
        }

        const int CS_DROPSHADOW   = 0x00020000;   // class style
        const int WS_MINIMIZEBOX  = 0x00020000;   // window style — the same number, a different field

        /// <summary>
        /// WS_MINIMIZEBOX on a window that has no minimise button of Windows' own.
        ///
        /// The window is borderless and draws its own three buttons, so WinForms leaves the
        /// style off — there is no caption to put a button in. But the shell reads exactly that
        /// bit to decide whether a click on the taskbar button of an already active window may
        /// minimise it. Without the style the click only re-activates what is already active,
        /// and the minimise animation into the taskbar is skipped as well.
        ///
        /// We do not take WS_SYSMENU with it: that one would hand the borderless window Alt+Space
        /// and Windows' own Move/Size menu, which has nothing to do with this chrome.
        ///
        /// WS_THICKFRAME is not here either, and that one was tried. It is what the shell reads
        /// to decide whether a window may be snapped — Win+arrows, dragging to the edge of the
        /// screen — and adding it does turn the snapping on. It cannot be honoured: MinimumSize
        /// below is 1340 points wide, and half of a 2048-point screen is 1024. Windows offers a
        /// snap the window is unable to take, and falls back on nonsense — measured, Win+Up
        /// moved the window to the neighbouring monitor instead of maximising it, while without
        /// the style Win+arrows do nothing at all. Nothing is the better of the two. Snapping
        /// becomes possible only if that minimum width goes, and it is there for a reason.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                cp.Style |= WS_MINIMIZEBOX;
                return cp;
            }
        }

        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST     = 0x0084;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE  = 0x0232;

        [StructLayout(LayoutKind.Sequential)]
        struct MinMaxInfo
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        /// <summary>The window is currently being dragged or resized by an edge — a modal
        /// Windows loop is running between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE.</summary>
        bool _inSizeMove;

        protected override void WndProc(ref Message m)
        {
            // A click outside an open modal window — this is where the system sound comes from.
            if (Chrome.SwallowBlockedClick(ref m)) return;

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyMinimize)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            // A second copy was started: it handed us its command line and left. Even an empty
            // one is an instruction — "show me the window I already have".
            string[] handed = SingleInstance.Received(ref m);
            if (handed != null)
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                BringToFront();
                Activate();
                try { SetForegroundWindow(Handle); } catch { }
                OpenPaths(handed);
                return;
            }

            MediaKeys.Cmd media = MediaKeys.Parse(m);
            if (media != MediaKeys.Cmd.None && HandleMedia(media))
            {
                m.Result = (IntPtr)1;      // taken for ourselves, not passed further down the chain
                return;
            }

            // The window has been taken in hand: for the duration of the drag we stop
            // repainting on every movement — see OnMove/OnResize. We do not touch the blur
            // itself: swapping the acrylic on the fly is visible to the eye and looks worse
            // than the lag. Whoever finds it heavy (Windows 10) switches it off for good —
            // Settings.DisableGlass.
            if (m.Msg == WM_ENTERSIZEMOVE) _inSizeMove = true;
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                _inSizeMove = false;
                LayoutAll();
                Invalidate(true);
            }

            base.WndProc(ref m);

            if (m.Msg == WM_GETMINMAXINFO)
            {
                // Windows stretches a maximized window with no frame across the whole MONITOR
                // rather than the work area — and it covers the taskbar. We have no frame, so
                // there is nowhere for "sliding under the taskbar" to come from: we set the
                // maximize bound ourselves.
                //
                // We count from the monitor the window is currently on: on a second screen the
                // taskbar may be on another side or absent altogether.
                Screen sc = Screen.FromHandle(Handle);
                Rectangle work = sc.WorkingArea, full = sc.Bounds;

                MinMaxInfo mmi = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                mmi.MaxPosition = new Point(work.Left - full.Left, work.Top - full.Top);
                mmi.MaxSize = new Point(work.Width, work.Height);
                Marshal.StructureToPtr(mmi, m.LParam, false);
                return;
            }

            if (m.Msg != WM_NCHITTEST || (int)m.Result != 1) return;

            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));

            // A maximized window has no edges to resize by at all. Otherwise it went like this:
            // drag the edge of a maximized window and Windows silently took it out of Maximized
            // into an ordinary state full screen, after which "maximize" no longer did anything
            // (it was full screen as it was) while "restore down" gave back a size other than
            // the one before. Dragging by the toolbar still works — that is the regular way to
            // bring the window back to its former size.
            if (WindowState != FormWindowState.Maximized)
            {
                int edge = Chrome.EdgeHit(p, ClientSize, Sc(6));
                if (edge != 0) { m.Result = (IntPtr)edge; return; }
            }

            // The window is dragged by the toolbar strip: everything above the content, minus a
            // little, is a caption as far as Windows is concerned. The buttons standing in that
            // strip are separate windows of their own and never see this message.
            if (p.Y < Sc(Theme.ContentY) - Sc(10)) m.Result = (IntPtr)2;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Sc(Theme.ContentY) - Sc(10)) ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }

        /// <summary>
        /// Open the settings dialog.
        /// </summary>
        void ShowSettings()
        {
            // The dot has done its job the moment the settings are opened: whatever it was
            // about is on the first screen now. We write down which release it was, so the same
            // one does not light it again tomorrow.
            if (_settingsBtn.Dot)
            {
                _settingsBtn.Dot = false;
                _settingsBtn.Invalidate();
                // Written down here and not only where the button in the dialog is pressed:
                // otherwise the background check would find the same release tomorrow and light
                // the dot again for somebody who has already looked.
                if (_dotVersion.Length > 0) _settings.SeenUpdate = _dotVersion;
            }

            bool rescan;
            using (SettingsDialog d = new SettingsDialog(_settings))
            {
                d.ShowDialog(this);
                rescan = d.RescanWanted;
                if (d.SeenVersion.Length > 0) _settings.SeenUpdate = d.SeenVersion;
            }

            _settings.Save();
            if (rescan) StartScan(true);
        }

        // ------------------------------------------------------------------- help

        void ShowHelp()
        {
            if (_help.Visible) { HideHelp(); return; }
            _help.Open(this);
        }

        void HideHelp()
        {
            if (!_help.Visible) return;
            _help.Close();
            Focus();
            Invalidate(true);
        }

        /// <summary>
        /// The arrows and Ctrl+1/2/3 go through ProcessCmdKey specifically rather than
        /// OnKeyDown. Measured: they do not reach OnKeyDown — Control.PreProcessMessage first
        /// asks IsInputKey of whatever has focus, and since ordinary buttons answer "no, that
        /// is not my key", the key goes off into focus navigation (ProcessDialogKey) and
        /// silently moves the focus to a neighbouring control. ProcessCmdKey is the one point
        /// that gets a key BEFORE focus navigation, regardless of what is selected at the time.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // While the help is open, the catalog beneath it hears no keys: the arrows page
            // through the help itself rather than the selection in the list hidden behind it.
            // We let Ctrl+Q through — quitting the program has to be possible from anywhere.
            if (_help.Visible && keyData != (Keys.Control | Keys.Q))
            {
                Keys hk = keyData & Keys.KeyCode;
                if (hk == Keys.Escape || hk == Keys.F1) { HideHelp(); return true; }
                if (_help.HandleKey(hk)) return true;
                if ((keyData & Keys.Alt) == 0) return true;   // Alt+F4 and other system things — past us
            }

            // The arrows drive the catalog regardless of where the focus currently is. Through
            // ProcessCmdKey rather than OnKeyDown: otherwise focus navigation handles them
            // first and the selection drives off into a neighbouring control instead of along
            // the sets.
            if (!_search.Box.Focused && (keyData & Keys.Modifiers) == Keys.None)
            {
                Keys k = keyData & Keys.KeyCode;
                if ((k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                     || k == Keys.PageUp || k == Keys.PageDown) && MoveSelection(k))
                    return true;
            }

            if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.NumPad1))
            {
                _mode.SelectedIndex = ModeHome;
                return true;
            }
            if (keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.NumPad2))
            {
                _mode.SelectedIndex = ModeSets;
                return true;
            }
            if (keyData == (Keys.Control | Keys.D3) || keyData == (Keys.Control | Keys.NumPad3))
            {
                _mode.SelectedIndex = ModePlugins;
                return true;
            }
            if (keyData == (Keys.Control | Keys.D4) || keyData == (Keys.Control | Keys.NumPad4))
            {
                _mode.SelectedIndex = ModeSamples;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool typing = _search.Box.Focused;

            // Esc no longer closes the window: too expensive a slip for the key that closes
            // dialogs. Only Ctrl+Q closes the program. Here Esc remained the way out of the
            // search field — which is what it is pressed for in a field.
            if (e.KeyCode == Keys.Escape)
            {
                if (!typing) return;
                if (_home.Visible) _home.Focus(); else _list.Focus();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Q) { Close(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Box.Focus(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Shift && !e.Control && !e.Alt && !typing)
            {
                EditFolders(SamplesDomain, false);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F && !e.Control && !e.Alt && !e.Shift && !typing)
            {
                if (SetsDomain) EditFilters();
                else if (PluginsDomain) EditPluginFilters();
                else ShowSampleLens();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F1) { ShowHelp(); e.Handled = true; }
            // Ctrl+, — as in any other program; on a Russian layout that is the same key, and
            // its code does not depend on the layout.
            else if (e.Control && e.KeyCode == Keys.Oemcomma)
            {
                ShowSettings();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.N) { NewProject(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.T && !typing && SetsDomain)
            {
                EditNotes(SelectedSet());
                e.Handled = e.SuppressKeyPress = true;
            }

            else if (e.Control && e.KeyCode == Keys.R && !typing && SetsDomain)
            {
                RescueSet(SelectedSet());
                e.Handled = e.SuppressKeyPress = true;
            }

            // Minimize the window. Win+M cannot be taken from Windows (see
            // RegisterMinimizeHotkey), and minimizing from the keyboard needs something that
            // always works.
            else if (e.Control && e.KeyCode == Keys.M)
            {
                WindowState = FormWindowState.Minimized;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                if (SamplesDomain) RescanSamples(); else StartScan(true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F11) { ToggleMaximize(); e.Handled = true; }
            else if (e.KeyCode == Keys.Apps && !typing)
            {
                ShowMenuForSelection();
                e.Handled = e.SuppressKeyPress = true;
            }
            // Ctrl+Space — the arrangement of the selected set full screen. Checked before a
            // bare space: that one does not look at modifiers and would otherwise intercept the
            // combination for itself, starting the preview instead of the full-screen view.
            else if (e.Control && e.KeyCode == Keys.Space && !typing && SetsDomain)
            {
                OpenPreview();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Space && !e.Control && !typing && SamplesDomain)
            {
                ToggleSelectedSample();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Space && !typing && SetsDomain)
            {
                // In the search field a space stays a space — otherwise there would be nothing
                // to search with.
                TogglePlaySelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Q && !e.Control && !e.Alt && !e.Shift && !typing && SetsDomain)
            {
                TogglePinAndRefresh(SelectedSet());
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && (_list.Selected != null || SelectedSet() != null))
            {
                // It works from the search field too: while nothing is selected Enter simply
                // does nothing, so a project cannot be opened by accident while typing.
                if (e.Shift) RevealSelected(); else ActivateSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, Theme.Backdrop);
            Theme.Smooth(g);

            // The counter ends where the reset button begins. In the ordinary case it stands
            // right after the text and cuts nothing; on a narrow window it is pushed against
            // the right edge of the strip (see LayoutReset) — and then it is the text that gets
            // clipped rather than the other way round.
            Rectangle count = _rCount;
            if (_resetBtn.Visible)
                count.Width = Math.Max(0, Math.Min(count.Width, _resetBtn.Left - count.X));
            Chrome.DrawText(g, CountLabel(CountRoom()), Theme.FButton, count, Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // A folder is being held over the window — we outline it so that it is visible that
            // this is where it can be dropped. In ordinary light rather than the accent — the
            // same principle as with the row/tile selection.
            if (_dragOverWindow)
            {
                RectangleF edge = new RectangleF(1.5f, 1.5f, ClientSize.Width - 3f, ClientSize.Height - 3f);
                using (GraphicsPath ep = Theme.Round(edge, Sc(Theme.WindowR) - 1.5f))
                using (Pen pen = new Pen(Color.FromArgb(0xE0, Theme.Light), 3f))
                    g.DrawPath(pen, ep);
            }

            // The transport is drawn everywhere it is laid out — the plugins tab included.
            bool showTransport = _player != null && !_player.IsDisposed;

            if (showTransport)
            {
                // Strictly by the top of the box: the rectangles have already been spread so
                // that the time and the track name sit on one baseline (see LayoutAll).
                TextFormatFlags tfL = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextFormatFlags tfR = TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextFormatFlags tfC = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

                if (!string.IsNullOrEmpty(_playerTimeLeftStr) && !_rPlayerTimeLeft.IsEmpty)
                    Chrome.DrawText(g, _playerTimeLeftStr, Theme.FMini, _rPlayerTimeLeft, Theme.TextDim, tfL);
                if (!string.IsNullOrEmpty(_playerTimeRightStr) && !_rPlayerTimeRight.IsEmpty)
                    Chrome.DrawText(g, _playerTimeRightStr, Theme.FMini, _rPlayerTimeRight, Theme.TextDim, tfR);

                // The track (file) name above the progress bar
                string trackName = (_player != null && !_player.IsDisposed) ? _player.CurrentFileName : "";
                if (!string.IsNullOrEmpty(trackName) && !_rPlayerTrack.IsEmpty)
                    Chrome.DrawText(g, trackName, Theme.FTitle, _rPlayerTrack, Theme.Text, tfC);
            }
            else if (SetsDomain)
            {
                Chrome.DrawText(g, StatusText(), Theme.FLabel, _rStatus, Theme.TextDim, Chrome.Left);
            }

            // Each tab shows the progress of its own walk: the sets' on Home, Sets and Plugins,
            // the library's on Samples.
            float share = SamplesDomain ? SampleProgressShare
                        : _scanning && _manualScan && _scanTotal > 0
                          ? Math.Max(0.01f, Math.Min(1.0f, (float)_scanDone / _scanTotal)) : -1f;
            if (share >= 0f)
            {
                int pad = Sc(Theme.Pad);
                int left = pad;
                int right = ClientSize.Width - pad;
                int top = Sc(Theme.ContentY);
                int barX = left;
                int barW = right - left;
                int barH = Sc(3);
                int barY = top - Sc(12);

                Rectangle barRect = new Rectangle(barX, barY, barW, barH);
                Theme.FillRound(g, barRect, barH / 2f, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

                int fillW = (int)(barW * share);
                if (fillW > 0)
                {
                    Rectangle fillRect = new Rectangle(barX, barY, fillW, barH);
                    Theme.FillRound(g, fillRect, barH / 2f, Color.FromArgb(0xFF, 0x00, 0x7A, 0xCC));
                }
            }
        }

        /// <summary>The reset button stands flush behind the counter, so its place depends on
        /// the width of the finished text — we compute it separately from the general layout
        /// and anew after every rebuild, when the number may have changed.</summary>
        void LayoutReset()
        {
            bool on = Filtering && !(SamplesDomain ? _sampleScanning : _scanning);
            _resetBtn.Visible = on;
            if (!on) return;

            int h = Sc(Theme.ControlH);
            int w = TextRenderer.MeasureText(CountLabel(CountRoom()), Theme.FButton).Width;
            // The strip for the counter ends where the panel buttons begin: without a stop the
            // reset button drove straight onto them on a narrow window. The button's own inner
            // padding already keeps the word apart from the count.
            int x = Math.Min(_rCount.X + w + Sc(2), Math.Max(_rCount.X, _rCount.Right - _resetBtn.Width));
            _resetBtn.SetBounds(x, _rCount.Y + (_rCount.Height - h) / 2, _resetBtn.Width, h);
        }

        /// <summary>How much room is left for the counter: the strip minus the reset button, if
        /// there is one.</summary>
        int CountRoom()
        {
            bool on = Filtering && !(SamplesDomain ? _sampleScanning : _scanning);
            return _rCount.Width - (on ? _resetBtn.Width + Sc(2) : 0);
        }

        /// <summary>
        /// The counter caption for the allotted width: the first of the variants that fits,
        /// longest first. A clipped "368 / 5…" reads as a completely different number, while a
        /// short "368 / 599" reads as the same one — so the words go before the digits do.
        /// </summary>
        string CountLabel(int room)
        {
            string[] variants = CountVariants();
            foreach (string v in variants)
                if (room <= 0 || TextRenderer.MeasureText(v, Theme.FButton).Width <= room) return v;
            return variants[variants.Length - 1];
        }

        string[] CountVariants()
        {
            if (SamplesDomain) return SampleCountVariants();
            string t = CountText();
            return t.EndsWith(" shown")
                 ? new string[] { t, t.Substring(0, t.Length - " shown".Length) }
                 : new string[] { t };
        }

        string CountText()
        {
            if (_scanning)
                // While the roots are still being walked the total is not yet known (total =
                // 0), and "0 / 0" lied here: on a big folder that is the only thing visible for
                // seconds, and it reads as "the program is doing nothing".
                return _scanTotal > 0
                     ? "Scanning " + _scanDone + " / " + _scanTotal
                     : "Looking for sets… " + _scanDone;
            // VisibleCount rather than VisibleSets().Count: the counter is drawn on every
            // repaint of the window (twenty times a second with the player open), and building
            // a list of sets for it would be pointless.
            int n = (Tiles)
                  ? _home.VisibleCount : _list.Rows.Count;
            return Filtering ? n + " / " + _total + " shown" : n + " shown";
        }

        string StatusText()
        {
            return _status.Length > 0 ? _status : "";
        }

        // ------------------------------------------------------------------ content

        void Refill() { Refill(true); }

        /// <summary>
        /// A rebuild after a scan the person did not order. An ordinary Refill builds the list
        /// anew, and SetRows always resets both the selection and the scroll along with it —
        /// that is, a spontaneous refresh would yank a reader back to the top of the catalog
        /// and clear their choice in the middle of their work. Here both are put back, and the
        /// rows do not fly in from below again.
        ///
        /// We look the set up by path rather than by reference: after a rescan the objects in
        /// the catalog are different even when the file on disk is the same.
        /// </summary>
        void RefillPreservingView()
        {
            if (SamplesDomain) { RefillSamplesKeeping(SelectedSamplePath()); return; }
            SetEntry keep = SelectedSet();
            string keepPath = keep != null ? keep.Path : null;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;

            Refill(false);

            if (keepPath != null)
            {
                if (Tiles)
                {
                    foreach (SetEntry s in _home.VisibleSets())
                        if (string.Equals(s.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                        { _home.Selected = s; break; }
                }
                else
                {
                    string path = keepPath;
                    _list.SelectRow(delegate (RowData r)
                    {
                        SetEntry s = r.Tag as SetEntry;
                        return s != null && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            // Strictly after SelectRow: that one nudges the list to the row it found.
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        /// <summary>
        /// animate=false — rebuild the content without starting the entrance of the rows and
        /// tiles again. Only typing in the search rebuilds that way: there Refill runs on every
        /// letter, and the list kept flying in from below instead of being filtered. Every
        /// other occasion (a scan finished, a tab or view switched, filters applied, columns
        /// reordered) still shows the entrance as before.
        /// </summary>
        void Refill(bool animate)
        {
            SetSearchCue();
            LayoutAll();
            if (SetsDomain)
            {
                _total = _index.Sets.Count;
                if (Tiles)
                {
                    _home.Filter = _search.Box.Text;
                    _home.Rebuild(animate);
                }
                else
                {
                    FillSets(animate);
                }
            }
            else if (PluginsDomain)
            {
                FillPlugins(animate);
            }
            else FillSamples(animate);
            LayoutReset();
            Invalidate();
        }

        void FillSets(bool animate)
        {
            _list.IndentColumn = "Set";
            _list.DragFilePath = null;

            // The visible columns, in the order the user arranged them in.
            _setVisible = new List<ColDef>();
            foreach (string id in _setOrder)
            {
                ColDef d = FindCol(id);
                if (d != null) _setVisible.Add(d);
            }

            Column[] cols = new Column[_setVisible.Count];
            for (int i = 0; i < _setVisible.Count; i++)
            {
                ColDef d = _setVisible[i];
                int w = d.Width;                                  // the logical width
                int ov;
                if (d.Width != 0 && _setColW.TryGetValue(d.Id, out ov) && ov > 0) w = ov;
                cols[i] = new Column(d.En, w)
                    { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color, Chips = d.Chips };
            }
            _list.ColumnsConfigurable = true;
            _list.ShowPlayButton = true;
            _list.ShowPinIndicator = true;
            _list.PlayingTag = _home.PlayingTag =
                _player != null && !_player.IsDisposed ? _player.CurrentSet : null;
            _list.Playing = _home.Playing = _player != null && !_player.IsDisposed && _player.IsPlaying;
            _list.SetColumns(cols);

            // The index of the sorted column for the arrow in the header (or -1 if it has been
            // hidden).
            int si = -1;
            if (_setSortId != null)
                for (int i = 0; i < _setVisible.Count; i++)
                    if (_setVisible[i].Id == _setSortId) { si = i; break; }
            _list.SortColumn = si;
            _list.SortDescending = _sortDesc;

            string q = _search.Box.Text.Trim();

            List<SetEntry> matched = new List<SetEntry>();
            foreach (SetEntry s in _index.Sets)
            {
                if (!_filter.Matches(s)) continue;
                if (q.Length > 0 && !MatchesSet(s, q)) continue;
                matched.Add(s);
            }
            // We remember the hidden versions BEFORE collapsing: afterwards the list of heads
            // no longer remembers whom it is holding under itself, and "show the rest" has to
            // show exactly what passed the current filter — not the whole folder from disk.
            Dictionary<string, List<SetEntry>> hidden = null;
            if (_settings.GroupByFolder)
            {
                List<SetEntry> heads = ProjectIndex.CollapseByFolder(matched);

                Dictionary<SetEntry, bool> isHead = new Dictionary<SetEntry, bool>();
                foreach (SetEntry h in heads) isHead[h] = true;

                hidden = new Dictionary<string, List<SetEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (SetEntry s in matched)
                {
                    if (isHead.ContainsKey(s)) continue;
                    string dir = s.Directory ?? "";
                    List<SetEntry> bucket;
                    if (!hidden.TryGetValue(dir, out bucket)) hidden[dir] = bucket = new List<SetEntry>();
                    bucket.Add(s);
                }
                matched = heads;
            }
            else
            {
                // CollapsedCount lives on the SetEntry itself and is reset only inside
                // CollapseByFolder — scanning does not touch it. The grouping may have been
                // switched off AFTER it once counted a "+N": without the reset here that
                // caption would have stayed hanging on every row even now, when the versions
                // are no longer collapsed and each is shown separately.
                foreach (SetEntry s in matched) s.CollapsedCount = 0;
            }

            Comparison<SetEntry> chosen = null;
            if (_setSortId != null) { ColDef d = FindCol(_setSortId); if (d != null) chosen = d.Sort; }

            Comparison<SetEntry> order;
            if (chosen == null)
                order = delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); };
            else if (_sortDesc)
                // We reverse the comparison rather than the list after sorting: with the pinned
                // on top, Reverse would have turned the groups themselves around as well.
                order = delegate (SetEntry a, SetEntry b) { return chosen(b, a); };
            else
                order = chosen;

            // Pinned first — but only as the first sort key: within their own group they are
            // ordered exactly as everything else is.
            HashSet<string> pins = null;
            if (_settings.PinnedFirst)
            {
                pins = new HashSet<string>(HomeStore.Pins, StringComparer.OrdinalIgnoreCase);
                Comparison<SetEntry> inner = order;
                HashSet<string> pinSet = pins;
                order = delegate (SetEntry a, SetEntry b)
                {
                    bool pa = pinSet.Contains(a.Path), pb = pinSet.Contains(b.Path);
                    if (pa != pb) return pa ? -1 : 1;
                    return inner(a, b);
                };
            }
            matched.Sort(order);

            int pIdx = VisibleIndex("Plugins"), fIdx = VisibleIndex("Files");

            List<RowData> rows = new List<RowData>();
            foreach (SetEntry s in matched)
            {
                string dir = s.Directory ?? "";
                bool open = s.CollapsedCount > 0 && _expandedDirs.Contains(dir);
                rows.Add(RowFor(s, pIdx, fIdx, open, false));
                if (!open || hidden == null) continue;

                List<SetEntry> kids;
                if (!hidden.TryGetValue(dir, out kids)) continue;

                // Within a project the versions always go newest first, regardless of how the
                // catalog itself is sorted: this is no longer a list of projects but the
                // history of one.
                kids.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });
                foreach (SetEntry k in kids) rows.Add(RowFor(k, pIdx, fIdx, false, true));
            }
            _list.SetRows(rows, animate);

            // The divider is exactly where the pinned ones ended. No line where there is no
            // group, and no line right at the bottom when there are no unpinned ones left.
            OnSelectionChanged();
        }

        int VisibleIndex(string id)
        {
            for (int i = 0; i < _setVisible.Count; i++) if (_setVisible[i].Id == id) return i;
            return -1;
        }

        /// <summary>
        /// One row of the catalog.
        ///
        /// expanded — this folder is currently expanded and the "+3" tail has to be shown as a
        /// minus: the sign is the only thing an expanded group differs from a collapsed one by,
        /// so we change it right in the finished cell — Catalog itself knows nothing about
        /// expansion and should not, it is static.
        ///
        /// childRow — this is one of the versions shown under an expanded row rather than the
        /// project itself. RowListView indents its name so the nesting is visible even without
        /// the group being highlighted — see RowData.Indent.
        /// </summary>
        RowData RowFor(SetEntry s, int pIdx, int fIdx, bool expanded, bool childRow)
        {
            RowData r = new RowData();
            string[] cells = new string[_setVisible.Count];
            for (int i = 0; i < _setVisible.Count; i++) cells[i] = _setVisible[i].Text(s);

            if (expanded)
                for (int i = 0; i < cells.Length; i++)
                {
                    if (_setVisible[i].Id != "Set" || cells[i] == null) continue;
                    int at = cells[i].LastIndexOf(RowListView.CountSep + RowListView.CountOpen,
                                                  StringComparison.Ordinal);
                    if (at < 0) continue;
                    cells[i] = cells[i].Substring(0, at) + RowListView.CountSep + RowListView.CountClose
                             + cells[i].Substring(at + RowListView.CountSep.Length + 1);
                }

            r.Cells = cells;
            r.Indent = childRow ? 1 : 0;
            r.Tag = s;
            // We do not show the listen button on versions under an expanded row: what plays is
            // always the render of the project's principal version (see RenderScan.Find — it
            // looks at the folder as a whole rather than at a particular .als), so play on each
            // version would play one and the same file — a button with no difference in the
            // result would only confuse.
            r.CanPlay = s.HasRenders && !childRow;
            r.Pinned = HomeStore.IsPinned(s.Path);

            // We set the marks only if their columns are currently visible.
            if (pIdx >= 0 && s.Plugins.Length > 0)
                r.Marks.Add(new CellMark(pIdx, s.MissingPlugins > 0 ? Theme.Red : Theme.Green,
                                         s.MissingPlugins == 0 ? "" : s.MissingPlugins + " "));

            if (fIdx >= 0)
            {
                if (s.Error.Length > 0)
                    r.Marks.Add(new CellMark(fIdx, Theme.Red, "unreadable"));
                else if (s.TotalRefs > 0 || s.MissingFiles > 0)
                    r.Marks.Add(new CellMark(fIdx, s.MissingFiles > 0 ? Theme.Red : Theme.Green,
                     s.MissingFiles == 0 ? ""
                                         : s.MissingFiles + " "));
            }
            return r;
        }

        /// <summary>
        /// The folders whose hidden versions are currently shown under their row. It lives in
        /// memory only: expanding is "let me see what is in there" rather than a setting, and
        /// there is no reason to carry it through a restart.
        /// </summary>
        readonly HashSet<string> _expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"+3" was clicked — show this folder's versions or hide them
        /// again.</summary>
        void OnRowCountClicked(int idx)
        {
            if (SamplesDomain)
            {
                if (idx >= 0 && idx < _list.Rows.Count) ToggleSampleFolder(_list.Rows[idx].Tag as SampleFolder);
                return;
            }
            if (!SetsDomain) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            string dir = s.Directory ?? "";
            if (!_expandedDirs.Remove(dir)) _expandedDirs.Add(dir);

            // No entrance animation: one group was expanded, while the whole catalog would fly
            // in from below — as though the list had been built anew.
            SetEntry keep = SelectedSet();
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;
            Refill(false);
            if (keep != null)
            {
                string path = keep.Path;
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry other = r.Tag as SetEntry;
                    return other != null && string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase);
                });
            }
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        void FillPlugins(bool animate)
        {
            _list.IndentColumn = "Set";
            _list.DragFilePath = null;
            _list.ColumnsConfigurable = true;
            _list.ShowPlayButton = false;
            _list.ShowPinIndicator = false;

            _pluginVisible = new List<PluginColDef>();
            foreach (string id in _pluginOrder)
            {
                PluginColDef d = FindPluginCol(id);
                if (d != null) _pluginVisible.Add(d);
            }

            Column[] pcols = new Column[_pluginVisible.Count];
            for (int i = 0; i < _pluginVisible.Count; i++)
            {
                PluginColDef d = _pluginVisible[i];
                int w = d.Width;
                int ov;
                if (d.Width != 0 && _pluginColW.TryGetValue(d.Id, out ov) && ov > 0) w = ov;
                pcols[i] = new Column(d.En, w)
                    { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color };
            }
            _list.SetColumns(pcols);

            int si2 = -1;
            if (_pluginSortId != null)
                for (int i = 0; i < _pluginVisible.Count; i++)
                    if (_pluginVisible[i].Id == _pluginSortId) { si2 = i; break; }
            _list.SortColumn = si2;
            _list.SortDescending = _sortDesc;

            List<PluginStat> all = _index.PluginUsage();
            _total = all.Count;
            _summary.Update(_index.Health(all));

            string q = _search.Box.Text.Trim();
            List<PluginStat> matched = new List<PluginStat>();
            foreach (PluginStat st in all)
            {
                if (!PassesView(st)) continue;
                if (!_pluginFilterObj.Matches(st)) continue;
                if (q.Length > 0
                    && st.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.Vendor.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.FxType.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.Format.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                matched.Add(st);
            }

            // PluginUsage() already gives a sensible default order (by the number of sets, then
            // by name) — we touch it only if the user explicitly clicked a heading. We sort by
            // column id rather than by its number: columns are now both reordered and hidden,
            // so a number on its own means nothing.
            if (_pluginSortId != null)
            {
                PluginColDef d = FindPluginCol(_pluginSortId);
                if (d != null && d.Sort != null)
                {
                    Comparison<PluginStat> cmp = d.Sort;
                    matched.Sort(_sortDesc
                        ? delegate (PluginStat a, PluginStat b) { return cmp(b, a); }
                        : cmp);
                }
            }

            int stIdx = PluginVisibleIndex("Status");

            List<RowData> rows = new List<RowData>();
            foreach (PluginStat st in matched)
            {
                RowData r = new RowData();
                string[] cells = new string[_pluginVisible.Count];
                for (int i = 0; i < _pluginVisible.Count; i++) cells[i] = _pluginVisible[i].Text(st);
                r.Cells = cells;
                r.Tag = st;

                // We set the mark only if the state column is currently visible: three states —
                // Installed (✔️), not installed (❌), other format.
                if (stIdx >= 0)
                {
                    if (st.Match == MatchKind.Missing || (st.Installed != null && st.Installed.FileMissing))
                        r.Marks.Add(new CellMark(stIdx, Theme.Red, "❌"));
                    else if (st.Match == MatchKind.OtherFormat)
                        r.Marks.Add(new CellMark(stIdx, Theme.TextDim, "other format"));
                    else
                        r.Marks.Add(new CellMark(stIdx, Theme.Green, "✔️"));
                }
                rows.Add(r);
            }
            _list.SetRows(rows, animate);
            OnSelectionChanged();
        }

        int PluginVisibleIndex(string id)
        {
            for (int i = 0; i < _pluginVisible.Count; i++) if (_pluginVisible[i].Id == id) return i;
            return -1;
        }

        /// <summary>Filtering by the selected summary card.</summary>
        bool PassesView(PluginStat st)
        {
            switch (_pluginView)
            {
                case PluginSummary.Total: return st.Sets > 0;
                case PluginSummary.Missing: return st.Sets > 0 && st.Match == MatchKind.Missing;
                case PluginSummary.Installed: return st.IsInstalled;
                case PluginSummary.Unused: return st.Sets == 0;
                default: return true;
            }
        }

        void OnSummaryCard(int card)
        {
            _pluginView = _pluginView == card ? -1 : card;
            _summary.Selected = _pluginView;
            _summary.Invalidate();
            Refill();
        }

        /// <summary>
        /// Versions are compared as numbers rather than as strings: character by character
        /// "12.4.3" comes out less than "9.7.2", because '1' comes before '9'.
        /// </summary>
        static int CompareVersion(string a, string b)
        {
            string[] pa = (a ?? "").Split('.'), pb = (b ?? "").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length ? Num(pa[i]) : 0;
                int vb = i < pb.Length ? Num(pb[i]) : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The leading digits of a version fragment: "6b3" -> 6, empty -> 0.</summary>
        static int Num(string s)
        {
            int v = 0, i = 0;
            while (i < s.Length && char.IsDigit(s[i])) { v = v * 10 + (s[i] - '0'); i++; }
            return v;
        }

        /// <summary>
        /// Sorting by column id rather than by its number, in both tables: the set and the
        /// order of the columns are movable, and an index on its own means nothing between
        /// repaints.
        /// </summary>
        void OnHeaderClicked(int column)
        {
            if (SamplesDomain) { SortSamplesBy(column); return; }
            SetEntry keepSet = SelectedSet();
            string keepSetPath = keepSet != null ? keepSet.Path : null;
            PluginStat keepPlugin = SelectedPlugin();
            string keepPluginName = keepPlugin != null ? keepPlugin.Name : null;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;

            if (SetsDomain)
            {
                if (column < 0 || column >= _setVisible.Count) return;
                string id = _setVisible[column].Id;
                if (_setSortId == id) _sortDesc = !_sortDesc;
                else { _setSortId = id; _sortDesc = false; }
                Refill(false);     // FillSets will set _list.SortColumn/Descending
                if (keepSetPath != null)
                {
                    _list.SelectRow(delegate (RowData r)
                    {
                        SetEntry s = r.Tag as SetEntry;
                        return s != null && string.Equals(s.Path, keepSetPath, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }
            else if (PluginsDomain)
            {
                if (column < 0 || column >= _pluginVisible.Count) return;
                string pid = _pluginVisible[column].Id;
                if (_pluginSortId == pid) _sortDesc = !_sortDesc;
                else { _pluginSortId = pid; _sortDesc = false; }
                Refill(false);         // FillPlugins will set _list.SortColumn/Descending
                if (keepPluginName != null)
                {
                    _list.SelectRow(delegate (RowData r)
                    {
                        PluginStat p = r.Tag as PluginStat;
                        return p != null && string.Equals(p.Name, keepPluginName, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        // ------------------------------------------------------- the column menu

        /// <summary>
        /// The column menu on the right button — one and the same for both tables: the set of
        /// columns, a reset to the defaults, and for sets the grouping by folders as well. The
        /// menu used to be on sets only, and the plugins table stayed hard-wired.
        /// </summary>
        void OnHeaderRightClick(Point pt)
        {
            if (SamplesDomain) return;       // its columns are fixed — see SamplesTab
            bool sets = SetsDomain;

            ContextMenuStrip menu = DarkMenu.Create();
            menu.ShowCheckMargin = true;   // it is visible which columns are already on — as in Explorer

            if (sets)
            {
                // The grouping lives here too: it is about what the list shows, exactly as the
                // set of columns is, and there is no other place for it in the interface.
                ToolStripMenuItem group = new ToolStripMenuItem(
                    "One row per folder");
                group.Checked = _settings.GroupByFolder;
                group.Click += delegate
                {
                    _settings.GroupByFolder = !_settings.GroupByFolder;
                    group.Checked = _settings.GroupByFolder;
                    _home.GroupByFolder = _settings.GroupByFolder;
                    _settings.Save();
                    Refill();
                };
                menu.Items.Add(group);
                menu.Items.Add(new ToolStripSeparator());
            }

            // A click on an item is a toggle rather than a "do it and go" command: we do not
            // close the menu, so that several boxes can be ticked in a row. It closes as usual
            // — with a click outside or Esc (which is no longer ItemClicked but another
            // reason).
            menu.Closing += delegate (object s, ToolStripDropDownClosingEventArgs e)
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };

            List<string> order = sets ? _setOrder : _pluginOrder;
            string mandatory = sets ? "Set" : "Plugin";

            // The items are in the catalog's canonical order rather than the user's: the menu
            // is a list of what there is at all, and reordering it after the table would mean
            // hunting for the right line in a new place every time.
            List<string> ids = new List<string>();
            List<string> titles = new List<string>();
            if (sets)
                foreach (ColDef d in Catalog) { ids.Add(d.Id); titles.Add(d.En); }
            else
                foreach (PluginColDef d in PluginCatalog) { ids.Add(d.Id); titles.Add(d.En); }

            List<ToolStripMenuItem> boxes = new List<ToolStripMenuItem>();
            for (int i = 0; i < ids.Count; i++)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(titles[i]);
                mi.Checked = HasCol(order, ids[i]);
                if (ids[i] == mandatory) mi.Enabled = false;   // the principal column cannot be removed
                else
                {
                    string id = ids[i];
                    List<string> captured = order;
                    ToolStripMenuItem box = mi;
                    mi.Click += delegate { ToggleColumn(id); box.Checked = HasCol(captured, id); };
                }
                boxes.Add(mi);
                menu.Items.Add(mi);
            }

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem reset = new ToolStripMenuItem("Reset to defaults");
            List<string> resetIds = ids;
            List<ToolStripMenuItem> resetBoxes = boxes;
            List<string> resetOrder = order;
            reset.Click += delegate
            {
                ResetColumns();
                for (int i = 0; i < resetBoxes.Count; i++)
                    resetBoxes[i].Checked = HasCol(resetOrder, resetIds[i]);
            };
            menu.Items.Add(reset);

            menu.Show(_list, pt);
        }

        void ToggleColumn(string id)
        {
            if (SamplesDomain) return;
            bool sets = SetsDomain;
            List<string> order = sets ? _setOrder : _pluginOrder;
            if (id == (sets ? "Set" : "Plugin")) return;

            if (HasCol(order, id))
            {
                for (int i = 0; i < order.Count; i++)
                    if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase))
                    { order.RemoveAt(i); break; }
            }
            // A column switched on takes its own place in the catalog rather than going to the
            // end: otherwise the table reads in a completely different order from the menu it
            // was just assembled from. The heading can still be dragged afterwards.
            else
            {
                int pos = CatalogPos(sets, id), at = order.Count;
                for (int i = 0; i < order.Count; i++)
                    if (CatalogPos(sets, order[i]) > pos) { at = i; break; }
                order.Insert(at, id);
            }

            // The sort may have been on a hidden column — we go back to the default order.
            if (sets) { if (_setSortId != null && !HasCol(order, _setSortId)) _setSortId = null; }
            else { if (_pluginSortId != null && !HasCol(order, _pluginSortId)) _pluginSortId = null; }

            SaveColumns();
            int sel = _list.SelectedIndex;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;
            Refill(false);
            _list.SelectIndex(sel);
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        void ResetColumns()
        {
            if (SamplesDomain) return;
            bool sets = SetsDomain;
            List<string> order = sets ? _setOrder : _pluginOrder;
            Dictionary<string, int> widths = sets ? _setColW : _pluginColW;

            order.Clear();
            widths.Clear();
            order.AddRange(sets ? DefaultSetCols : DefaultPluginCols);
            if (sets) _setSortId = null; else _pluginSortId = null;

            SaveColumns();
            Refill();
        }

        void OnColumnsResized()
        {
            if (SamplesDomain)
            {
                // Only what differs from the default is kept, as for the sets: a default that
                // changes in a later version then reaches the columns nobody has touched.
                foreach (Column c in _list.ColumnList)
                    foreach (SampleCol d in _sampleCols)
                        if (d.Id == c.Id && c.Width > 0)
                        {
                            if (c.Width == d.Width) _sampleColW.Remove(c.Id);
                            else _sampleColW[c.Id] = c.Width;
                        }
                List<string> toks = new List<string>();
                foreach (KeyValuePair<string, int> kv in _sampleColW) toks.Add(kv.Key + ":" + kv.Value);
                _settings.SampleColumns = string.Join(",", toks.ToArray());
                _settings.Save();
                return;
            }
            Dictionary<string, int> widths = SetsDomain ? _setColW : _pluginColW;
            foreach (Column c in _list.ColumnList)
                if (c.Width > 0 && !string.IsNullOrEmpty(c.Id)) widths[c.Id] = c.Width;
            SaveColumns();
        }

        /// <summary>
        /// A heading was dragged to a new place. We move the id within the user order — that is
        /// what the table draws and what goes into the settings.
        /// </summary>
        void OnColumnsReordered(int from, int to)
        {
            if (SamplesDomain) return;
            List<string> order = SetsDomain ? _setOrder : _pluginOrder;
            if (from < 0 || from >= order.Count || to < 0 || to > order.Count || from == to) return;

            string moved = order[from];
            order.RemoveAt(from);
            if (to > from) to--;                 // after the removal everything to the right has shifted
            if (to > order.Count) to = order.Count;
            order.Insert(to, moved);
            SaveColumns();

            // The rows have not changed — only a column moved, so we put the selection and the
            // scroll back straight by row number, with no search. Without this, moving a column
            // would cost the selected project and one's place in the list.
            int sel = _list.SelectedIndex;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;
            Refill(false);                        // the rows are the same — there is no reason for them to fly in from below
            _list.SelectIndex(sel);
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
        }

        static bool MatchesSet(SetEntry s, string q)
        {
            if (s.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.ProjectName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.Path.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            foreach (string p in s.Plugins)
                if (p.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // A render is the same "project name" for somebody searching by sound rather than
            // by a set's name. The selection is the same as the preview uses (without Samples),
            // see RenderNames.
            foreach (string r in s.RenderNames)
                if (r.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // One's own tags and notes are search material too: otherwise a label put on by
            // hand would be visible only to the eye in the panel on the right.
            foreach (string tag in ProjectMeta.TagsOf(s.ProjectDir))
                if (tag.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (ProjectMeta.NoteOf(s.ProjectDir).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                return true;

            return false;
        }

        void OnSelectionChanged()
        {
            // The audition follows the person's selection only — not the refill that clears the
            // selection for a moment before putting it back.
            if (SamplesDomain) { ShowSampleDetails(); AuditionSelection(); return; }
            if (PluginsDomain)
            {
                RowData sel = _list.Selected;
                _detail.ShowPlugin(sel == null ? null : sel.Tag as PluginStat);
                return;
            }

            SetEntry s = SelectedSet();
            _detail.Show(s);
            if (s != null) _lastSetPath = s.Path;

            if (Tiles)
            {
                RowData cur = _list.Selected;
                if (s != null && (cur == null || !ReferenceEquals(cur.Tag, s)))
                    _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, s); });
            }
            else
            {
                if (s != null && !ReferenceEquals(_home.Selected, s))
                    _home.Selected = s;
            }
        }

        PluginStat SelectedPlugin()
        {
            RowData sel = _list.Selected;
            return sel == null ? null : sel.Tag as PluginStat;
        }

        void ActivateSelected()
        {
            // There is nothing to activate on a plugin: a double click used to carry one off to
            // the Sets tab filtered by that plugin — an unexpected jump instead of an action on
            // what was clicked. A plugin's sets are listed in the details panel as it is.
            if (SamplesDomain) { ActivateSample(); return; }
            if (!SetsDomain) return;
            OpenSelected();
        }

        /// <summary>A click on a set in a plugin's "Sets" list or on "Show Details" on the home
        /// page — a jump to it on the Sets tab in table view, with the details panel
        /// opened.</summary>
        void OnSetRequested(SetEntry set)
        {
            if (set == null) return;
            _search.Box.Text = "";
            _mode.SelectedIndex = ModeSets;
            _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, set); });

            // A version hidden under a collapsed row has no row of its own in the list —
            // SelectRow will not find it. We show it straight in the panel: a click on a
            // version has to show the version rather than silently do nothing.
            RowData sel = _list.Selected;
            if (sel == null || !ReferenceEquals(sel.Tag, set)) _detail.Show(set);
        }

        /// <summary>A click on a plugin in a set's "Plugins" list — the reverse jump, to the
        /// plugin itself on the Plugins tab. The rows there are rebuilt on every Refill, so we
        /// look it up by name rather than by object reference.</summary>
        void OnPluginRequested(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName)) return;
            _search.Box.Text = "";
            _mode.SelectedIndex = ModePlugins;
            _list.SelectRow(delegate (RowData r)
            {
                PluginStat st = r.Tag as PluginStat;
                return st != null && string.Equals(st.Name, pluginName, StringComparison.OrdinalIgnoreCase);
            });
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Show the set at this path — or anything lying inside its project folder. False: the
        /// catalog knows nothing about it, and the caller decides what that means. Asked from
        /// another thread the answer is not available, so the work is handed to the UI thread
        /// and false comes back.
        /// </summary>
        public bool SelectSetByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { SelectSetByPath(path); }); } catch { }
                return false;
            }

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            _search.Box.Text = "";
            if (!_filter.IsEmpty)
            {
                _filter.Clear();
                UpdateFiltersButton();
            }

            // Changing the tab will rebuild the list itself; if we are already on it, we
            // rebuild here.
            bool tabChanged = !TableView;
            _mode.SelectedIndex = ModeSets;
            if (!tabChanged) Refill();

            SetEntry matched = null;
            _list.SelectRow(delegate (RowData r)
            {
                SetEntry s = r.Tag as SetEntry;
                if (s == null) return false;
                if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    matched = s;
                    return true;
                }
                if (!string.IsNullOrEmpty(s.ProjectDir) &&
                    (string.Equals(s.ProjectDir, path, StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith(s.ProjectDir, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = s;
                    return true;
                }
                return false;
            });

            SetEntry specific = null;
            foreach (SetEntry s in _index.Sets)
            {
                if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    specific = s;
                    break;
                }
            }

            if (specific != null)
                _detail.Show(specific);
            else if (matched != null && _list.Selected == null)
                _detail.Show(matched);

            BringToFront();
            Activate();
            try { SetForegroundWindow(Handle); } catch { }
            return specific != null || matched != null;
        }

        // ------------------------------------------------------------------ actions

        SetEntry SelectedSet()
        {
            if (Tiles)
                return _home.Selected;
            RowData sel = _list.Selected;
            return sel == null ? null : sel.Tag as SetEntry;
        }

        /// <summary>
        /// Returning to the Sets tab (from Plugins, say, where a click on a plugin in the
        /// details panel took us) rebuilds the list and clears the selection — here we find the
        /// same set by path and select it again. It quietly does nothing if a set with that
        /// path is not visible right now — excluded by a filter, for instance.
        /// </summary>
        /// <summary>
        /// The star was toggled — on the tiles or in the table header, it makes no difference.
        /// The setting is one for both views, and the other view has to learn of it at once
        /// rather than on the next visit.
        /// </summary>
        void PinnedFirstChanged(bool on)
        {
            _settings.PinnedFirst = on;
            _settings.Save();
            _list.PinnedFirst = on;
            _home.PinnedFirst = on;
            // We do not rebuild the tiles anew but move them: the pinned ones travel upwards
            // before one's eyes, and it is visible what exactly changed.
            if (Tiles) _home.RebuildTransition(); else Refill();
        }

        void RestoreLastSetSelection()
        {
            if (_lastSetPath == null) return;
            string path = _lastSetPath;

            if (Tiles)
            {
                foreach (SetEntry s in _home.VisibleSets())
                    if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                    { _home.Select(s); return; }
            }
            else
            {
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry s = r.Tag as SetEntry;
                    return s != null && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        /// <summary>
        /// The die next to the view switch — it picks a random set out of the same set of them
        /// as is on screen right now (filters and search included) and does not touch the view
        /// itself: the tiles stay tiles and the list stays a list.
        /// </summary>
        /// <summary>
        /// The die shows a new face on every throw. We throw out a repeat: the same face twice
        /// in a row reads as "the button did not work" rather than as honest chance — behind
        /// the glyph's spin there has to be a visible change.
        /// </summary>
        void RollDiceFace()
        {
            int cur = _dice.Icon - Glyph.Dice1;
            int next = _rng.Next(5);
            if (next >= cur) next++;          // 0..5 without the current face
            _dice.Icon = Glyph.Dice1 + next;
        }

        void RollRandomSet()
        {
            bool tiles = Tiles;
            SetEntry pick;
            if (tiles)
            {
                List<SetEntry> pool = _home.VisibleSets();
                if (pool.Count == 0) return;
                pick = pool[_rng.Next(pool.Count)];
                _home.Select(pick);
            }
            else
            {
                List<SetEntry> pool = new List<SetEntry>();
                foreach (RowData row in _list.Rows)
                {
                    SetEntry s = row.Tag as SetEntry;
                    if (s != null) pool.Add(s);
                }
                if (pool.Count == 0) return;
                pick = pool[_rng.Next(pool.Count)];
                _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, pick); });
            }
        }

        void OpenSelected()
        {
            OpenSet(SelectedSet());
        }

        void RescueSelected()
        {
            RescueSet(SelectedSet());
        }

        /// <summary>Open a specific set in Live — shared by the list and the home
        /// tiles.</summary>
        void OpenSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true });
                // Live does not come up at once — the same "as though the button did not work"
                // as with starting an empty Live, only here it is also not immediately clear
                // WHICH set is opening: it keeps the name in the title bar of its splash lines
                // too.
                Notify("Opening “" + s.Name + ".als”…");
            }
            catch (Exception ex) { Status("Could not open: " + ex.Message); }
        }

        /// <summary>Show a set in Explorer — shared by the list and the home tiles.</summary>
        void RevealSet(SetEntry s)
        {
            if (s == null) return;
            try
            {
                if (File.Exists(s.Path)) Process.Start("explorer.exe", "/select,\"" + s.Path + "\"");
                else if (Directory.Exists(s.Directory)) Process.Start("explorer.exe", "\"" + s.Directory + "\"");
            }
            catch { }
        }

        /// <summary>
        /// The rescue helper: a set will not open and it has to be worked out which plugin is
        /// bringing it down. It does not touch the original — it works on probe copies, see
        /// RescueDialog.
        /// </summary>
        void RescueSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }

            using (RescueDialog d = new RescueDialog(s, _index.Inventory))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                    Notify("Saved " + Path.GetFileName(d.Produced));
            }
        }

        void CollectSelected()
        {
            SetEntry s = SelectedSet();
            CollectSet(s);
        }

        /// <summary>
        /// Collect a project: every media file it needs into one folder beside it, plus a copy
        /// of the set with the paths rewritten. The original is not touched — see CollectAll.
        /// </summary>
        void CollectSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }

            using (CollectDialog d = new CollectDialog(s, _index.Env, _settings))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                {
                    Notify(d.Failed > 0
                        ? string.Format("Exported to {0} — {1} file(s) could not be copied, see the log",
                                        Path.GetFileName(d.Produced), d.Failed)
                        : "Exported to " + Path.GetFileName(d.Produced));

                    // Do not open Explorer on a half-collected folder: the toast about the
                    // failures has already sent the person to the log rather than to look at
                    // what is missing there.
                    if (d.Failed == 0)
                    {
                        // We show the archive selected in its folder: opening a .zip as a
                        // folder would mean hiding the very thing the person has just
                        // collected.
                        string arg = File.Exists(d.Produced)
                            ? "/select,\"" + d.Produced + "\""
                            : "\"" + d.Produced + "\"";
                        try { Process.Start("explorer.exe", arg); }
                        catch (Exception ex) { Diag.Line("collect: explorer: " + ex.Message); }
                    }
                }
            }
        }

        void RevealSelected()
        {
            if (SamplesDomain) { RevealSample(); return; }
            if (PluginsDomain)
            {
                PluginStat st = SelectedPlugin();
                if (st == null || st.Installed == null || st.Installed.Path.Length == 0) return;
                try
                {
                    if (File.Exists(st.Installed.Path) || Directory.Exists(st.Installed.Path))
                        Process.Start("explorer.exe", "/select,\"" + st.Installed.Path + "\"");
                    else Status("The plugin file is gone: " + st.Installed.Path);
                }
                catch { }
                return;
            }

            RevealSet(SelectedSet());
        }

        void EditFilters()
        {
            using (FiltersDialog d = new FiltersDialog(_filter, _index.Sets, _versions))
            {
                // The filters apply live: while the window is open, the list and the "N shown"
                // counter behind it change before one's eyes, and the button at the bottom
                // simply closes.
                d.Changed += delegate
                {
                    _filter.CopyFrom(d.Result);
                    UpdateFiltersButton();
                    Refill(false);
                };
                d.ShowDialog(this);
                _filter.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill(false);
            }
        }

        void EditPluginFilters()
        {
            List<PluginStat> all = _index.PluginUsage();
            using (PluginFiltersDialog d = new PluginFiltersDialog(_pluginFilterObj, all))
            {
                // The filters apply live: while the window is open, the plugin list behind it
                // changes before one's eyes, and the button at the bottom simply closes.
                d.Changed += delegate
                {
                    _pluginFilterObj.CopyFrom(d.Result);
                    UpdateFiltersButton();
                    Refill(false);
                };
                d.ShowDialog(this);
                _pluginFilterObj.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill(false);
            }
        }

        // --------------------------------------------------- keyboard: navigation

        /// <summary>An arrow moves to a neighbouring set. The list and the tiles walk
        /// differently.</summary>
        bool MoveSelection(Keys k)
        {
            if (Tiles)
            {
                switch (k)
                {
                    case Keys.Left: return _home.MoveSelection(-1, 0);
                    case Keys.Right: return _home.MoveSelection(+1, 0);
                    case Keys.Up: case Keys.PageUp: return _home.MoveSelection(0, -1);
                    case Keys.Down: case Keys.PageDown: return _home.MoveSelection(0, +1);
                }
                return false;
            }

            if (SamplesDomain && (k == Keys.Left || k == Keys.Right)) return SampleTreeKey(k == Keys.Right);

            switch (k)
            {
                case Keys.Up: return _list.MoveSelection(-1);
                case Keys.Down: return _list.MoveSelection(+1);
                case Keys.PageUp: return _list.MoveSelection(-_list.PageStep);
                case Keys.PageDown: return _list.MoveSelection(+_list.PageStep);
            }
            return false;   // there is nowhere to go left and right in a list
        }

        /// <summary>The context menu key — the same menu as on the right mouse
        /// button.</summary>
        void ShowMenuForSelection()
        {
            if (SamplesDomain)
            {
                int i = _list.SelectedIndex;
                if (i >= 0) SampleRowMenu(i, _list.RowMenuPoint(i));
                return;
            }
            if (!SetsDomain) return;
            if (Tiles) { _home.ShowMenuForSelected(); return; }

            int idx = _list.SelectedIndex;
            if (idx >= 0) OnListRowRightClick(idx, _list.RowMenuPoint(idx));
        }

        // -------------------------------------------------- keyboard: listening

        /// <summary>
        /// Space previews the render of the selected set. If the selected one is the same set
        /// as is already in the player, this is pause/resume (PlayOrToggle sees to that); if it
        /// is a different one, the player moves to it.
        /// </summary>
        void TogglePlaySelected()
        {
            SetEntry s = SelectedSet();
            bool player = _player != null && !_player.IsDisposed && _player.CurrentSet != null;

            if (s == null || !s.HasRenders)
            {
                // There is nothing to listen to on the selected one — but if something is
                // already playing, it is more logical to read space as a pause than to do
                // nothing.
                if (player) { _player.PlayPause(); UpdatePlayerTransport(); }
                else if (s != null)
                    Status("No renders next to this set");
                return;
            }

            if (Tiles) { OpenPlayer(s); return; }
            int idx = _list.SelectedIndex;
            if (idx >= 0) OnRowPlay(idx);
        }

        /// <summary>
        /// The media keys. "Next track" here means the next SET in the playlist currently on
        /// screen rather than the next render within one set: the buttons in the player itself
        /// walk between the renders of one project.
        ///
        /// false — we do not take the command, and Windows passes it on to an ordinary player:
        /// when nothing of ours is playing, there is no reason to take a person's pause in
        /// Spotify away from them.
        /// </summary>
        bool HandleMedia(MediaKeys.Cmd cmd)
        {
            bool player = _player != null && !_player.IsDisposed && _player.CurrentSet != null;

            switch (cmd)
            {
                case MediaKeys.Cmd.Next:
                    if (!player) return false;
                    _player.NextSet();
                    break;

                case MediaKeys.Cmd.Prev:
                    if (!player) return false;
                    _player.PrevSet();
                    break;

                case MediaKeys.Cmd.Stop:
                case MediaKeys.Cmd.Pause:
                    if (!player || !_player.IsPlaying) return false;
                    _player.PlayPause();
                    break;

                case MediaKeys.Cmd.Play:
                case MediaKeys.Cmd.PlayPause:
                    if (player) _player.PlayPause();
                    else if (SetsDomain) TogglePlaySelected();
                    else return false;
                    break;

                default: return false;
            }

            UpdatePlayerTransport();
            return true;
        }

        /// <summary>
        /// A project's tags and note. They are bound to the folder, so an edit is immediately
        /// visible to every version of the set in it — and the list has to be rebuilt whole
        /// rather than just the row.
        /// </summary>
        void EditNotes(SetEntry s)
        {
            if (s == null) return;
            using (NotesDialog d = new NotesDialog(s))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int scroll = _list.ScrollOffset;
                int scrollX = _list.ScrollOffsetX;
                Refill(false);

                // Refill rebuilds the list from scratch (SetRows always resets the selection —
                // so both the panel on the right and the row highlight go out in the middle of
                // editing that very row's tags). The set object does not change, so we simply
                // select it again.
                if (Tiles) _home.Selected = s;
                else
                {
                    _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, s); });
                    _list.ScrollOffset = scroll;
                    _list.ScrollOffsetX = scrollX;
                }
            }
        }

        void OpenPreview()
        {
            SetEntry s = SelectedSet();
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }
            using (PreviewDialog d = new PreviewDialog(s, _arrangements))
                d.ShowDialog(this);
        }

        /// <summary>
        /// Listening to a render without starting Live. The player is one for the whole
        /// application: a second set simply drives into the same window, or after a dozen
        /// presses the screen would be littered with players sounding over each other.
        /// </summary>
        void OnRowPlay(int idx)
        {
            if (!SetsDomain) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            PlayOrToggle(s, VisiblePlaylist());
        }

        /// <summary>
        /// The queue the player walks: whatever the person has in front of them right now — the
        /// rows of the sets list, or the tiles of the home page in the order they lie there
        /// (pinned first, then the recent). The next track is logically taken from the same
        /// selection that is on screen.
        /// </summary>
        List<SetEntry> VisiblePlaylist()
        {
            List<SetEntry> playlist = new List<SetEntry>();
            if (Tiles)
            {
                foreach (SetEntry t in _home.VisibleSets())
                    if (t.HasRenders) playlist.Add(t);
            }
            else
            {
                foreach (RowData row in _list.Rows)
                {
                    SetEntry candidate = row.Tag as SetEntry;
                    if (candidate != null && candidate.HasRenders) playlist.Add(candidate);
                }
            }
            return playlist;
        }

        /// <summary>
        /// The player window on this set — not just its sound. The transport in the footer
        /// plays the main render and says nothing about the rest; here the project's whole list
        /// of renders is in front of you, with the waveform and the choice of which one counts
        /// as the preview.
        /// </summary>
        void OpenPlayerWindow(SetEntry s)
        {
            if (s == null) return;
            ShowPlayer(s, VisiblePlaylist(), false);
            ExpandPlayer();
        }

        /// <summary>
        /// A home tile: we take what is shown on the home page as the playlist, in the order it
        /// lies there — that is, the pinned first in pin order, then the recent. null used to
        /// go in here, the playlist degenerated into a single set, and the "previous/next"
        /// buttons in the footer on the home page did nothing at all.
        /// </summary>
        void OpenPlayer(SetEntry s)
        {
            PlayOrToggle(s, VisiblePlaylist());
        }

        /// <summary>A right click on a row in the sets list — the same menu as on a home tile,
        /// so that projects can be pinned without leaving the main catalog.</summary>
        void OnListRowRightClick(int idx, Point at)
        {
            if (SamplesDomain) { SampleRowMenu(idx, at); return; }
            if (!SetsDomain) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem open = new ToolStripMenuItem("Open in Live");
            open.ShortcutKeyDisplayString = "Enter";
            open.Click += delegate { OpenSet(s); };
            m.Items.Add(open);

            if (s.HasRenders)
            {
                ToolStripMenuItem play = new ToolStripMenuItem("Play render");
                play.ShortcutKeyDisplayString = "Space";
                play.Click += delegate { OnRowPlay(idx); };
                m.Items.Add(play);

                ToolStripMenuItem player = new ToolStripMenuItem("Open player");
                player.Click += delegate { OpenPlayerWindow(s); };
                m.Items.Add(player);
            }

            ToolStripMenuItem pin = new ToolStripMenuItem(
                HomeStore.IsPinned(s.Path) ? "Unpin" : "Pin project");
            pin.Checked = HomeStore.IsPinned(s.Path);
            pin.ShortcutKeyDisplayString = "Q";
            pin.Click += delegate { TogglePinAndRefresh(s); };
            m.Items.Add(pin);

            ToolStripMenuItem notes = new ToolStripMenuItem(
                ProjectMeta.HasAnything(s.ProjectDir)
                    ? "Tags and notes…"
                    : "Add tags or a note…");
            notes.ShortcutKeyDisplayString = "Ctrl+T";
            notes.Click += delegate { EditNotes(s); };
            m.Items.Add(notes);

            ToolStripMenuItem rescue = new ToolStripMenuItem("Rescue project…");
            rescue.ShortcutKeyDisplayString = "Ctrl+R";
            rescue.Click += delegate { RescueSet(s); };
            m.Items.Add(rescue);

            ToolStripMenuItem reveal = new ToolStripMenuItem("Show in Explorer");
            reveal.ShortcutKeyDisplayString = "Shift+Enter";
            reveal.Click += delegate { RevealSet(s); };
            m.Items.Add(reveal);

            m.Show(_list, at);
        }

        /// <summary>Pinning a set — shared by the star in the row and the menu item: it
        /// recolours that particular row at once, without rebuilding the whole list.</summary>
        void TogglePinAndRefresh(SetEntry s)
        {
            if (s == null) return;
            HomeStore.TogglePin(s.Path);
            foreach (RowData r in _list.Rows)
            {
                SetEntry rs = r.Tag as SetEntry;
                if (rs != null && rs.Path == s.Path) { r.Pinned = HomeStore.IsPinned(s.Path); break; }
            }
            _list.Invalidate();
            if (_home.Visible) _home.RebuildTransition();
        }

        /// <summary>
        /// Hand a set to the player, creating it if there is none yet. autoStart false only
        /// loads it — see PlayerDialog.LoadSet.
        /// </summary>
        void ShowPlayer(SetEntry s, List<SetEntry> playlist, bool autoStart = true)
        {
            if (s == null) return;

            if (_player == null || _player.IsDisposed)
            {
                _player = new PlayerDialog();
                // We set the owner ourselves — there will be no Show(owner) here, and the
                // window still has to be centred and positioned relative to the main one.
                _player.Owner = this;
                // We force the handle without showing the window: a preview is sound and a mini
                // transport in the footer, not a popup window. Without a handle BeginInvoke
                // inside the player (the waveform, the auto-advance) will not work.
                // CreateControl() will not do here — with Visible=false it quietly does
                // nothing; and Handle by itself does not lay the child controls out, so a
                // gentle nudge with a resize follows — the same device used in this session to
                // check a form offline, only with no screen it shows nothing.
                IntPtr forceHandle = _player.Handle;
                Size s0 = _player.Size;
                _player.Size = new Size(s0.Width + 1, s0.Height);
                _player.Size = s0;
                _player.SetChanged += delegate (SetEntry changed) {
                    _home.PlayingTag = changed;
                    if (!SamplesDomain) _list.PlayingTag = changed;
                    UpdatePlayerTransport();   // the track changed — we re-check Playing
                    _list.Invalidate();
                    _home.Invalidate();
                };
                _player.PlayStateChanged += UpdatePlayerTransport;
                _player.FormClosed += delegate
                {
                    _playerTimer.Stop();
                    _playerTimeLeftStr = _playerTimeRightStr = "";
                    _playerSeek.Progress = 0f;
                    _player = null;
                    _playerVolPopup.Visible = false;
                    _volPopupTimer.Stop();
                    _playerSetLink.SetName = "";
                    _home.PlayingTag = null;
                    _home.Playing = false;
                    if (!SamplesDomain) { _list.PlayingTag = null; _list.Playing = false; }
                    _list.Invalidate();
                    _home.Invalidate();
                    LayoutAll();       // hides the mini transport — there is nothing left to control
                    Invalidate(true);
                };
                _playerTimer.Start();
                _status = "";          // the "no renders" caption belonged to the previous set
                LayoutAll();           // shows the mini transport now that there is a player
                // The layout does not repaint the form background by itself, and the old status
                // line stayed in the footer's place — the transport buttons stood on top of it.
                // A full repaint exactly once, on opening.
                Invalidate(true);
            }

            if (playlist == null) playlist = new List<SetEntry>();
            if (playlist.Count == 0) playlist.Add(s);

            int playlistIndex = -1;
            for (int i = 0; i < playlist.Count; i++)
                if (SetEntry.SameSet(playlist[i], s)) { playlistIndex = i; break; }
            if (playlistIndex < 0) playlistIndex = 0;

            _player.LoadSet(s, playlist, playlistIndex, autoStart);
            _list.PlayingTag = _home.PlayingTag = s;
            _list.Invalidate();
            _home.Invalidate();
            UpdatePlayerTransport();

            // We do not raise the window ourselves: if the user has already expanded it, let it
            // stay visible and simply refresh, and if not, let it play quietly until "expand"
            // is pressed.
            if (_player.Visible)
            {
                if (_player.WindowState == FormWindowState.Minimized)
                    _player.WindowState = FormWindowState.Normal;
                _player.Activate();
            }
        }

        /// <summary>The "expand" button in the footer is the only way to show the player
        /// window.</summary>
        void ExpandPlayer()
        {
            if (_player == null || _player.IsDisposed) return;
            if (!_player.Visible) _player.Show(this);
            if (_player.WindowState == FormWindowState.Minimized) _player.WindowState = FormWindowState.Normal;
            _player.Activate();
        }

        /// <summary>
        /// The play/pause glyph in the footer follows what is really playing in the player. It
        /// is also the one place where the list and the tiles learn that this is not merely
        /// "the current track" but whether it is actually SOUNDING right now — otherwise after
        /// a pause the button in a row or on a tile would go on showing a pause although there
        /// is nothing left playing.
        /// </summary>
        static string TimeStr(int ms)
        {
            if (ms < 0) ms = 0;
            int total = ms / 1000;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        void StartVolPopupCloseTimer()
        {
            _volPopupTimer.Stop();
            _volPopupTimer.Start();
        }

        void UpdatePlayerVolumeIcon()
        {
            float vol = _player != null && !_player.IsDisposed ? _player.Volume : 0.5f;
            Glyph g = vol <= 0.001f ? Glyph.Volume0 : vol <= 0.5f ? Glyph.VolumeLow : Glyph.VolumeHigh;
            if (_playerVolBtn.Icon != g)
            {
                _playerVolBtn.Icon = g;
                _playerVolBtn.Invalidate();
            }
        }

        void UpdatePlayerTransport()
        {
            bool playing = _player != null && !_player.IsDisposed && _player.IsPlaying;
            // A render started from the footer or a media key silences the sample preview: two
            // sounds at once are never wanted.
            if (playing && _previewing != null) StopSample();
            Glyph want = playing ? Glyph.Pause : Glyph.Play;
            if (_playerPlayPause.Icon != want) { _playerPlayPause.Icon = want; _playerPlayPause.Invalidate(); }

            // On the Samples tab the list's pulse belongs to the preview.
            if (!SamplesDomain && _list.Playing != playing) { _list.Playing = playing; _list.Invalidate(); }
            if (_home.Playing != playing) { _home.Playing = playing; _home.Invalidate(); }

            UpdatePlayerVolumeIcon();
            string sname = (_player != null && !_player.IsDisposed && _player.CurrentSet != null) ? _player.CurrentSet.Name : "";
            if (_playerSetLink.SetName != sname)
            {
                _playerSetLink.SetName = sname;
                LayoutAll();
            }
            if (!_rPlayerTrack.IsEmpty) Invalidate(_rPlayerTrack);
        }

        void NavigateToSet(SetEntry target)
        {
            if (target == null) return;
            if (!SetsDomain)
            {
                _mode.SelectedIndex = ModeSets;
            }
            _lastSetPath = target.Path;

            if (Tiles)
            {
                bool found = false;
                foreach (SetEntry s in _home.VisibleSets())
                {
                    if (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found && (_search.Text.Length > 0 || !_filter.IsEmpty))
                {
                    _search.Text = "";
                    _filter.Clear();
                    UpdateFiltersButton();
                    Refill();
                }
                _home.Select(target);
            }
            else
            {
                bool found = false;
                foreach (RowData r in _list.Rows)
                {
                    SetEntry s = r.Tag as SetEntry;
                    if (s != null && (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found && (_search.Text.Length > 0 || !_filter.IsEmpty))
                {
                    _search.Text = "";
                    _filter.Clear();
                    UpdateFiltersButton();
                    Refill();
                }
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry s = r.Tag as SetEntry;
                    return s != null && (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase));
                });
            }
            OnSelectionChanged();
        }

        /// <summary>
        /// A click on play/pause in a row or on a tile: if this is already the track in the
        /// player, we simply toggle the pause rather than restarting it from the beginning. If
        /// it is a different one, we load and play it anew, as before.
        /// </summary>
        void PlayOrToggle(SetEntry s, List<SetEntry> playlist)
        {
            if (_player != null && !_player.IsDisposed && SetEntry.SameSet(_player.CurrentSet, s))
            {
                _player.PlayPause();
                return;
            }
            ShowPlayer(s, playlist);
        }

        /// <summary>
        /// "New Live Set" simply opens Live itself — like a double click on its icon, with no
        /// folder chosen and no set template slipped in. From there the user decides for
        /// themselves, by Live's own means: a new project, the recent ones or a master
        /// template.
        /// </summary>
        void NewProject()
        {
            string exe = LiveEnvironment.FindExecutable();
            if (exe.Length == 0)
            {
                Status("Could not find Ableton Live — is it installed?");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                // Live takes long seconds to come up and gives no sign of life until its first
                // window — without this line the press looks like "the button did not work" and
                // gets pressed again.
                Notify("Starting Live…");
            }
            catch (Exception ex) { Status(ex.Message); }
        }

        // ------------------------------------------------- a folder by drag and drop

        /// <summary>Highlighting the window while a folder is held over it.</summary>
        bool _dragOverWindow;

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (!HasFolder(e.Data)) return;
            e.Effect = DragDropEffects.Copy;
            if (!_dragOverWindow) { _dragOverWindow = true; Invalidate(); }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (_dragOverWindow) { _dragOverWindow = false; Invalidate(); }
        }

        /// <summary>
        /// A folder dropped onto the window becomes a new root. Roots used to be added only
        /// through the separate "Folders…" window, although dragging is the first thing one
        /// tries with a file manager.
        /// </summary>
        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            _dragOverWindow = false;
            Invalidate();

            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null)
            {
                if (SamplesDomain) AddSampleRoots(paths);
                else AddRoots(paths);
            }
        }

        /// <summary>
        /// Take paths in as new folders to watch. A file counts as its folder: dropping a set
        /// on the window means "watch the project it lies in". The command line gives the same
        /// answer to the same question — see OpenPaths.
        /// </summary>
        void AddRoots(string[] paths)
        {
            List<string> added = new List<string>();
            foreach (string path in paths)
            {
                string folder = path;
                if (File.Exists(path)) folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                if (HasRoot(folder)) continue;
                _settings.Roots.Add(folder);
                _settings.DisabledRoots.Remove(folder);
                added.Add(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
            }
            if (added.Count == 0) { Notify("Already watching that folder"); return; }

            _settings.Save();
            Settings.NotifyRootsChanged(this);
            Notify(added.Count == 1 ? "Added " + added[0] : "Added " + added.Count + " folders");
            StartScan(true);
            Rewatch();
        }

        // A path from the command line, or from a second copy that handed us its own and left.
        // It waits for the first scan: until the catalog has been read there is nothing to look
        // a set up in, and "not found" would turn into "add its folder as a new root".
        readonly List<string> _pendingOpen = new List<string>();
        bool _shown;

        /// <summary>
        /// Paths the program was started with. A path we already know is shown; anything else
        /// is taken in as a folder to watch — the same two answers a drop onto the window
        /// gives.
        /// </summary>
        public void OpenPaths(string[] paths)
        {
            if (paths == null) return;
            foreach (string p in paths)
                if (!string.IsNullOrEmpty(p)) _pendingOpen.Add(p);
            FlushPending();
        }

        void FlushPending()
        {
            if (!_shown || _scanning || _pendingOpen.Count == 0) return;

            string[] paths = _pendingOpen.ToArray();
            _pendingOpen.Clear();

            List<string> unknown = new List<string>();
            foreach (string p in paths)
                if (!SelectSetByPath(p)) unknown.Add(p);

            if (unknown.Count > 0) AddRoots(unknown.ToArray());
        }

        bool HasRoot(string folder)
        {
            foreach (string r in _settings.Roots)
                if (string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool HasFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return false;
            foreach (string p in paths)
                if (Directory.Exists(p) || File.Exists(p)) return true;
            return false;
        }

        /// <summary>
        /// The folders window: where the projects are and where the samples are, a tab each. It
        /// opens on the tab of what is being looked at. Scan rescans what changed, and the tab
        /// it was pressed on even unchanged — the button has always meant "scan these".
        /// fromLive — the empty Samples tab's "Add from Live": Live's own sample folders come
        /// already in the list, so what is left is to look and press Scan.
        /// </summary>
        bool EditFolders(bool samplesTab, bool fromLive)
        {
            RootsDialog.Kind sampleKind = RootsDialog.Samples(LiveEnvironment.Detect(), _settings.Roots);
            List<string> sampleStart = new List<string>(_settings.SampleRoots);
            if (fromLive)
                foreach (RootsDialog.Suggestion s in sampleKind.FromLive)
                    if (!s.Projects && !SampleIndex.ContainsPath(sampleStart, s.Path)) sampleStart.Add(s.Path);

            RootsDialog.Page projects = new RootsDialog.Page(RootsDialog.Projects, _settings.Roots, _settings.DisabledRoots);
            RootsDialog.Page samples = new RootsDialog.Page(sampleKind, sampleStart, _settings.DisabledSampleRoots);
            using (RootsDialog d = new RootsDialog(projects, samples))
            {
                d.Tab = samplesTab ? 1 : 0;
                if (d.ShowDialog(this) != DialogResult.OK) return false;

                bool scanProjects = d.Tab == 0 || !projects.Same(_settings.Roots, _settings.DisabledRoots);
                bool scanSamples = d.Tab == 1 || !samples.Same(_settings.SampleRoots, _settings.DisabledSampleRoots);
                if (scanProjects)
                {
                    _settings.Roots.Clear();
                    _settings.Roots.AddRange(projects.Roots);
                    _settings.DisabledRoots.Clear();
                    _settings.DisabledRoots.AddRange(projects.DisabledList);
                }
                if (scanSamples)
                {
                    _settings.SampleRoots.Clear();
                    _settings.SampleRoots.AddRange(samples.Roots);
                    _settings.DisabledSampleRoots.Clear();
                    _settings.DisabledSampleRoots.AddRange(samples.DisabledList);
                }
                _settings.Save();

                if (scanProjects)
                {
                    Settings.NotifyRootsChanged(this);
                    StartScan(true);
                    Rewatch();          // the set of roots is different — we re-point the watch
                }
                if (scanSamples)
                {
                    // A folder taken out leaves the tree now rather than after the walk.
                    _samples = _samples.Only(_settings.SampleRoots, _settings.DisabledSampleRoots);
                    LayoutAll();
                    if (SamplesDomain) Refill();
                    RescanSamples();
                }
                return true;
            }
        }

        void OnGlobalRootsChanged(object source)
        {
            if (source == this || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { OnGlobalRootsChanged(source); }); } catch { }
                return;
            }
            _settings.ReloadRoots();
            StartScan(true);
            Rewatch();
        }

        // -------------------------------------------------------- auto-refresh

        /// <summary>
        /// A rescan that started by itself. It differs from F5 in two ways: it keeps quiet (no
        /// progress bar over the catalog) and it looks after what the person is currently
        /// looking at — see RefillPreservingView.
        /// </summary>
        FolderWatch _watch;
        bool _rescanPending;

        void Rewatch()
        {
            if (_watch == null)
            {
                _watch = new FolderWatch(this);
                _watch.Changed += OnFoldersChanged;
            }
            _watch.Watch(_settings.Roots, _settings.DisabledRoots);
        }

        void OnFoldersChanged()
        {
            // While a scan is running we do not start a second one over it — but neither do we
            // lose it: the changes may have arrived in exactly the folders already passed, and
            // without the mark they would have waited for the next F5.
            if (_scanning) { _rescanPending = true; return; }
            StartScan(false);
        }

        // ----------------------------------------------------------- scanning

        void StartScan(bool force)
        {
            if (_scanning) return;
            if (_settings.Roots.Count == 0)
            {
                Status("No folders selected yet — press “Folders…”.");
                return;
            }

            Diag.Line("scan: start, roots = " + string.Join(" | ", _settings.Roots.ToArray()));
            _scanning = true;
            _manualScan = force;
            _scanDone = 0; _scanTotal = 0;
            _cancel = new CancellationTokenSource();
            Status("Scanning…");
            Invalidate();

            CancellationToken token = _cancel.Token;
            _scanThread = new Thread(delegate ()
            {
                try
                {
                    _index.Scan(_settings, delegate (int done, int total, string current)
                    {
                        _scanDone = done; _scanTotal = total;
                        int now = Environment.TickCount;
                        if (now - _lastScanInvalidate > 100)
                        {
                            _lastScanInvalidate = now;
                            try { BeginInvoke((MethodInvoker)delegate { Invalidate(); }); }
                            catch { }
                        }
                    }, token);
                    Diag.Line("scan: done, " + _index.Sets.Count + " sets");
                }
                catch (Exception ex) { Diag.Fail("scan", ex); }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        bool wasManual = _manualScan;
                        _scanning = false;
                        RefreshVersions();
                        if (wasManual) Refill(); else RefillPreservingView();
                        // We no longer write the final summary ("Indexed N sets · M with
                        // missing files"): how many sets are shown stands at the top as it is,
                        // and the losses are visible as coloured marks in the rows themselves.
                        // The line at the bottom is left only for what there is no other way of
                        // learning — errors from actions.
                        Status("");

                        // Now there is a catalog to look a path up in — see OpenPaths.
                        FlushPending();
                        CheckUpdatesInBackground();

                        // The library is walked once per session, after the sets: two walks on
                        // one disk at once only slow each other down.
                        if (!_samplesWalked) { _samplesWalked = true; StartSampleScan(false); }

                        // Something else on disk changed while we were scanning — we go round
                        // once more, or those edits would wait for the next occasion.
                        if (_rescanPending) { _rescanPending = false; StartScan(false); }
                    });
                }
                catch { }
            });
            _scanThread.IsBackground = true;
            _scanThread.Start();
        }

        void RefreshVersions()
        {
            _versions.Clear();
            foreach (SetEntry s in _index.Sets)
            {
                string v = s.ShortVersion;
                if (v.Length > 0 && !_versions.Contains(v)) _versions.Add(v);
            }
            _versions.Sort(delegate (string a, string b) { return CompareVersion(b, a); });   // the new ones on top
        }

        void Status(string msg)
        {
            // A status line appearing and disappearing changes the height of the content (the
            // bottom strip is now reserved only for what is in it), so we recompute the layout
            // — but only when the line really did appear or vanish rather than on every update
            // of it.
            bool had = _status.Length > 0;
            _status = msg;
            if (had != (_status.Length > 0)) LayoutAll();
            Invalidate();
        }

        // ------------------------------------------------------------------ updates

        /// <summary>
        /// Ask GitHub, quietly, whether there is a newer release — the one network request the
        /// program makes, and only if the settings allow it.
        ///
        /// It says nothing. Failure, no network, a machine behind a proxy, GitHub rate-limiting
        /// the address: all of it ends the same way, with no dot and no message. A catalog of
        /// local files has no business interrupting anybody over its own version. The only
        /// thing that can come of this is the dot on the gear, and only for a release that
        /// moved the major or the minor number — a fix waits for somebody to ask.
        ///
        /// Once a day. It runs after the first scan rather than at startup so it never competes
        /// with the thing people actually opened the program for.
        /// </summary>
        /// <summary>Which release the dot is burning about, so that opening the settings can
        /// write it down and tomorrow's check stays quiet about the same one.</summary>
        string _dotVersion = "";

        void CheckUpdatesInBackground()
        {
            if (!_settings.CheckUpdates) return;

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_settings.LastUpdateCheck == today) return;

            string current = Application.ProductVersion;
            Thread t = new Thread(delegate ()
            {
                UpdateCheck.Result r = UpdateCheck.Fetch();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;

                        // The day is written down even when the answer was no use: retrying on
                        // every scan of a machine with no network would be a request a minute.
                        _settings.LastUpdateCheck = today;
                        _settings.Save();

                        if (r.Error.Length > 0 || r.Version.Length == 0) return;
                        if (UpdateCheck.Compare(current, r.Version) != UpdateCheck.Step.Big) return;

                        // Already shown once and the settings were opened — do not light it
                        // again for the same release.
                        if (string.Equals(_settings.SeenUpdate, r.Version, StringComparison.Ordinal)) return;

                        _dotVersion = r.Version;
                        _settingsBtn.Dot = true;
                        _settingsBtn.Invalidate();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------- where the window was left

        /// <summary>
        /// Put the window back where it was last closed.
        ///
        /// The rectangle is checked against the screens as they are NOW: a window left on a
        /// second monitor that has since been unplugged would otherwise open somewhere nobody
        /// can reach it. It is enough that the saved place still meets some working area —
        /// Windows drags the rest into view itself.
        /// </summary>
        void RestoreGeometry()
        {
            string[] parts = _settings.WindowBounds.Split(',');
            if (parts.Length != 4) return;

            int x, y, w, h;
            if (!int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y) ||
                !int.TryParse(parts[2], out w) || !int.TryParse(parts[3], out h)) return;
            if (w < 100 || h < 100) return;

            Rectangle saved = new Rectangle(x, y, w, h);
            bool onScreen = false;
            foreach (Screen sc in Screen.AllScreens)
                if (sc.WorkingArea.IntersectsWith(saved)) { onScreen = true; break; }
            if (!onScreen) return;

            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        void SaveGeometry()
        {
            // Minimized is not a state to come back to, and the bounds of a minimized window are
            // nonsense (-32000): what we want either way is the place it unfolds to.
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (b.Width < 100 || b.Height < 100) return;

            _settings.WindowBounds = b.X + "," + b.Y + "," + b.Width + "," + b.Height;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveGeometry();
            if (_minimizeHotkey)
            {
                try { UnregisterHotKey(Handle, HotkeyMinimize); } catch { }
                _minimizeHotkey = false;
            }
            Settings.RootsChanged -= OnGlobalRootsChanged;
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            if (_sampleCancel != null) { try { _sampleCancel.Cancel(); } catch { } }
            if (_watch != null) { try { _watch.Dispose(); } catch { } _watch = null; }
            if (_player != null && !_player.IsDisposed) { try { _player.Close(); } catch { } }
            _previewTimer.Stop();
            _preview.Dispose();
            base.OnFormClosing(e);
        }
    }
}
