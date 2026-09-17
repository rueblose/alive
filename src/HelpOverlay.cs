using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Help over the whole window: what the program can do, what its windows show, and which
    /// keys do it.
    ///
    /// Not a separate window but a control filling the main one's client area — that way the
    /// help does not get lost behind the window, travels with it, and does not add a second
    /// taskbar button. The dimming is done from a snapshot: neighbouring controls (the list,
    /// the detail panel) do not show through a sibling control, WinForms cannot do that, so the
    /// window is captured into a picture BEFORE the help is shown and drawn muted underneath
    /// it.
    /// </summary>
    public sealed class HelpOverlay : Control
    {
        public event Action CloseRequested;

        Bitmap _shot;
        int _scroll, _contentH, _viewH;
        Rectangle _card, _closeRect;
        bool _closeHot;

        readonly List<Entry>[] _columns = new List<Entry>[2];

        public HelpOverlay()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Visible = false;
            TabStop = false;
            BuildContent();
        }

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        // ------------------------------------------------------------------ showing

        /// <summary>
        /// Capture the window and open over it. The snapshot is taken before we show ourselves
        /// — otherwise we would catch ourselves in it.
        /// </summary>
        public void Open(Form owner)
        {
            Visible = false;
            Snap(owner);
            _scroll = 0;
            Bounds = owner.ClientRectangle;
            Visible = true;
            BringToFront();
            Focus();
            Invalidate();
        }

        void Snap(Form owner)
        {
            if (_shot != null) { _shot.Dispose(); _shot = null; }
            try
            {
                Rectangle r = owner.ClientRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                Bitmap b = new Bitmap(r.Width, r.Height);
                owner.DrawToBitmap(b, r);
                _shot = b;
            }
            catch { /* no luck - we show the picture whole */ }
        }

        public void Close()
        {
            Visible = false;
            if (_shot != null) { _shot.Dispose(); _shot = null; }
        }

        /// <summary>Scrolling from the keyboard. true means the key was ours.</summary>
        public bool HandleKey(Keys k)
        {
            int step = 0;
            switch (k)
            {
                case Keys.Down: step = Sc(60); break;
                case Keys.Up: step = -Sc(60); break;
                case Keys.PageDown: step = _viewH - Sc(40); break;
                case Keys.PageUp: step = -(_viewH - Sc(40)); break;
                case Keys.Home: step = -_contentH; break;
                case Keys.End: step = _contentH; break;
                default: return false;
            }
            ScrollBy(step);
            return true;
        }

        void ScrollBy(int dy)
        {
            int max = Math.Max(0, _contentH - _viewH);
            int want = Math.Max(0, Math.Min(max, _scroll + dy));
            if (want == _scroll) return;
            _scroll = want;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-(int)(e.Delta / 120f * Sc(70)));
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = _closeRect.Contains(e.Location);
            if (hot != _closeHot)
            {
                _closeHot = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                Invalidate(_closeRect);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // A click on the cross or outside the card closes it. Inside the card a click does
            // nothing: there is text there being read, and closing it by accident is a shame.
            if (_closeRect.Contains(e.Location) || !_card.Contains(e.Location))
            {
                if (CloseRequested != null) CloseRequested();
                return;
            }
            base.OnMouseDown(e);
        }

        // --------------------------------------------------------------- drawing

        protected override void OnPaintBackground(PaintEventArgs e) { }

        // The card is opaque, and that is not decoration: the columns are drawn into a separate
        // buffer of the same colour and transferred here whole (see OnPaint). Otherwise the
        // text cannot be clipped — TextRenderer, which the entire interface is drawn with, does
        // not honour Graphics.Clip, and the lines spilled past the bottom edge of the card.
        static readonly Color CardFill = Color.FromArgb(0xFF, 0x17, 0x17, 0x1A);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // The snapshot of the window under the dimming. If the window was resized in the
            // meantime, the snapshot keeps its old size — we draw it as is from the top-left
            // corner and simply fill the remainder: the help is dimmed anyway and the seam does
            // not read.
            g.Clear(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0D));
            if (_shot != null) g.DrawImageUnscaled(_shot, 0, 0);
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(0xBE, 0x07, 0x07, 0x09)))
                g.FillRectangle(dim, ClientRectangle);

            Theme.Smooth(g);
            LayoutCard();

            Theme.FillRound(g, _card, Sc(Theme.CardR), CardFill);
            using (Pen edge = new Pen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)))
            using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(_card, Sc(Theme.CardR)))
                g.DrawPath(edge, p);

            int pad = Sc(34);
            int x = _card.Left + pad;
            int top = _card.Top + pad;
            int innerW = _card.Width - pad * 2;

            // The header lives outside the scroll — the title has to stay visible at all times.
            int titleH;
            using (Font big = Theme.UISemibold(21f))
            {
                string title = "Alive " + Application.ProductVersion;
                Size titleSize = TextRenderer.MeasureText(title, big);
                titleH = LineH(big);
                Chrome.DrawText(g, title, big,
                                new Rectangle(x, top, titleSize.Width + Sc(4), titleH), Theme.Text, Line1);

                // The subtitle is pinned to the heading's baseline rather than to its top.
                int subH = LineH(Theme.FLabel);
                Chrome.DrawText(g, "Keyboard Shortcuts", Theme.FLabel,
                                new Rectangle(x + titleSize.Width + Sc(12), top + titleH - subH - Sc(2),
                                              innerW - titleSize.Width - Sc(12), subH),
                                Theme.TextDim, Line1);
            }

            int icon = Sc(18);
            int iconX = _card.Right - pad - icon;
            int iconY = top + (titleH - icon) / 2;
            Rectangle iconRect = new Rectangle(iconX, iconY, icon, icon);

            Size escSize = TextRenderer.MeasureText("Esc", Theme.FBadge);
            int capH = LineH(Theme.FBadge) + Sc(6);
            int capW = escSize.Width + Sc(14);
            int capY = top + (titleH - capH) / 2;
            int capX = iconX - capW - Sc(10);
            Rectangle escCap = new Rectangle(capX, capY, capW, capH);

            _closeRect = new Rectangle(capX - Sc(4), top - Sc(2), (_card.Right - pad) - (capX - Sc(4)), titleH + Sc(4));

            Color escBg = _closeHot ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
            Color escFg = _closeHot ? Color.White : Theme.TextDim;
            Color iconFg = _closeHot ? Color.White : Theme.TextDim;

            Theme.FillRound(g, escCap, Sc(5), escBg);
            Chrome.DrawText(g, "Esc", Theme.FBadge, escCap, escFg, Chrome.Center | TextFormatFlags.NoClipping);
            Icons.Draw(g, Glyph.Close, iconRect, iconFg, 1.5f);

            int listTop = top + Sc(48);
            _viewH = _card.Bottom - pad - listTop;
            if (_viewH <= 0) return;

            PaintColumns(g, x, listTop, innerW);

            if (_contentH > _viewH) PaintScrollBar(g, listTop);
        }

        /// <summary>
        /// Three columns into a buffer and one picture into place — that way a line that does
        /// not fit the visible part is clipped exactly at the edge instead of spilling past the
        /// card.
        /// </summary>
        void PaintColumns(Graphics g, int x, int listTop, int innerW)
        {
            int gap = Sc(30);
            int colW = (innerW - gap * (_columns.Length - 1)) / _columns.Length;

            using (Bitmap buf = new Bitmap(innerW, _viewH))
            {
                using (Graphics bg = Graphics.FromImage(buf))
                {
                    bg.Clear(CardFill);
                    Theme.Smooth(bg);

                    _contentH = 0;
                    for (int i = 0; i < _columns.Length; i++)
                    {
                        int h = Flow(bg, _columns[i], i * (colW + gap), -_scroll, colW, true);
                        if (h > _contentH) _contentH = h;
                    }

                    // A line cut by the edge is faded with a gradient — that way it reads as
                    // text continuing rather than as text somebody bit off.
                    if (_contentH - _scroll > _viewH) Fade(bg, innerW, _viewH - Sc(34), false);
                    if (_scroll > 0) Fade(bg, innerW, 0, true);
                }
                g.DrawImageUnscaled(buf, x, listTop);
            }

            // Scrolled past the end (the window was resized, there is less text) — pull it
            // back.
            int max = Math.Max(0, _contentH - _viewH);
            if (_scroll > max) { _scroll = max; Invalidate(); }
        }

        void Fade(Graphics g, int w, int y, bool downwards)
        {
            Rectangle r = new Rectangle(0, y, w, Sc(34));
            using (System.Drawing.Drawing2D.LinearGradientBrush lb =
                       new System.Drawing.Drawing2D.LinearGradientBrush(
                           r, downwards ? CardFill : Color.FromArgb(0, CardFill),
                              downwards ? Color.FromArgb(0, CardFill) : CardFill, 90f))
                g.FillRectangle(lb, r);
        }

        void PaintScrollBar(Graphics g, int listTop)
        {
            int track = _viewH;
            int h = Math.Max(Sc(40), (int)(track * (float)_viewH / _contentH));
            int max = Math.Max(1, _contentH - _viewH);
            int y = listTop + (int)((track - h) * (_scroll / (float)max));
            Theme.FillRound(g, new Rectangle(_card.Right - Sc(14), y, Sc(4), h), Sc(2),
                            Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// The card is centred but does not fill the window: a visible band of the dimmed
        /// application has to remain around it — otherwise this is no longer an overlay but a
        /// second page.
        /// </summary>
        void LayoutCard()
        {
            int w = Math.Min(Sc(900), Width - Sc(104));
            int h = Math.Min(Sc(600), Height - Sc(84));
            _card = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }

        /// <summary>
        /// Lays a column out from top to bottom and returns its height. It measures and draws
        /// with the same code (draw=false counts only), or the scroll height and the picture
        /// drift apart at the very first edit of the text.
        /// </summary>
        int Flow(Graphics g, List<Entry> items, int x, int top, int w, bool draw)
        {
            // One cap per key — the longest combination is wider than the old third of a column
            // and ran into the description on the right.
            int keyW = Math.Min(Sc(150), w * 2 / 5);
            int keyGap = Sc(12);
            int textX = x + keyW + keyGap;
            int textW = w - keyW - keyGap;
            int y = top;

            foreach (Entry it in items)
            {
                switch (it.Kind)
                {
                    case Kind.Section:
                        {
                            y += Sc(26);
                            int hh = LineH(Theme.FTitle);
                            if (draw)
                            {
                                Chrome.DrawText(g, it.Key, Theme.FTitle,
                                                new Rectangle(x, y, w, hh), Theme.Text, Line1);
                                using (Pen p = new Pen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)))
                                    g.DrawLine(p, x, y + hh + Sc(5), x + w, y + hh + Sc(5));
                            }
                            y += hh + Sc(18);
                            break;
                        }

                    case Kind.Note:
                        {
                            int h = Wrap(g, it.Text, Theme.FLabel, w);
                            if (draw)
                                TextRenderer.DrawText(g, it.Text, Theme.FLabel,
                                                      new Rectangle(x, y, w, h), Theme.TextDim, Chrome.Wrap);
                            y += h + Sc(14);
                            break;
                        }

                    case Kind.Key:
                    case Kind.Term:
                        {
                            int h = Wrap(g, it.Text, Theme.FLabel, textW);
                            if (draw)
                            {
                                if (it.Kind == Kind.Key)
                                    DrawCaps(g, it.Key, x, y);
                                else
                                {
                                    Chrome.DrawText(g, it.Key, Theme.FLabel,
                                                    new Rectangle(x, y, keyW, LineH(Theme.FLabel)),
                                                    Theme.Text, Line1);
                                }
                                TextRenderer.DrawText(g, it.Text, Theme.FLabel,
                                                      new Rectangle(textX, y, textW, h), Theme.TextDim, Chrome.Wrap);
                            }
                            y += Math.Max(h, Sc(20)) + Sc(9);
                            break;
                        }
                }
            }
            return y - top;
        }

        /// <summary>
        /// A combination gets one cap per key: "Shift Enter" is two keys, and on one shared
        /// plate they read as a single long one. Separators ("/" between alternatives) stay as
        /// plain text between the caps.
        /// </summary>
        void DrawCaps(Graphics g, string combo, int x, int y)
        {
            int capH = LineH(Theme.FBadge) + Sc(6);
            int top = y - Sc(2);
            foreach (string token in combo.Split(' '))
            {
                if (token.Length == 0) continue;
                Size ts = TextRenderer.MeasureText(token, Theme.FBadge);
                if (token == "/" || token == "+" || token == "..")
                {
                    Chrome.DrawText(g, token, Theme.FBadge, new Rectangle(x, top, ts.Width, capH),
                                    Theme.TextDim, Chrome.Center | TextFormatFlags.NoClipping);
                    x += ts.Width + Sc(5);
                    continue;
                }
                Rectangle cap = new Rectangle(x, top, ts.Width + Sc(14), capH);
                Theme.FillRound(g, cap, Sc(5), Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                Chrome.DrawText(g, token, Theme.FBadge, cap, Theme.Text,
                                Chrome.Center | TextFormatFlags.NoClipping);
                x += cap.Width + Sc(5);
            }
        }

        /// <summary>
        /// The height of a line with wrapping. It has to be measured with the same Graphics the
        /// line is later drawn with: the column buffer has ClearTypeGridFit on while the screen
        /// DC does not, and the longest captions measured as two lines but drew as one. The
        /// room for a second line that never existed stayed a hole in the column.
        /// </summary>
        static int Wrap(Graphics g, string text, Font f, int w)
        {
            return TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Chrome.Wrap).Height;
        }

        /// <summary>
        /// The height of a line in this font — with room to spare for descenders. Fixed heights
        /// will not do here: Chrome.Left centres text in a rectangle, and in a short one the
        /// tails of "g", "y" and "p" were shaved off, and on headings the tops as well.
        /// </summary>
        static int LineH(Font f)
        {
            return TextRenderer.MeasureText("Ayg", f).Height;
        }

        /// <summary>Single-line text: no wrapping and no clipping at the rectangle's
        /// edges.</summary>
        const TextFormatFlags Line1 = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                    | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                                    | TextFormatFlags.NoClipping;

        protected override void Dispose(bool disposing)
        {
            if (disposing && _shot != null) { _shot.Dispose(); _shot = null; }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- content

        enum Kind { Section, Key, Term, Note }

        sealed class Entry
        {
            public Kind Kind;
            public string Key = "";
            public string Text = "";
        }

        static Entry Section(string s) { Entry e = new Entry(); e.Kind = Kind.Section; e.Key = s; return e; }
        static Entry Note(string s) { Entry e = new Entry(); e.Kind = Kind.Note; e.Text = s; return e; }
        static Entry Key(string k, string t) { Entry e = new Entry(); e.Kind = Kind.Key; e.Key = k; e.Text = t; return e; }
        static Entry Term(string k, string t) { Entry e = new Entry(); e.Kind = Kind.Term; e.Key = k; e.Text = t; return e; }

        void BuildContent()
        {
            _columns[0] = new List<Entry>
            {
                Section("Navigation & Views"),
                Key("F1", "Toggle this help dialog"),
                Key("Ctrl 1 .. 3", "Home / Sets / Plugins"),
                Key("F", "Open filters dialog"),
                Key("Shift F", "Open scan folders window"),
                Key("Ctrl F", "Focus search field"),
                Key("Ctrl ,", "Open settings"),
                Key("F11", "Toggle fullscreen"),
                Key("Ctrl M", "Minimize window"),
                Key("Ctrl Q", "Quit application"),
            };

            _columns[1] = new List<Entry>
            {
                Section("Actions & Controls"),
                Key("Enter", "Open set in Live"),
                Key("Shift Enter", "Open set in Explorer"),
                Key("Space", "Play or pause audio render"),
                Key("Ctrl Space", "Open or close set preview"),
                Key("Q", "Pin or unpin selected set"),
                Key("Ctrl T", "Edit set tags and notes"),
                Key("Ctrl R", "Rescue a set that will not open"),
                Key("F5", "Rescan catalog"),
                Key("Ctrl N", "Launch Live"),
            };
        }
    }
}
