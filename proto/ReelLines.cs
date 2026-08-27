using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AbletonManager;

namespace Reel
{
    internal static class ReelTheme
    {
        /// <summary>
        /// Alive держит две сигнальные краски — Green и Red. Разнице нужна третья:
        /// «изменилось» — это не находка и не потеря. Собрана тем же способом, что и
        /// те две (одна компонента 0xE1, остальные приглушены), чтобы стоять с ними в
        /// одном ряду, а не выделяться чужим тоном.
        /// </summary>
        public static readonly Color Amber = Color.FromArgb(0xFF, 0xE1, 0xC1, 0x61);
    }

    /// <summary>Строка в списке разницы или зависимостей.</summary>
    internal sealed class LineItem
    {
        public string Text = "";
        public string Trailing = "";   // приглушённый хвост: путь, формат, счётчик
        public Color Color;
        public Font Font;
        public int Indent;
        public bool Rule;              // волосяная линия над строкой — начало блока

        /// <summary>
        /// Цвет точки перед текстом; прозрачный — точки нет. Отдельно от цвета текста
        /// намеренно: у найденной зависимости точка зелёная, а текст обычный. Красить
        /// зелёным ещё и текст — значит превратить список из двадцати найденных файлов
        /// в ёлку, на которой не видно двух красных строк, ради которых его открыли.
        /// </summary>
        public Color DotColor = Color.Transparent;

        public LineItem(string text, Color color) { Text = text; Color = color; }
    }

    /// <summary>
    /// Список цветных строк: разница между версиями и отчёт о зависимостях.
    ///
    /// Не RowListView: там колонки, сортировка, перетаскивание краёв и шапка — всё то,
    /// чего этим двум панелям не нужно вовсе. Зато нужны отступы вложенности, цвет на
    /// строку и волосяная линия между блоками. Рисуется теми же Theme и Chrome, что и
    /// весь Alive, поэтому выглядит частью того же окна, а не вставкой.
    /// </summary>
    internal sealed class ReelLines : GlassControl
    {
        readonly List<LineItem> _items = new List<LineItem>();
        int _scroll;

        public string EmptyText = "";

        public ReelLines()
        {
            Font = Theme.FBody;
            Surface = Theme.Backdrop;
        }

        int RowH { get { return Sc(26); } }
        int PadX { get { return Sc(2); } }

        public void SetItems(IEnumerable<LineItem> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            _scroll = 0;
            Invalidate();
        }

        public void Clear() { _items.Clear(); _scroll = 0; Invalidate(); }

        int ContentHeight { get { return _items.Count * RowH; } }
        int MaxScroll { get { return Math.Max(0, ContentHeight - Height + Sc(6)); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int before = _scroll;
            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - e.Delta / 120 * RowH * 3));
            if (_scroll != before) Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_scroll > MaxScroll) _scroll = MaxScroll;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            if (_items.Count == 0)
            {
                if (EmptyText.Length > 0)
                    Chrome.DrawText(g, EmptyText, Theme.FBody,
                                    new Rectangle(PadX, 0, Width - PadX * 2, RowH),
                                    Theme.TextDim, Chrome.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            int first = Math.Max(0, _scroll / RowH);
            int last = Math.Min(_items.Count - 1, (_scroll + Height) / RowH);

            for (int i = first; i <= last; i++)
            {
                LineItem it = _items[i];
                int top = i * RowH - _scroll;

                if (it.Rule && i > 0)
                    using (Pen p = new Pen(Theme.Hairline))
                        g.DrawLine(p, PadX, top, Width - PadX, top);

                int x = PadX + Sc(it.Indent * 18);
                Font font = it.Font ?? Theme.FBody;

                if (it.DotColor.A > 0)
                {
                    int d = Sc(7);
                    Rectangle dot = new Rectangle(x + Sc(2), top + (RowH - d) / 2, d, d);
                    Theme.FillRound(g, dot, d / 2f, it.DotColor);
                    x += Sc(16);
                }

                Rectangle tr = new Rectangle(x, top, Math.Max(0, Width - x - PadX), RowH);
                Chrome.DrawText(g, it.Text, font, tr, it.Color,
                                Chrome.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (it.Trailing.Length == 0) continue;

                // Хвост ставим за текстом, а не по правому краю: строки разной длины,
                // и прижатый вправо путь висел бы отдельно от того, к чему относится.
                int used = TextRenderer.MeasureText(g, it.Text, font).Width;
                int tx = x + used + Sc(12);
                // Полоса ползунка справа — своя, плюс запас: GDI при PathEllipsis
                // укладывается в отведённое лишь примерно, и без запаса хвост пути
                // вылезал за край панели.
                int tw = Width - tx - Sc(14);
                if (tw < Sc(60)) continue;

                Chrome.DrawText(g, it.Trailing, Theme.FSmall, new Rectangle(tx, top, tw, RowH),
                                Theme.TextDim,
                                Chrome.Left | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.PathEllipsis);
            }

            PaintScrollBar(g);
        }

        /// <summary>Тот же тонкий ползунок, что у таблицы Alive: виден, только когда есть что прокручивать.</summary>
        void PaintScrollBar(Graphics g)
        {
            int max = MaxScroll;
            if (max <= 0) return;

            int barW = Sc(4);
            int trackH = Height;
            int thumbH = Math.Max(Sc(30), (int)((float)trackH * trackH / Math.Max(1, ContentHeight)));
            int y = (int)((float)_scroll / max * (trackH - thumbH));

            Rectangle thumb = new Rectangle(Width - barW - Sc(2), y, barW, thumbH);
            Theme.FillRound(g, thumb, barW / 2f, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }
    }
}
