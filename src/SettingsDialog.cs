using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The program's settings. The program name in the header used to open the hotkeys and
    /// nothing else, while the single setting hid in a right-click menu — findable only by
    /// someone who already knew it was there.
    ///
    /// What lands here is what concerns the program as a whole: how the window looks and where
    /// the plugin list comes from. Catalog settings do not move here — they already live where
    /// they are looked at: grouping and columns in the table header menu, pinning in the star,
    /// folders in "Folders…".
    ///
    /// The row layout is one for the whole window: caption on the left, control on the right —
    /// as in Live's own Preferences, so it reads vertically as a single column of values.
    /// </summary>
    public sealed class SettingsDialog : GlassDialog
    {
        readonly Settings _s;

        readonly PillToggle _compat = new PillToggle();
        readonly PillToggle _smooth = new PillToggle();

        readonly Segmented _source = new Segmented();
        readonly DropField _install = new DropField();
        readonly PillToggle _vst2On = new PillToggle();
        readonly GlassButton _vst2Browse = new GlassButton();
        readonly PillToggle _vst3SysOn = new PillToggle();
        readonly PillToggle _vst3On = new PillToggle();
        readonly GlassButton _vst3Browse = new GlassButton();
        readonly GlassButton _rescan = new GlassButton();

        readonly GlassButton _openCache = new GlassButton();
        readonly GlassButton _update = new GlassButton();
        readonly PillToggle _autoUpdate = new PillToggle();
        readonly GlassButton _restart = new GlassButton();

        /// <summary>The transparency toggle was touched — we offer a restart. This used to be
        /// asked by a system MessageBox: a foreign style over a glass window, and a mandatory
        /// answer to an accidental click on top of that.</summary>
        bool _restartPending;

        /// <summary>Rebuild the catalog: the plugin settings changed.</summary>
        public bool RescanWanted;

        /// <summary>The release the check turned up, for the caller to write down: the dot
        /// on the gear must not light for the same one twice. Empty — nothing was found or
        /// nobody asked.</summary>
        public string SeenVersion = "";

        // The grey line under the update button — the only place the check speaks.
        string _updateNote = "";
        string _updateUrl = "";
        int _updateJob;


        readonly List<string> _installs = new List<string>();

        /// <summary>The scrollable middle of the window — everything below the
        /// heading.</summary>
        readonly Body _body;

        public SettingsDialog(Settings s)
        {
            _s = s;
            Caption = "Settings";
            ClientSize = new Size(Sc(700), WindowH);

            _body = new Body(this);
            Controls.Add(_body);

            _smooth.Checked = _s.SmoothScroll;
            _smooth.CheckedChanged += delegate
            {
                _s.SmoothScroll = _smooth.Checked;
                Theme.SmoothScroll = _s.SmoothScroll;
            };
            State(_smooth);

            _compat.Checked = _s.DisableGlass;
            _compat.CheckedChanged += delegate { ToggleCompat(); };
            State(_compat);

            _source.SetItems("Live's database", "Scan folders");
            _source.SelectedIndex = _s.PluginsFromFolders ? 1 : 0;
            _source.SelectedChanged += delegate
            {
                _s.PluginsFromFolders = _source.SelectedIndex == 1;
                RescanWanted = true;
                Relayout();
                DescribeInventoryAsync();
            };
            _body.Controls.Add(_source);

            // "All installs" as the first item: the plugin list is assembled from all of them
            // at once, and that is the right default — see PluginInventory.Load. The item list
            // here is provisional (PluginInventory.Installs() does not parse the contents — see
            // its comment); the real one, counted by a full parse, is substituted by
            // RefreshInstallsAsync below as soon as it finishes.
            _installs.Add("All installs");
            _installs.AddRange(PluginInventory.Installs());
            int at = _installs.IndexOf(_s.PluginSource);
            _install.SetItems(_installs, at > 0 ? at : 0);
            _install.Width = Sc(240);
            _install.SelectedChanged += delegate
            {
                _s.PluginSource = _install.SelectedIndex <= 0 ? "" : _install.SelectedItem;
                RescanWanted = true;
                DescribeInventoryAsync();
            };
            _body.Controls.Add(_install);

            Toggle(_vst2On, _s.Vst2CustomOn, delegate { _s.Vst2CustomOn = _vst2On.Checked; });
            Toggle(_vst3SysOn, _s.Vst3SystemOn, delegate { _s.Vst3SystemOn = _vst3SysOn.Checked; });
            Toggle(_vst3On, _s.Vst3CustomOn, delegate { _s.Vst3CustomOn = _vst3On.Checked; });

            Browse(_vst2Browse, delegate
            {
                string p = Pick("VST2 plug-in folder", _s.Vst2CustomPath);
                if (p != null) { _s.Vst2CustomPath = p; _s.Vst2CustomOn = true; _vst2On.Checked = true; }
            });
            Browse(_vst3Browse, delegate
            {
                string p = Pick("VST3 plug-in folder", _s.Vst3CustomPath);
                if (p != null) { _s.Vst3CustomPath = p; _s.Vst3CustomOn = true; _vst3On.Checked = true; }
            });

            _autoUpdate.Checked = _s.CheckUpdates;
            _autoUpdate.CheckedChanged += delegate { _s.CheckUpdates = _autoUpdate.Checked; };
            State(_autoUpdate);

            _update.Text = "Check for updates";
            _update.FitToText(16);
            _update.Click += delegate { OnUpdateButton(); };
            _body.Controls.Add(_update);

            _rescan.Text = "Rescan";
            _rescan.FitToText(16);
            _rescan.Click += delegate { RescanWanted = true; Close(); };
            _body.Controls.Add(_rescan);

            _restart.Text = "Restart now";
            _restart.Primary = true;
            _restart.Visible = false;
            _restart.Click += delegate { _s.Save(); Application.Restart(); };
            _body.Controls.Add(_restart);

            _openCache.Text = "Open folder";
            _openCache.FitToText(16);
            _openCache.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                    Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
                }
                catch { }
            };
            _body.Controls.Add(_openCache);

            // One width for every button in the right column: three different widths gave three
            // different left edges in one column, and the right column fell apart.
            GlassButton[] rightButtons = new GlassButton[] { _openCache, _rescan, _restart };
            int buttonW = Sc(128);
            foreach (GlassButton b in rightButtons) buttonW = Math.Max(buttonW, b.Width);
            foreach (GlassButton b in rightButtons) b.Width = buttonW;

            DescribeInventoryAsync();
            RefreshInstallsAsync();
        }

        void Toggle(PillToggle t, bool on, EventHandler changed)
        {
            t.Checked = on;
            t.CheckedChanged += changed;
            t.CheckedChanged += delegate { RescanWanted = true; Relayout(); DescribeInventoryAsync(); };
            State(t);
        }

        /// <summary>
        /// An Apple-style switch: purely geometric (an accent bed plus a smooth white knob).
        /// </summary>
        void State(PillToggle t)
        {
            t.IsSwitch = true;
            t.Size = new Size(Sc(42), Sc(24));
            _body.Controls.Add(t);
        }

        void Browse(GlassButton b, MethodInvoker click)
        {
            b.Text = "Browse";
            b.FitToText(16);
            b.Click += delegate { click(); RescanWanted = true; Relayout(); DescribeInventoryAsync(); };
            _body.Controls.Add(b);
        }

        string Pick(string title, string current)
        {
            return ModernFolderPicker.PickFolder(Handle, title, current);
        }

        /// <summary>
        /// Window transparency is only switched through a restart, and that is not laziness:
        /// some of the theme's colours are static readonly and are computed once off
        /// Glass.Enabled (see MainForm.ToggleGlass, which this moved from).
        /// </summary>
        void ToggleCompat()
        {
            _s.DisableGlass = _compat.Checked;
            _s.Save();

            // The offer to restart lives as a line in this same window: an accidentally clicked
            // toggle must not demand an answer in a foreign dialog. The line is visible only
            // while the setting disagrees with the current session (DisableGlass and
            // Glass.Enabled are of opposite polarity, hence the == comparison): put the toggle
            // back and there is nothing to restart for, and the line goes.
            _restartPending = Glass.Supported() && _s.DisableGlass == Glass.Enabled;
            Relayout();
        }

        // ------------------------------------------------------------------ state

        string _inventory = "";
        int _job;

        /// <summary>
        /// How many plugins are visible right now and where from. Read in the background:
        /// parsing Live's database means a megabyte of text, and walking the folders goes to
        /// the disk outright. While it counts, a person manages to click again, so answers from
        /// older passes are discarded by number — otherwise a number from the previous settings
        /// would settle on the screen.
        /// </summary>
        /// <summary>
        /// The button asks, and once something has been found it opens the page instead. Two
        /// jobs on one button because they are one errand: the answer to "is there anything
        /// new" is either "no" or a place to go.
        /// </summary>
        void OnUpdateButton()
        {
            if (_updateUrl.Length > 0)
            {
                try { Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true }); }
                catch (Exception ex) { _updateNote = ex.Message; Invalidate(); }
                return;
            }

            _updateNote = "Checking…";
            _update.Enabled = false;
            Invalidate();

            string current = Application.ProductVersion;
            int mine = ++_updateJob;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateCheck.Result r = UpdateCheck.Fetch();
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (mine != _updateJob || IsDisposed) return;
                        _update.Enabled = true;

                        // Asked by hand, so a failure is reported. The background check stays
                        // silent about exactly the same thing — there nobody is waiting for an
                        // answer.
                        if (r.Error.Length > 0) { _updateNote = r.Error; Invalidate(); return; }

                        // Any newer release is worth mentioning here, a fix included. The dot on
                        // the gear is the one that keeps to big steps only.
                        if (UpdateCheck.Compare(current, r.Version) == UpdateCheck.Step.None)
                        {
                            _updateNote = "You have the latest version";
                            Invalidate();
                            return;
                        }

                        SeenVersion = r.Version;
                        _updateUrl = r.Url;
                        _updateNote = "Version " + r.Version + " is available";
                        _update.Text = "Open release page";
                        _update.FitToText(16);
                        Relayout();
                    });
                }
                catch { }
            });
        }

        void DescribeInventoryAsync()
        {
            _inventory = "Reading…";
            Invalidate();

            int mine = ++_job;
            Settings snapshot = _s;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                PluginInventory inv = PluginInventory.Load(snapshot);
                // There can be a dozen and a half Live installs on a machine, and they do not
                // fit into a line as a list — what matters is not which ones but how many added
                // up.
                string where = inv.Sources.Count == 1
                    ? inv.Sources[0]
                    : inv.Sources.Count > 1
                      ? inv.Sources.Count + " installs of Live"
                      : "";
                string text = inv.All.Count == 0
                    ? (inv.Error ?? "nothing found")
                    : inv.All.Count + " plug-ins"
                      + (where.Length > 0 ? "   ·   " + where : "");
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (mine != _job) return;
                        _inventory = text;
                        Invalidate();
                    });
                }
                catch { }
            });
        }

        // ------------------------------------------------------------------ installs

        int _installJob;

        /// <summary>
        /// The exact list of installs with at least one plugin — by the same full parse Load
        /// uses for the status line (PluginInventory.Sources). The provisional
        /// PluginInventory.Installs() has already lied once: it offered Live 12.0.10 on the
        /// strength of a "found: Serum" line in the scanner's cumulative log, while the Serum
        /// file has long been gone from the disk — LoadFrom throws that entry out itself while
        /// parsing. Hence the next bug: choosing such an install silently shortens the plugin
        /// list, with nothing about it visible in the dropdown.
        ///
        /// It is always counted with the defaults (a fresh Settings, not the current _s): the
        /// dropdown has to answer "what is there on this machine at all" rather than depend on
        /// what is selected in the filter right now.
        /// </summary>
        void RefreshInstallsAsync()
        {
            int mine = ++_installJob;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                PluginInventory inv = PluginInventory.Load(new Settings());
                List<string> names = new List<string>();
                foreach (string src in inv.Sources)
                {
                    int dot = src.IndexOf(" · ", StringComparison.Ordinal);
                    names.Add(dot < 0 ? src : src.Substring(0, dot));
                }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (mine != _installJob) return;
                        ApplyInstalls(names);
                    });
                }
                catch { }
            });
        }

        void ApplyInstalls(List<string> names)
        {
            string current = _install.SelectedItem;
            _installs.Clear();
            _installs.Add("All installs");
            _installs.AddRange(names);

            int at = _installs.IndexOf(current);
            if (at <= 0 && _s.PluginSource.Length > 0)
            {
                // The selected install yields not a single real plugin (or is gone from the
                // list) — we forget it rather than leave the catalog silently short.
                _s.PluginSource = "";
                RescanWanted = true;
                DescribeInventoryAsync();
            }
            _install.SetItems(_installs, Math.Max(0, at));
            Invalidate();
        }

        // ------------------------------------------------------------------ layout

        bool Folders { get { return _source.SelectedIndex == 1; } }

        readonly List<Row> _rows = new List<Row>();

        sealed class Row
        {
            public string Title = "", Detail = "";
            public Rectangle Rect;
            public bool Section;
            public bool Separator;
        }

        void Relayout()
        {
            if (_body == null) return;
            _body.Rebuild();
            FitHeight(_body.Top + _body.ContentHeight + Sc(16));
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_body == null) return;
            int top = Card.Top + Sc(92);
            _body.SetBounds(Card.Left, top, Card.Width, Math.Max(0, Card.Bottom - top - Sc(16)));
            Relayout();
        }

        /// <summary>
        /// Lay the rows out inside the panel: caption on the left, control on the right. The
        /// coordinates are the panel's, shifted by the scroll; it returns the full height of
        /// the content.
        /// </summary>
        internal int BuildRows(int scroll)
        {
            _rows.Clear();
            _rail = Rectangle.Empty;

            int pad = Sc(Theme.Pad);
            int x = pad;
            int w = Math.Max(0, _body.Width - pad * 2);
            int h = Sc(Theme.ControlH);
            int y = -scroll;

            Section(x, ref y, w, "General");

            Line(x, ref y, w, h, _update, "Alive " + Application.ProductVersion, "");
            _updateRect = Rectangle.Empty;
            if (_updateNote.Length > 0)
            {
                _updateRect = new Rectangle(x, y - Sc(8), w - _update.Width - Sc(16), Sc(24));
                y += Sc(22);
            }

            Line(x, ref y, w, h, _autoUpdate,
                 "Check automatically",
                 "Once a day, asks GitHub for the newest release. Nothing about you, your "
                 + "library or this machine is sent.");

            Line(x, ref y, w, h, _openCache,
                 "Temporary files",
                 Settings.Dir);

            Separator(x, ref y, w);
            Section(x, ref y, w, "Appearance");
            Line(x, ref y, w, h, _smooth,
                 "Smooth scrolling",
                 "");
            Line(x, ref y, w, h, _compat,
                 "Disable transparency (Win10 Compatible)",
                 "Turns off glass effect if window dragging lags. Applies after restart.");

            // A button with no caption: the line above has already said a restart is needed,
            // and repeating it as a heading with an explanation makes three statements of one
            // thing.
            _restart.Visible = _restartPending;
            if (_restartPending)
            {
                y -= Sc(12);   // pushed against the toggle row — they are one whole
                Line(x, ref y, w, h, _restart, "", "");
            }

            Separator(x, ref y, w);
            Section(x, ref y, w, "Plug-ins");
            Line(x, ref y, w, h, _source,
                 "Plug-in source",
                 Folders
                 ? "Direct folder scan (VST2 / VST3 files)."
                 : "Uses internal database from installed Live versions.");

            // The source's child rows: what there is to ask about at all is decided by the row
            // above, so they are indented and on a shared rail — like versions under a set in
            // the list.
            int cx = x + Sc(20);
            int cw = Math.Max(0, w - Sc(20));
            int railTop = y;

            _install.Visible = !Folders;
            if (!Folders)
                Line(cx, ref y, cw, h, _install, "Live install", "");

            foreach (Control c in new Control[] { _vst2On, _vst2Browse, _vst3SysOn, _vst3On, _vst3Browse })
                c.Visible = Folders;

            if (Folders)
            {
                Line(cx, ref y, cw, h, _vst2On,
                     "Custom VST2 folder", "");
                Line(cx, ref y, cw, h, _vst2Browse,
                     "VST2 folder path",
                     _s.Vst2CustomPath.Length > 0 ? _s.Vst2CustomPath : "not set");

                Line(cx, ref y, cw, h, _vst3SysOn,
                     "System VST3 folders", "");
                Line(cx, ref y, cw, h, _vst3On,
                     "Custom VST3 folder", "");
                Line(cx, ref y, cw, h, _vst3Browse,
                     "VST3 folder path",
                     _s.Vst3CustomPath.Length > 0 ? _s.Vst3CustomPath : "not set");
            }

            _rail = new Rectangle(x + Sc(6), railTop + Sc(2), Sc(2),
                                  Math.Max(0, (y - Sc(20)) - railTop - Sc(4)));

            Line(x, ref y, w, h, _rescan, "Installed right now", "");
            _statusRect = new Rectangle(x, y - Sc(6), w - _rescan.Width - Sc(16), Sc(28));
            y += Sc(32);

            return y + scroll;
        }

        bool _sizing;

        /// <summary>
        /// The window height fits the full list of the "Live's database" source — which is also
        /// the default. The window holds to it: switching the source changes the number of rows
        /// threefold, and a frame jumping about from that reads as a different window rather
        /// than as different content. Whatever does not fit scrolls inside the panel.
        /// </summary>
        int WindowH { get { return Sc(760); } }

        /// <summary>Fit the window height to the content, but no taller than WindowH.</summary>
        void FitHeight(int need)
        {
            if (_sizing) return;
            int max = Math.Min(WindowH, Screen.FromControl(this).WorkingArea.Height - Sc(80));
            need = Math.Min(need, max);
            if (Math.Abs(ClientSize.Height - need) <= Sc(2)) return;

            _sizing = true;
            try { ClientSize = new Size(ClientSize.Width, need); }
            finally { _sizing = false; }
        }

        Rectangle _statusRect, _updateRect;

        /// <summary>The rail to the left of the plugin source's child rows.</summary>
        Rectangle _rail;

        int DetailHeight(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            const TextFormatFlags wrapFlags = TextFormatFlags.Left | TextFormatFlags.Top |
                                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding;
            return TextRenderer.MeasureText(text, Theme.FSmall, new Size(width, 1000), wrapFlags).Height;
        }

        void Section(int x, ref int y, int w, string title)
        {
            Row r = new Row();
            r.Title = title;
            r.Section = true;
            r.Rect = new Rectangle(x, y, w, Sc(20));
            _rows.Add(r);
            y += Sc(28);
        }

        void Separator(int x, ref int y, int w)
        {
            y += Sc(16);
            Row r = new Row();
            r.Separator = true;
            r.Rect = new Rectangle(x, y, w, Sc(1));
            _rows.Add(r);
            y += Sc(8);
        }

        /// <summary>A settings row: caption on the left, control pushed to the right
        /// edge.</summary>
        void Line(int x, ref int y, int w, int h, Control c, string title, string detail)
        {
            int ch = (c is Segmented || (c is PillToggle && ((PillToggle)c).IsSwitch)) ? c.Height : h;
            int cy = y + (h - ch) / 2;
            c.SetBounds(x + w - c.Width, cy, c.Width, ch);

            Row r = new Row();
            r.Title = title;
            r.Detail = detail;
            r.Rect = new Rectangle(x, y, w - c.Width - Sc(16), h);
            _rows.Add(r);

            y += r.Rect.Height;
            if (detail.Length > 0)
            {
                y += Sc(4);
                y += DetailHeight(detail, r.Rect.Width);
            }
            y += Sc(20);
        }

        /// <summary>The rows are drawn by the panel — which also clips them at its own
        /// edge.</summary>
        internal void PaintRows(Graphics g)
        {
            const TextFormatFlags leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                              TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoClipping;
            const TextFormatFlags wrapFlags = TextFormatFlags.Left | TextFormatFlags.Top |
                                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoClipping;

            if (_rail.Height > 0)
                g.FillRectangle(Theme.GetBrush(Color.FromArgb(120, Theme.TextDim)), _rail);

            foreach (Row r in _rows)
            {
                if (r.Separator)
                {
                    using (Pen pen = new Pen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1f))
                        g.DrawLine(pen, r.Rect.X, r.Rect.Y, r.Rect.Right, r.Rect.Y);
                    continue;
                }

                if (r.Section)
                {
                    Chrome.DrawText(g, r.Title, Theme.FLabel, r.Rect, Theme.TextDim, leftFlags);
                    continue;
                }

                Chrome.DrawText(g, r.Title, Theme.FBody, r.Rect, Theme.Text, leftFlags);
                if (r.Detail.Length == 0) continue;

                Rectangle d = new Rectangle(r.Rect.X, r.Rect.Bottom + Sc(4),
                                            r.Rect.Width, DetailHeight(r.Detail, r.Rect.Width) + Sc(8));
                Chrome.DrawText(g, r.Detail, Theme.FSmall, d, Theme.TextDim, wrapFlags);
            }

            Chrome.DrawText(g, _inventory, Theme.FSmall, _statusRect, Theme.TextDim, leftFlags);
            Chrome.DrawText(g, _updateNote, Theme.FSmall, _updateRect, Theme.TextDim, leftFlags);
        }

        /// <summary>The wheel over the heading or the close button scrolls too.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_body != null) _body.Scroll(e.Delta);
            base.OnMouseWheel(e);
        }

        /// <summary>
        /// The scrollable middle of the window. A real child control rather than a coordinate
        /// shift: it clips buttons that have moved past the edge itself (the captions are
        /// written by TextRenderer, which honours no Clip — see RowListView), and the wheel
        /// over any of them arrives here as well.
        /// </summary>
        sealed class Body : GlassControl
        {
            readonly SettingsDialog _d;
            readonly ScrollFade _fade;
            int _scroll, _contentH;
            bool _building, _dragging;
            int _dragOffset;

            public Body(SettingsDialog d)
            {
                _d = d;
                _fade = new ScrollFade(this, BarRect);
            }

            public int ContentHeight { get { return _contentH; } }

            int MaxScroll { get { return Math.Max(0, _contentH - Height); } }

            /// <summary>Rebuild the rows for the current scroll position and size.</summary>
            public void Rebuild()
            {
                if (_building) return;
                _building = true;
                try
                {
                    _contentH = _d.BuildRows(_scroll);
                    // The content got shorter (the source was switched) — the scroll would hang
                    // past the end of the list, leaving emptiness gaping below.
                    if (_scroll > MaxScroll) { _scroll = MaxScroll; _contentH = _d.BuildRows(_scroll); }
                }
                finally { _building = false; }
                Invalidate();
            }

            /// <summary>Scroll by one wheel notch.</summary>
            public void Scroll(int delta)
            {
                if (delta == 0 || MaxScroll <= 0) return;
                int steps = delta / 120;
                if (steps == 0) steps = delta > 0 ? 1 : -1;
                SetScroll(_scroll - steps * Sc(60));
            }

            void SetScroll(int v)
            {
                v = Math.Max(0, Math.Min(MaxScroll, v));
                if (v == _scroll) return;
                _scroll = v;
                _fade.Ping();
                Rebuild();
            }

            Rectangle BarRect()
            {
                if (_contentH <= Height || Height <= 0) return Rectangle.Empty;
                int track = Height - Sc(10);
                int h = Math.Max(Sc(40), (int)(track * (float)Height / _contentH));
                int max = Math.Max(1, _contentH - Height);
                int y = Sc(5) + (int)((track - h) * (_scroll / (float)max));
                return new Rectangle(Width - Sc(8), y, Sc(4), h);
            }

            protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

            protected override void OnMouseWheel(MouseEventArgs e) { Scroll(e.Delta); base.OnMouseWheel(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Rectangle bar = BarRect();
                if (!bar.IsEmpty &&
                    new Rectangle(bar.X - Sc(6), bar.Y, bar.Width + Sc(10), bar.Height).Contains(e.Location))
                {
                    _dragging = true;
                    _dragOffset = e.Y - bar.Y;
                    _fade.Ping();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                Rectangle bar = BarRect();
                if (_dragging && !bar.IsEmpty)
                {
                    int track = Height - Sc(10) - bar.Height;
                    if (track > 0)
                        SetScroll((int)Math.Round((e.Y - _dragOffset - Sc(5)) * (MaxScroll / (float)track)));
                }
                else _fade.SetHot(!bar.IsEmpty && e.X >= bar.X - Sc(8));
                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

            protected override void OnMouseLeave(EventArgs e) { _fade.SetHot(false); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                PaintSurface(g);
                Theme.Smooth(g);
                _d.PaintRows(g);
                Chrome.PaintFadingBar(g, BarRect(), _dragging ? 1f : _fade.Alpha,
                                      _dragging ? 1f : _fade.Thick, true, Sc(4));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _fade.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
