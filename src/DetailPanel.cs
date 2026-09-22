using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The panel on the right: the details of the selected set, plugin, library folder or
    /// sample. A card of one colour, the primary action pinned to the bottom, and everything
    /// else a column of blocks with identical insets.
    /// </summary>
    public sealed class DetailPanel : GlassControl
    {
        readonly GlassButton _action = new GlassButton();
        readonly IconButton _toggle = new IconButton();

        ContextMenuStrip _openMenu;   // the popup of secondary actions, while it is open
        int _menuClosedTick;          // when it closed — see ShowActionMenu

        enum PanelMode { Set, Plugin, Folder, Sample }
        PanelMode _mode = PanelMode.Set;

        SetEntry _set;
        PluginStat _plugin;

        // A folder or a sample of the library, and what the sets use of it. _unknown — the
        // sets are still being read, and "never" would be a lie.
        SampleFolder _folder;
        SampleFile _sample;
        SampleUsage _use = SampleUsage.Empty;
        bool _unknown;
        readonly List<SampleFile> _topFiles = new List<SampleFile>();
        readonly List<SetEntry> _projects = new List<SetEntry>();
        readonly List<Rectangle> _fileRowRects = new List<Rectangle>();
        int _fileRowHot = -1;

        // The sample's picture and what it is — read in the background when it is picked, and
        // kept for its path: the list re-selects the same row after every refill.
        Waveform _wave;
        string _waveFor = "";
        string _format = "";
        int _durationMs;
        float _waveProgress;
        bool _wavePlaying;
        Rectangle _waveRect;
        bool _waveHot;

        int _scroll;
        int _contentHeight;
        float _scrollTarget, _scrollCurrent;
        readonly SmoothScroller _scroller;

        Arrangement _arr;
        Bitmap _thumb;
        Size _thumbSize;
        Rectangle _thumbRect, _linkRect, _notesRect, _topRect;
        bool _thumbHot, _linkHot, _notesHot, _topHot;
        bool _thumbRendering;

        // The "Sets" list on a plugin: the rows are clickable and jump to a set, but we
        // underline only the one currently under the cursor rather than all of them at once.
        readonly List<Rectangle> _setRowRects = new List<Rectangle>();
        readonly List<SetEntry> _setRowSets = new List<SetEntry>();
        int _setRowHot = -1;

        // The "Plugins" list on a set: the same logic in reverse — a jump to a plugin.
        readonly List<Rectangle> _pluginRowRects = new List<Rectangle>();
        readonly List<string> _pluginRowNames = new List<string>();
        int _pluginRowHot = -1;

        public ProjectIndex Index;
        public ArrangementLoader Loader;

        public event Action PreviewRequested;
        public event Action RevealRequested;
        public event Action OpenRequested;
        public event Action RescueRequested;

        /// <summary>Collect the project into a portable folder — see CollectDialog.</summary>
        public event Action CollectRequested;

        Action _showInListRequested;
        public event Action ShowInListRequested
        {
            add
            {
                _showInListRequested += value;
                ApplyAction();
            }
            remove
            {
                _showInListRequested -= value;
                ApplyAction();
            }
        }

        /// <summary>A click on the tags-and-note block opens the editor.</summary>
        public event Action<SetEntry> NotesRequested;
        public event Action<SetEntry> SetRequested;
        public event Action<string> PluginRequested;

        /// <summary>A sample picked in a folder's "Most used" list.</summary>
        public event Action<SampleFile> SampleRequested;

        /// <summary>A click on a sample's wave, 0..1 across it.</summary>
        public event Action<float> WaveClicked;

        public DetailPanel()
        {
            Cursor = Cursors.Default;
            Surface = Theme.Backdrop;

            _action.Primary = true;
            // The button lies on the panel's card rather than on the bare window — we repeat
            // both layers.
            _action.Surface = Theme.Backdrop;
            _action.SurfaceOverlay = Theme.Surface;
            _action.Click += delegate
            {
                if (_mode != PanelMode.Set) { if (RevealRequested != null) RevealRequested(); }
                else if (OpenRequested != null) OpenRequested();
            };
            Controls.Add(_action);

            // Secondary actions for a set are for a set only: a plugin has no .als of its own,
            // so there is nothing to mend. They live in a popup that rises from this button —
            // where they used to stand as separate pills.
            _toggle.Icon = Glyph.HiddenBtnsOpen;
            _toggle.Surface = Theme.CardFill;
            _toggle.Click += delegate { ShowActionMenu(); };
            Controls.Add(_toggle);

            _scroller = new SmoothScroller(this,
                delegate (int s) { _scroll = s; _scrollCurrent = s; ClampScroll(); },
                delegate { return Math.Max(0, _contentHeight - BodyBottom); });
            ApplyAction();
        }

        int Pad { get { return Sc(Theme.PanelPad); } }

        static readonly TextFormatFlags PanelLeft =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        static readonly TextFormatFlags PanelRight =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        /// <summary>
        /// With PanelLeft (TextFormatFlags.NoPadding) the first pixel of text is drawn exactly
        /// from the edge of rr.X.
        /// </summary>
        int UnderlinePad { get { return 0; } }

        // ------------------------------------------------------------------ content

        public void Show(SetEntry s)
        {
            bool same = _set != null && s != null && _set.Path == s.Path;
            _plugin = null;
            _folder = null;
            _sample = null;
            _mode = PanelMode.Set;
            _set = s;
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            if (_scroller != null) _scroller.SyncPosition(0);
            _topHot = false;
            _topRect = Rectangle.Empty;
            _pluginRowHot = -1;
            if (!same)
            {
                _arr = null;
                DropThumb();
                if (s != null && Loader != null)
                {
                    _arr = Loader.Cached(s.Path);
                    if (_arr == null) Loader.Request(s.Path);
                }
            }
            ApplyAction();
            Invalidate();
        }

        /// <summary>
        /// The sets the displayed plugin is in. We count once when a plugin is selected rather
        /// than in OnPaint: the walk here is every set against every one of its plugins, and
        /// the panel is repainted often (hover, scrolling). There is deliberately no length
        /// limit — the list used to break off at the sixtieth set silently, and "not all the
        /// projects show up" was exactly that.
        /// </summary>
        readonly List<SetEntry> _users = new List<SetEntry>();

        public void ShowPlugin(PluginStat p)
        {
            _plugin = p;
            _folder = null;
            _sample = null;
            _mode = PanelMode.Plugin;
            _set = null;
            _arr = null;
            DropThumb();
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            if (_scroller != null) _scroller.SyncPosition(0);
            _topHot = false;
            _topRect = Rectangle.Empty;
            _setRowHot = -1;
            _pluginRowHot = -1;

            _users.Clear();
            if (p != null && Index != null && p.Sets > 0)
                foreach (SetEntry s in Index.Sets)
                    foreach (string n in s.Plugins)
                        if (string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))
                        { _users.Add(s); break; }

            ApplyAction();
            Invalidate();
        }

        /// <summary>A folder of the sample library: its numbers, its most used samples, the
        /// projects that use it. d == null — nothing is selected.</summary>
        public void ShowFolder(SampleFolder d, SampleUsage use, bool unknown)
        {
            ShowLibrary(PanelMode.Folder, use, unknown);
            _folder = d;
            if (d == null || unknown) return;

            List<SetEntry> sets = new List<SetEntry>();
            foreach (SampleFile x in _use.UsedUnder(d))
            {
                sets.AddRange(_use.Of(x).Sets);
                if (_topFiles.Count < 10) _topFiles.Add(x);
            }
            _projects.AddRange(SampleUsage.Newest(sets));
        }

        /// <summary>One sample: its wave and format (read in the background), its path, the
        /// projects that use it.</summary>
        public void ShowSample(SampleFile f, SampleUsage use, bool unknown)
        {
            ShowLibrary(PanelMode.Sample, use, unknown);
            _sample = f;
            if (f == null) return;
            if (!string.Equals(_waveFor, f.Path, StringComparison.OrdinalIgnoreCase)) LoadWave(f);
            SampleUse u = unknown ? null : _use.Of(f);
            if (u != null) _projects.AddRange(SampleUsage.Newest(u.Sets));
        }

        void ShowLibrary(PanelMode mode, SampleUsage use, bool unknown)
        {
            _mode = mode;
            _set = null;
            _plugin = null;
            _arr = null;
            DropThumb();
            _folder = null;
            _sample = null;
            _use = use ?? SampleUsage.Empty;
            _unknown = unknown;
            _topFiles.Clear();
            _projects.Clear();
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            if (_scroller != null) _scroller.SyncPosition(0);
            _topHot = _waveHot = false;
            _topRect = Rectangle.Empty;
            _setRowHot = _pluginRowHot = _fileRowHot = -1;
            ApplyAction();
            Invalidate();
        }

        public bool ShowsSample(SampleFile f)
        {
            return _mode == PanelMode.Sample && _sample != null && f != null
                && string.Equals(_sample.Path, f.Path, StringComparison.OrdinalIgnoreCase);
        }

        public int SampleDurationMs { get { return _durationMs; } }

        public void SetWaveProgress(float t)
        {
            if (_wavePlaying && Math.Abs(t - _waveProgress) < 0.001f) return;
            _wavePlaying = true;
            _waveProgress = t;
            Invalidate(_waveRect);
        }

        public void StopWave()
        {
            if (!_wavePlaying) return;
            _wavePlaying = false;
            _waveProgress = 0f;
            Invalidate(_waveRect);
        }

        /// <summary>The wave and the format line, off the UI thread. A file only Live plays gets
        /// the format alone — there is no wave to read out of it.</summary>
        void LoadWave(SampleFile f)
        {
            string path = f.Path;
            bool wave = f.CanPreview;
            _waveFor = path;
            _wave = null;
            _format = "";
            _durationMs = 0;
            _wavePlaying = false;
            _waveProgress = 0f;
            int buckets = Math.Max(120, Width - Pad * 2);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int ms = 0;
                string fmt = "";
                Waveform w = null;
                // A drive that went away mid-read throws — on a pool thread that would take
                // the whole program down.
                try
                {
                    fmt = MediaDecoder.Describe(path, out ms);
                    if (wave) w = WaveReader.Read(path, buckets);
                }
                catch (Exception ex)
                {
                    Diag.Line("samples: wave: " + ex.Message);
                    w = new Waveform();
                    w.Note = "cannot decode";
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        // Another sample was picked meanwhile — this answer is nobody's now.
                        if (!string.Equals(_waveFor, path, StringComparison.OrdinalIgnoreCase)) return;
                        _wave = w;
                        _format = fmt;
                        _durationMs = ms;
                        Invalidate();
                    });
                }
                catch { }
            });
        }

        public void OnArrangement(Arrangement a)
        {
            if (a == null || _set == null || a.Path != _set.Path) return;
            _arr = a;
            DropThumb();
            Invalidate();
        }

        void ApplyAction()
        {
            if (_mode != PanelMode.Set)
            {
                // The "Show in Explorer" button was removed: the plugin path became a link
                // itself, and the button at the bottom repeated what one wants to click anyway.
                _action.Visible = false;
                _toggle.Visible = false;
            }
            else
            {
                _action.Text = "Open in Live";
                _action.Visible = _set != null;
                _toggle.Visible = _set != null;
            }
        }

        /// <summary>
        /// Secondary actions as a popup rising from the button, exactly where the separate
        /// pills used to stand. The set of them depends on whether the set is already shown in
        /// the list on the left: there "Show in List", otherwise mending and collecting.
        /// </summary>
        void ShowActionMenu()
        {
            if (_set == null || _mode != PanelMode.Set) return;

            // A click on the button while the popup is open closes it first — the popup leaves
            // on any click outside itself, and only then does the click reach the button.
            // Without this check the button would reopen it at once, and the popup would look
            // impossible to close.
            if (Environment.TickCount - _menuClosedTick < 250) return;

            ContextMenuStrip m = DarkMenu.Create();
            if (_showInListRequested != null)
            {
                ToolStripMenuItem show = new ToolStripMenuItem("Show in List");
                show.Click += delegate { if (_showInListRequested != null) _showInListRequested(); };
                m.Items.Add(show);
            }
            else
            {
                // The order is that of the former column of pills: Collect All on top.
                ToolStripMenuItem collect = new ToolStripMenuItem("Export");
                collect.Click += delegate { if (CollectRequested != null) CollectRequested(); };
                m.Items.Add(collect);

                ToolStripMenuItem rescue = new ToolStripMenuItem("Rescue Project");
                rescue.ShortcutKeyDisplayString = "Ctrl+R";
                rescue.Click += delegate { if (RescueRequested != null) RescueRequested(); };
                m.Items.Add(rescue);
            }

            // While the popup is open the button glyph is flipped — as on an expanded list.
            _openMenu = m;
            _toggle.Icon = Glyph.HiddenBtnsClose;
            _toggle.Invalidate();
            m.Closed += delegate
            {
                _openMenu = null;
                _menuClosedTick = Environment.TickCount;
                _toggle.Icon = Glyph.HiddenBtnsOpen;
                _toggle.Invalidate();
            };

            m.Show(_toggle, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        }

        void DropThumb()
        {
            if (_thumb != null) { _thumb.Dispose(); _thumb = null; }
            _thumbSize = Size.Empty;
        }

        // ------------------------------------------------------------------ layout

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Sc(Theme.ControlH);
            int gap = Sc(8);
            _toggle.SetBounds(Pad, Height - Pad - h, h, h);
            _action.SetBounds(Pad + h + gap, Height - Pad - h, Math.Max(Sc(40), Width - Pad * 2) - h - gap, h);
            ClampScroll();
        }

        /// <summary>
        /// How far down content may be drawn (and where the "to the top" button stands, see
        /// PaintScrollTop). Room for the buttons is always reserved so the list does not jump
        /// when the button is there and then is not: a plugin has no row at all, a set has one
        /// (Open in Live and the secondary actions button). Secondary actions live in a popup
        /// and take up no room in the panel.
        /// </summary>
        int BodyBottom
        {
            get
            {
                int buttons = _mode != PanelMode.Set ? 0 : Sc(Theme.ControlH);
                return Height - Pad - buttons - Sc(16);
            }
        }

        /// <summary>
        /// We simply stop drawing list rows past the bottom boundary: clipping through
        /// Graphics.Clip will not do — TextRenderer draws past GDI+ and ignores the clip.
        /// </summary>
        bool Below(int y) { return y > BodyBottom; }

        void ClampScroll()
        {
            int max = Math.Max(0, _contentHeight - BodyBottom);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroller.OnMouseWheel(e.Delta, Sc(60));
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------------- mouse

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool t = _arr != null && _arr.HasContent && _thumbRect.Contains(e.Location);
            bool l = !_linkRect.IsEmpty && _linkRect.Contains(e.Location);

            int rowHot = -1;
            for (int i = 0; i < _setRowRects.Count; i++)
                if (_setRowRects[i].Contains(e.Location)) { rowHot = i; break; }

            int pluginRowHot = -1;
            for (int i = 0; i < _pluginRowRects.Count; i++)
                if (_pluginRowRects[i].Contains(e.Location)) { pluginRowHot = i; break; }

            int fileRowHot = -1;
            for (int i = 0; i < _fileRowRects.Count; i++)
                if (_fileRowRects[i].Contains(e.Location)) { fileRowHot = i; break; }

            // The wave is a button only where something will sound.
            bool wave = WaveClicked != null && _sample != null && _sample.CanPreview
                     && !_waveRect.IsEmpty && _waveRect.Contains(e.Location);

            bool n = _set != null && _mode == PanelMode.Set && _notesRect.Contains(e.Location);
            bool up = !_topRect.IsEmpty && _topRect.Contains(e.Location);
            if (up) { t = l = n = wave = false; rowHot = pluginRowHot = fileRowHot = -1; }

            if (t != _thumbHot || l != _linkHot || n != _notesHot || up != _topHot || wave != _waveHot
                || rowHot != _setRowHot || pluginRowHot != _pluginRowHot || fileRowHot != _fileRowHot)
            {
                _thumbHot = t; _linkHot = l; _notesHot = n; _topHot = up; _waveHot = wave;
                _setRowHot = rowHot; _pluginRowHot = pluginRowHot; _fileRowHot = fileRowHot;
                Cursor = (t || l || n || up || wave || rowHot >= 0 || pluginRowHot >= 0 || fileRowHot >= 0)
                       ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_thumbHot || _linkHot || _notesHot || _topHot || _waveHot
                || _setRowHot >= 0 || _pluginRowHot >= 0 || _fileRowHot >= 0)
            {
                _thumbHot = _linkHot = _notesHot = _topHot = _waveHot = false;
                _setRowHot = -1;
                _pluginRowHot = -1;
                _fileRowHot = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_topHot)
                {
                    _scroll = 0;
                    _scrollTarget = _scrollCurrent = 0;
                    if (_scroller != null) _scroller.SyncPosition(0);
                    _topHot = false;
                    Invalidate();
                    return;
                }
                if (_thumbHot && PreviewRequested != null) { PreviewRequested(); return; }
                if (_linkHot && RevealRequested != null) { RevealRequested(); return; }
                if (_notesHot && _set != null && NotesRequested != null)
                {
                    NotesRequested(_set);
                    return;
                }
                if (_setRowHot >= 0 && _setRowHot < _setRowSets.Count && SetRequested != null)
                {
                    SetRequested(_setRowSets[_setRowHot]);
                    return;
                }
                if (_pluginRowHot >= 0 && _pluginRowHot < _pluginRowNames.Count && PluginRequested != null)
                {
                    PluginRequested(_pluginRowNames[_pluginRowHot]);
                    return;
                }
                if (_fileRowHot >= 0 && _fileRowHot < _topFiles.Count && SampleRequested != null)
                {
                    SampleRequested(_topFiles[_fileRowHot]);
                    return;
                }
                if (_waveHot && WaveClicked != null)
                {
                    // The same inset as the wave's own box in WaveView.PaintWave.
                    int inset = Sc(10);
                    float at = (e.X - _waveRect.X - inset) / (float)Math.Max(1, _waveRect.Width - inset * 2);
                    WaveClicked(Math.Max(0f, Math.Min(1f, at)));
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        // ---------------------------------------------------------------- drawing

        void PaintCard(Graphics g)
        {
            RectangleF card = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, card, Sc(Theme.CardR), Theme.GlassAlpha);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // The control's corners outside the rounded card are painted with the window
            // background — on glass that is Backdrop, and the corners then merge seamlessly
            // with the same glass around the panel, as on ordinary pills.
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            PaintCard(g);

            int w = Width - Pad * 2;

            // The rubber-band overshoot past the edge — the same as in the list and on the
            // tiles.
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            int y = Pad - _scroll - over;

            // Every clickable rectangle is laid out anew by the paint below.
            _thumbRect = _linkRect = _topRect = _waveRect = Rectangle.Empty;
            _setRowRects.Clear();
            _setRowSets.Clear();
            _pluginRowRects.Clear();
            _pluginRowNames.Clear();
            _fileRowRects.Clear();

            if (_mode == PanelMode.Plugin) { PaintPlugin(g, Pad, y, w, over); return; }
            if (_mode == PanelMode.Folder) { PaintFolder(g, y, w, over); return; }
            if (_mode == PanelMode.Sample) { PaintSample(g, y, w, over); return; }

            if (_set == null)
            {
                PaintEmpty(g, Pad, w, "No set selected", "Pick one from the list");
                return;
            }

            // The heading and the weight of the whole project folder on one line — the same
            // number as in the list column.
            y = Header(g, _set.Name, MainForm.SizeMB(_set.ProjectSize), y, w) + Sc(16);

            y = Thumb(g, Pad, y, w) + Sc(16);

            // The path is itself the link: a separate "Show in Explorer…" line repeated what
            // one wants to click anyway. We do not underline it — it merely lightens.
            int pathTop = y;
            int pathBottom = Wrapped(g, _set.Path, Theme.FLabel,
                                     _linkHot ? Theme.Text : Theme.TextDim, Pad, y, w);
            _linkRect = new Rectangle(Pad, pathTop, w, pathBottom - pathTop);
            y = pathBottom + Sc(24);

            y = TagsAndNote(g, y, w);
            y = Versions(g, y, w);

            // Files
            y = Line(g, "Files:", Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(8);
            Chrome.DrawText(g, Chrome.Plural(_set.TotalRefs, "file"), Theme.FLabel,
                new Rectangle(Pad, y, w, Sc(28)), Theme.Text, PanelLeft);
            // Colour only when things are bad. A green zero promised an event that is not
            // there, and red among it stopped catching the eye.
            if (_set.MissingFiles > 0)
                Chrome.DrawText(g, _set.MissingFiles + " missing", Theme.FLabel,
                    new Rectangle(Pad, y, w, Sc(28)), Theme.Red, PanelRight);
            y += Sc(28) + Sc(24);

            if (_set.Error.Length > 0)
                y = Wrapped(g, _set.Error, Theme.FLabel, Theme.Red, Pad, y, w) + Sc(24);

            // Plugins — the count of missing ones opposite the heading, by the same device as
            // Files.
            Rectangle plugHead = new Rectangle(Pad, y, w, Sc(28));
            Chrome.DrawText(g, "Plugins" + " (" + _set.Plugins.Length + "):",
                            Theme.FLabel, plugHead, Theme.TextDim, PanelLeft);
            if (_set.MissingPlugins > 0)
                Chrome.DrawText(g, _set.MissingPlugins + " missing", Theme.FLabel,
                    plugHead, Theme.Red, PanelRight);
            y += Sc(28) + Sc(8);

            if (_set.Plugins.Length == 0)
                y = Line(g, "only Live's own devices",
                         Theme.FLabel, Theme.TextDim, Pad, y, w);
            else
            {
                int shown = 0;
                for (int i = 0; i < _set.Plugins.Length; i++)
                {
                    if (Below(y + Sc(28))) break;
                    MatchKind m = MatchKind.Exact;
                    if (Index != null)
                        m = Index.Inventory.Match(
                                i < _set.PluginUids.Length ? _set.PluginUids[i] : "", _set.Plugins[i]).Kind;

                    // A missing plugin is visible from the red text as it is — a caption next
                    // to each of them only duplicates what the number opposite the "Plugins"
                    // heading has already said. We do not change the colour under the cursor:
                    // red on a plugin that is not installed is a message rather than
                    // decoration, and a "white on hover" highlight erased it at exactly the
                    // moment the row is being looked at. That the row is clickable is said by
                    // the underline below.
                    bool hot = _pluginRowHot == shown;
                    Color c = m == MatchKind.Missing ? Theme.Red : Theme.Text;
                    Rectangle rr = new Rectangle(Pad, y, w, Sc(28));
                    Chrome.DrawText(g, _set.Plugins[i], Theme.FLabel, rr, c, PanelLeft);
                    if (m == MatchKind.OtherFormat)
                        Chrome.DrawText(g, "other format", Theme.FLabel,
                                        rr, Theme.TextDim, PanelRight);
                    // The same presentation as the clickable sets in a plugin's panel.
                    if (hot) Underline(g, _set.Plugins[i], Theme.FLabel, rr, c);
                    _pluginRowRects.Add(rr);
                    _pluginRowNames.Add(_set.Plugins[i]);
                    y += Sc(28);
                    shown++;
                }
                // The list can be longer than fits above the button at the bottom - as in the
                // list of sets on a plugin, we say outright how many more are hidden rather
                // than breaking off silently.
                if (_set.Plugins.Length > shown)
                {
                    if (!Below(y + Sc(28)))
                        y = Line(g, "… " + (_set.Plugins.Length - shown) + " more",
                                 Theme.FLabel, Theme.TextDim, Pad, y, w);
                    else
                        y += Sc(28) * (_set.Plugins.Length - shown);
                }
            }

            _contentHeight = y + _scroll + over + Pad;
            ClampScroll();

            PaintScrollTop(g);
        }

        /// <summary>
        /// The "to the top" button in the bottom-right corner of the panel body. A plugin's
        /// list of sets can run to a hundred rows, and going back to the header with the wheel
        /// takes a while.
        /// </summary>
        void PaintScrollTop(Graphics g)
        {
            if (_scroll < Sc(120)) { _topRect = Rectangle.Empty; return; }

            int d = Sc(30);
            _topRect = new Rectangle(Width - Pad - d, BodyBottom - d, d, d);
            Theme.PaintGlassSurface(this, g, _topRect, d / 2f,
                _topHot ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha);

            float cx = _topRect.X + d / 2f, cy = _topRect.Y + d / 2f, a = Sc(5);
            using (Pen pen = new Pen(_topHot ? Theme.Text : Theme.TextDim, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(cx - a, cy + a * 0.45f),
                    new PointF(cx, cy - a * 0.55f),
                    new PointF(cx + a, cy + a * 0.45f) });
            }
        }

        /// <summary>
        /// A project's tags and note. While there are none, one dim line with a glyph, so the
        /// feature can be discovered at all; as soon as something has been written, the line
        /// turns into tag chips and the text of the note. A click anywhere on the block opens
        /// the editor.
        /// </summary>
        int TagsAndNote(Graphics g, int y, int w)
        {
            string dir = _set.ProjectDir;
            List<string> tags = ProjectMeta.TagsOf(dir);
            string note = ProjectMeta.NoteOf(dir);

            int icon = Sc(15);
            int iconOffset = (int)Math.Round(icon * 0.16f);
            int iconLeft = Pad - iconOffset;
            int textX = Pad + icon - iconOffset * 2 + Sc(6);
            int textW = w - (textX - Pad);
            int top = y;

            if (tags.Count == 0 && note.Length == 0)
            {
                Rectangle line = new Rectangle(iconLeft, y, w + iconOffset, Sc(24));
                Icons.Draw(g, Glyph.Tag,
                           // Exactly on the centre of the line: the 2 px lift here was fitted
                           // to text that the former correction in Chrome.DrawText raised by
                           // the same amount. The correction is gone — and so is the fitting.
                           new RectangleF(iconLeft, y + (Sc(24) - icon) / 2f, icon, icon),
                           _notesHot ? Theme.Text : Theme.TextDim, 1.3f);
                Chrome.DrawText(g, "Add tags or a note…",
                                Theme.FBadge, new Rectangle(textX, y, textW, Sc(24)),
                                _notesHot ? Theme.Text : Theme.TextDim, PanelLeft);
                _notesRect = line;
                return y + Sc(24) + Sc(14);
            }

            if (tags.Count > 0)
            {
                // The height comes from the font, by one calculation shared with every other
                // pill.
                int chipH = Chrome.PillHeight(Theme.FBadge);
                // No glyph: the pills read as tags by themselves, and the icon only ate into
                // the room for the first line.
                int cx = Pad, cy = y;
                foreach (string tag in tags)
                {
                    Size ts = TextRenderer.MeasureText(tag, Theme.FBadge);
                    int cw = Math.Min(w, ts.Width + Sc(18));
                    if (cx > Pad && cx + cw > Pad + w) { cx = Pad; cy += chipH + Sc(3); }
                    Rectangle chip = new Rectangle(cx, cy, cw, chipH);
                    Theme.FillRound(g, chip, chip.Height / 2f, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                    Chrome.DrawText(g, tag, Theme.FBadge,
                                    new Rectangle(chip.X, chip.Y + Chrome.PillTop(g, Theme.FBadge, chip.Height),
                                                  chip.Width, chip.Height),
                                    Theme.Text, Chrome.PillText);
                    cx += cw + Sc(6);
                }
                y = cy + chipH + Sc(8);
            }

            // The note is a section of the panel just like "Files:" and "Plugins (8):": a dim
            // heading and text under it, in the same type size and on the same left boundary.
            // There is no glyph: the neighbouring sections have none either, and it alone stuck
            // out of the row.
            if (note.Length > 0)
            {
                y = Line(g, "Note:", Theme.FLabel, _notesHot ? Theme.Text : Theme.TextDim,
                         Pad, y, w) + Sc(8);
                y = Wrapped(g, note, Theme.FLabel, Theme.Text, Pad, y, w);
            }

            _notesRect = new Rectangle(iconLeft, top, w + iconOffset, Math.Max(Sc(24), y - top));
            return y + Sc(14);
        }

        /// <summary>
        /// The other .als files of the same folder. The sets list collapses them into one row,
        /// and without this block there would be nowhere to see what exactly is hidden under
        /// "+3". The rows are clickable: they show the chosen version even when it has no row
        /// of its own in the list.
        /// </summary>
        int Versions(Graphics g, int y, int w)
        {
            if (Index == null) return y;
            List<SetEntry> all = Index.InSameFolder(_set);
            if (all.Count < 2) return y;

            y = Line(g, "Versions" + " (" + all.Count + "):",
                     Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(4);

            foreach (SetEntry v in all)
            {
                if (Below(y + Sc(24))) break;
                bool current = ReferenceEquals(v, _set);
                bool hot = _setRowHot == _setRowRects.Count;
                Rectangle rr = new Rectangle(Pad, y, w, Sc(24));

                Color c = current ? Theme.Text : Theme.TextDim;
                Size ds = TextRenderer.MeasureText(g, v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge, new Size(rr.Width, rr.Height), PanelRight);
                Chrome.DrawText(g, v.Name, Theme.FBadge,
                                new Rectangle(rr.X, rr.Y, rr.Width - ds.Width - Sc(8), rr.Height),
                                hot ? Color.White : c, PanelLeft);
                Chrome.DrawText(g, v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge,
                                rr, Theme.TextDim, PanelRight);

                // We do not underline the current one even under the cursor: there is no point
                // clicking it.
                if (hot && !current) Underline(g, v.Name, Theme.FBadge, rr, Color.White);

                _setRowRects.Add(rr);
                _setRowSets.Add(v);
                y += Sc(24);
            }
            return y + Sc(14);
        }

        /// <summary>
        /// An empty panel: a heading and what to do next. The glyph is gone from here — three
        /// grey bars above the words "nothing selected" read as a list item that for some
        /// reason does not click. We measure line heights with the font rather than with
        /// constants: at a fixed 24 and 22 pixels the tails of "g" and "p" were shaved off and
        /// the lines themselves merged into one.
        /// </summary>
        void PaintEmpty(Graphics g, int pad, int w, string title, string hint)
        {
            int th = TextRenderer.MeasureText("Ayg", Theme.FTitle).Height;
            int hh = TextRenderer.MeasureText("Ayg", Theme.FLabel).Height;
            int gap = Sc(10);
            int top = (Height - th - gap - hh) / 2;

            Chrome.DrawText(g, title, Theme.FTitle, new Rectangle(pad, top, w, th),
                            Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), Chrome.Center);
            Chrome.DrawText(g, hint, Theme.FLabel, new Rectangle(pad, top + th + gap, w, hh),
                            Theme.TextDim, Chrome.Center);
        }

        void PaintPlugin(Graphics g, int pad, int y, int w, int over)
        {
            PluginStat p = _plugin;
            if (p == null)
            {
                PaintEmpty(g, pad, w, "No plug-in selected", "Pick one to see where it is used");
                return;
            }
            InstalledPlugin inst = p.Installed;

            y = Line(g, p.Name, Theme.FTitle, Theme.Text, pad, y, w) + Sc(2);
            y = Line(g, p.Vendor.Length > 0 ? p.Vendor : "unknown developer",
                     Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(16);

            string state;
            Color stateColor;
            if (p.Match == MatchKind.Exact && (inst == null || !inst.FileMissing))
            {
                state = "installed";
                stateColor = Theme.Green;
            }
            else if (p.Match == MatchKind.OtherFormat)
            {
                state = "installed as " + (inst != null ? inst.Format : "?");
                stateColor = Theme.TextDim;
            }
            else
            {
                state = "not installed";
                stateColor = Theme.Red;
            }

            y = Row(g, "Status:", state, stateColor, pad, y, w);
            y = Row(g, "Format:", p.Format, Theme.Text, pad, y, w);
            if (inst != null)
            {
                y = Row(g, "Version:", inst.Version, Theme.Text, pad, y, w);
                y = Row(g, "Category:", inst.Category.Replace("|", " · "), Theme.Text, pad, y, w);
            }
            // The brackets are mandatory: "+" binds tighter than "?:", and without them the
            // expression collapsed into the single word " sets" and the number vanished.
            y = Row(g, "Used in:", Chrome.Plural(p.Sets, "set"),
                    p.Sets == 0 ? Theme.TextDim : Theme.Text, pad, y, w);
            y += Sc(16);

            // A plugin path is a link in itself, like a set path on a set.
            if (inst != null && inst.Path.Length > 0)
            {
                int pathTop = y;
                int pathBottom = Wrapped(g, inst.Path, Theme.FSmall,
                                         inst.FileMissing ? Theme.Red
                                                          : (_linkHot ? Theme.Text : Theme.TextDim),
                                         pad, y, w);
                _linkRect = new Rectangle(pad, pathTop, w, pathBottom - pathTop);
                y = pathBottom + Sc(24);
            }
            else _linkRect = Rectangle.Empty;

            List<SetEntry> users = _users;

            y = Line(g, "Sets" + " (" + p.Sets + "):", Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(8);
            // if (users.Count == 0)
            //     y = Line(g, "not used anywhere — safe to uninstall",
            //              Theme.FLabel, Theme.TextDim, pad, y, w);
            int shown = 0;
            foreach (SetEntry s in users)
            {
                if (Below(y + Sc(28))) break;
                Rectangle rr = new Rectangle(pad, y, w, Sc(28));
                bool hot = _setRowHot == shown;
                Chrome.DrawText(g, s.Name, Theme.FLabel, rr, hot ? Color.White : Theme.Text, PanelLeft);
                if (hot) Underline(g, s.Name, Theme.FLabel, rr, Color.White);
                _setRowRects.Add(rr);
                _setRowSets.Add(s);
                y += Sc(28);
                shown++;
            }
            // We count by the actual length of the list rather than by p.Sets: they ought to
            // match, but should they diverge, lying about "N more" is worse than showing
            // nothing.
            if (users.Count > shown)
            {
                if (!Below(y + Sc(28)))
                    y = Line(g, "… " + (users.Count - shown) + " more",
                             Theme.FLabel, Theme.TextDim, pad, y, w);
                else
                    // We allow for the height of the rows not drawn all the same, or the panel
                    // thinks itself shorter than it is and the tail cannot be scrolled to.
                    y += Sc(28) * (users.Count - shown);
            }

            _contentHeight = y + _scroll + over + pad;
            ClampScroll();

            PaintScrollTop(g);
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        void PaintFolder(Graphics g, int y, int w, int over)
        {
            SampleFolder d = _folder;
            if (d == null)
            {
                PaintEmpty(g, Pad, w, "No folder selected", "Pick a folder or a sample");
                return;
            }

            y = Header(g, d.Name, MainForm.SizeMB(d.TotalBytes), y, w) + Sc(10);
            y = PathLink(g, d.Path, y, w) + Sc(16);

            FolderUse u = _unknown ? null : _use.Of(d);
            y = Row(g, "Samples:", d.TotalSamples.ToString("N0", Inv), Theme.Text, Pad, y, w);
            y = UseRow(g, "Used:", u == null ? null : u.Used.ToString("N0", Inv) + " · " + Share(u.Used, d.TotalSamples), "none", y, w);
            y = UseRow(g, "Projects:", u == null ? null : u.Projects.ToString("N0", Inv), "none", y, w);
            y = UseRow(g, "Last used:", u == null ? null : u.LastUsed.ToLocalTime().ToString("yyyy-MM-dd"), "never", y, w);
            y += Sc(16);

            if (u != null && _topFiles.Count > 0)
            {
                y = Line(g, "Most used (" + u.Used.ToString("N0", Inv) + "):", Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(8);
                foreach (SampleFile f in _topFiles)
                {
                    if (Below(y + Sc(28))) break;
                    bool hot = _fileRowHot == _fileRowRects.Count;
                    Rectangle rr = new Rectangle(Pad, y, w, Sc(28));
                    // How many projects, at the right — the reason the sample is in the list.
                    string count = _use.Of(f).Projects.ToString("N0", Inv);
                    Chrome.DrawText(g, count, Theme.FLabel, rr, Theme.TextDim, PanelRight);
                    int cw = TextRenderer.MeasureText(g, count, Theme.FLabel, rr.Size, PanelRight).Width;
                    Rectangle nr = new Rectangle(rr.X, rr.Y, Math.Max(0, rr.Width - cw - Sc(8)), rr.Height);
                    Chrome.DrawText(g, f.Name, Theme.FLabel, nr, hot ? Color.White : Theme.Text, PanelLeft);
                    if (hot) Underline(g, f.Name, Theme.FLabel, nr, Color.White);
                    _fileRowRects.Add(rr);
                    y += Sc(28);
                }
                // Ten at most, on purpose: the whole list is the Most used lens.
                if (u.Used > _fileRowRects.Count && !Below(y + Sc(28)))
                    y = Line(g, "… " + (u.Used - _fileRowRects.Count).ToString("N0", Inv) + " more",
                             Theme.FLabel, Theme.TextDim, Pad, y, w);
                y += Sc(16);
            }

            y = ProjectsList(g, y, w);
            _contentHeight = y + _scroll + over + Pad;
            ClampScroll();
            PaintScrollTop(g);
        }

        void PaintSample(Graphics g, int y, int w, int over)
        {
            SampleFile f = _sample;
            if (f == null)
            {
                PaintEmpty(g, Pad, w, "No folder selected", "Pick a folder or a sample");
                return;
            }

            y = Header(g, f.Name, MainForm.SampleSize(f.Size), y, w) + Sc(16);

            // Where a set shows its arrangement, a sample shows its wave.
            _waveRect = new Rectangle(Pad, y, w, Sc(96));
            string hint = !f.CanPreview ? "only Live plays this file"
                        : _wave == null ? "reading…"
                        : _wave.Ok ? "" : _wave.Note;
            WaveView.PaintWave(g, _waveRect, _wave, _waveProgress, _wavePlaying, hint, DeviceDpi / 96f);
            y = _waveRect.Bottom + Sc(16);

            y = PathLink(g, f.Path, y, w) + Sc(16);

            SampleUse u = _unknown ? null : _use.Of(f);
            if (_durationMs > 0) y = Row(g, "Duration:", Duration(_durationMs), Theme.Text, Pad, y, w);
            if (_format.Length > 0) y = Row(g, "Format:", _format, Theme.Text, Pad, y, w);
            y = UseRow(g, "Projects:", u == null ? null : u.Projects.ToString("N0", Inv), "none", y, w);
            y = UseRow(g, "Last used:", u == null ? null : u.LastUsed.ToLocalTime().ToString("yyyy-MM-dd"), "never", y, w);
            y += Sc(16);

            y = ProjectsList(g, y, w);
            _contentHeight = y + _scroll + over + Pad;
            ClampScroll();
            PaintScrollTop(g);
        }

        /// <summary>A usage line: the value, or — while the sets are still being read — "…", or
        /// dim "none"/"never" when nothing uses it.</summary>
        int UseRow(Graphics g, string label, string value, string nothing, int y, int w)
        {
            if (value != null) return Row(g, label, value, Theme.Text, Pad, y, w);
            return Row(g, label, _unknown ? "…" : nothing, Theme.TextDim, Pad, y, w);
        }

        /// <summary>The projects of a folder or a sample, newest first, each under its newest
        /// set — a click goes to that set on the Sets tab, as from a plugin's panel. Names
        /// only, as there: a date beside them cut the longer ones short.</summary>
        int ProjectsList(Graphics g, int y, int w)
        {
            if (_projects.Count == 0) return y;
            y = Line(g, "Projects (" + _projects.Count.ToString("N0", Inv) + "):", Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(8);
            int shown = 0;
            foreach (SetEntry s in _projects)
            {
                if (Below(y + Sc(28))) break;
                bool hot = _setRowHot == shown;
                Rectangle rr = new Rectangle(Pad, y, w, Sc(28));
                Chrome.DrawText(g, s.Name, Theme.FLabel, rr, hot ? Color.White : Theme.Text, PanelLeft);
                if (hot) Underline(g, s.Name, Theme.FLabel, rr, Color.White);
                _setRowRects.Add(rr);
                _setRowSets.Add(s);
                y += Sc(28);
                shown++;
            }
            // As on a plugin: what did not fit is counted, and its height is kept so the tail
            // can be scrolled to.
            if (_projects.Count > shown)
            {
                if (!Below(y + Sc(28)))
                    y = Line(g, "… " + (_projects.Count - shown) + " more", Theme.FLabel, Theme.TextDim, Pad, y, w);
                else
                    y += Sc(28) * (_projects.Count - shown);
            }
            return y;
        }

        static string Share(int part, int whole)
        {
            if (whole <= 0) return "";
            double p = part * 100.0 / whole;
            if (p > 0 && p < 0.1) return "<0.1%";
            return p.ToString(p < 10 ? "0.0" : "0", Inv) + "%";
        }

        static string Duration(int ms)
        {
            if (ms < 60000) return (ms / 1000.0).ToString("0.0", Inv) + " s";
            int s = ms / 1000;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // ------------------------------------------------------------------ pieces

        /// <summary>
        /// The name and the weight on one line — the heading of every kind of panel. A name too
        /// long for it takes a second line rather than losing its end: names in a sample library
        /// run long ("Session Drums Multimic" already does not fit beside "12.31 GB").
        /// </summary>
        int Header(Graphics g, string name, string size, int y, int w)
        {
            int titleH = Sc(26);
            Size sw = TextRenderer.MeasureText(g, size, Theme.FLabel, new Size(w, titleH), PanelRight);
            int nw = w - sw.Width - Sc(10);
            Chrome.DrawText(g, size, Theme.FLabel, new Rectangle(Pad, y, w, titleH), Theme.TextDim, PanelRight);

            const TextFormatFlags oneLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (TextRenderer.MeasureText(g, name, Theme.FTitle, Size.Empty, oneLine).Width <= nw)
            {
                Chrome.DrawText(g, name, Theme.FTitle, new Rectangle(Pad, y, nw, titleH), Theme.Text, PanelLeft);
                return y + titleH;
            }

            // The first line stands where the one-line title would; the second, if even two
            // are not enough, ends in an ellipsis.
            int line = TextRenderer.MeasureText(g, "Ag", Theme.FTitle).Height;
            int top = y + (titleH - line) / 2;
            int h = Math.Min(line * 2, TextRenderer.MeasureText(g, name, Theme.FTitle, new Size(nw, int.MaxValue), Chrome.Wrap).Height);
            TextRenderer.DrawText(g, name, Theme.FTitle, new Rectangle(Pad, top, nw, h), Theme.Text,
                                  Chrome.Wrap | TextFormatFlags.EndEllipsis);
            return top + h + (titleH - line) / 2;
        }

        /// <summary>A path that is itself the link to Explorer, as on a plugin.</summary>
        int PathLink(Graphics g, string path, int y, int w)
        {
            int bottom = Wrapped(g, path, Theme.FSmall, _linkHot ? Theme.Text : Theme.TextDim, Pad, y, w);
            _linkRect = new Rectangle(Pad, y, w, bottom - y);
            return bottom;
        }

        /// <summary>Only this row and only to the width of its text — a link, not a
        /// button.</summary>
        void Underline(Graphics g, string text, Font f, Rectangle rr, Color c)
        {
            Size ts = TextRenderer.MeasureText(g, text, f, new Size(rr.Width, rr.Height), PanelLeft);
            int ly = rr.Y + (rr.Height + ts.Height) / 2;
            int lx = rr.X + UnderlinePad;
            using (Pen ln = new Pen(c))
                g.DrawLine(ln, lx, ly, lx + Math.Min(ts.Width, rr.Width - UnderlinePad), ly);
        }

        int Thumb(Graphics g, int x, int y, int w)
        {
            int h = (int)Math.Round(w * 180f / 302f);      // proportions from the mockup
            _thumbRect = new Rectangle(x, y, w, h);
            Theme.FillRound(g, _thumbRect, Sc(Theme.ThumbR), Theme.Bg);

            string hint = null;
            if (_arr == null) hint = "reading…";
            else if (_arr.Error != null) hint = "could not read the set";
            else if (!_arr.HasContent) hint = "arrangement is empty";

            if (hint != null)
            {
                Chrome.DrawText(g, hint, Theme.FSmall, _thumbRect, Theme.TextDim, Chrome.Center);
                return y + h;
            }

            int inset = Sc(6);
            Rectangle inner = Rectangle.Inflate(_thumbRect, -inset, -inset);
            if (inner.Width > 0 && inner.Height > 0)
            {
                if (_thumb == null || _thumbSize != inner.Size)
                {
                    if (!_thumbRendering)
                    {
                        _thumbRendering = true;
                        DropThumb();
                        Size sz = inner.Size;
                        float dpi = DeviceDpi / 96f;
                        Arrangement arr = _arr;
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            RenderOptions o = new RenderOptions();
                            o.Dpi = dpi;
                            o.MaxLane = 10;
                            Bitmap bmp = ArrangementRender.ToBitmap(arr, sz.Width, sz.Height, o);
                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    DropThumb();
                                    _thumb = bmp;
                                    _thumbSize = sz;
                                    _thumbRendering = false;
                                    Invalidate(_thumbRect);
                                });
                            }
                            catch { if (bmp != null) bmp.Dispose(); }
                        });
                    }
                }
                if (_thumb != null) g.DrawImageUnscaled(_thumb, inner.Location);
            }

            // The magnifier in the corner hints that the preview opens full screen.
            int mg = Sc(18);
            RectangleF mr = new RectangleF(_thumbRect.Right - mg - Sc(8), _thumbRect.Bottom - mg - Sc(8), mg, mg);
            Icons.Draw(g, Glyph.Magnifier, mr, _thumbHot ? Color.White : Theme.TextDim, 1.6f);
            return y + h;
        }

        int Line(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, text, f, new Rectangle(x, y, w, h), c, PanelLeft);
            return y + h;
        }

        int Wrapped(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Chrome.Wrap).Height;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), c, Chrome.Wrap);
            return y + h;
        }

        /// <summary>
        /// A "label — value" line. The value is pushed right but not across the full width: a
        /// long value covered the label with itself (on "Category: Fx · Dynamics · Mastering"
        /// the colon disappeared under the first word of the value). The width for the value is
        /// counted as what remains after the label, and if it does not fit — an ellipsis.
        /// </summary>
        int Row(Graphics g, string label, string value, Color valueColor, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(value)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, label, Theme.FLabel, new Rectangle(x, y, w, h), Theme.TextDim, PanelLeft);

            int lw = TextRenderer.MeasureText(g, label, Theme.FLabel, new Size(w, h), PanelLeft).Width;
            int vx = x + lw + Sc(12);
            int vw = Math.Max(Sc(24), x + w - vx);
            Chrome.DrawText(g, value, Theme.FLabel, new Rectangle(vx, y, vw, h), valueColor, PanelRight);
            return y + h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scroller != null) _scroller.Dispose();
                DropThumb();
            }
            base.Dispose(disposing);
        }
    }
}


