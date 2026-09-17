using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Сводка по плагинам карточками над списком. Карточка — это ещё и фильтр: клик по
    /// «не установлено» оставляет в списке ровно те плагины, о которых карточка говорит,
    /// иначе число само по себе ничего не даёт.
    /// </summary>
    public sealed class PluginSummary : GlassControl
    {
        public const int Total = 0, Installed = 1, Missing = 2, Unused = 3;

        sealed class Card
        {
            public string Label = "";
            public string Value = "";
            public Color Color;
            public Rectangle Rect;
        }

        readonly List<Card> _cards = new List<Card>();
        int _hot = -1;

        /// <summary>Какая карточка сейчас задаёт фильтр списка; -1 — никакая.</summary>
        public int Selected = -1;

        public event Action<int> CardClicked;

        public const int PreferredHeight = 50;    // однострочные карточки

        public PluginSummary() { Cursor = Cursors.Default; Height = PreferredHeight; }

        public void Update(PluginHealth h)
        {
            _cards.Clear();
            Add("Used in sets", h.Used, Theme.Text);
            Add("Installed", h.InstalledTotal, Theme.Green);
            Add("Not installed", h.Missing,
                h.Missing > 0 ? Theme.Red : Theme.Text);
            Add("Never used", h.InstalledUnused, Theme.TextDim);
            PlaceCards();
            Invalidate();
        }

        void Add(string label, int value, Color color)
        {
            Card c = new Card();
            c.Label = label;
            c.Value = value.ToString();
            c.Color = color;
            _cards.Add(c);
        }

        void PlaceCards()
        {
            if (_cards.Count == 0) return;
            int gap = Sc(10);
            int w = (Width - gap * (_cards.Count - 1)) / _cards.Count;
            int x = 0;
            foreach (Card c in _cards)
            {
                c.Rect = new Rectangle(x, 0, w, Height);
                x += w + gap;
            }
        }

        protected override void OnResize(EventArgs e) { PlaceCards(); base.OnResize(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hot = -1;
            for (int i = 0; i < _cards.Count; i++) if (_cards[i].Rect.Contains(e.Location)) hot = i;
            if (hot != _hot)
            {
                _hot = hot;
                Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot >= 0) { _hot = -1; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].Rect.Contains(e.Location) && CardClicked != null) { CardClicked(i); return; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            for (int i = 0; i < _cards.Count; i++)
            {
                Card c = _cards[i];
                bool on = i == Selected;
                Theme.PaintGlassSurface(this, g, c.Rect, Sc(Theme.CardR),
                                on ? Theme.GlassSurfacePressedAlpha : i == _hot ? Theme.GlassSurfaceHotAlpha : Theme.GlassSurfaceAlpha);

                // Число и подпись в одну строку, не столбиком — значение по своей
                // ширине, подпись сразу за ним.
                Rectangle content = new Rectangle(c.Rect.X + Sc(18), c.Rect.Y,
                                                  Math.Max(0, c.Rect.Width - Sc(26)), c.Rect.Height);
                Size vs = TextRenderer.MeasureText(c.Value, Theme.FHead);
                Rectangle vr = new Rectangle(content.X, content.Y,
                                             Math.Min(vs.Width + Sc(4), content.Width), content.Height);
                Chrome.DrawText(g, c.Value, Theme.FHead, vr, c.Color, Chrome.Left);

                Rectangle lr = new Rectangle(vr.Right + Sc(8), content.Y,
                                             Math.Max(0, content.Right - vr.Right - Sc(8)), content.Height);
                Chrome.DrawText(g, c.Label, Theme.FLabel, lr, Theme.TextDim, Chrome.Left);
            }
        }
    }
}
