using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager
{
    public static class Chrome
    {
        /// <summary>
        /// The control's background. The window is flat, so filling with the colour of the
        /// surface the control lies on is enough: for the toolbar that is the window
        /// background, for a button inside a card the card's colour. Without it the rounded
        /// corners get outlined in a foreign colour.
        /// </summary>
        public static void PaintBase(Control c, Graphics g, Color surface)
        {
            PaintBase(c, g, c.ClientRectangle, surface);
        }

        /// <summary>
        /// The same, but in two layers — for a control lying not on the bare window but on a
        /// card. On glass a card is the window background plus a translucent overlay; we repeat
        /// both, or the control cuts a hole of its own density into the card.
        /// </summary>
        public static void PaintBase(Control c, Graphics g, Rectangle clip, Color surface, Color overlay)
        {
            PaintBase(c, g, clip, surface);
            if (overlay.A == 0) return;
            clip.Intersect(c.ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            g.FillRectangle(Theme.GetBrush(overlay), clip);
        }

        public static void PaintBase(Control c, Graphics g, Rectangle clip, Color surface)
        {
            clip.Intersect(c.ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;

            // A translucent background only makes sense where DWM really reads the window's
            // alpha. On a dialog with UseGlass=false nobody reads it, and transparency written
            // with SourceCopy turned into a black rectangle around the pill (which the
            // control's region clip used to hide; there is no clip now).
            if (surface.A < 255 && !Theme.IsBlurred(c)) surface = Color.FromArgb(255, surface);

            // A translucent surface is glass, and it has to be written into the buffer as it is
            // rather than blended on top. There are two reasons: the WinForms buffer is reused
            // between frames, so the alpha would accumulate from frame to frame; and DWM shows
            // the blur by exactly the alpha that ends up in the pixel.
            CompositingMode old = g.CompositingMode;
            if (surface.A < 255) g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(Theme.GetBrush(surface), clip);
            g.CompositingMode = old;
        }

        /// <summary>
        /// An overlay scrollbar: thin at rest, thicker under the cursor, growing from the far
        /// edge so as not to climb onto the text. The geometry and the fade are computed by the
        /// owner (see ScrollFade); only the general look is here — it is one for the whole
        /// project.
        /// </summary>
        public static void PaintFadingBar(Graphics g, Rectangle bar, float alpha, float thick, bool vertical, int grow)
        {
            if (bar.IsEmpty || alpha <= 0.01f) return;

            grow = (int)Math.Round(grow * thick);
            if (vertical) bar = new Rectangle(bar.Right - bar.Width - grow, bar.Y, bar.Width + grow, bar.Height);
            else bar = new Rectangle(bar.X, bar.Bottom - bar.Height - grow, bar.Width, bar.Height + grow);

            int a = (int)Math.Round((0x4A + 0x50 * thick) * alpha);
            float r = (vertical ? bar.Width : bar.Height) / 2f;
            Theme.FillRound(g, bar, r, Color.FromArgb(a, 0xFF, 0xFF, 0xFF));
        }

        public static readonly TextFormatFlags Left =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags Right =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags Center =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags CellLeft =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        public static readonly TextFormatFlags CellRight =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        public static readonly TextFormatFlags CellCenter =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix;

        /// <summary>
        /// Text in a pill: the line box is placed by its top, because we compute the top
        /// ourselves — see PillTop.
        /// </summary>
        public static readonly TextFormatFlags PillText =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping;

        /// <summary>The same, but with clipping: for a pill whose width was forced.</summary>
        public static readonly TextFormatFlags PillTextClipped =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        /// <summary>
        /// The inset from the top of a pill to the top of the line box at which THE LETTERS
        /// THEMSELVES sit centred in the pill. TextFormatFlags.VerticalCenter centres the whole
        /// box, and inside it above the lowercase there is room for descenders which a word
        /// usually does not have: in a 19 px pill at eleven point there were 5 px left above
        /// the letters and 3 below — and that reads as "the text has slipped downwards".
        ///
        /// GDI+ does not give out capital height, so we take a fraction of the size: 0.72 em —
        /// measured by rendering Segoe UI Variable Text into a bitmap at pill heights of 19, 21
        /// and 24.
        /// </summary>
        /// <summary>
        /// What height a pill has to be for this font: the line box plus air. The box flush is
        /// not enough — it ends exactly at the tail of a descender, and a "y" in a pill looks
        /// shaved off by its edge (measured: one pixel was left under the tail).
        ///
        /// The height comes from the font rather than from Sc(): the type size is set in points
        /// and does not depend on DeviceDpi, so a pill tied to Sc would drift apart from its
        /// own text on another monitor.
        /// </summary>
        public static int PillHeight(Font f) { return PillHeight(f, 6); }

        /// <summary>
        /// "There are more" — three dots drawn by hand and exactly centred in the rectangle.
        /// The "…" glyph will not do for this: its ink sits on the baseline while the pills
        /// beside it are aligned by their capitals (see PillTop), and among them the ellipsis
        /// came out noticeably below the middle.
        /// </summary>
        public static void DrawDots(Graphics g, Rectangle r, Color ink, int dot)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(Theme.GetBrush(ink), cx + i * dot * 2f - dot / 2f, cy - dot / 2f, dot, dot);
        }

        /// <summary>The width the dots from DrawDots will take.</summary>
        public static int DotsWidth(int dot) { return dot * 5; }

        /// <summary>
        /// The same calculation with an explicit allowance of air. Less than six is used where
        /// height is tight — two rows of tags in a table row, for instance.
        /// </summary>
        public static int PillHeight(Font f, int air)
        {
            return TextRenderer.MeasureText("Ag", f).Height + air;
        }

        public static int PillTop(Graphics g, Font f, int pillH)
        {
            float capH = f.SizeInPoints * g.DpiY / 72f * 0.72f;
            return (int)Math.Round(pillH / 2f + capH / 2f - Theme.Baseline(f));
        }

        /// <summary>
        /// Wrapped text, measured and drawn by the same rule. TextBoxControl is what makes it
        /// the same rule: without it a word wider than the line (a path — "Effect\Freesound4live\
        /// Downloads\01530") widens the measured rectangle, the other lines are laid out on
        /// that width, and the height comes out a line or two short of what is drawn — the file
        /// name at the end of a sample's path simply was not there.
        /// </summary>
        public static readonly TextFormatFlags Wrap =
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // ------------------------------------------------------------ the system beep

        const int WM_SETCURSOR = 0x0020;
        const int WM_MOUSEMOVE = 0x0200;
        const short HTERROR = -2;

        /// <summary>
        /// Swallow the system sound on a click outside a modal window. Called from the WndProc
        /// of any window that may turn out to be a dialog's owner.
        ///
        /// A click on a window disabled by a modal dialog reaches it through no mouse message
        /// at all — the system takes an interest earlier. But one message does get through:
        /// WM_SETCURSOR, where the low word of lParam holds HTERROR and the high word the code
        /// of the button pressed (verified with the owner's message log: 0x0020 with lParam
        /// 0x0201FFFE on a left press). It is on that very pair that DefWindowProc calls
        /// MessageBeep — this is how the WM_SETCURSOR documentation describes it too. We do not
        /// pass the message on: there is no sound, while all the rest of the system's behaviour
        /// is intact — the dialog still stays in front and does not close.
        ///
        /// In place of the sound we light up the dialog's own outline: a refusal has to be
        /// visible.
        /// </summary>
        public static bool SwallowBlockedClick(ref Message m)
        {
            if (m.Msg != WM_SETCURSOR) return false;

            int l = m.LParam.ToInt32();
            if ((short)(l & 0xFFFF) != HTERROR) return false;
            if (((l >> 16) & 0xFFFF) == WM_MOUSEMOVE) return false;   // only a press beeps

            GlassDialog blocking = Form.ActiveForm as GlassDialog;
            if (blocking != null) blocking.Flash();

            m.Result = (IntPtr)1;      // TRUE means "handled", DefWindowProc is not called
            return true;
        }

        /// <summary>
        /// Which resize edge of a borderless window a point of its client area is on — the
        /// WM_NCHITTEST code (HTLEFT … HTBOTTOMRIGHT), or 0 when it is on none. border — how
        /// thick the grabbing strip is. Shared by the main window and the resizable dialogs.
        /// </summary>
        public static int EdgeHit(Point p, Size client, int border)
        {
            bool l = p.X <= border, r = p.X >= client.Width - border;
            bool t = p.Y <= border, d = p.Y >= client.Height - border;
            if (t && l) return 13;
            if (t && r) return 14;
            if (d && l) return 16;
            if (d && r) return 17;
            if (l) return 10;
            if (r) return 11;
            if (t) return 12;
            if (d) return 15;
            return 0;
        }

        public static void Chevron(Graphics g, float cx, float cy, float size, Color color)
        {
            Icons.Draw(g, Glyph.ChevronDown,
                       new RectangleF(cx - size, cy - size, size * 2, size * 2), color, 1.5f);
        }

        /// <summary>
        /// There used to be a correction here — "lift everything with VerticalCenter by 2 px" —
        /// on the assumption that GDI seats a line below the geometric centre. Re-measured by
        /// the ink (rendering into a Bitmap, the bounds of the opaque pixels):
        /// DT_VCENTER|DT_SINGLELINE centres exactly, with a deviation of 0–0.5 px at every size
        /// from 9.5 to 22 pt and at any pill height. The earlier measurement was fooled by a
        /// descender: on a line with a "g" or a "p" the ink runs downwards, and the centre of
        /// the ink turns out below the centre of the box — as it should be. The correction,
        /// meanwhile, lifted the text an honest 2 px above the centre, which is what could be
        /// seen in every pill and hint.
        ///
        /// Important: DT_VCENTER without DT_SINGLELINE is silently ignored by the system — the
        /// text stands at the top of the rectangle. That is exactly why every single-line flag
        /// set below includes SingleLine.
        /// </summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color,
                                    TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags);
        }

        /// <summary>
        /// "1 reference", "18 references" — agreement, not "1 references". One shared helper
        /// instead of a copy in every window that counts something.
        /// </summary>
        public static string Plural(int n, string word)
        {
            return n + " " + word + (n == 1 ? "" : "s");
        }
    }

    public interface IAnimatable
    {
        bool OnAnimTick();
    }

    public static class AnimEngine
    {
        static readonly System.Windows.Forms.Timer _timer;
        static readonly List<IAnimatable> _active = new List<IAnimatable>();

        static AnimEngine()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 16;
            _timer.Tick += OnTick;
        }

        public static void Register(IAnimatable anim)
        {
            if (anim == null) return;
            if (!_active.Contains(anim))
            {
                _active.Add(anim);
                if (!_timer.Enabled) _timer.Start();
            }
        }

        static void OnTick(object sender, EventArgs e)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!_active[i].OnAnimTick())
                        _active.RemoveAt(i);
                }
                catch { _active.RemoveAt(i); }
            }
            if (_active.Count == 0)
                _timer.Stop();
        }
    }

    /// <summary>
    /// The "now playing" pulse: three bars that move while the sound is going. Shared across
    /// the whole application — one 90 ms timer instead of a timer per list, and what gets
    /// repainted is not the whole list but only the rectangle of the glyph itself (the
    /// subscriber supplies it as a function, because the playing row travels with the scroll).
    /// </summary>
    public static class PlayPulse
    {
        static readonly System.Windows.Forms.Timer _timer;
        static readonly List<Control> _subs = new List<Control>();
        static readonly List<Func<Rectangle>> _rects = new List<Func<Rectangle>>();

        static PlayPulse()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 90;
            _timer.Tick += delegate
            {
                for (int i = _subs.Count - 1; i >= 0; i--)
                {
                    Control c = _subs[i];
                    if (c.IsDisposed || !c.IsHandleCreated) { _subs.RemoveAt(i); _rects.RemoveAt(i); continue; }
                    Rectangle r = _rects[i]();
                    if (r.Width > 0 && r.Height > 0) c.Invalidate(Rectangle.Inflate(r, 2, 2));
                }
                if (_subs.Count == 0) _timer.Stop();
            };
        }

        public static void Attach(Control c, Func<Rectangle> rect)
        {
            if (c == null || rect == null) return;
            int i = _subs.IndexOf(c);
            if (i >= 0) { _rects[i] = rect; }
            else { _subs.Add(c); _rects.Add(rect); }
            if (!_timer.Enabled) _timer.Start();
        }

        public static void Detach(Control c)
        {
            int i = _subs.IndexOf(c);
            if (i < 0) return;
            _subs.RemoveAt(i);
            _rects.RemoveAt(i);
            if (_subs.Count == 0) _timer.Stop();
        }

        /// <summary>The height of bar number i, 0.25..1. The phase is shared — from the clock,
        /// with no state of its own.</summary>
        public static float Level(int i)
        {
            double t = Environment.TickCount / 260.0 + i * 0.7;
            return 0.28f + 0.72f * (float)((Math.Sin(t) * 0.5 + 0.5) * (0.55 + 0.45 * (Math.Sin(t * 1.7 + i) * 0.5 + 0.5)));
        }

        /// <summary>Three bars along the bottom edge of the rectangle.</summary>
        public static void Paint(Graphics g, RectangleF r, Color color)
        {
            float barW = r.Width / 5f;
            for (int i = 0; i < 3; i++)
            {
                float h = Math.Max(1.5f, r.Height * Level(i));
                RectangleF b = new RectangleF(r.X + i * barW * 2f, r.Bottom - h, barW, h);
                Theme.FillRound(g, b, barW / 2f, color);
            }
        }
    }

    /// <summary>
    /// An overlay scrollbar: thin at rest, thicker under the cursor, fading a second after the
    /// last scroll. It holds only its own two fractions (visibility and thickness) — the
    /// geometry and the colour are drawn by the owner, because the bars stand differently on a
    /// list and on a grid of tiles.
    /// </summary>
    public sealed class ScrollFade : IDisposable
    {
        readonly Control _owner;
        readonly Func<Rectangle> _rect;
        readonly System.Windows.Forms.Timer _t = new System.Windows.Forms.Timer();
        int _lastPing;
        float _alpha, _thick;
        bool _hot;

        const int HoldMs = 900;

        public ScrollFade(Control owner, Func<Rectangle> rect)
        {
            _owner = owner;
            _rect = rect;
            _t.Interval = 30;
            _t.Tick += Step;
        }

        /// <summary>How visible the bar is, 0..1.</summary>
        public float Alpha { get { return _alpha; } }

        /// <summary>How "thick" the bar is, 0..1 — from rest to hover.</summary>
        public float Thick { get { return _thick; } }

        /// <summary>There was a scroll — show the bar and start the countdown to the
        /// fade.</summary>
        public void Ping()
        {
            _lastPing = Environment.TickCount;
            if (!_t.Enabled) _t.Start();
            InvalidateBar();
        }

        /// <summary>The cursor is right by the bar: it does not fade and grows
        /// thicker.</summary>
        public void SetHot(bool value)
        {
            if (_hot == value) return;
            _hot = value;
            Ping();
        }

        void Step(object sender, EventArgs e)
        {
            if (_owner.IsDisposed) { _t.Stop(); return; }

            bool want = _hot || Environment.TickCount - _lastPing < HoldMs;
            float ta = want ? 1f : 0f;
            float tt = _hot ? 1f : 0f;

            _alpha += (ta - _alpha) * (ta > _alpha ? 0.40f : 0.13f);
            _thick += (tt - _thick) * 0.30f;

            if (Math.Abs(ta - _alpha) < 0.01f && Math.Abs(tt - _thick) < 0.01f)
            {
                _alpha = ta; _thick = tt;
                _t.Stop();
            }
            InvalidateBar();
        }

        void InvalidateBar()
        {
            if (!_owner.IsHandleCreated || _owner.IsDisposed) return;
            Rectangle r = _rect();
            if (r.Width > 0 && r.Height > 0) _owner.Invalidate(Rectangle.Inflate(r, 8, 8));
        }

        public void Dispose() { _t.Stop(); _t.Dispose(); }
    }

    /// <summary>A base for controls: no flicker, with the background of its own surface and a
    /// smooth hover/press animation.</summary>
    public class GlassControl : Control, IAnimatable
    {
        protected bool Hot, Pressed;
        public float HoverFactor;
        public float PressFactor;

        /// <summary>The colour of what the control lies on — the background under the rounding
        /// is filled with it.</summary>
        public Color Surface = Theme.Backdrop;

        /// <summary>An overlay over the background if the control lies on a card. See
        /// Chrome.PaintBase.</summary>
        public Color SurfaceOverlay = Color.Transparent;

        protected void PaintSurface(Graphics g)
        {
            Chrome.PaintBase(this, g, ClientRectangle, Surface, SurfaceOverlay);
        }

        protected virtual float PillRadius { get { return -1f; } }

        public GlassControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.FButton;
            ForeColor = Theme.Text;
        }

        protected int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        /// <summary>
        /// Whether the control needs the right (and middle) mouse button. Not by default: a
        /// bare Control raises Click on any button — only Button filters for the left — and
        /// because of that a right click on our pills and icons fired as a left one. Lists with
        /// a context menu override the property and handle e.Button themselves.
        /// </summary>
        protected virtual bool WantsRightClick { get { return false; } }

        // WM_RBUTTONDOWN..WM_MBUTTONDBLCLK — the right and middle buttons with all their
        // down/up/dblclk. We swallow them entirely so they never reach Control's Click logic.
        const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206;
        const int WM_MBUTTONDBLCLK = 0x0209;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg >= WM_RBUTTONDOWN && m.Msg <= WM_MBUTTONDBLCLK)
            {
                // Nobody needs a right (or middle) double click: even the lists that need the
                // right button for a menu opened a row on a second poke, just as on a left
                // double click. So dblclk is always muted, and the rest only for those that did
                // not ask for the right button.
                bool dbl = m.Msg == WM_RBUTTONDBLCLK || m.Msg == WM_MBUTTONDBLCLK;
                if (dbl || !WantsRightClick)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// How far a control is "pushed in" when pressed — all its content is drawn that many
        /// pixels inwards. The tactility Apple's controls give is this, not a colour animation.
        /// </summary>
        protected float PressInset { get { return PressFactor * Sc(2); } }

        // The region clip is gone from here. It was one-bit, and the antialiased edge of a pill
        // was cut off by it in steps — visible on any filled button (Open in Live, Apply). The
        // background under the rounding is written by PaintBase, so the region held nothing but
        // those steps.

        protected override void OnMouseEnter(EventArgs e) { Hot = true; AnimEngine.Register(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; AnimEngine.Register(this); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; AnimEngine.Register(this); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; AnimEngine.Register(this); base.OnMouseUp(e); }

        public virtual bool OnAnimTick()
        {
            if (IsDisposed) return false;

            float targetHover = Hot ? 1.0f : 0.0f;
            float targetPress = Pressed ? 1.0f : 0.0f;

            float dh = targetHover - HoverFactor;
            float dp = targetPress - PressFactor;

            if (Math.Abs(dh) < 0.01f && Math.Abs(dp) < 0.01f)
            {
                HoverFactor = targetHover;
                PressFactor = targetPress;
                Invalidate();
                return false;
            }

            // Entry quick, return lazy — symmetric speeds read as "the interface is thinking".
            // The numbers are the fraction of the remainder per frame (16 ms): hover ~110 ms in
            // / ~260 ms out, press ~55 / ~210.
            HoverFactor += dh * (dh > 0f ? 0.45f : 0.18f);
            PressFactor += dp * (dp > 0f ? 0.70f : 0.22f);
            Invalidate();
            return true;
        }
    }

    /// <summary>A pill button. Ordinary — on the surface colour; primary — a light
    /// fill.</summary>
    public class GlassButton : GlassControl
    {
        public bool Primary;
        public bool Quiet;   // no fill, like a text link
        public bool Checked;

        protected override float PillRadius { get { return Height / 2f; } }

        public GlassButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        public void FitToText(int hPadding)
        {
            Size s = TextRenderer.MeasureText(Text, Font);
            Width = s.Width + Sc(hPadding) * 2;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);

            if (!Enabled)
            {
                using (GraphicsPath path = Theme.Round(r, Height / 2f))
                using (Pen pen = new Pen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 1f))
                    g.DrawPath(pen, path);

                Color disabledText = Color.FromArgb(0xFF, 0x48, 0x48, 0x4C);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), disabledText, Chrome.Center);
                return;
            }

            Color text;

            if (Primary)
            {
                RectangleF pr = RectangleF.Inflate(r, -PressInset, -PressInset);
                float rad = pr.Height / 2f;

                // A vertical gradient with the light on top — the same as the toggle's knob:
                // the button reads as raised and therefore pressable. A flat fill looked
                // disabled next to the outlined buttons.
                Color top = Theme.Interpolate(Theme.LightTop, Color.White, HoverFactor);
                Color bottom = Theme.Interpolate(Theme.Light, Theme.LightTop, HoverFactor);
                if (PressFactor > 0.01f)
                {
                    top = Theme.Interpolate(top, Theme.LightPressed, PressFactor);
                    bottom = Theme.Interpolate(bottom, Theme.LightPressed, PressFactor);
                }

                using (GraphicsPath path = Theme.Round(pr, rad))
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                           new RectangleF(pr.X, pr.Y - 1f, pr.Width, pr.Height + 2f),
                           top, bottom, LinearGradientMode.Vertical))
                    g.FillPath(lgb, path);

                // A highlight along the top arc — 1px white, fading when pressed.
                using (Pen hi = new Pen(Color.FromArgb((int)Math.Round(0x66 * (1f - PressFactor)), 255, 255, 255), 1f))
                using (GraphicsPath tp = Theme.RoundTop(RectangleF.Inflate(pr, -0.5f, -0.5f), rad - 0.5f))
                    g.DrawPath(hi, tp);

                text = Theme.OnLight;
                Chrome.DrawText(g, Text, Font,
                    new Rectangle((int)Math.Round(pr.X), (int)Math.Round(pr.Y),
                                  (int)Math.Round(pr.Width), (int)Math.Round(pr.Height)),
                    text, Chrome.Center);
                return;
            }
            else if (Quiet)
            {
                // An ordinary button merges with rest long before HoverFactor==0 (its alpha
                // runs from 0x14, not from zero). On a Quiet one the fill runs from zero, so
                // the tail is visible twice as long and the hover "sticks". We shift the scale
                // by that same fraction.
                float rest = Theme.GlassSurfaceAlpha / (float)Theme.GlassSurfaceHotAlpha;
                float shown = Math.Max(0f, (HoverFactor - rest) / (1f - rest));
                if (shown > 0.001f)
                {
                    int alpha = (int)Math.Round(shown * Theme.GlassSurfaceHotAlpha);
                    Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
                }
                text = Theme.Interpolate(Theme.TextDim, Theme.Text, shown);
            }
            else
            {
                int baseAlpha = Checked || PressFactor > 0.5f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceAlpha;
                int targetAlpha = HoverFactor > 0.001f ? Theme.GlassSurfaceHotAlpha : baseAlpha;
                int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, HoverFactor));
                if (PressFactor > 0.01f)
                    alpha = (int)Math.Round(Theme.Lerp(alpha, Theme.GlassSurfacePressedAlpha, PressFactor));

                RectangleF pr = RectangleF.Inflate(r, -PressInset, -PressInset);
                Theme.PaintGlassSurface(this, g, pr, pr.Height / 2f, alpha);
                text = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
            }

            Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, Chrome.Center);
        }
    }

    /// <summary>
    /// The filters button: the sliders glyph, a caption and the number of active conditions
    /// after a dot. A control of its own, because the glyph and the counter have to keep their
    /// places regardless of the caption's length.
    /// </summary>
    public class FiltersButton : GlassControl
    {
        public int Count;

        /// <summary>The caption with the counter — it also sets the pill width.</summary>
        string Label { get { return Count > 0 ? Text + " · " + Count : Text; } }

        /// <summary>
        /// The width for the current caption. A fixed 140 pixels was exactly enough for
        /// "Filters": the moment a counter appeared, the number went off into an ellipsis.
        /// </summary>
        public int PreferredWidth
        {
            get { return Sc(15) + Sc(17) + Sc(8) + TextRenderer.MeasureText(Label, Font).Width + Sc(16); }
        }

        protected override float PillRadius { get { return Height / 2f; } }

        public FiltersButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
            Font = Theme.FButton;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            int baseAlpha = Theme.GlassSurfaceAlpha;
            int targetAlpha = PressFactor > 0.01f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha;
            int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, Math.Max(HoverFactor, PressFactor)));

            Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);

            float box = Sc(17);
            Color iconColor = Theme.Interpolate(Theme.TextDim, Theme.Light, HoverFactor);
            Icons.Draw(g, Glyph.Filters,
                       new RectangleF(Sc(15), (Height - box) / 2f, box, box), iconColor, 1.5f);

            Color textColor = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
            Chrome.DrawText(g, Label, Font,
                new Rectangle(Sc(15) + (int)box + Sc(8), 0, Width - Sc(15) - (int)box - Sc(16), Height),
                textColor, Chrome.Left);
        }
    }

    /// <summary>A round glyph button — the toolbar and the window buttons.</summary>
    public class IconButton : GlassControl
    {
        public Glyph Icon = Glyph.Close;
        public bool Danger;          // the window close button turns red under the cursor

        /// <summary>
        /// A dot in the top right corner of the glyph. The quietest thing a toolbar button
        /// can say: something is waiting inside, look when you feel like it. Used by the
        /// gear when a newer release has been found.
        /// </summary>
        public bool Dot;
        public bool Quiet;           // no backing: just the glyph, lightening under the cursor
        public float IconScale = 0.46f;

        /// <summary>Spin the glyph half a turn when pressed — for the die.</summary>
        public bool SpinOnClick;
        float _spin;                 // the current angle, in degrees
        int _spinFrom;

        protected override float PillRadius { get { return Height / 2f; } }

        public IconButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Size = new Size(Theme.IconSize, Theme.IconSize);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (SpinOnClick)
            {
                _spinFrom = (int)Math.Round(_spin) + 180;
                _spin = _spinFrom;
                AnimEngine.Register(this);
            }
            base.OnClick(e);
        }

        public override bool OnAnimTick()
        {
            bool more = base.OnAnimTick();
            if (_spin > 0.5f)
            {
                // We settle the angle to zero by the same exponential as the hover: the die
                // turns and comes to rest rather than spinning forever.
                _spin += (0f - _spin) * 0.22f;
                Invalidate();
                return true;
            }
            if (_spin != 0f) { _spin = 0f; Invalidate(); }
            return more;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            if (Danger && HoverFactor > 0.001f)
            {
                Color bg = Color.FromArgb((int)Math.Round(HoverFactor * 255), Theme.Red);
                Theme.FillRound(g, r, Height / 2f, bg);
            }
            else if (!Quiet)
            {
                int baseAlpha = Theme.GlassSurfaceAlpha;
                int targetAlpha = PressFactor > 0.01f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha;
                int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, Math.Max(HoverFactor, PressFactor)));
                Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
            }

            float box = Width * IconScale;
            // The glyph's travel is half as long: it is smaller than a pill as it is, and a
            // full inset would eat a noticeable share of the drawing itself.
            float inset = PressInset * 0.5f;
            RectangleF ir = new RectangleF((Width - box) / 2f + inset, (Height - box) / 2f + inset,
                                           box - inset * 2, box - inset * 2);
            // The glyph lightens under the cursor on every kind of button: on the ordinary one
            // it used to hold exactly one colour, and the only sign of hover was the backing —
            // which is barely visible on a blurred background.
            Color ink = Danger ? Theme.Interpolate(Theme.Light, Color.White, HoverFactor)
                      : (Quiet ? Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor)
                               : Theme.Interpolate(Theme.Light, Color.White, HoverFactor));

            if (_spin > 0.5f)
            {
                System.Drawing.Drawing2D.Matrix old = g.Transform;
                g.TranslateTransform(Width / 2f, Height / 2f);
                g.RotateTransform(_spin);
                g.TranslateTransform(-Width / 2f, -Height / 2f);
                Icons.Draw(g, Icon, ir, ink, Math.Max(1.4f, Width / 24f));
                g.Transform = old;
                return;
            }
            Icons.Draw(g, Icon, ir, ink, Math.Max(1.4f, Width / 24f));
            PaintDot(g);
        }

        /// <summary>
        /// The dot sits on the glyph rather than beside it — the button is round and has no
        /// corner of its own to spare — and is ringed in the colour of whatever lies under
        /// the button, so it reads as a dot and not as a smudge on a busy icon.
        /// </summary>
        void PaintDot(Graphics g)
        {
            if (!Dot) return;
            float d = Math.Max(5f, Width * 0.20f);
            float x = Width - d - Width * 0.14f, y = Width * 0.14f;
            using (SolidBrush ring = new SolidBrush(Surface))
                g.FillEllipse(ring, x - 1.5f, y - 1.5f, d + 3f, d + 3f);
            using (SolidBrush fill = new SolidBrush(Theme.Green))
                g.FillEllipse(fill, x, y, d, d);
        }
    }

    /// <summary>A toggle pill: a choice of two states, or a switch (IsSwitch).</summary>
    public class PillToggle : GlassControl
    {
        bool _checked;

        public bool IsSwitch;
        float _thumbPos;
        float _targetPos;
        System.Windows.Forms.Timer _animTimer;

        public event EventHandler CheckedChanged;

        protected override float PillRadius { get { return Height / 2f; } }

        public PillToggle()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
            Font = Theme.FButton;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _targetPos = _checked ? 1f : 0f;
                if (IsSwitch && IsHandleCreated && Visible)
                {
                    StartSwitchAnim();
                }
                else
                {
                    _thumbPos = _targetPos;
                    Invalidate();
                }
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        void StartSwitchAnim()
        {
            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Interval = 15;
                _animTimer.Tick += delegate
                {
                    float diff = _targetPos - _thumbPos;
                    if (Math.Abs(diff) < 0.04f)
                    {
                        _thumbPos = _targetPos;
                        _animTimer.Stop();
                    }
                    else
                    {
                        _thumbPos += diff * 0.35f;
                    }
                    Invalidate();
                };
            }
            if (!_animTimer.Enabled) _animTimer.Start();
        }

        public void FitToText() { Width = TextRenderer.MeasureText(Text, Font).Width + Sc(32); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null)
            {
                _animTimer.Dispose();
                _animTimer = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);

            if (IsSwitch)
            {
                PaintAppleSwitch(g, r);
                return;
            }

            if (!Enabled)
            {
                using (GraphicsPath path = Theme.Round(r, Height / 2f))
                using (Pen pen = new Pen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 1f))
                    g.DrawPath(pen, path);

                Color disabledText = Color.FromArgb(0xFF, 0x48, 0x48, 0x4C);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), disabledText, Chrome.Center);
                return;
            }

            if (_checked)
            {
                Color fill = Theme.Interpolate(Theme.Light, Color.White, HoverFactor);
                Theme.FillRound(g, r, Height / 2f, fill);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                                Theme.OnLight, Chrome.Center);
            }
            else
            {
                int alpha = (int)Math.Round(Theme.Lerp(Theme.GlassSurfaceAlpha, Theme.GlassSurfaceHotAlpha, HoverFactor));
                if (PressFactor > 0.01f)
                    alpha = (int)Math.Round(Theme.Lerp(alpha, Theme.GlassSurfacePressedAlpha, PressFactor));
                Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
                Color text = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, Chrome.Center);
            }
        }

        void PaintAppleSwitch(Graphics g, RectangleF r)
        {
            float trackW = Math.Min(Width, Sc(38));
            float trackH = Math.Min(Height, Sc(22));
            float trackX = r.X + (r.Width - trackW) / 2f;
            float trackY = r.Y + (r.Height - trackH) / 2f;
            RectangleF track = new RectangleF(trackX, trackY, trackW, trackH);
            float radius = trackH / 2f;

            float t = (_animTimer != null && _animTimer.Enabled) ? _thumbPos : (_checked ? 1f : 0f);

            // 1. The track bed: OFF: a recessed dark glass track (Theme.Sunken plus a delicate
            // translucency) ON: the ordinary light one (Theme.Light)
            Color offSunken = Theme.Sunken;
            Color offOverlay = Color.FromArgb((int)(0x14 + 0x0E * HoverFactor), 0xFF, 0xFF, 0xFF);

            using (GraphicsPath trackPath = Theme.Round(track, radius))
            {
                if (!Enabled)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)))
                        g.FillPath(b, trackPath);
                }
                else
                {
                    if (t < 0.999f)
                    {
                        using (Brush b = new SolidBrush(offSunken)) g.FillPath(b, trackPath);
                        using (Brush b = new SolidBrush(offOverlay)) g.FillPath(b, trackPath);
                    }
                    if (t > 0.001f)
                    {
                        int alpha = (int)(255 * t);
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Theme.Light)))
                            g.FillPath(b, trackPath);

                        if (HoverFactor > 0.01f)
                        {
                            using (Brush b = new SolidBrush(Color.FromArgb((int)(0x20 * HoverFactor * t), 255, 255, 255)))
                                g.FillPath(b, trackPath);
                        }
                    }
                }
            }

            // 2. The glass contour of the bed with a highlight along the top edge (in the
            // PaintGlassBorder idiom)
            if (Enabled)
            {
                int borderAlpha = (int)(0x26 * (1f - t) + 0x30 * t + 0x10 * HoverFactor);
                using (Pen pen = new Pen(Color.FromArgb(borderAlpha, 255, 255, 255), 1f))
                using (GraphicsPath path = Theme.Round(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f), radius - 0.5f))
                {
                    g.DrawPath(pen, path);
                }

                int hiAlpha = (int)(0x1A * (1f - t) + 0x38 * t + 0x10 * HoverFactor);
                using (Pen hiPen = new Pen(Color.FromArgb(hiAlpha, 255, 255, 255), 1f))
                using (GraphicsPath topPath = Theme.RoundTop(new RectangleF(track.X + 1f, track.Y + 1f, track.Width - 2f, track.Height - 2f), radius - 1f))
                {
                    g.DrawPath(hiPen, topPath);
                }
            }
            else
            {
                using (Pen pen = new Pen(Color.FromArgb(0x12, 255, 255, 255), 1f))
                using (GraphicsPath path = Theme.Round(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f), radius - 0.5f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 3. The knob: tactile, with a soft gradient, silvery in OFF and white in ON
            float inset = Sc(2);
            float knobDiam = trackH - inset * 2;
            float offX = trackX + inset;
            float onX = trackX + trackW - inset - knobDiam;
            float knobX = Theme.Lerp(offX, onX, t);
            float knobY = trackY + inset;

            RectangleF knobRect = new RectangleF(knobX, knobY, knobDiam, knobDiam);

            if (Enabled)
            {
                // A soft diffuse shadow under the knob
                RectangleF s1 = new RectangleF(knobX - 0.5f, knobY + 0.5f, knobDiam + 1f, knobDiam + 1f);
                using (Brush b1 = new SolidBrush(Color.FromArgb(0x18, 0, 0, 0))) g.FillEllipse(b1, s1);
                RectangleF s2 = new RectangleF(knobX, knobY + 1f, knobDiam, knobDiam);
                using (Brush b2 = new SolidBrush(Color.FromArgb(0x35, 0, 0, 0))) g.FillEllipse(b2, s2);

                Color knobTop = Theme.Interpolate(Color.FromArgb(255, 0xE8, 0xE8, 0xEB), Color.FromArgb(255, 0xFF, 0xFF, 0xFF), t);
                Color knobBottom = Theme.Interpolate(Color.FromArgb(255, 0xC6, 0xC6, 0xCA), Color.FromArgb(255, 0xEE, 0xEE, 0xF2), t);

                if (knobRect.Width > 0 && knobRect.Height > 0)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(knobRect, knobTop, knobBottom, LinearGradientMode.Vertical))
                    {
                        g.FillEllipse(lgb, knobRect);
                    }
                }
                else
                {
                    using (Brush b = new SolidBrush(knobTop)) g.FillEllipse(b, knobRect);
                }

                using (Pen knobBorder = new Pen(Color.FromArgb(0x22, 0, 0, 0), 1f))
                {
                    g.DrawEllipse(knobBorder, knobRect.X, knobRect.Y, knobRect.Width, knobRect.Height);
                }
                using (Pen knobHi = new Pen(Color.FromArgb(0x50, 255, 255, 255), 1f))
                {
                    g.DrawArc(knobHi, knobRect.X + 0.5f, knobRect.Y + 0.5f, knobRect.Width - 1f, knobRect.Height - 1f, 200, 140);
                }
            }
            else
            {
                Color disKnob = Color.FromArgb(0x60, 255, 255, 255);
                using (Brush b = new SolidBrush(disKnob))
                    g.FillEllipse(b, knobRect);
            }
        }
    }

    /// <summary>A segmented control: an outline with a moving pill inside.</summary>
    public class Segmented : GlassControl
    {
        string[] _items = new string[0];
        int _index;
        int _hotIndex = -1;

        public event EventHandler SelectedChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Height = Sc(Theme.ControlH);
            Cursor = Cursors.Hand;
            Font = Theme.FButton;
        }

        public void SetItems(params string[] items)
        {
            _items = items;
            int w = 0;
            foreach (string s in items) w += Math.Max(Sc(60), TextRenderer.MeasureText(s, Font).Width + Sc(36));
            Width = w;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_index == value) return;
                _index = value; Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        RectangleF SegRect(int i)
        {
            float w = (Width - 1f) / Math.Max(1, _items.Length);
            return new RectangleF(0.5f + i * w, 0.5f, w, Height - 1f);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = -1;
            for (int i = 0; i < _items.Length; i++) if (SegRect(i).Contains(e.Location)) idx = i;
            if (idx != _hotIndex) { _hotIndex = idx; AnimEngine.Register(this); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotIndex = -1; AnimEngine.Register(this); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < _items.Length; i++)
                if (SegRect(i).Contains(e.Location)) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            MouseEventArgs me = e as MouseEventArgs;
            if (me != null)
            {
                for (int i = 0; i < _items.Length; i++)
                    if (SegRect(i).Contains(me.Location)) SelectedIndex = i;
            }
            base.OnDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassBorder(g, r, Height / 2f);

            for (int i = 0; i < _items.Length; i++)
            {
                if (i == _index)
                {
                    RectangleF sr = SegRect(i);
                    Theme.DrawRound(g, sr, sr.Height / 2f, Color.FromArgb(115, 255, 255, 255), 1f);
                }
            }

            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF sr = SegRect(i);
                bool sel = i == _index;
                Color textColor = sel ? Theme.Text : (i == _hotIndex ? Theme.Text : Theme.TextDim);
                Rectangle textRect = new Rectangle((int)Math.Round(sr.X), 0, (int)Math.Round(sr.Width), Height);
                Chrome.DrawText(g, _items[i], Font, textRect, textColor, Chrome.Center);
            }
        }
    }

    /// <summary>
    /// An input field is the one genuinely native window in the whole interface, and on a glass
    /// window it behaves unlike the other controls. Edit draws its background with an ordinary
    /// GDI brush, and a brush's colour is a COLORREF, where there is simply no byte for alpha:
    /// zero goes into the surface. Zero in the alpha means "a hole here" to DWM, and the
    /// acrylic layer starts shining through the field.
    ///
    /// The first version fixed this AFTER the regular WM_PAINT: let Edit draw a hole in the
    /// real window and we will patch the alpha over it afterwards. At rest the difference is
    /// invisible, but while the window is being dragged DWM manages to read a frame at exactly
    /// the moment between "the hole is drawn" and "the alpha is patched" — and a transparent
    /// rectangle flickers. It can only be cured atomically: Edit prints itself into our
    /// offscreen buffer (WM_PRINTCLIENT rather than WM_PAINT), gets alpha 0xFF right there, and
    /// what reaches the real window is a finished, trouble-free frame in a single BitBlt —
    /// nobody ever sees the raw hole from outside, rather than "usually does not see it".
    /// </summary>
    /// <summary>
    /// A text input field built on an invisible native TextBox. It removes the UxTheme
    /// background rectangle artefacts on acrylic glass completely.
    /// </summary>
    public class GlassTextBox : TextBox
    {
        public string Cue = "";

        /// <summary>
        /// The second source of that same system beep: a single-line input field cannot insert
        /// a line break, and on Enter (and on Escape too) the edit control itself calls
        /// MessageBeep from its WM_CHAR. We swallow the character specifically — KeyDown
        /// reaches the subscribers as before, so Enter still does something, just silently.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_CHAR = 0x0102;
            if (m.Msg == WM_CHAR)
            {
                int ch = m.WParam.ToInt32();
                if (ch == 13 || ch == 10 || ch == 27) return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>An input field: a recessed pill drawn through GDI+ with selection and caret
    /// support.</summary>
    public class FieldBox : GlassControl
    {
        public readonly GlassTextBox Box = new GlassTextBox();
        public string Glyph;      // an optional glyph on the left
        public bool ShowClear;    // a cross on the right while the field has text — it clears it

        /// <summary>A glyph button in that same left slot: a click on it does not place the
        /// caret but calls IconLeftClicked (the calendar on date fields).</summary>
        public AbletonManager.Glyph? IconLeft;
        public event Action IconLeftClicked;

        public string Cue
        {
            get { return Box.Cue; }
            set { Box.Cue = value ?? ""; Invalidate(); }
        }

        bool _focused, _clearHot, _caretVisible, _dragSelecting, _iconHot;
        int _anchor;      // the fixed end of the selection: we work out where the caret is from it
        int _scroll;      // how many pixels the text has moved left under the left edge of the field
        Rectangle _clearRect;
        Timer _timer = new Timer();

        /// <summary>
        /// One flag set for measuring and for drawing — otherwise the caret position disagrees
        /// with where the letter really stands. NoPadding is mandatory: without it TextRenderer
        /// adds several pixels of margin to the line, and they have to be subtracted with
        /// constants picked by eye (which is how it used to be here). With it the width of a
        /// substring exactly equals the offset of the next character, trailing spaces included.
        /// SingleLine so that VerticalCenter works, see Chrome.DrawText.
        /// </summary>
        static readonly TextFormatFlags TextFlags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        protected override float PillRadius { get { return Height / 2f; } }

        public FieldBox()
        {
            Height = Theme.ControlH;
            // The pill is only a shell: the focus is held by the hidden Box. While the shell
            // was a tab stop of its own, Tab landed on it first — with no caret and no visible
            // frame, that is, for nothing — and the field was reached on the second press.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Box.Size = new Size(0, 0);
            Box.Location = new Point(-100, -100);
            Controls.Add(Box);

            _timer.Interval = 500;
            _timer.Tick += delegate { _caretVisible = !_caretVisible; Invalidate(); };

            Box.GotFocus += delegate { _focused = true; _caretVisible = true; _timer.Start(); Invalidate(); };
            Box.LostFocus += delegate { _focused = false; _caretVisible = false; _timer.Stop(); Invalidate(); };
            Box.TextChanged += delegate { UpdateLayout(); Sync(); Invalidate(); };
            Box.KeyDown += OnBoxKeyDown;
            Box.KeyUp += delegate { Sync(); Invalidate(); };
            Box.Click += delegate { Sync(); Invalidate(); };
        }

        /// <summary>The selection collapsed — meaning the anchor is where the caret
        /// is.</summary>
        void Sync() { if (Box.SelectionLength == 0) _anchor = Box.SelectionStart; }

        /// <summary>
        /// The caret is the moving end of the selection. EM_GETSEL gives only the start and the
        /// length, not which end is currently being moved; we work it out from the anchor.
        /// </summary>
        int Caret
        {
            get
            {
                int s = Box.SelectionStart, l = Box.SelectionLength;
                if (l == 0) return s;
                return s == _anchor ? s + l : s;
            }
        }

        bool HasClear { get { return ShowClear && Box.Text.Length > 0; } }

        protected override void OnResize(EventArgs e) { UpdateLayout(); base.OnResize(e); }

        void UpdateLayout()
        {
            int cs = Sc(20);
            _clearRect = new Rectangle(Width - Sc(26), (Height - cs) / 2, cs, cs);
        }

        /// <summary>The left glyph slot — also the click zone for IconLeft.</summary>
        Rectangle IconRect
        {
            get { int s = Sc(20); return new Rectangle(Sc(11), (Height - s) / 2, s, s); }
        }

        Rectangle GetTextRect()
        {
            int left = string.IsNullOrEmpty(Glyph) && !IconLeft.HasValue ? Sc(16) : Sc(34);
            int right = HasClear ? Sc(30) : Sc(14);
            return new Rectangle(left, 0, Math.Max(10, Width - left - right), Height);
        }

        /// <summary>The width of the first `upto` characters, in pixels from the start of the
        /// line.</summary>
        int TextW(Graphics g, int upto)
        {
            if (upto <= 0) return 0;
            string txt = Box.Text;
            if (upto > txt.Length) upto = txt.Length;
            return TextRenderer.MeasureText(g, txt.Substring(0, upto), Font,
                                            new Size(int.MaxValue, Height), TextFlags).Width;
        }

        int GetCharIndexAt(Graphics g, int mouseX, Rectangle textRect)
        {
            string txt = Box.Text;
            if (string.IsNullOrEmpty(txt)) return 0;
            int want = mouseX - textRect.Left + _scroll;
            if (want <= 0) return 0;

            int bestIdx = 0, minDiff = int.MaxValue;
            for (int i = 0; i <= txt.Length; i++)
            {
                int diff = Math.Abs(want - TextW(g, i));
                if (diff < minDiff) { minDiff = diff; bestIdx = i; }
            }
            return bestIdx;
        }

        // ------------------------------------------------------------- word bounds A native
        // edit of zero width does not walk by Ctrl+arrows, so we work out word movement and
        // double-click word selection ourselves. The rule is as in Explorer: letters, digits
        // and the underscore are a word; other non-whitespace characters are a group of their
        // own; spaces stick to the word on the right when moving right.

        // public rather than private: the word bounds are checked by scratch\wordnav_check.cs.
        public static bool IsWordChar(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        public static int WordLeft(string s, int i)
        {
            while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
            if (i > 0 && IsWordChar(s[i - 1])) { while (i > 0 && IsWordChar(s[i - 1])) i--; }
            else while (i > 0 && !IsWordChar(s[i - 1]) && !char.IsWhiteSpace(s[i - 1])) i--;
            return i;
        }

        public static int WordRight(string s, int i)
        {
            int n = s.Length;
            if (i < n && IsWordChar(s[i])) { while (i < n && IsWordChar(s[i])) i++; }
            else while (i < n && !IsWordChar(s[i]) && !char.IsWhiteSpace(s[i])) i++;
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        void MoveCaret(int to, bool extend)
        {
            if (!extend) _anchor = to;
            Box.Select(Math.Min(_anchor, to), Math.Abs(to - _anchor));
            _caretVisible = true;
            Invalidate();
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && !e.Alt && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            {
                string txt = Box.Text;
                int to = e.KeyCode == Keys.Left ? WordLeft(txt, Caret) : WordRight(txt, Caret);
                MoveCaret(to, e.Shift);
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            // The same zero-width bug as with Ctrl+arrows above: native whole-word deletion
            // does not work on a 0×0 edit control either. If there is a selection, Ctrl has
            // nothing to do with it — ordinary deletion removes exactly that anyway; otherwise
            // we work the word boundary out ourselves and cut the range by hand.
            if (e.Control && !e.Alt && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete))
            {
                if (Box.SelectionLength > 0)
                {
                    Box.SelectedText = "";
                }
                else
                {
                    string txt = Box.Text;
                    int caret = Caret;
                    int from = e.KeyCode == Keys.Back ? WordLeft(txt, caret) : caret;
                    int to = e.KeyCode == Keys.Back ? caret : WordRight(txt, caret);
                    if (to > from)
                    {
                        Box.Text = txt.Substring(0, from) + txt.Substring(to);
                        Box.Select(from, 0);
                    }
                }
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;

            string txt = Box.Text;
            if (txt.Length == 0) return;

            int i;
            using (Graphics g = CreateGraphics())
                i = GetCharIndexAt(g, e.X, GetTextRect());

            // A click at a word's right edge gives the index past its last character — we take
            // the character to the left, or a double click at the end of a word would select
            // emptiness.
            if (i >= txt.Length || (i > 0 && !IsWordChar(txt[i]) && IsWordChar(txt[i - 1]))) i--;
            if (i < 0 || !IsWordChar(txt[i])) return;

            int a = i, b = i + 1;
            while (a > 0 && IsWordChar(txt[a - 1])) a--;
            while (b < txt.Length && IsWordChar(txt[b])) b++;

            _dragSelecting = false;
            _anchor = a;
            Box.Select(a, b - a);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = HasClear && _clearRect.Contains(e.Location);
            bool ihot = IconLeft.HasValue && IconRect.Contains(e.Location);
            if (hot != _clearHot || ihot != _iconHot)
            {
                _clearHot = hot; _iconHot = ihot;
                Cursor = hot || ihot ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            if (_dragSelecting && e.Button == MouseButtons.Left)
                using (Graphics g = CreateGraphics())
                    MoveCaret(GetCharIndexAt(g, e.X, GetTextRect()), true);

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_clearHot || _iconHot) { _clearHot = _iconHot = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (HasClear && _clearRect.Contains(e.Location))
            {
                Box.Clear();
                Box.Focus();
                return;
            }

            if (IconLeft.HasValue && IconRect.Contains(e.Location))
            {
                if (IconLeftClicked != null) IconLeftClicked();
                return;
            }

            Box.Focus();
            using (Graphics g = CreateGraphics())
                MoveCaret(GetCharIndexAt(g, e.X, GetTextRect()), false);
            _dragSelecting = true;

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragSelecting = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            float insetY = Sc(1);
            RectangleF r = new RectangleF(0, insetY, Width, Height - insetY * 2);
            Theme.FillRound(g, r, r.Height / 2f, Theme.Sunken);
            if (_focused) Theme.DrawRound(g, r, r.Height / 2f, Theme.SurfacePressed, 1f);

            if (IconLeft.HasValue)
                Icons.Draw(g, IconLeft.Value, RectangleF.Inflate(IconRect, -Sc(2), -Sc(2)),
                           _iconHot ? Theme.Text : Theme.TextDim, 1.4f);
            else if (!string.IsNullOrEmpty(Glyph))
                Chrome.DrawText(g, Glyph, Font, new Rectangle(Sc(12), 0, Sc(20), Height),
                               Theme.TextDim, Chrome.Center);

            if (HasClear)
                Icons.Draw(g, AbletonManager.Glyph.Close, RectangleF.Inflate(_clearRect, -Sc(5), -Sc(5)),
                          _clearHot ? Theme.Text : Theme.TextDim, 1.4f);

            Rectangle textRect = GetTextRect();
            string txt = Box.Text;

            // Everything to do with the text lives strictly inside textRect: the selection is
            // drawn as a rectangle and used to run off past the pill and under the cross, while
            // a long line simply spilled outside.
            GraphicsState clip = g.Save();
            g.IntersectClip(textRect);

            if (string.IsNullOrEmpty(txt))
            {
                _scroll = 0;
                // We do not show the placeholder while focused: next to the caret it reads as
                // text already typed that for some reason will not delete.
                if (!_focused && !string.IsNullOrEmpty(Cue))
                    Chrome.DrawText(g, Cue, Font, textRect, Theme.TextDim, TextFlags);
            }
            else
            {
                int total = TextW(g, txt.Length);
                int caretIdx = Math.Min(Caret, txt.Length);
                int caretX = TextW(g, caretIdx);

                // The line is longer than the field — we do not cut it at the edge but carry it
                // under the caret.
                if (!_focused) _scroll = 0;
                else
                {
                    if (caretX - _scroll > textRect.Width - Sc(2)) _scroll = caretX - textRect.Width + Sc(2);
                    if (caretX - _scroll < 0) _scroll = caretX;
                }
                if (_scroll > total - textRect.Width) _scroll = total - textRect.Width;
                if (_scroll < 0) _scroll = 0;

                int x0 = textRect.Left - _scroll;

                int selStart = Box.SelectionStart;
                int selLen = Math.Min(Box.SelectionLength, txt.Length - selStart);
                if (_focused && selLen > 0)
                {
                    int sx = x0 + TextW(g, selStart);
                    int sw = TextW(g, selStart + selLen) - TextW(g, selStart);
                    int cy = (Height - Sc(20)) / 2;
                    using (SolidBrush selBrush = new SolidBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x5E)))
                        g.FillRectangle(selBrush, sx, cy, Math.Max(Sc(2), sw), Sc(20));
                }

                Chrome.DrawText(g, txt, Font, new Rectangle(x0, textRect.Y, total + Sc(8), textRect.Height),
                                Theme.Text, TextFlags);

                if (_focused && _caretVisible && selLen == 0)
                {
                    int cx = x0 + caretX;
                    int cy = (Height - Sc(18)) / 2;
                    using (Pen p = new Pen(Theme.Text, 1.5f))
                        g.DrawLine(p, cx, cy, cx, cy + Sc(18));
                }
            }

            g.Restore(clip);

            // An empty field in focus: the caret at the left edge, with no text under it yet.
            if (_focused && _caretVisible && txt.Length == 0)
            {
                int cy = (Height - Sc(18)) / 2;
                using (Pen p = new Pen(Theme.Text, 1.5f))
                    g.DrawLine(p, textRect.Left, cy, textRect.Left, cy + Sc(18));
            }
        }
    }

    /// <summary>A dropdown: a pill and a dark menu.</summary>
    public class DropField : GlassControl
    {
        readonly List<string> _items = new List<string>();
        int _index = -1;

        public event EventHandler SelectedChanged;
        public string Label;

        protected override float PillRadius { get { return Height / 2f; } }

        public DropField() { Height = Theme.ControlH; Cursor = Cursors.Hand; Font = Theme.FButton; }

        public void SetItems(IEnumerable<string> items, int index)
        {
            _items.Clear();
            _items.AddRange(items);
            _index = index;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_index == value) return;
                _index = value; Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return _index >= 0 && _index < _items.Count ? _items[_index] : ""; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_items.Count == 0) return;

            ContextMenuStrip menu = DarkMenu.Create();
            for (int i = 0; i < _items.Count; i++)
            {
                int captured = i;
                ToolStripMenuItem mi = new ToolStripMenuItem(_items[i]);
                mi.Checked = i == _index;
                mi.Click += delegate { SelectedIndex = captured; };
                menu.Items.Add(mi);
            }
            menu.Closed += delegate { Pressed = false; Invalidate(); };
            menu.Show(this, new Point(0, Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, r, Height / 2f, Hot ? Theme.GlassSurfaceHotAlpha : Theme.GlassSurfaceAlpha);

            int x = Sc(16);
            if (!string.IsNullOrEmpty(Label))
            {
                Size ls = TextRenderer.MeasureText(Label, Theme.FLabel);
                Chrome.DrawText(g, Label, Theme.FLabel, new Rectangle(x, 0, ls.Width, Height),
                                Theme.TextDim, Chrome.Left);
                x += ls.Width + Sc(8);
            }

            Rectangle tr = new Rectangle(x, 0, Math.Max(10, Width - x - Sc(32)), Height);
            Chrome.DrawText(g, SelectedItem, Font, tr, Theme.Text, Chrome.Left);
            Chrome.Chevron(g, Width - Sc(17), Height / 2f, Sc(6), Theme.TextDim);
        }
    }

    /// <summary>
    /// A calendar under a date field. Inside is the native MonthCalendar: the month, the year
    /// and choosing a day are already written for us, and writing our own for the sake of dark
    /// colouring would be pointless.
    ///
    /// One subtlety: with visual styles enabled MonthCalendar is drawn by the Windows theme and
    /// silently ignores its own BackColor/TitleBackColor — the calendar stays white.
    /// SetWindowTheme with an empty name strips the theme off it, after which the colours start
    /// working. It can only be called on an existing window, hence — after Show.
    /// </summary>
    public static class CalendarPopup
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string appName, string partList);

        public static void Show(Control anchor, DateTime? current, Action<DateTime> picked)
        {
            MonthCalendar cal = new MonthCalendar();
            cal.MaxSelectionCount = 1;
            cal.ShowTodayCircle = false;

            // We strip the theme BEFORE showing: without it the calendar measures itself
            // differently, and doing it after Show leaves the dropdown at its former size,
            // cutting off the calendar's month header and its last week. Touching Handle
            // creates the window by itself — SetWindowTheme will not work without it.
            try { SetWindowTheme(cal.Handle, "", ""); } catch { }
            cal.BackColor = Theme.SolidSurface;
            cal.ForeColor = Theme.Text;
            cal.TitleBackColor = Theme.Bg;
            cal.TitleForeColor = Theme.Text;
            cal.TrailingForeColor = Theme.TextDim;
            if (current.HasValue) cal.SetDate(current.Value.Date);
            cal.Size = cal.SingleMonthSize;

            ToolStripControlHost slot = new ToolStripControlHost(cal);
            slot.Margin = Padding.Empty;
            slot.Padding = Padding.Empty;
            slot.AutoSize = false;
            slot.Size = cal.Size;

            ToolStripDropDown host = new ToolStripDropDown();
            host.Padding = Padding.Empty;
            host.AutoSize = true;
            host.DropShadowEnabled = true;
            host.BackColor = Theme.SolidSurface;
            host.Items.Add(slot);

            cal.DateSelected += delegate (object s, DateRangeEventArgs e)
            {
                picked(e.Start.Date);
                host.Close();
            };
            // The dropdown must not be torn down inside its own Closed — at that moment it is
            // still finishing with its message. We remove it on the next turn of the queue.
            host.Closed += delegate { anchor.BeginInvoke((Action)delegate { host.Dispose(); }); };

            host.Show(anchor, 0, anchor.Height + 4);
        }
    }

    public static class DarkMenu
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.BackColor = Theme.SolidSurface;
            m.ForeColor = Theme.Text;
            m.Font = Theme.FButton;
            m.ShowImageMargin = false;
            m.DropShadowEnabled = true;
            m.Opening += delegate { RoundCorners(m); };
            return m;
        }

        /// <summary>
        /// We round the menu with the same radius as the compact cards elsewhere in the
        /// interface — otherwise against a uniformly rounded background it sticks out with
        /// sharp corners. We do it on Opening: by that point the menu has already laid out its
        /// items and Width/Height are final.
        /// </summary>
        /// <summary>The menu's corner radius — shared by the region and the outline.</summary>
        internal static float Radius(ToolStrip t)
        {
            return 10f * (t.DeviceDpi / 96f);
        }

        static void RoundCorners(ContextMenuStrip m)
        {
            float r = Radius(m);
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, m.Width, m.Height), r))
                m.Region = new Region(p);
        }

        class DarkColors : ProfessionalColorTable
        {
            // A menu is a separate window with no glass, so the colours here are opaque only.
            public override Color MenuItemSelected { get { return Theme.SolidPressed; } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
            public override Color MenuItemBorder { get { return MenuItemSelected; } }
            public override Color ToolStripDropDownBackground { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.SolidSurface; } }
            public override Color MenuBorder { get { return Theme.SolidPressed; } }
        }

        class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { }

            /// <summary>
            /// WinForms draws an item's shortcut with the same call as its name — we tell them
            /// apart by the text and shade it, so that the right column does not argue with the
            /// item itself.
            /// </summary>
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
                bool shortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
                                && e.Text == mi.ShortcutKeyDisplayString;
                e.TextColor = shortcut ? Theme.TextDim : Theme.Text;
                base.OnRenderItemText(e);
            }

            /// <summary>
            /// Our own outline instead of the standard one. The standard one is a rectangle
            /// along the menu's edge, while the menu is rounded by a region (see
            /// DarkMenu.RoundCorners): the straight sides broke off at the cut corners and the
            /// frame looked ragged. We draw 1px along the same arc.
            /// </summary>
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                RectangleF b = new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f);
                using (GraphicsPath p = Theme.Round(b, Math.Max(0f, DarkMenu.Radius(e.ToolStrip) - 0.5f)))
                using (Pen pen = new Pen(Theme.Hairline, 1f))
                    g.DrawPath(pen, p);

                g.SmoothingMode = old;
            }

            /// <summary>
            /// Our own tick instead of the system one — the standard one draws a raised square
            /// in Windows colours and is barely visible on a dark background.
            /// </summary>
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                RectangleF box = RectangleF.Inflate(r, -r.Width * 0.16f, -r.Height * 0.16f);
                Icons.Draw(e.Graphics, Glyph.Check, box, Theme.Text, 1.5f);
            }
        }
    }

    /// <summary>A track scrubber for the mini transport in the footer, in the Apple Music
    /// style.</summary>
    public sealed class SeekSlider : GlassControl
    {
        float _progress;
        bool _drag;

        public event Action<float> Seeked;

        public SeekSlider()
        {
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        public float Progress
        {
            get { return _progress; }
            set
            {
                float v = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(v - _progress) < 0.001f) return;
                _progress = v;
                Invalidate();
            }
        }

        RectangleF Track
        {
            get
            {
                int trackH = Sc(5);
                float padX = Sc(4);
                // The trough's row is a whole number: at half a pixel the antialiasing smears
                // the outer lines and the played part looks trimmed at the bottom.
                return new RectangleF(padX, (Height - trackH) / 2, Math.Max(Sc(20), Width - padX * 2), trackH);
            }
        }

        void Grab(int x)
        {
            RectangleF t = Track;
            float p = (x - t.X) / Math.Max(1f, t.Width);
            Progress = p;
            if (Seeked != null) Seeked(Progress);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _drag = true;
            Grab(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) Grab(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            RectangleF t = Track;
            float r = t.Height / 2f;

            // The background trough with rounded ends
            Color trackBg = Hot || _drag
                ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

            Theme.FillRound(g, t, r, trackBg);

            // The played part is the same rounded trough, only shorter. No GraphicsPath clip
            // was needed here: the shape lies inside the trough as it is, while a Region is
            // one-bit and cut the antialiased edge off in a flat step.
            float doneW = t.Width * _progress;
            if (doneW > 0f)
                Theme.FillRound(g, new RectangleF(t.X, t.Y, doneW, t.Height), r,
                                Hot || _drag ? Color.White : Theme.Text);

            // The knob appears only under the cursor: at rest the bar reads as an even progress
            // line, and it can still be dragged — the cursor will say so.
            if (Hot || _drag)
            {
                float knob = Sc(11);
                float kx = t.X + doneW;
                float ky = t.Y + t.Height / 2f;
                Theme.FillRound(g, new RectangleF(kx - knob / 2f, ky - knob / 2f, knob, knob),
                                knob / 2f, Color.White);
            }
        }
    }

    /// <summary>
    /// A clickable set name in the mini transport. On hover it lights up smoothly in white,
    /// changes the cursor to a hand, and on a click jumps to the set.
    /// </summary>
    public sealed class PlayerSetLink : GlassControl
    {
        string _text = "";

        public PlayerSetLink()
        {
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        public string SetName
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value ?? "";
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // The background is mandatory: without it the control leaves an opaque rectangle on
            // the glass — its own BackColor, inherited from the window.
            PaintSurface(g);
            if (string.IsNullOrEmpty(_text)) return;
            Theme.Smooth(g);

            Color col = Theme.Interpolate(Theme.TextDim, Color.White, HoverFactor);
            Font font = Theme.FTitle;

            Chrome.DrawText(g, _text, font, ClientRectangle, col,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// A vertical volume slider as a capsule popping up over the volume button.
    /// </summary>
    public sealed class VolumePopupControl : GlassControl
    {
        float _value = 0.5f;
        bool _dragging;

        public event EventHandler ValueChanged;

        public VolumePopupControl()
        {
            Cursor = Cursors.Hand;
            Visible = false;
        }

        // The popup hangs over the list rather than over the window background, so the corners
        // cannot be painted with the "right" colour — there are rows under them. We clip the
        // control to the pill shape, and then what was there stays behind the rounding. The
        // same device as in Toast.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width, Height), Width / 2f))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Region != null) { Region.Dispose(); Region = null; }
            base.Dispose(disposing);
        }

        public float Value
        {
            get { return _value; }
            set
            {
                float v = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(v - _value) < 0.001f) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        void UpdateFromY(int y)
        {
            float padY = Sc(14);
            float trackH = Height - padY * 2;
            if (trackH <= 0f) return;
            float v = 1f - (y - padY) / trackH;
            Value = Math.Max(0f, Math.Min(1f, v));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                UpdateFromY(e.Y);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
                UpdateFromY(e.Y);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta != 0)
            {
                int steps = e.Delta / 120;
                if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                float newVal = (float)Math.Round((_value + steps * 0.05f) / 0.05f) * 0.05f;
                Value = Math.Max(0f, Math.Min(1f, newVal));
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float cornerR = Width / 2f;

            // The fill covers the whole box rather than following the contour: the shape is
            // held by the Region, and the antialiased edge of a fill inside it would give a
            // dark rim from BackColor.
            using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF4, 0x1A, 0x1A, 0x1D)))
                g.FillRectangle(b, ClientRectangle);
            using (GraphicsPath path = Theme.Round(rect, cornerR))
            using (Pen p = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
                g.DrawPath(p, path);

            float padY = Sc(14);
            float trackH = Height - padY * 2;
            float tx = Width / 2f;
            float trackW = Sc(5);
            float r = trackW / 2f;
            float knobY = padY + (1f - _value) * trackH;

            // The grey background track
            RectangleF fullTrack = new RectangleF(tx - r, padY, trackW, trackH);
            Theme.FillRound(g, fullTrack, r, Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

            // The filled white part from the bottom up to the knob
            if (knobY < padY + trackH)
            {
                RectangleF playedRect = new RectangleF(tx - r, knobY, trackW, (padY + trackH) - knobY);
                Theme.FillRound(g, playedRect, r, Color.White);
            }

            // The white round knob
            float knobR = Sc(6);
            Theme.FillRound(g, new RectangleF(tx - knobR, knobY - knobR, knobR * 2, knobR * 2), knobR, Color.White);
        }
    }
}
