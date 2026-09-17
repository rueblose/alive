using System;
using System.Drawing;
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
            _view.Options.Dpi = DeviceDpi / 96f;
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
            string tempo = _arr.Tempo > 0 ? _arr.Tempo.ToString("0.##") + " BPM" : "";
            string key = _set.Key.Length > 0 ? "   ·   " + _set.Key : "";
            string body = _arr.Bars + " bars   ·   " + _arr.Tracks.Count + " tracks   ·   "
                        + _arr.ClipCount + " clips";
            return (tempo.Length > 0 ? tempo + key + "   ·   " : "") + body;
        }
    }

    /// <summary>The arrangement canvas itself. The picture is cached — there can be tens of
    /// thousands of notes.</summary>
    public sealed class ArrangementView : GlassControl
    {
        public readonly RenderOptions Options = new RenderOptions();

        Arrangement _arr;
        Bitmap _cache;
        Size _cacheSize;

        public ArrangementView()
        {
            Cursor = Cursors.Default;
            Options.Dpi = DeviceDpi / 96f;
        }

        public void Set(Arrangement a)
        {
            _arr = a;
            DropCache();
            Invalidate();
        }

        void DropCache()
        {
            if (_cache != null) { _cache.Dispose(); _cache = null; }
        }

        protected override void OnResize(EventArgs e) { DropCache(); base.OnResize(e); }

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

            int pad = (int)Math.Round(12 * Options.Dpi);
            Rectangle inner = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
            if (inner.Width <= 0 || inner.Height <= 0) return;

            if (_cache == null || _cacheSize != inner.Size)
            {
                DropCache();
                _cache = ArrangementRender.ToBitmap(_arr, inner.Width, inner.Height, Options);
                _cacheSize = inner.Size;
            }
            if (_cache != null) g.DrawImageUnscaled(_cache, inner.Location);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DropCache();
            base.Dispose(disposing);
        }
    }
}
