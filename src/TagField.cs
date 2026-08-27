using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Поле-«теги»: выбранные значения лежат чипами, клик по пустому месту открывает
    /// список остальных, клик по чипу убирает его. Нужно там, где выбирают не одно
    /// значение из списка, а сразу несколько — например, полдюжины тональностей.
    /// </summary>
    public class TagField : GlassControl
    {
        public readonly List<string> Options = new List<string>();
        public readonly List<int> Selected = new List<int>();
        public readonly HashSet<int> DisabledOptions = new HashSet<int>();
        public string Placeholder = "";

        public event EventHandler Changed;

        readonly List<Rectangle> _chips = new List<Rectangle>();
        int _hotChip = -1;

        protected override float PillRadius { get { return Sc(12); } }

        public TagField()
        {
            Cursor = Cursors.Hand;
            // На FLabel текст в узкой пилюле обрезался — шрифтом на размер меньше
            // он гарантированно влезает вместе с крестиком.
            Font = Theme.FBadge;
            Height = 34;
        }

        public void SetOptions(IEnumerable<string> options)
        {
            Options.Clear();
            Options.AddRange(options);
            Selected.RemoveAll(delegate (int i) { return i < 0 || i >= Options.Count; });
            Invalidate();
        }

        public void SetSelected(IEnumerable<int> values)
        {
            Selected.Clear();
            foreach (int i in values)
                if (i >= 0 && i < Options.Count && !Selected.Contains(i)) Selected.Add(i);
            Selected.Sort();
            Invalidate();
        }

        string Label(int i) { return i >= 0 && i < Options.Count ? Options[i] : ""; }

        int ChipH { get { return Sc(24); } }
        int Pad { get { return Sc(6); } }
        int Gap { get { return Sc(5); } }

        // Отступ текста от края пилюли и отдельная зона под крестик справа — если
        // и то и другое не заложить в ширину чипа заранее, длинные подписи вроде
        // «Super Locrian» упираются в крестик и обрезаются по EndEllipsis.
        int ChipPadX { get { return Sc(10); } }
        int ChipCrossZone { get { return Sc(16); } }

        static readonly TextFormatFlags PillText =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        /// <summary>Раскладывает чипы по ширине и возвращает высоту, которая для этого нужна.</summary>
        public int Relayout()
        {
            _chips.Clear();
            int x = Pad, y = Pad, right = Width - Pad - Sc(20);   // справа место под шеврон
            for (int k = 0; k < Selected.Count; k++)
            {
                // Мерим тем же шрифтом и с тем же NoPrefix, которым потом рисуем — иначе
                // ширина и фактический рендер расходятся на пару пикселей и текст режется.
                int textW = TextRenderer.MeasureText(Label(Selected[k]), Font,
                    new Size(short.MaxValue, ChipH), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                int w = textW + ChipPadX * 2 + ChipCrossZone;
                if (w > Math.Max(Sc(40), right - Pad)) w = Math.Max(Sc(40), right - Pad);
                if (x > Pad && x + w > right) { x = Pad; y += ChipH + Gap; }
                _chips.Add(new Rectangle(x, y, w, ChipH));
                x += w + Gap;
            }
            return Math.Max(Sc(34), y + ChipH + Pad);
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        // ------------------------------------------------------------------- мышь

        int ChipAt(Point p)
        {
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ChipAt(e.Location);
            if (i != _hotChip) { _hotChip = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hotChip >= 0) { _hotChip = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int chip = ChipAt(e.Location);
            if (chip >= 0)
            {
                Selected.RemoveAt(chip);
                Fire();
                return;
            }
            ShowMenu();
        }

        void ShowMenu()
        {
            ContextMenuStrip menu = DarkMenu.Create();
            int added = 0;
            for (int i = 0; i < Options.Count; i++)
            {
                if (Selected.Contains(i)) continue;
                int captured = i;
                ToolStripMenuItem mi = new ToolStripMenuItem(Options[i]);
                if (DisabledOptions.Contains(i)) mi.Enabled = false;
                mi.Click += delegate
                {
                    if (!Selected.Contains(captured)) { Selected.Add(captured); Selected.Sort(); }
                    Fire();
                };
                menu.Items.Add(mi);
                added++;
            }
            if (added == 0 && Selected.Count == 0) return;

            if (Selected.Count > 0)
            {
                if (added > 0) menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem clear = new ToolStripMenuItem(L.S("Clear", "Очистить"));
                clear.Click += delegate { Selected.Clear(); Fire(); };
                menu.Items.Add(clear);
            }

            menu.Closed += delegate { Pressed = false; Invalidate(); };
            menu.Show(this, new Point(0, Height + 4));
        }

        void Fire()
        {
            Relayout();
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // -------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            if (_chips.Count != Selected.Count) Relayout();

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, r, Sc(12), Hot ? Theme.GlassSurfaceHotAlpha : Theme.GlassSurfaceAlpha);

            if (Selected.Count == 0)
                Chrome.DrawText(g, Placeholder, Font,
                    new Rectangle(Sc(16), 0, Width - Sc(40), Sc(Theme.ControlH)), Theme.TextDim, Chrome.Left);

            for (int i = 0; i < _chips.Count && i < Selected.Count; i++)
            {
                Rectangle c = _chips[i];
                bool hot = i == _hotChip;
                Theme.FillRound(g, c, c.Height / 2f, hot ? Theme.Red : Theme.Light);

                // Текст центрируем в зоне без крестика: не липнет к левому краю и не
                // заходит на крестик у правого.
                Rectangle tr = new Rectangle(c.X + ChipPadX, c.Y, c.Width - ChipPadX - ChipCrossZone, c.Height);
                Chrome.DrawText(g, Label(Selected[i]), Font, tr,
                               hot ? Color.White : Theme.OnLight, PillText);

                // Крестик у правого края чипа: клик по чипу его же и убирает.
                float cx = c.Right - Sc(10), cy = c.Y + c.Height / 2f, s = Sc(3);
                using (Pen p = new Pen(hot ? Color.White : Theme.OnLight, 1.4f))
                {
                    g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                }
            }

            Chrome.Chevron(g, Width - Sc(17), Sc(18), Sc(6), Theme.TextDim);
        }
    }
}
