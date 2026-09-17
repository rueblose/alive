using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Collecting a project into a portable folder. One window, two states — choosing and
    /// copying — by the same trick as RescueDialog and for the same reason: this is one act
    /// rather than two separate affairs, and a second window over the first would only get in
    /// the way.
    ///
    /// The items are exactly the four "Collect All and Save" asks in Live. Over and above that,
    /// the file count and the weight are shown: 5.8 GB of packs has to be seen BEFORE OK is
    /// pressed, not after.
    /// </summary>
    public sealed class CollectDialog : GlassDialog
    {
        sealed class Row
        {
            public string Label = "";
            public SampleOrigin Origin;
            public PillToggle Toggle;
            public int Files;
            public long Bytes;
            public Rectangle Rect;
        }

        readonly SetEntry _set;
        readonly LiveEnvironment _env;
        readonly Settings _settings;
        readonly List<Row> _rows = new List<Row>();
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly PillToggle _zip = new PillToggle();

        const string ZipLabel = "Add to ZIP";

        /// <summary>The widest total there can be — the shelf is measured by it.</summary>
        const string WidestSummary = "Will copy 9999 files, 999.9 GB";

        // The width is a lower bound rather than a size: the shelf at the bottom may ask for
        // more, see OnHandleCreated. The height covers four rows, the totals and the shelf; the
        // "not found" line is not always there, and room for it is allowed.
        const int DialogW = 720;
        const int DialogH = 350;
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();

        AlsInfo _info;
        List<SampleDep> _deps;
        CollectPlan _plan;
        CollectOptions _opt = new CollectOptions();

        int _inProjectFiles; long _inProjectBytes;
        int _notFound;

        bool _counting = true;
        bool _running;
        string _error = "";

        // A field of its own rather than reusing _error: that one is about reading the set (the
        // .als would not parse), this one about a failure of the copying itself. Different
        // causes, different text.
        string _collectError = "";

        // Progress is written by the worker thread and read by the window's timer. Plain
        // fields: an int and a long are read and written atomically, and accuracy down to a
        // single file matters to nobody here — this is a bar, not a report.
        volatile int _done, _total;
        volatile string _current = "";
        CancellationTokenSource _cts;

        /// <summary>The folder of the collected project. Empty if it was cancelled or never got
        /// that far.</summary>
        public string Produced = "";

        /// <summary>How many files could not be copied — busy, or the path too long.</summary>
        public int Failed;

        public CollectDialog(SetEntry set, LiveEnvironment env, Settings settings)
        {
            _set = set;
            _env = env;
            _settings = settings;

            Caption = "Export: " + set.Name;
            ClientSize = new Size(Sc(DialogW), Sc(DialogH));

            // The same instance MainForm loads and saves, not a fresh Settings.Load(): a copy
            // of our own would not see changes from other windows and, worse, would be
            // overwritten by the next Save() from elsewhere out of a stale snapshot on disk.
            _opt.FromElsewhere = _settings.CollectElsewhere;
            _opt.FromOtherProjects = _settings.CollectOtherProjects;
            _opt.FromUserLibrary = _settings.CollectUserLibrary;
            _opt.FromFactoryPacks = _settings.CollectFactoryPacks;

            AddRow("Files from elsewhere", SampleOrigin.Elsewhere, _opt.FromElsewhere);
            AddRow("Files from other Projects", SampleOrigin.OtherProject, _opt.FromOtherProjects);
            AddRow("Files from User Library", SampleOrigin.UserLibrary, _opt.FromUserLibrary);
            AddRow("Files from Factory Packs", SampleOrigin.FactoryPack, _opt.FromFactoryPacks);

            _ok.Text = "Export";
            _ok.Primary = true;
            _ok.FitToText(20);
            _ok.Enabled = false;
            _ok.Click += delegate { Start(); };
            Controls.Add(_ok);

            _cancel.Text = "Cancel";
            _cancel.FitToText(20);
            _cancel.Click += delegate { OnCancel(); };
            Controls.Add(_cancel);

            // The same toggle as the rows above, and on the same shelf as the buttons: an
            // archive is about how the collecting ends, not another kind of file for it.
            _zip.IsSwitch = true;
            _zip.Size = new Size(Sc(42), Sc(24));
            _zip.Checked = _settings.CollectToZip;
            _zip.Enabled = false;
            _zip.CheckedChanged += delegate { Recount(); };
            Controls.Add(_zip);

            _tick.Interval = 100;
            _tick.Tick += delegate { Invalidate(); };
            _tick.Start();
        }

        /// <summary>
        /// The window width is not set by Sc() alone. Sc() counts from DeviceDpi while GDI
        /// draws text at the font's DPI, and those are different numbers: on a system at 125%
        /// the window came out 96-point while the letters in it were 120-point. The bottom
        /// shelf is the one line where everything stands flush, and it stopped fitting into its
        /// own window: the total on the left was cut off with an ellipsis. So we measure the
        /// shelf with real text and, if it is tight, give the window exactly as much as it asks
        /// for.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _ok.FitToText(20);
            _cancel.FitToText(20);

            int need = Sc(24) * 2
                     + TextRenderer.MeasureText(WidestSummary, Theme.FLabel).Width
                     + Sc(16) + _zip.Width + Sc(14)
                     + TextRenderer.MeasureText(ZipLabel, Theme.FBody).Width
                     + Sc(24) + _cancel.Width + Sc(10) + _ok.Width;

            // Only when it is tight: assigning ClientSize to an already-created window goes
            // through a frame recalculation and adds extra to the height, while at ordinary
            // scale there is nothing to change — the size from the constructor is right as it
            // is.
            if (need > ClientSize.Width) ClientSize = new Size(need, ClientSize.Height);
        }

        bool _started;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // We only start counting once the window is shown. BeginInvoke before the handle
            // exists throws InvalidOperationException, and the count can well finish before
            // ShowDialog gets round to showing the window.
            if (_started) return;
            _started = true;
            ThreadPool.QueueUserWorkItem(delegate { CountInBackground(); });
        }

        void AddRow(string label, SampleOrigin origin, bool on)
        {
            Row r = new Row();
            r.Label = label;
            r.Origin = origin;
            r.Toggle = new PillToggle();
            r.Toggle.IsSwitch = true;
            r.Toggle.Size = new Size(Sc(42), Sc(24));
            r.Toggle.Checked = on;
            r.Toggle.Enabled = false;           // nothing to touch until the count is done
            r.Toggle.CheckedChanged += delegate { Recount(); };
            Controls.Add(r.Toggle);
            _rows.Add(r);
        }

        // ------------------------------------------------------------------ counting

        /// <summary>
        /// Return to the window's thread from a background one. The window can be closed while
        /// background work is running: the handle is then gone and BeginInvoke throws — we
        /// catch it here, in one place, rather than by an IsDisposed check in every handler
        /// (which is a race anyway).
        /// </summary>
        void Post(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception) { }
        }

        void CountInBackground()
        {
            AlsInfo info = AlsFile.Read(_set.Path);
            List<SampleDep> deps = info.Error == null
                ? SampleScan.Of(info, Path.GetDirectoryName(_set.Path), _env)
                : new List<SampleDep>();

            Post(delegate
            {
                _info = info;
                _deps = deps;
                _counting = false;
                if (info.Error != null) { _error = info.Error; Invalidate(); return; }

                foreach (Row r in _rows)
                {
                    r.Files = 0; r.Bytes = 0;
                    r.Toggle.Enabled = true;
                }
                _zip.Enabled = true;
                _inProjectFiles = 0; _inProjectBytes = 0; _notFound = 0;

                foreach (SampleDep d in deps)
                {
                    if (d.Origin == SampleOrigin.Missing) { _notFound++; continue; }
                    if (d.Origin == SampleOrigin.InProject)
                    { _inProjectFiles++; _inProjectBytes += d.Size; continue; }
                    foreach (Row r in _rows)
                        if (r.Origin == d.Origin) { r.Files++; r.Bytes += d.Size; break; }
                }

                Recount();
                LayoutRows();
                Invalidate();
            });
        }

        void Recount()
        {
            if (_deps == null || _info == null) return;
            // By Origin rather than by index: the index is the order of AddRow in the
            // constructor, and a silent dependency on it breaks with the first reordering of
            // the rows.
            foreach (Row r in _rows)
            {
                switch (r.Origin)
                {
                    case SampleOrigin.Elsewhere: _opt.FromElsewhere = r.Toggle.Checked; break;
                    case SampleOrigin.OtherProject: _opt.FromOtherProjects = r.Toggle.Checked; break;
                    case SampleOrigin.UserLibrary: _opt.FromUserLibrary = r.Toggle.Checked; break;
                    case SampleOrigin.FactoryPack: _opt.FromFactoryPacks = r.Toggle.Checked; break;
                }
            }
            _opt.ToZip = _zip.Checked;

            _plan = CollectAll.Plan(_set, _deps, _opt);
            _ok.Enabled = !_running && Fits;
            Invalidate();
        }

        /// <summary>
        /// Whether there is room. In .zip mode twice as much is needed: the archive is written
        /// next to the folder, and until it is finished both are on disk. Compression is not
        /// counted on — samples do not compress.
        /// </summary>
        bool Fits
        {
            get
            {
                if (_plan == null) return false;
                long need = _plan.Zip ? _plan.TotalBytes * 2 : _plan.TotalBytes;
                return need < _plan.FreeBytes;
            }
        }

        // ------------------------------------------------------------------ collecting

        void Start()
        {
            if (_plan == null || _running) return;

            _settings.CollectElsewhere = _opt.FromElsewhere;
            _settings.CollectOtherProjects = _opt.FromOtherProjects;
            _settings.CollectUserLibrary = _opt.FromUserLibrary;
            _settings.CollectFactoryPacks = _opt.FromFactoryPacks;
            _settings.CollectToZip = _opt.ToZip;
            _settings.Save();

            _running = true;
            _ok.Enabled = false;
            foreach (Row r in _rows) r.Toggle.Enabled = false;
            _zip.Enabled = false;
            LayoutRows();            // resync Visible — otherwise the rows do not hide
            _total = _plan.Copy.Count;
            _done = 0;
            _cts = new CancellationTokenSource();

            CollectPlan plan = _plan;
            AlsInfo info = _info;
            CancellationToken token = _cts.Token;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "";
                bool done = false;
                try
                {
                    CollectAll.Run(plan, _set, info,
                        delegate (int n, int of, string what) { _done = n; _total = of; _current = what; },
                        token);
                    done = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // In the window this line sits on the bottom shelf and a long message there
                    // gets clipped — it stays whole in the log.
                    error = ex.Message;
                    Diag.Line("export: " + ex);
                }

                Post(delegate
                {
                    _running = false;
                    if (done)
                    {
                        Produced = plan.Zip ? plan.ZipPath : plan.TargetDir;
                        Failed = plan.Failed.Count;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        _collectError = error;
                        if (error.Length == 0) Close();     // cancelled — we simply close
                        else
                        {
                            // A real copying failure (not a cancellation) — the window stays
                            // open and the selection screen has to come back in full: both the
                            // Enabled and the Visible of the rows, or Collect hits a wall.
                            // Enabled is decided by Recount() rather than a blunt "true": the
                            // disk space may have run out during the very attempt that failed,
                            // and a plan on stale numbers would enable Collect where it will
                            // not fit again.
                            foreach (Row r in _rows) r.Toggle.Enabled = true;
                            _zip.Enabled = true;
                            Recount();
                            LayoutRows();
                            Invalidate();
                        }
                    }
                });
            });

            Invalidate();
        }

        void OnCancel()
        {
            if (_running && _cts != null) { _cts.Cancel(); return; }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------ layout

        // Right under the window header: that ends at +59 (title and cross), and the list
        // follows.
        int RowTop { get { return Card.Top + Sc(72); } }
        int RowStep { get { return Sc(34); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutRows();
        }

        void LayoutRows()
        {
            int h = Sc(Theme.ControlH);
            int pad = Sc(24);
            int by = Card.Bottom - pad - h;

            _ok.SetBounds(Card.Right - pad - _ok.Width, by, _ok.Width, h);
            _cancel.SetBounds(_ok.Left - Sc(10) - _cancel.Width, by, _cancel.Width, h);

            // The toggle's caption is drawn by OnPaint — we measure its width here in order to
            // push the toggle itself over by exactly as much as the caption will take to its
            // right.
            int labelW = TextRenderer.MeasureText(ZipLabel, Theme.FBody).Width;
            _zip.Location = new Point(_cancel.Left - Sc(24) - labelW - Sc(14) - _zip.Width,
                                      by + (h - _zip.Height) / 2);
            _zip.Visible = !_running && !_counting;

            int y = RowTop;
            foreach (Row r in _rows)
            {
                r.Rect = new Rectangle(Card.Left + pad, y, Card.Width - pad * 2, RowStep);
                r.Toggle.Location = new Point(r.Rect.X, y + (RowStep - r.Toggle.Height) / 2);
                r.Toggle.Visible = !_running && !_counting;
                y += RowStep;
            }
        }

        // ------------------------------------------------------------------ painting

        static string Mb(long bytes)
        {
            if (bytes >= 1073741824L)
                return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            int pad = Sc(24);
            int left = Card.Left + pad, right = Card.Right - pad;

            // The bottom shelf is there in every state: Cancel sits on it both while counting
            // and while copying. A rule separates it from the list.
            int barTop = _ok.Top;
            int rule = barTop - Sc(14);
            using (Pen p = new Pen(Theme.Hairline)) g.DrawLine(p, left, rule, right, rule);

            Rectangle head = new Rectangle(left, RowTop, right - left, Sc(24));

            if (_error.Length > 0)
            {
                Chrome.DrawText(g, "Cannot read this set: " + _error, Theme.FBody, head, Theme.Red, Chrome.Left);
                return;
            }

            if (_counting)
            {
                Chrome.DrawText(g, "Counting…", Theme.FBody, head, Theme.TextDim, Chrome.Left);
                return;
            }

            if (_running)
            {
                // "Exporting" rather than "Copying": in .zip mode the copying is followed by a
                // second pass, the packing, and the counter runs the scale twice. What exactly
                // is going on is told by the line under the bar.
                int total = _total, done = _done;
                Chrome.DrawText(g, string.Format("Exporting {0} of {1}", done, total),
                                Theme.FBody, head, Theme.Text, Chrome.Left);

                RectangleF bar = new RectangleF(left, head.Bottom + Sc(16), right - left, Sc(6));
                Theme.FillRound(g, bar, bar.Height / 2f, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                if (total > 0 && done > 0)
                {
                    RectangleF fill = new RectangleF(bar.X, bar.Y, bar.Width * done / (float)total, bar.Height);
                    Theme.FillRound(g, fill, bar.Height / 2f, Theme.Text);
                }

                Rectangle now = new Rectangle(left, (int)bar.Bottom + Sc(10), right - left, Sc(20));
                Chrome.DrawText(g, _current ?? "", Theme.FLabel, now, Theme.TextDim, Chrome.Left);
                return;
            }

            foreach (Row r in _rows)
            {
                int textX = r.Toggle.Right + Sc(14);
                Rectangle label = new Rectangle(textX, r.Rect.Y, Sc(260), r.Rect.Height);
                Chrome.DrawText(g, r.Label, Theme.FBody, label, Theme.Text, Chrome.Left);

                Rectangle nums = new Rectangle(right - Sc(240), r.Rect.Y, Sc(240), r.Rect.Height);
                string s = r.Files == 0 ? "—"
                    : string.Format("{0}    {1}", Chrome.Plural(r.Files, "file"), Mb(r.Bytes));
                Chrome.DrawText(g, s, Theme.FLabel, nums,
                                r.Toggle.Checked ? Theme.Text : Theme.TextDim, Chrome.Right);
            }

            int y = RowTop + _rows.Count * RowStep + Sc(14);
            Extra(g, left, right, ref y, "In project (always copied)",
                  string.Format("{0}    {1}", Chrome.Plural(_inProjectFiles, "file"), Mb(_inProjectBytes)));
            if (_notFound > 0)
                Extra(g, left, right, ref y, "Not found — left as they are",
                      Chrome.Plural(_notFound, "file"));

            int barH = Sc(Theme.ControlH);
            Chrome.DrawText(g, ZipLabel, Theme.FBody,
                            new Rectangle(_zip.Right + Sc(14), barTop, _cancel.Left - Sc(24) - _zip.Right, barH),
                            Theme.Text, Chrome.Left);

            // The left edge of the shelf holds the total, and the same place serves for "will
            // not fit" and for a failure of the collecting: all of it answers one question,
            // whether Export can be pressed.
            string sum = "";
            Color color = Theme.Text;
            if (_collectError.Length > 0) { sum = "Could not export: " + _collectError; color = Theme.Red; }
            else if (_plan != null)
            {
                // It does not fit — the numbers give way to the reason: they are broken down by
                // row above, and what matters here is the one thing, why Export will not press.
                if (Fits)
                    sum = string.Format("Will copy {0}, {1}",
                                        Chrome.Plural(_plan.Copy.Count, "file"), Mb(_plan.TotalBytes));
                else { sum = "Not enough space"; color = Theme.Red; }
            }
            if (sum.Length > 0)
                Chrome.DrawText(g, sum, Theme.FLabel,
                                new Rectangle(left, barTop, _zip.Left - Sc(16) - left, barH), color, Chrome.Left);
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value)
        {
            Rectangle l = new Rectangle(left, y, right - left - Sc(240), Sc(22));
            Rectangle v = new Rectangle(right - Sc(240), y, Sc(240), Sc(22));
            Chrome.DrawText(g, label, Theme.FLabel, l, Theme.TextDim, Chrome.Left);
            Chrome.DrawText(g, value, Theme.FLabel, v, Theme.TextDim, Chrome.Right);
            y += Sc(22);
        }
    }
}
