using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>The arrangement full screen: tracks, their colours and clips on a time
    /// ruler.</summary>
    public sealed class PreviewDialog : GlassDialog
    {
        readonly ArrangementView _view = new ArrangementView();
        readonly SetEntry _set;
        readonly ArrangementLoader _loader;
        Arrangement _arr;

        public PreviewDialog(SetEntry set, ArrangementLoader loader)
        {
            _set = set;
            _loader = loader;
            Caption = set.Name;

            Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(Sc(1360), screen.Width - Sc(80)),
                                  Math.Min(Sc(860), screen.Height - Sc(80)));

            _view.Options.ShowRuler = true;
            _view.Options.MaxLane = 40;
            // The track names belong here, at full size: the picture alone tells a track by its
            // colour only.
            _view.Options.ShowNames = true;
            Controls.Add(_view);

            _arr = loader.Cached(set.Path);
            _view.Set(_arr);
            if (_arr == null)
            {
                loader.Ready += OnReady;
                loader.Request(set.Path);
            }
        }

        void OnReady(Arrangement a)
        {
            if (IsDisposed || a == null || a.Path != _set.Path) return;
            _arr = a;
            _view.Set(a);
            Invalidate();
        }

        /// <summary>
        /// Ctrl+Space closes the preview with the same combination that opened it — the window
        /// is borderless and full screen, and toggling it with one key is nicer than opening
        /// from the keyboard and closing with the mouse. Esc still works — every GlassDialog
        /// has it.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Space)
            {
                e.Handled = e.SuppressKeyPress = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>The wheel goes to the control with the focus unless Windows sends it to the
        /// one under the cursor (a setting): the canvas takes the focus from the start.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _view.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _loader.Ready -= OnReady;
            base.OnFormClosed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Between the caption (ending at Sc(76)) and the card there used to be some Sc(2) —
            // practically touching, and at certain DPI the card's fill covered the bottom edge
            // of the caption text. Give it a real gap.
            int pad = Sc(22);
            int top = Sc(92);
            _view.SetBounds(pad, top, Math.Max(Sc(80), ClientSize.Width - pad * 2),
                            Math.Max(Sc(80), ClientSize.Height - top - pad));
            _view.Options.Dpi = Theme.Dpi;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Chrome.DrawText(g, Subtitle(), Theme.FSmall,
                new Rectangle(Card.Left + Sc(Theme.Pad), Card.Top + Sc(52), Card.Width - Sc(90), Sc(24)),
                Theme.TextDim, Chrome.Left | TextFormatFlags.NoClipping);
        }

        string Subtitle()
        {
            string s = _set.Directory;
            if (_arr == null) return s + "   ·   reading…";

            if (_arr.Error != null) return s + "   ·   " + _arr.Error;
            string tempo = _arr.Tempo > 0 ? _arr.Tempo.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " BPM" : "";
            string key = _set.Key.Length > 0 ? "   ·   " + _set.Key : "";
            string body = Chrome.Plural(_arr.Bars, "bar") + "   ·   " + Chrome.Plural(_arr.Tracks.Count, "track")
                        + "   ·   " + Chrome.Plural(_arr.ClipCount, "clip");
            return (tempo.Length > 0 ? tempo + key + "   ·   " : "") + body;
        }
    }

    /// <summary>
    /// The arrangement canvas of the full-screen preview, moved about as Live's arrangement is:
    /// the wheel scrolls the tracks, Shift+wheel the time, Ctrl+wheel zooms the time around the
    /// cursor, Alt+wheel makes the tracks taller or lower, and the middle button drags both ways
    /// at once. A track is never lower than its name: with many of them the tracks scroll
    /// rather than squeeze the names into one another. A few fill the height as before.
    /// </summary>
    public sealed class ArrangementView : GlassControl
    {
        public readonly RenderOptions Options = new RenderOptions();

        Arrangement _arr;

        // The view. Zero until the first layout, which fits the whole song across and the
        // tracks down; _fitted keeps the width the whole song through a resize until a zoom.
        double _pxPerBeat, _scrollX;
        int _laneH, _scrollY;
        bool _fitted = true;

        bool _panning;
        Point _panFrom;
        double _panX;
        int _panY;

        // The clips and the grid as last drawn, kept while the view stands still: a fading
        // scrollbar repaints dozens of times a second and must not draw tens of thousands of
        // notes each time.
        Bitmap _frame;
        string _frameKey;

        readonly ScrollFade _vBar, _hBar;

        public ArrangementView()
        {
            Cursor = Cursors.Default;
            Options.Dpi = Theme.Dpi;
            _vBar = new ScrollFade(this, VBarRect);
            _hBar = new ScrollFade(this, HBarRect);
        }

        public void Set(Arrangement a)
        {
            _arr = a;
            _pxPerBeat = _scrollX = 0;
            _laneH = _scrollY = 0;
            _fitted = true;
            DropFrame();
            Invalidate();
        }

        void DropFrame()
        {
            if (_frame != null) { _frame.Dispose(); _frame = null; }
        }

        // ---------------------------------------------------------------- the view

        Rectangle Inner
        {
            get { int pad = Sc(12); return new Rectangle(pad, pad, Math.Max(0, Width - pad * 2), Math.Max(0, Height - pad * 2)); }
        }

        Rectangle Plot { get { return ArrangementRender.Plot(Inner, Options); } }

        bool Ready { get { return _arr != null && _arr.Error == null && _arr.HasContent; } }

        double End { get { return Math.Max(_arr.End, 4); } }

        double FitPx { get { return Plot.Width / End; } }

        double MaxPx { get { return Sc(160); } }               // a beat at most this wide

        /// <summary>A name's line and a little air: lower than that, names ran into each
        /// other.</summary>
        int MinLane { get { return TextRenderer.MeasureText("Ag", Theme.FBadge).Height + Sc(6); } }

        int MaxLane { get { return Sc(160); } }

        double ContentW { get { return End * _pxPerBeat; } }

        int ContentH { get { return ArrangementRender.LanesHeight(_arr.Tracks.Count, _laneH, Options); } }

        void EnsureView()
        {
            if (!Ready) return;
            if (_pxPerBeat <= 0 || _fitted) _pxPerBeat = FitPx;
            if (_laneH <= 0)
            {
                // As tall as fits, as before — but never lower than a name.
                int n = _arr.Tracks.Count;
                int fit = (Plot.Height - Sc(2) * (n - 1)) / Math.Max(1, n);
                _laneH = Math.Max(MinLane, Math.Min(Sc(Options.MaxLane), fit));
            }
            Clamp();
        }

        void Clamp()
        {
            Rectangle plot = Plot;
            _scrollX = Math.Max(0, Math.Min(ContentW - plot.Width, _scrollX));
            _scrollY = Math.Max(0, Math.Min(ContentH - plot.Height, _scrollY));
        }

        /// <summary>
        /// One notch of the wheel, at a point of the view, with these keys held — apart from
        /// OnMouseWheel so the keys can be given rather than read off the keyboard.
        /// </summary>
        internal void Wheel(int delta, Point at, Keys mods)
        {
            if (!Ready) return;
            EnsureView();
            float notches = delta / 120f;
            Rectangle plot = Plot;
            if ((mods & Keys.Control) != 0)
            {
                // The time zooms around the cursor: the beat under it stays under it.
                int x = Math.Max(plot.X, Math.Min(plot.Right, at.X)) - plot.X;
                double beat = (x + _scrollX) / _pxPerBeat;
                double px = Math.Max(FitPx, Math.Min(MaxPx, _pxPerBeat * Math.Pow(1.25, notches)));
                _fitted = px <= FitPx;
                _pxPerBeat = px;
                _scrollX = beat * px - x;
                _hBar.Ping();
            }
            else if ((mods & Keys.Alt) != 0)
            {
                // Taller or lower tracks; the track under the cursor stays under it.
                int y = Math.Max(0, at.Y - plot.Y);
                double lanes = (y + _scrollY) / (double)(_laneH + ArrangementRender.Gap(_laneH, Options));
                _laneH = Math.Max(MinLane, Math.Min(MaxLane, _laneH + (int)Math.Round(notches * Sc(6))));
                _scrollY = (int)Math.Round(lanes * (_laneH + ArrangementRender.Gap(_laneH, Options))) - y;
                _vBar.Ping();
            }
            else if ((mods & Keys.Shift) != 0)
            {
                _scrollX -= notches * Sc(90);
                _hBar.Ping();
            }
            else
            {
                _scrollY -= (int)Math.Round(notches * Sc(60));
                _vBar.Ping();
            }
            Clamp();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Wheel(e.Delta, e.Location, ModifierKeys);
            base.OnMouseWheel(e);
        }

        const int WM_MOUSEHWHEEL = 0x020E, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONDBLCLK = 0x0209;

        // GlassControl swallows the middle button of a control that does not ask for it — and
        // the middle button drags the view.
        protected override bool WantsRightClick { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            // A touchpad's sideways swipe comes as a wheel of its own: the time scrolls, as
            // with Shift+wheel.
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                Wheel(-delta, PointToClient(Cursor.Position), Keys.Shift);
                m.Result = IntPtr.Zero;
                return;
            }
            // A quick second press of the middle button arrives as a double click, which
            // GlassControl mutes; here it is a press like any other — the start of a drag.
            if (m.Msg == WM_MBUTTONDBLCLK) m.Msg = WM_MBUTTONDOWN;
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Focused) Focus();                  // the wheel comes to the one with the focus
            if (e.Button == MouseButtons.Middle && Ready)
            {
                EnsureView();
                _panning = true;
                _panFrom = e.Location;
                _panX = _scrollX;
                _panY = _scrollY;
                Cursor = Cursors.SizeAll;
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_panning)
            {
                // The arrangement goes with the hand.
                _scrollX = _panX - (e.X - _panFrom.X);
                _scrollY = _panY - (e.Y - _panFrom.Y);
                Clamp();
                _hBar.Ping();
                _vBar.Ping();
                Invalidate();
                return;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle && _panning)
            {
                _panning = false;
                Cursor = Cursors.Default;
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnResize(EventArgs e)
        {
            DropFrame();
            if (Ready && _laneH > 0) EnsureView();
            base.OnResize(e);
        }

        // ------------------------------------------------------------- scrollbars

        Rectangle VBarRect()
        {
            if (!Ready || _laneH <= 0) return Rectangle.Empty;
            Rectangle plot = Plot;
            int total = ContentH;
            if (total <= plot.Height) return Rectangle.Empty;
            int track = plot.Height - Sc(4);
            int h = Math.Max(Sc(40), (int)(track * (float)plot.Height / total));
            int y = plot.Y + Sc(2) + (int)((track - h) * (_scrollY / (float)Math.Max(1, total - plot.Height)));
            return new Rectangle(Width - Sc(8), y, Sc(4), h);
        }

        Rectangle HBarRect()
        {
            if (!Ready || _pxPerBeat <= 0) return Rectangle.Empty;
            Rectangle plot = Plot;
            double total = ContentW;
            if (total <= plot.Width + 0.5) return Rectangle.Empty;
            int track = plot.Width - Sc(4);
            int w = Math.Max(Sc(40), (int)(track * plot.Width / total));
            int x = plot.X + Sc(2) + (int)((track - w) * (_scrollX / Math.Max(1, total - plot.Width)));
            return new Rectangle(x, Height - Sc(8), w, Sc(4));
        }

        // --------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            Rectangle card = new Rectangle(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, card, Sc(Theme.CardR), Theme.GlassAlpha);

            string hint = null;
            if (_arr == null) hint = "Reading the set…";
            else if (_arr.Error != null) hint = _arr.Error;
            else if (!_arr.HasContent) hint = "Nothing on the arrangement timeline";
            if (hint != null)
            {
                Chrome.DrawText(g, hint, Theme.FBody, card, Theme.TextDim, Chrome.Center);
                return;
            }

            Rectangle inner = Inner;
            if (inner.Width <= 0 || inner.Height <= 0) return;
            EnsureView();

            string key = inner.Size + "|" + _pxPerBeat + "|" + _laneH + "|" + _scrollX + "|" + _scrollY;
            if (_frame == null || _frameKey != key)
            {
                DropFrame();
                _frame = new Bitmap(inner.Width, inner.Height);
                using (Graphics fg = Graphics.FromImage(_frame))
                {
                    fg.SmoothingMode = SmoothingMode.None;
                    fg.PixelOffsetMode = PixelOffsetMode.Half;
                    ArrangementRender.DrawView(fg, new Rectangle(0, 0, inner.Width, inner.Height), _arr, Options,
                                               _pxPerBeat, _laneH, _scrollX, _scrollY);
                }
                _frameKey = key;
            }
            g.DrawImageUnscaled(_frame, inner.Location);
            // The names and the bar numbers straight onto the screen — see DrawView.
            ArrangementRender.DrawViewText(g, inner, _arr, Options, _pxPerBeat, _laneH, _scrollX, _scrollY);

            Chrome.PaintFadingBar(g, VBarRect(), _vBar.Alpha, _vBar.Thick, true, Sc(4));
            Chrome.PaintFadingBar(g, HBarRect(), _hBar.Alpha, _hBar.Thick, false, Sc(4));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropFrame();
                _vBar.Dispose();
                _hBar.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
