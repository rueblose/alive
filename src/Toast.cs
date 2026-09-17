using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// A short message in the corner of the content: shown, held for a couple of seconds, faded
    /// out. It asks nothing and waits for no answer, which is why it is a control on the form
    /// rather than a dialog: a modal window for "launching" would block work exactly where
    /// there is no reason to interrupt it, and would demand to be dismissed by hand.
    /// </summary>
    public sealed class Toast : GlassControl
    {
        string _text = "";
        float _alpha;
        bool _fadingOut;
        readonly Timer _hideTimer = new Timer();

        int InnerPad { get { return Sc(16); } }
        int TextLeft { get { return InnerPad; } }

        // Clips the control to the pill shape — without it a rectangle would remain behind the
        // rounded corners, covering the content underneath.
        protected override float PillRadius { get { return Sc(Theme.CardR); } }

        public Toast()
        {
            Visible = false;
            // The mouse passes through to whatever is below: the message appears on its own,
            // over the tiles, and has no business catching clicks that were not addressed to
            // it.
            Enabled = false;
            Font = Theme.FBody;

            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                _fadingOut = true;
                AnimEngine.Register(this);
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            float r = PillRadius;
            if (r <= 0f)
            {
                if (Region != null) { Region.Dispose(); Region = null; }
                return;
            }
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width, Height), r))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        /// <summary>Show the text and fade it out after ms milliseconds.</summary>
        public void Post(string text, int ms)
        {
            _text = text ?? "";
            _fadingOut = false;
            Visible = true;
            BringToFront();

            _hideTimer.Stop();
            _hideTimer.Interval = Math.Max(1, ms);
            _hideTimer.Start();

            AnimEngine.Register(this);
            Invalidate();
        }

        /// <summary>
        /// Sit in the bottom-left corner of the given area, with a gap from the bottom edge —
        /// flush against the footer the pill would look glued to it rather than like a separate
        /// floating message. The width comes from the text itself: the message is short and not
        /// known in advance, and a fixed-width box would either clip it or gape with emptiness.
        /// </summary>
        public void PlaceIn(Rectangle content)
        {
            Size sz = TextRenderer.MeasureText(_text, Font, new Size(Sc(420), Sc(40)),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            int w = Math.Min(content.Width, TextLeft + sz.Width + InnerPad);
            int h = Sc(44);
            int margin = Sc(18);
            int y = Math.Max(content.Top, content.Bottom - h - margin);
            SetBounds(content.Left, y, w, h);
            UpdateRegion();
        }

        public override bool OnAnimTick()
        {
            if (IsDisposed) return false;

            float target = _fadingOut ? 0f : 1f;
            float d = target - _alpha;
            if (Math.Abs(d) <= 0.01f)
            {
                _alpha = target;
                Invalidate();
                if (_alpha <= 0f) { Visible = false; return false; }
                // Fully shown — from here the pill sits still until _hideTimer fires; ticking
                // for nothing is pointless.
                return false;
            }

            _alpha += d * 0.25f;
            Invalidate();
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hideTimer.Dispose();
                if (Region != null) { Region.Dispose(); Region = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            if (_alpha <= 0.01f) return;

            Color baseBg = Theme.Backdrop;
            Color cardColor = Theme.Interpolate(baseBg, Theme.SurfacePressed, _alpha);
            RectangleF card = new RectangleF(0, 0, Width, Height);
            float r = PillRadius;
            g.Clear(cardColor);
            Theme.FillRound(g, card, r, cardColor);

            Chrome.DrawText(g, _text, Font,
                new Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - InnerPad), Height),
                Theme.Interpolate(baseBg, Theme.Text, _alpha), Chrome.Left);
        }
    }
}
