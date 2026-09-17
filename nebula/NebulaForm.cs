using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager.Nebula
{
    /// <summary>
    /// The Nebula window. The chrome is the same as Alive's (glass, pills, type sizes from
    /// Theme), and there is only one thing inside — the cloud of projects and a panel assigning
    /// a value of the set to each of the six channels.
    /// </summary>
    public sealed class NebulaForm : Form
    {
        readonly Settings _settings;
        readonly ProjectIndex _index = new ProjectIndex();

        readonly CloudView _cloud = new CloudView();
        readonly DetailPanel _detail = new DetailPanel();
        ArrangementLoader _arrangements;

        public DetailPanel Detail { get { return _detail; } }
        public string SelectedPath { get { return _cloud.Selected != null ? _cloud.Selected.Path : null; } }
        public string InitialPath { get; set; }
        public event Action<string> ShowInListRequested;

        readonly IconButton _folders = new IconButton();
        readonly IconButton _min = new IconButton();
        readonly IconButton _max = new IconButton();
        readonly IconButton _close = new IconButton();

        readonly PillToggle _spin = new PillToggle();
        readonly GlassButton _reset = new GlassButton();
        readonly CameraPresetBar _camera = new CameraPresetBar();

        readonly SeekSlider _minFade = new SeekSlider();
        readonly SeekSlider _maxFade = new SeekSlider();
        readonly SeekSlider _minSize = new SeekSlider();
        readonly SeekSlider _maxSize = new SeekSlider();
        readonly DropField _dgrad = new DropField();

        // The ranges the Size sliders map 0..1 into — logical px, before Sc(). Fade's range is
        // already 0..1 (alpha), so no mapping is needed there.
        const float MinSizeLo = 0f, MinSizeHi = 12f;
        const float MaxSizeLo = 4f, MaxSizeHi = 48f;

        readonly DropField _dx = new DropField();
        readonly DropField _dy = new DropField();
        readonly DropField _dz = new DropField();
        readonly DropField _dsize = new DropField();
        readonly DropField _dalpha = new DropField();
        readonly DropField _dcolor = new DropField();

        // A checkbox beside each of the six — switch a channel off without losing the choice in
        // the list.
        readonly ChannelSwitch _ex = new ChannelSwitch();
        readonly ChannelSwitch _ey = new ChannelSwitch();
        readonly ChannelSwitch _ez = new ChannelSwitch();
        readonly ChannelSwitch _esize = new ChannelSwitch();
        readonly ChannelSwitch _ealpha = new ChannelSwitch();
        readonly ChannelSwitch _ecolor = new ChannelSwitch();

        static readonly string[] ChannelLabels = { "X", "Y", "Z", "Size", "Fade", "Colour" };

        DropField[] Fields() { return new DropField[] { _dx, _dy, _dz, _dsize, _dalpha, _dcolor }; }
        ChannelSwitch[] Switches() { return new ChannelSwitch[] { _ex, _ey, _ez, _esize, _ealpha, _ecolor }; }

        Rectangle _rPanel, _rStatus;

        bool _scanning;
        bool _rescanPending;
        int _scanDone, _scanTotal;
        CancellationTokenSource _cancel;

        string _hint = "";
        bool _weighing;

        public NebulaForm()
        {
            Text = "Nebula — Ableton projects in six dimensions";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1360, 860);
            MinimumSize = new Size(1120, 680);
            BackColor = Theme.Bg;
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _settings = Settings.Load();
            Settings.RootsChanged += OnGlobalRootsChanged;
            Build();
            LoadChannels();
        }

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        // -------------------------------------------------------------------- building

        void Build()
        {
            Controls.Add(_cloud);
            _cloud.SelectionChanged += delegate
            {
                _detail.Show(_cloud.Selected);
            };
            _cloud.OpenRequested += delegate { RevealSelected(); };

            _detail.Index = _index;
            _arrangements = new ArrangementLoader(this);
            _arrangements.Ready += _detail.OnArrangement;
            _detail.Loader = _arrangements;
            _detail.OpenRequested += delegate { OpenInLive(); };
            _detail.RevealRequested += delegate { RevealSelected(); };
            _detail.PreviewRequested += delegate { OpenPreview(); };
            _detail.ShowInListRequested += delegate { OnShowInList(); };
            _detail.NotesRequested += delegate (SetEntry s) { EditNotes(s); };
            _detail.SetRequested += delegate (SetEntry s) { OnSetRequested(s); };
            _detail.PluginRequested += delegate (string p) { OnPluginRequested(p); };
            Controls.Add(_detail);

            _min.Icon = Glyph.Minimize;
            _max.Icon = Glyph.Maximize;
            _close.Icon = Glyph.Close;
            _close.Danger = true;
            _folders.Icon = Glyph.Folder;

            _min.Click += delegate { WindowState = FormWindowState.Minimized; };
            _max.Click += delegate { ToggleMaximize(); };
            _close.Click += delegate { Close(); };
            _folders.Click += delegate { EditRoots(); };

            foreach (IconButton b in new IconButton[] { _folders, _min, _max, _close })
                Controls.Add(b);

            _spin.Text = "Spin";
            _spin.Checked = false;
            _spin.FitToText();
            _spin.CheckedChanged += delegate { _cloud.Spin = _spin.Checked; SaveChannels(); };
            Controls.Add(_spin);

            _reset.Text = "Reset view";
            _reset.Width = Sc(120);
            _reset.Height = Sc(Theme.ControlH);
            _reset.Click += delegate { _cloud.ResetView(); };
            Controls.Add(_reset);

            // An orthographic preset means exactly this view, with no rotation on top; we
            // switch the spin off, or a frame that has just squared up to an axis goes askew
            // again a second later.
            _camera.PresetClicked += delegate (CameraPreset p)
            {
                _spin.Checked = false;
                _cloud.SetPreset(p);
            };
            Controls.Add(_camera);

            _cloud.Softness = 0.04f;

            _minFade.Progress = 0.08f;
            _minFade.Seeked += delegate (float v) { _cloud.MinAlpha = v; SaveChannels(); Invalidate(); };
            Controls.Add(_minFade);

            _maxFade.Progress = 1f;
            _maxFade.Seeked += delegate (float v) { _cloud.MaxAlpha = v; SaveChannels(); Invalidate(); };
            Controls.Add(_maxFade);

            _minSize.Progress = (1f - MinSizeLo) / (MinSizeHi - MinSizeLo);
            _minSize.Seeked += delegate (float v)
            {
                _cloud.MinPointRadius = MinSizeLo + v * (MinSizeHi - MinSizeLo);
                SaveChannels(); Invalidate();
            };
            Controls.Add(_minSize);

            _maxSize.Progress = (16f - MaxSizeLo) / (MaxSizeHi - MaxSizeLo);
            _maxSize.Seeked += delegate (float v)
            {
                _cloud.MaxPointRadius = MaxSizeLo + v * (MaxSizeHi - MaxSizeLo);
                SaveChannels(); Invalidate();
            };
            Controls.Add(_maxSize);

            _dgrad.Label = "Palette";
            _dgrad.SetItems(Palette.GradientTitles(), 0);
            _dgrad.SelectedChanged += delegate
            {
                _cloud.ColorGradient = GradientOf(_dgrad);
                SaveChannels(); Invalidate();
            };
            Controls.Add(_dgrad);

            string[] titles = Metrics.Titles();
            DropField[] fields = Fields();
            ChannelSwitch[] switches = Switches();
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].Label = ChannelLabels[i];
                fields[i].SetItems(titles, 0);
                fields[i].SelectedChanged += delegate { ApplyChannels(); SaveChannels(); };
                Controls.Add(fields[i]);

                switches[i].CheckedChanged += delegate { ApplyChannels(); SaveChannels(); };
                Controls.Add(switches[i]);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Glass.Apply(this);

            // The catalog has already been built by Alive — we read its cache and show the
            // cloud at once. There is no scan of our own at startup: it would achieve nothing
            // here and costs seconds.
            bool loaded = false;
            try { loaded = _index.LoadFromCache(); }
            catch { }

            if (loaded) { Apply(); StartWeighing(); }
            else if (_settings.Roots.Count > 0) StartScan();
            else _hint = "No folders yet — press the folder button and point at your Ableton projects.";

            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Glass.Apply(this);
        }

        // -------------------------------------------------------------------- channels

        static readonly string[] DefaultChannels =
            { "tracks", "plugins", "bpm", "setsize", "created", "live" };

        static string ConfigPath { get { return Path.Combine(Settings.Dir, "nebula.cfg"); } }

        void LoadChannels()
        {
            string[] ids = (string[])DefaultChannels.Clone();
            bool[] on = { true, true, true, true, true, true };
            bool spin = false;
            float blur = 0.05f;
            float minFade = 0.08f, maxFade = 1f;
            float minSize = 1f, maxSize = 16f;
            string gradId = Palette.Gradients[0].Id;
            try
            {
                if (File.Exists(ConfigPath))
                    foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq < 0) continue;
                        string key = raw.Substring(0, eq).Trim(), val = raw.Substring(eq + 1).Trim();
                        for (int i = 0; i < ChannelKeys.Length; i++)
                        {
                            if (key == ChannelKeys[i]) ids[i] = val;
                            else if (key == ChannelKeys[i] + "_on") on[i] = val != "0";
                        }
                        if (key == "spin") spin = val == "1";
                        else if (key == "blur") float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out blur);
                        else if (key == "minfade") float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out minFade);
                        else if (key == "maxfade") float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out maxFade);
                        else if (key == "minsize") float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out minSize);
                        else if (key == "maxsize") float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out maxSize);
                        else if (key == "gradient") gradId = val;
                    }
            }
            catch { }

            DropField[] fields = Fields();
            ChannelSwitch[] switches = Switches();
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].SelectedIndex = Metrics.IndexOf(Metrics.ById(ids[i]));
                switches[i].Checked = on[i];
            }

            _spin.Checked = spin;
            _cloud.Spin = spin;

            _cloud.Softness = 0.04f;

            _minFade.Progress = Clamp(minFade, 0f, 1f);
            _cloud.MinAlpha = minFade;
            _maxFade.Progress = Clamp(maxFade, 0f, 1f);
            _cloud.MaxAlpha = maxFade;

            _minSize.Progress = (Clamp(minSize, MinSizeLo, MinSizeHi) - MinSizeLo) / (MinSizeHi - MinSizeLo);
            _cloud.MinPointRadius = minSize;
            _maxSize.Progress = (Clamp(maxSize, MaxSizeLo, MaxSizeHi) - MaxSizeLo) / (MaxSizeHi - MaxSizeLo);
            _cloud.MaxPointRadius = maxSize;

            Gradient grad = Palette.GradientById(gradId);
            _dgrad.SelectedIndex = Palette.GradientIndexOf(grad);
            _cloud.ColorGradient = grad;

            ApplyChannels();
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static readonly string[] ChannelKeys = { "x", "y", "z", "size", "fade", "colour" };

        void SaveChannels()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                DropField[] fields = Fields();
                ChannelSwitch[] switches = Switches();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Nebula — what each channel shows, and whether it's on");
                for (int i = 0; i < fields.Length; i++)
                {
                    sb.Append(ChannelKeys[i]).Append('=').AppendLine(MetricOf(fields[i]).Id);
                    sb.Append(ChannelKeys[i]).Append("_on=").AppendLine(switches[i].Checked ? "1" : "0");
                }
                sb.Append("spin=").AppendLine(_spin.Checked ? "1" : "0");
                sb.Append("blur=").AppendLine(_cloud.Softness.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append("minfade=").AppendLine(_cloud.MinAlpha.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append("maxfade=").AppendLine(_cloud.MaxAlpha.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append("minsize=").AppendLine(_cloud.MinPointRadius.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append("maxsize=").AppendLine(_cloud.MaxPointRadius.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append("gradient=").AppendLine(GradientOf(_dgrad).Id);
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        static Gradient GradientOf(DropField f)
        {
            int i = f.SelectedIndex;
            return i >= 0 && i < Palette.Gradients.Length ? Palette.Gradients[i] : Palette.Gradients[0];
        }

        static Metric MetricOf(DropField f)
        {
            int i = f.SelectedIndex;
            return i >= 0 && i < Metrics.All.Count ? Metrics.All[i] : Metrics.All[0];
        }

        void ApplyChannels()
        {
            DropField[] fields = Fields();
            ChannelSwitch[] switches = Switches();
            _cloud.SetChannels(MetricOf(fields[0]), switches[0].Checked,
                               MetricOf(fields[1]), switches[1].Checked,
                               MetricOf(fields[2]), switches[2].Checked,
                               MetricOf(fields[3]), switches[3].Checked,
                               MetricOf(fields[4]), switches[4].Checked,
                               MetricOf(fields[5]), switches[5].Checked);

            // The palette only colours continuous values — on Key/Scale/Collection it decides
            // nothing (those have their own per-class colouring), and offering the choice then
            // would only confuse.
            _dgrad.Visible = switches[5].Checked && MetricOf(fields[5]).Color == ColorMode.Ramp;

            Invalidate();
        }

        // ---------------------------------------------------------------------- data

        void Apply()
        {
            List<SetEntry> visible = new List<SetEntry>();
            foreach (SetEntry s in _index.Sets)
            {
                if (s.Error.Length > 0) continue;
                if (s.IsBackup) continue;
                visible.Add(s);
            }
            _cloud.SetData(visible);
            _hint = visible.Count == 0 && !_scanning
                  ? "Nothing found. Add a folder with .als projects."
                  : "";
            if (!string.IsNullOrEmpty(InitialPath))
            {
                foreach (SetEntry s in visible)
                {
                    if (string.Equals(s.Path, InitialPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _cloud.Select(s);
                        break;
                    }
                }
                InitialPath = null;
            }
            _detail.Show(_cloud.Selected);
            Invalidate();
        }

        /// <summary>
        /// The same dialog as in Alive — with the Windows file browser (IFileOpenDialog) and a
        /// per-folder set count. Our own FolderBrowserDialog did not do well here: it is not
        /// modal to the acrylic layer (SetWindowCompositionAttribute) and surfaced behind the
        /// main window rather than in front of it, which from outside looked like "the button
        /// does not work". GlassDialog, which RootsDialog is built on, already gets along with
        /// that layer.
        /// </summary>
        bool EditRoots()
        {
            using (RootsDialog d = new RootsDialog(_settings.Roots, _settings.DisabledRoots))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                _settings.Roots.Clear();
                _settings.Roots.AddRange(d.Result);
                _settings.DisabledRoots.Clear();
                _settings.DisabledRoots.AddRange(d.DisabledRoots);
                _settings.Save();
                Settings.NotifyRootsChanged(this);
                StartScan();
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
            StartScan();
        }

        void StartScan()
        {
            if (_scanning)
            {
                _rescanPending = true;
                if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
                return;
            }
            if (_settings.Roots.Count == 0) { EditRoots(); return; }

            _scanning = true;
            _scanDone = _scanTotal = 0;
            _hint = "";
            Invalidate();

            _cancel = new CancellationTokenSource();
            CancellationToken token = _cancel.Token;

            Thread t = new Thread(delegate ()
            {
                try
                {
                    _index.Scan(_settings, delegate (int done, int total, string current)
                    {
                        _scanDone = done; _scanTotal = total;
                        try { BeginInvoke((MethodInvoker)delegate { Invalidate(); }); }
                        catch { }
                    }, token);
                }
                catch { }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _scanning = false;
                        Apply();
                        if (_rescanPending)
                        {
                            _rescanPending = false;
                            StartScan();
                        }
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// The size of project folders. It is deliberately absent from Alive's cache (record a
        /// sample and the folder grows heavier while the .als has not changed), so we count the
        /// size channel ourselves and in the background: the cloud is already visible, and the
        /// dots simply swell when the numbers arrive.
        /// </summary>
        void StartWeighing()
        {
            if (_weighing) return;
            List<SetEntry> sets = new List<SetEntry>(_index.Sets);
            bool need = false;
            foreach (SetEntry s in sets) if (s.ProjectSize == 0) { need = true; break; }
            if (!need) return;

            _weighing = true;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    Dictionary<string, FolderScan.Weight> done =
                        new Dictionary<string, FolderScan.Weight>(StringComparer.OrdinalIgnoreCase);
                    foreach (SetEntry s in sets)
                    {
                        string dir = s.ProjectDir;
                        if (dir.Length == 0) continue;
                        FolderScan.Weight w;
                        if (!done.TryGetValue(dir, out w))
                        {
                            w = FolderScan.Weigh(dir, null);
                            done[dir] = w;
                        }
                        s.ProjectSize = w.Bytes;
                        s.ProjectFiles = w.Files;
                    }
                }
                catch { }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _weighing = false;
                        _cloud.Rebuild(true);
                        if (_cloud.Selected != null) _detail.Show(_cloud.Selected);
                        Invalidate();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        void OpenInLive()
        {
            SetEntry s = _cloud.Selected;
            if (s == null || !File.Exists(s.Path)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s.Path) { UseShellExecute = true });
            }
            catch { }
        }

        void RevealSelected()
        {
            SetEntry s = _cloud.Selected;
            if (s == null) return;
            try
            {
                if (File.Exists(s.Path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + s.Path + "\"");
                else if (Directory.Exists(s.ProjectDir)) System.Diagnostics.Process.Start("explorer.exe", "\"" + s.ProjectDir + "\"");
            }
            catch { }
        }

        void OpenPreview()
        {
            SetEntry s = _cloud.Selected;
            if (s == null || !File.Exists(s.Path)) return;
            using (PreviewDialog d = new PreviewDialog(s, _arrangements))
                d.ShowDialog(this);
        }

        void OnShowInList()
        {
            string path = SelectedPath;
            if (ShowInListRequested != null)
            {
                ShowInListRequested(path);
            }
            else
            {
                MainForm mf = Owner as MainForm;
                if (mf != null && !string.IsNullOrEmpty(path))
                    mf.SelectSetByPath(path);
            }
        }

        void EditNotes(SetEntry s)
        {
            if (s == null) return;
            using (NotesDialog d = new NotesDialog(s))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _detail.Show(s);
            }
        }

        void OnSetRequested(SetEntry s)
        {
            if (s == null) return;
            _cloud.Select(s);
            _detail.Show(s);
        }

        void OnPluginRequested(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName)) return;
            foreach (PluginStat p in _index.PluginUsage())
            {
                if (string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                {
                    _detail.ShowPlugin(p);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------- layout

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
            Glass.SetCornerRounding(this, WindowState != FormWindowState.Maximized);
            _max.Icon = WindowState == FormWindowState.Maximized ? Glyph.CloseFullscreen : Glyph.Maximize;
            Invalidate();
        }

        void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        void LayoutAll()
        {
            int pad = Sc(Theme.Pad);
            int h = Sc(Theme.ControlH);
            int icon = Sc(Theme.IconSize);
            int step = icon + Sc(Theme.IconGap);
            int right = ClientSize.Width - pad;
            int y = pad;

            _close.SetBounds(right - icon, y, icon, icon);
            _max.SetBounds(_close.Left - step, y, icon, icon);
            _min.SetBounds(_max.Left - step, y, icon, icon);

            int detailW = Sc(Theme.PanelW);
            int detailX = right - detailW;

            _folders.SetBounds(detailX, y, icon, icon);

            int x = pad;
            _spin.SetBounds(x, y, _spin.Width, h); x += _spin.Width + Sc(10);
            _reset.SetBounds(x, y, _reset.Width, h); x += _reset.Width + Sc(14);
            _camera.SetBounds(x, y, Sc(150), h);

            int panelW = Sc(300);
            int panelX = pad;

            int top = Sc(Theme.ContentY);
            int panelBottom = ClientSize.Height - pad;
            _detail.SetBounds(detailX, top, detailW, Math.Max(Sc(120), panelBottom - top));

            int gap = Sc(16);
            int cloudX = panelX + panelW + gap;
            int cloudRight = detailX - gap;
            int cloudW = Math.Max(Sc(200), cloudRight - cloudX);

            int bottom = ClientSize.Height - pad - Sc(24);
            int cloudH = Math.Max(Sc(200), bottom - top);
            _cloud.SetBounds(cloudX, top, cloudW, cloudH);

            _rStatus = new Rectangle(cloudX, bottom + Sc(4), cloudW, Sc(20));
            _rPanel = new Rectangle(panelX, top, panelW, Math.Max(0, panelBottom - top));

            DropField[] fields = Fields();
            ChannelSwitch[] switches = Switches();
            int swW = Sc(20), swGap = Sc(8);
            int fieldW = panelW - swW - swGap;
            int fy = top + Sc(30);
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].SetBounds(panelX, fy, fieldW, h);
                switches[i].SetBounds(panelX + fieldW + swGap, fy + (h - swW) / 2, swW, swW);
                fy += h + Sc(8);
            }

            // The palette sits directly under the channels, next to Colour: its own row, but
            // visible only when there is something for it to colour (see ApplyChannels). Room
            // for it is always reserved so that switching it on and off does not shift
            // everything below.
            fy += Sc(6);
            _dgrad.SetBounds(panelX, fy, panelW, h);
            fy += h + Sc(18);

            // Four point sliders — the caption is drawn above each of them in PaintPanel, so
            // the line above the slider itself counts towards the step too.
            int sliderH = Sc(18), labelH = Sc(18), sliderStep = labelH + sliderH + Sc(10);
            _minFade.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _maxFade.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _minSize.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _maxSize.SetBounds(panelX, fy + labelH, panelW, sliderH);
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, Theme.Backdrop);
            Theme.Smooth(g);

            Chrome.DrawText(g, StatusText(), Theme.FLabel, _rStatus, Theme.TextDim, Chrome.Left);

            if (_scanning && _scanTotal > 0)
            {
                int pad = Sc(Theme.Pad);
                int barY = Sc(Theme.ContentY) - Sc(12);
                int barW = ClientSize.Width - pad * 2, barH = Sc(3);
                Theme.FillRound(g, new Rectangle(pad, barY, barW, barH), barH / 2f,
                                Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                float pct = Math.Max(0.01f, Math.Min(1f, (float)_scanDone / _scanTotal));
                Theme.FillRound(g, new Rectangle(pad, barY, (int)(barW * pct), barH), barH / 2f,
                                Color.FromArgb(0xFF, 0x00, 0x7A, 0xCC));
            }

            PaintPanel(g);

            if (_hint.Length > 0)
                Chrome.DrawText(g, _hint, Theme.FBody,
                    new Rectangle(_cloud.Left, _cloud.Top + _cloud.Height / 2 - Sc(12), _cloud.Width, Sc(24)),
                    Theme.TextDim, Chrome.Center);
        }

        string StatusText()
        {
            if (_scanning)
                return _scanTotal > 0
                     ? "Scanning " + _scanDone + " / " + _scanTotal
                     : "Looking for sets… " + _scanDone;

            string s = _cloud.Sets.Count + " projects";
            if (_weighing) s += "  ·  weighing project folders…";
            s += "   ·   drag — rotate   ·   wheel — zoom   ·   right drag — pan   ·   double click — open folder";
            return s;
        }

        void PaintPanel(Graphics g)
        {
            int x = _rPanel.X, w = _rPanel.Width;

            Chrome.DrawText(g, "MAPPING", Theme.FBadge,
                            new Rectangle(x + Sc(4), _rPanel.Y, w, Sc(18)), Theme.TextDim, Chrome.Left);

            if (_dgrad.Visible)
                SliderLabel(g, x, w, _dgrad.Top - Sc(20), "PALETTE");

            SliderLabel(g, x, w, _minFade.Top,
                        "MIN FADE  ·  " + Math.Round(_cloud.MinAlpha * 100f) + "%");
            SliderLabel(g, x, w, _maxFade.Top,
                        "MAX FADE  ·  " + Math.Round(_cloud.MaxAlpha * 100f) + "%");

            SliderLabel(g, x, w, _minSize.Top,
                        "MIN SIZE  ·  " + _cloud.MinPointRadius.ToString("0.#", CultureInfo.InvariantCulture) + "px");
            SliderLabel(g, x, w, _maxSize.Top,
                        "MAX SIZE  ·  " + _cloud.MaxPointRadius.ToString("0.#", CultureInfo.InvariantCulture) + "px");

            int y = _maxSize.Bottom + Sc(22);

            // The colour legend is the one channel that does not read without a caption: size
            // and density speak for themselves, while colour is a code.
            bool colorOn = _ecolor.Checked;
            Chrome.DrawText(g, "COLOUR · " + (colorOn ? MetricOf(_dcolor).Title.ToUpperInvariant() : "OFF"),
                            Theme.FBadge, new Rectangle(x + Sc(4), y, w, Sc(18)), Theme.TextDim, Chrome.Left);
            y += Sc(22);
            if (colorOn) y = PaintLegend(g, x, y, w);
        }

        /// <summary>A small caption strictly above a control — used over the sliders and the
        /// palette, which, unlike DropField, have nowhere to write their own name.</summary>
        void SliderLabel(Graphics g, int x, int w, int aboveY, string text)
        {
            Chrome.DrawText(g, text, Theme.FBadge, new Rectangle(x + Sc(4), aboveY - Sc(20), w, Sc(18)),
                            Theme.TextDim, Chrome.Left);
        }

        int PaintLegend(Graphics g, int x, int y, int w)
        {
            Metric m = MetricOf(_dcolor);

            if (m.Color == ColorMode.Ramp)
            {
                Gradient grad = GradientOf(_dgrad);
                Rectangle bar = new Rectangle(x + Sc(4), y, w - Sc(8), Sc(10));
                // The strip is 32 samples of the same Palette.Sample the dots themselves are
                // coloured with (the same path through LAB), rather than a separate,
                // approximately similar gradient built with GDI+: the legend would then
                // sometimes disagree with what is actually visible in the cloud.
                const int steps = 32;
                using (LinearGradientBrush br = new LinearGradientBrush(
                           new Rectangle(bar.X, bar.Y, bar.Width, bar.Height + 1),
                           Color.Black, Color.White, LinearGradientMode.Horizontal))
                {
                    ColorBlend blend = new ColorBlend(steps);
                    Color[] colors = new Color[steps];
                    float[] pos = new float[steps];
                    for (int i = 0; i < steps; i++)
                    {
                        pos[i] = i / (float)(steps - 1);
                        colors[i] = Palette.Sample(grad, i / (double)(steps - 1));
                    }
                    blend.Colors = colors; blend.Positions = pos;
                    br.InterpolationColors = blend;
                    using (GraphicsPath p = Theme.Round(bar, bar.Height / 2f))
                        g.FillPath(br, p);
                }
                y += Sc(14);
                Chrome.DrawText(g, _cloud.ColorCh.LoText, Theme.FBadge,
                                new Rectangle(x + Sc(4), y, w / 2, Sc(16)), Theme.TextDim, Chrome.Left);
                Chrome.DrawText(g, _cloud.ColorCh.HiText, Theme.FBadge,
                                new Rectangle(x + w / 2 - Sc(4), y, w / 2, Sc(16)), Theme.TextDim, Chrome.Right);
                return y + Sc(18);
            }

            // Classes: chips in two columns. We show the most populated ones — the rest only
            // clutter the legend.
            List<string> names = new List<string>();
            List<Color> colors2 = new List<Color>();
            List<int> counts = new List<int>();
            Dictionary<int, int> seen = new Dictionary<int, int>();

            foreach (SetEntry s in _cloud.Sets)
            {
                double v = m.Value(s);
                if (double.IsNaN(v)) continue;
                int cls = (int)v;
                int at;
                if (seen.TryGetValue(cls, out at)) { counts[at]++; continue; }
                seen[cls] = names.Count;
                names.Add(m.Text(s));
                colors2.Add(m.Color == ColorMode.Key ? Palette.Key(s.ScaleRoot, s.ScaleIndex) : Palette.Class(cls));
                counts.Add(1);
            }

            int[] order = new int[names.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate (int a, int b) { return counts[b].CompareTo(counts[a]); });

            int rows = Math.Min(order.Length, 10);
            int colW = w / 2;
            for (int i = 0; i < rows; i++)
            {
                int idx = order[i];
                int cxp = x + (i % 2) * colW + Sc(4);
                int cyp = y + (i / 2) * Sc(20);

                Rectangle dot = new Rectangle(cxp, cyp + Sc(4), Sc(8), Sc(8));
                Theme.FillRound(g, dot, dot.Height / 2f, colors2[idx]);
                Chrome.DrawText(g, names[idx], Theme.FBadge,
                                new Rectangle(cxp + Sc(14), cyp, colW - Sc(22), Sc(16)),
                                Theme.TextDim, Chrome.Left);
            }
            return y + ((rows + 1) / 2) * Sc(20) + Sc(6);
        }

        // ------------------------------------------------------- window and keys

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x0084 || (int)m.Result != 1) return;

            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            int b = Sc(6);
            bool l = p.X <= b, r = p.X >= ClientSize.Width - b;
            bool t = p.Y <= b, d = p.Y >= ClientSize.Height - b;

            if (t && l) m.Result = (IntPtr)13;
            else if (t && r) m.Result = (IntPtr)14;
            else if (d && l) m.Result = (IntPtr)16;
            else if (d && r) m.Result = (IntPtr)17;
            else if (l) m.Result = (IntPtr)10;
            else if (r) m.Result = (IntPtr)11;
            else if (t) m.Result = (IntPtr)12;
            else if (d) m.Result = (IntPtr)15;
            else if (p.Y < Sc(Theme.ContentY) - Sc(10)) m.Result = (IntPtr)2;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Sc(Theme.ContentY) - Sc(10)) ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Ctrl+Q closes the whole program from here as well: Nebula is a separate window,
            // the main one's keys do not reach it, and there was no way to quit from it.
            if (e.Control && e.KeyCode == Keys.Q)
            {
                e.Handled = e.SuppressKeyPress = true;
                Close();
                Application.Exit();
            }
            else if (e.KeyCode == Keys.Space) { _spin.Checked = !_spin.Checked; e.Handled = true; }
            else if (e.KeyCode == Keys.R) { _cloud.ResetView(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { StartScan(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { OpenInLive(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { _cloud.Select(null); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Settings.RootsChanged -= OnGlobalRootsChanged;
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
