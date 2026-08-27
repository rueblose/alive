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
    /// Окно Nebula. Оформление то же, что у Alive (стекло, пилюли, кегли из Theme), а
    /// содержимое одно — облако проектов и панель, в которой каждому из шести каналов
    /// назначается величина сета.
    /// </summary>
    public sealed class NebulaForm : Form
    {
        readonly Settings _settings;
        readonly ProjectIndex _index = new ProjectIndex();

        readonly CloudView _cloud = new CloudView();

        readonly IconButton _folders = new IconButton();
        readonly IconButton _rescan = new IconButton();
        readonly IconButton _min = new IconButton();
        readonly IconButton _max = new IconButton();
        readonly IconButton _close = new IconButton();

        readonly PillToggle _spin = new PillToggle();
        readonly PillToggle _backups = new PillToggle();
        readonly GlassButton _reset = new GlassButton();
        readonly CameraPresetBar _camera = new CameraPresetBar();

        readonly SeekSlider _blur = new SeekSlider();
        readonly SeekSlider _minFade = new SeekSlider();
        readonly SeekSlider _maxFade = new SeekSlider();
        readonly SeekSlider _minSize = new SeekSlider();
        readonly SeekSlider _maxSize = new SeekSlider();
        readonly DropField _dgrad = new DropField();

        // Диапазоны, в которые ползунки Size переводят 0..1 — логические px, до Sc().
        // У Fade диапазон и так 0..1 (альфа), никакого перевода не нужно.
        const float MinSizeLo = 0f, MinSizeHi = 12f;
        const float MaxSizeLo = 4f, MaxSizeHi = 48f;

        readonly DropField _dx = new DropField();
        readonly DropField _dy = new DropField();
        readonly DropField _dz = new DropField();
        readonly DropField _dsize = new DropField();
        readonly DropField _dalpha = new DropField();
        readonly DropField _dcolor = new DropField();

        // Чекбокс рядом с каждым из шести — выключить канал, не теряя выбор в списке.
        readonly ChannelSwitch _ex = new ChannelSwitch();
        readonly ChannelSwitch _ey = new ChannelSwitch();
        readonly ChannelSwitch _ez = new ChannelSwitch();
        readonly ChannelSwitch _esize = new ChannelSwitch();
        readonly ChannelSwitch _ealpha = new ChannelSwitch();
        readonly ChannelSwitch _ecolor = new ChannelSwitch();

        static readonly string[] ChannelLabels = { "X", "Y", "Z", "Size", "Fade", "Colour" };

        DropField[] Fields() { return new DropField[] { _dx, _dy, _dz, _dsize, _dalpha, _dcolor }; }
        ChannelSwitch[] Switches() { return new ChannelSwitch[] { _ex, _ey, _ez, _esize, _ealpha, _ecolor }; }

        Rectangle _rPanel, _rStatus, _rTitle;

        bool _scanning;
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
            MinimumSize = new Size(1040, 660);
            BackColor = Theme.Bg;
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _settings = Settings.Load();
            Build();
            LoadChannels();
        }

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        // -------------------------------------------------------------------- сборка

        void Build()
        {
            Controls.Add(_cloud);
            _cloud.HoverChanged += delegate { Invalidate(_rPanel); };
            _cloud.SelectionChanged += delegate { Invalidate(_rPanel); };
            _cloud.OpenRequested += delegate { OpenSelected(); };

            _min.Icon = Glyph.Minimize;
            _max.Icon = Glyph.Maximize;
            _close.Icon = Glyph.Close;
            _close.Danger = true;
            _folders.Icon = Glyph.Folder;
            _rescan.Icon = Glyph.Refresh;

            _min.Click += delegate { WindowState = FormWindowState.Minimized; };
            _max.Click += delegate { ToggleMaximize(); };
            _close.Click += delegate { Close(); };
            _folders.Click += delegate { EditRoots(); };
            _rescan.Click += delegate { StartScan(); };

            foreach (IconButton b in new IconButton[] { _folders, _rescan, _min, _max, _close })
                Controls.Add(b);

            _spin.Text = "Spin";
            _spin.Checked = false;
            _spin.FitToText();
            _spin.CheckedChanged += delegate { _cloud.Spin = _spin.Checked; SaveChannels(); };
            Controls.Add(_spin);

            _backups.Text = "Backups";
            _backups.FitToText();
            _backups.CheckedChanged += delegate { Apply(); };
            Controls.Add(_backups);

            _reset.Text = "Reset view";
            _reset.Width = Sc(120);
            _reset.Height = Sc(Theme.ControlH);
            _reset.Click += delegate { _cloud.ResetView(); };
            Controls.Add(_reset);

            // Ортографический пресет — это ровно вот такой вид, без вращения поверх;
            // спин выключаем, иначе кадр, который только что встал по оси, через секунду
            // снова косит.
            _camera.PresetClicked += delegate (CameraPreset p)
            {
                _spin.Checked = false;
                _cloud.SetPreset(p);
            };
            Controls.Add(_camera);

            _blur.Progress = 0.42f;
            _blur.Seeked += delegate (float v) { _cloud.Softness = v; SaveChannels(); Invalidate(); };
            Controls.Add(_blur);

            _minFade.Progress = 0.16f;
            _minFade.Seeked += delegate (float v) { _cloud.MinAlpha = v; SaveChannels(); Invalidate(); };
            Controls.Add(_minFade);

            _maxFade.Progress = 1f;
            _maxFade.Seeked += delegate (float v) { _cloud.MaxAlpha = v; SaveChannels(); Invalidate(); };
            Controls.Add(_maxFade);

            _minSize.Progress = (2f - MinSizeLo) / (MinSizeHi - MinSizeLo);
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

            // Каталог уже собран Alive — читаем его кеш и показываем облако сразу.
            // Своего сканирования при старте нет: оно тут ни к чему, а секунды стоит.
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

        // ------------------------------------------------------------------- каналы

        static readonly string[] DefaultChannels =
            { "tracks", "plugins", "bpm", "projsize", "created", "live" };

        static string ConfigPath { get { return Path.Combine(Settings.Dir, "nebula.cfg"); } }

        void LoadChannels()
        {
            string[] ids = (string[])DefaultChannels.Clone();
            bool[] on = { true, true, true, true, true, true };
            bool spin = false;
            float blur = 0.42f;
            float minFade = 0.16f, maxFade = 1f;
            float minSize = 2f, maxSize = 16f;
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

            _blur.Progress = blur;
            _cloud.Softness = blur;

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
                sb.Append("blur=").AppendLine(_blur.Progress.ToString("0.###", CultureInfo.InvariantCulture));
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

            // Палитра красит только непрерывные величины — на Key/Scale/Collection она
            // ничего не решает (там своя раскраска по классам), и предлагать её выбор
            // тогда только сбивало бы с толку.
            _dgrad.Visible = switches[5].Checked && MetricOf(fields[5]).Color == ColorMode.Ramp;

            Invalidate();
        }

        // -------------------------------------------------------------------- данные

        void Apply()
        {
            List<SetEntry> visible = new List<SetEntry>();
            foreach (SetEntry s in _index.Sets)
            {
                if (s.Error.Length > 0) continue;
                if (!_backups.Checked && s.IsBackup) continue;
                visible.Add(s);
            }
            _cloud.SetData(visible);
            _hint = visible.Count == 0 && !_scanning
                  ? "Nothing found. Add a folder with .als projects."
                  : "";
            Invalidate();
        }

        /// <summary>
        /// Тот же диалог, что и в Alive — с проводником Windows (IFileOpenDialog) и
        /// счётчиком сетов по папке. Собственный FolderBrowserDialog здесь себя не
        /// показывал: он не модальный к слою акрила (SetWindowCompositionAttribute),
        /// и всплывал за главным окном, а не перед ним — снаружи это выглядело как
        /// «кнопка не работает». GlassDialog, на котором стоит RootsDialog, с этим
        /// слоем уже дружит.
        /// </summary>
        bool EditRoots()
        {
            using (RootsDialog d = new RootsDialog(_settings.Roots, false, _settings.DisabledRoots))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                _settings.Roots.Clear();
                _settings.Roots.AddRange(d.Result);
                _settings.DisabledRoots.Clear();
                _settings.DisabledRoots.AddRange(d.DisabledRoots);
                _settings.Save();
                StartScan();
                return true;
            }
        }

        void StartScan()
        {
            if (_scanning) return;
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
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// Вес папок проектов. В кеше Alive его нет намеренно (записал сэмпл — папка
        /// потяжелела, а .als не изменился), поэтому канал размера считаем сами и в
        /// фоне: облако уже видно, точки просто раздуются, когда числа приедут.
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
                        Invalidate();
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        void OpenSelected()
        {
            SetEntry s = _cloud.Selected;
            if (s == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + s.Path + "\""); }
            catch { }
        }

        // ------------------------------------------------------------------ раскладка

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

            int panelW = Sc(300);
            int panelX = right - panelW;

            _folders.SetBounds(panelX, y, icon, icon);
            _rescan.SetBounds(panelX + step, y, icon, icon);

            Size ts = TextRenderer.MeasureText("Nebula", Theme.FHead);
            _rTitle = new Rectangle(pad, y, ts.Width + Sc(6), icon);

            int x = _rTitle.Right + Sc(20);
            _spin.SetBounds(x, y, _spin.Width, h); x += _spin.Width + Sc(10);
            _backups.SetBounds(x, y, _backups.Width, h); x += _backups.Width + Sc(10);
            _reset.SetBounds(x, y, _reset.Width, h); x += _reset.Width + Sc(14);
            _camera.SetBounds(x, y, Sc(150), h);

            int top = Sc(Theme.ContentY);
            int bottom = ClientSize.Height - pad - Sc(24);

            _cloud.SetBounds(pad, top, Math.Max(Sc(200), panelX - Sc(26) - pad), Math.Max(Sc(200), bottom - top));
            _rStatus = new Rectangle(pad, bottom + Sc(4), Math.Max(0, panelX - pad), Sc(20));
            _rPanel = new Rectangle(panelX, top, panelW, Math.Max(0, ClientSize.Height - pad - top));

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

            // Палитра — сразу под каналами, рядом с Colour: своя строка, но видна
            // только когда есть что ею красить (см. ApplyChannels), место под неё
            // резервируется всегда, чтобы включение/выключение не сдвигало всё, что ниже.
            fy += Sc(6);
            _dgrad.SetBounds(panelX, fy, panelW, h);
            fy += h + Sc(18);

            // Пять ползунков точек — подпись рисуется над каждым в PaintPanel, поэтому
            // строка выше самого ползунка тоже входит в шаг.
            int sliderH = Sc(18), labelH = Sc(18), sliderStep = labelH + sliderH + Sc(10);
            _blur.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _minFade.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _maxFade.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _minSize.SetBounds(panelX, fy + labelH, panelW, sliderH); fy += sliderStep;
            _maxSize.SetBounds(panelX, fy + labelH, panelW, sliderH);
        }

        // ------------------------------------------------------------------ отрисовка

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, Theme.Backdrop);
            Theme.Smooth(g);

            Chrome.DrawText(g, "Nebula", Theme.FHead, _rTitle, Theme.Text, Chrome.Left);

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

            string blurWord = _blur.Progress < 0.15f ? "crisp" : _blur.Progress > 0.75f ? "soft" : "";
            SliderLabel(g, x, w, _blur.Top, "BLUR" + (blurWord.Length > 0 ? "  ·  " + blurWord.ToUpperInvariant() : ""));

            SliderLabel(g, x, w, _minFade.Top,
                        "MIN FADE  ·  " + Math.Round(_cloud.MinAlpha * 100f) + "%");
            SliderLabel(g, x, w, _maxFade.Top,
                        "MAX FADE  ·  " + Math.Round(_cloud.MaxAlpha * 100f) + "%");

            SliderLabel(g, x, w, _minSize.Top,
                        "MIN SIZE  ·  " + _cloud.MinPointRadius.ToString("0.#", CultureInfo.InvariantCulture) + "px");
            SliderLabel(g, x, w, _maxSize.Top,
                        "MAX SIZE  ·  " + _cloud.MaxPointRadius.ToString("0.#", CultureInfo.InvariantCulture) + "px");

            int y = _maxSize.Bottom + Sc(22);

            // Легенда цвета — единственный канал, который без подписи не читается:
            // размер и плотность понятны сами по себе, а цвет это код.
            bool colorOn = _ecolor.Checked;
            Chrome.DrawText(g, "COLOUR · " + (colorOn ? MetricOf(_dcolor).Title.ToUpperInvariant() : "OFF"),
                            Theme.FBadge, new Rectangle(x + Sc(4), y, w, Sc(18)), Theme.TextDim, Chrome.Left);
            y += Sc(22);
            if (colorOn) y = PaintLegend(g, x, y, w);

            SetEntry s = _cloud.Hovered != null ? _cloud.Hovered : _cloud.Selected;
            if (s != null) PaintCard(g, x, y + Sc(18), w, s);
            else
                Chrome.DrawText(g, "Point at a dot to read it,\nclick to keep it,\ndouble click to open its folder.",
                                Theme.FSmall, new Rectangle(x + Sc(4), y + Sc(24), w - Sc(8), Sc(96)),
                                Theme.TextDim, Chrome.Wrap);
        }

        /// <summary>Мелкая подпись строго над контролом — используется поверх ползунков
        /// и палитры, которым, в отличие от DropField, некуда вписать своё имя самим.</summary>
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
                // Полоса — 32 сэмпла того же Palette.Sample, которым красятся сами точки
                // (тот же путь через LAB), а не отдельный, приблизительно похожий градиент
                // средствами GDI+: тогда легенда иногда расходилась бы с тем, что на самом
                // деле видно на облаке.
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

            // Классы: чипсы в две колонки. Показываем самые населённые — остальные
            // в легенде только мешают.
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

        void PaintCard(Graphics g, int x, int y, int w, SetEntry s)
        {
            int pad = Sc(14);
            int rowH = Sc(22);
            int h = Sc(58) + rowH * 7 + pad;
            if (y + h > ClientSize.Height - Sc(Theme.Pad)) h = ClientSize.Height - Sc(Theme.Pad) - y;
            if (h < Sc(80)) return;

            Rectangle card = new Rectangle(x, y, w, h);
            Theme.PaintGlassSurface(this, g, card, Sc(Theme.CardR), Theme.GlassSurfaceAlpha);

            int tx = x + pad, tw = w - pad * 2;
            int ty = y + pad;

            Chrome.DrawText(g, s.Name, Theme.FTitle, new Rectangle(tx, ty, tw, Sc(20)), Theme.Text, Chrome.Left);
            ty += Sc(20);
            Chrome.DrawText(g, s.Place, Theme.FBadge, new Rectangle(tx, ty, tw, Sc(16)), Theme.TextDim, Chrome.Left);
            ty += Sc(24);

            DropField[] fields = Fields();
            ChannelSwitch[] switches = Switches();
            for (int i = 0; i < fields.Length; i++)
            {
                if (ty + rowH > y + h) break;
                Metric m = MetricOf(fields[i]);
                bool on = switches[i].Checked;
                string value = on ? m.Text(s) : "off";

                // Ширины меряем, а не делим пополам: «Size · Project size» и «2026-08-07»
                // в одной строке иначе оба обрезаются многоточием ради пустого места
                // посередине. Если и так не влезает — от подписи остаётся имя канала:
                // какая под ним величина, видно в списке прямо над карточкой.
                int vw = Math.Min((int)(tw * 0.55f), TextRenderer.MeasureText(value, Theme.FSmall).Width + Sc(4));
                int lw = tw - vw - Sc(8);
                string label = ChannelLabels[i] + " · " + m.Title;
                if (TextRenderer.MeasureText(label, Theme.FBadge).Width > lw) label = ChannelLabels[i];

                Chrome.DrawText(g, label, Theme.FBadge,
                                new Rectangle(tx, ty, lw, rowH), Theme.TextDim, Chrome.Left);
                Chrome.DrawText(g, value, Theme.FSmall,
                                new Rectangle(tx + tw - vw, ty, vw, rowH), on ? Theme.Text : Theme.TextDim, Chrome.Right);
                ty += rowH;
            }

            if (ty + rowH <= y + h)
            {
                Chrome.DrawText(g, s.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                                Theme.FBadge, new Rectangle(tx, ty, tw, rowH), Theme.TextDim, Chrome.Left);
            }
        }

        // ------------------------------------------------------------ окно и клавиши

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
            if (e.Y < Sc(Theme.ContentY) - Sc(10)) ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { _spin.Checked = !_spin.Checked; e.Handled = true; }
            else if (e.KeyCode == Keys.R) { _cloud.ResetView(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { StartScan(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { _cloud.Select(null); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
