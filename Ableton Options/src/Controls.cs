using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonOptions
{
    public static class Chrome
    {
        /// <summary>
        /// Каждый контрол сам рисует свой кусочек размытой подложки и заливку карточки.
        /// Так не нужна возня с прозрачностью WinForms, и стыки сходятся пиксель в пиксель.
        /// </summary>
        public static void PaintGlass(Control c, Graphics g)
        {
            PaintGlass(c, g, c.ClientRectangle);
        }

        /// <summary>Рисует подложку только в пределах clip — при точечной перерисовке.</summary>
        public static void PaintGlass(Control c, Graphics g, Rectangle clip)
        {
            clip.Intersect(c.ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            Backdrop.Paint(g, clip, c.RectangleToScreen(clip));
            using (SolidBrush b = new SolidBrush(Theme.CardFill))
                g.FillRectangle(b, clip);
        }

        public static readonly TextFormatFlags Left =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis;

        public static readonly TextFormatFlags Center =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

        public static readonly TextFormatFlags Wrap =
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        public static void Chevron(Graphics g, float cx, float cy, float size, Color color)
        {
            using (Pen p = new Pen(color, 1.5f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLines(p, new PointF[] {
                    new PointF(cx - size, cy - size * 0.5f),
                    new PointF(cx,        cy + size * 0.5f),
                    new PointF(cx + size, cy - size * 0.5f) });
            }
        }
    }

    /// <summary>База для всех наших контролов: без мигания, с подложкой и hover-состоянием.</summary>
    public class GlassControl : Control
    {
        protected bool Hot, Pressed;

        public GlassControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.FButton;
            ForeColor = Theme.Text;
        }

        protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    }

    public class GlassButton : GlassControl
    {
        public bool Primary;
        public bool Quiet;   // без рамки и заливки — как текстовая ссылка

        public GlassButton()
        {
            Cursor = Cursors.Hand;
            Height = 32;
        }

        public void FitToText(int hPadding)
        {
            Size s = TextRenderer.MeasureText(Text, Font);
            Width = s.Width + hPadding * 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Color fill, border, text;

            if (Primary)
            {
                fill = Pressed ? Color.FromArgb(0xFF, 0x06, 0x6A, 0xD0)
                     : Hot     ? Color.FromArgb(0xFF, 0x2B, 0x96, 0xFF)
                               : Theme.Blue;
                border = Color.FromArgb(0x00, 0, 0, 0);
                text = Color.White;
            }
            else if (Quiet)
            {
                fill = Hot ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0, 0, 0, 0);
                border = Color.FromArgb(0, 0, 0, 0);
                text = Hot ? Theme.Text : Theme.TextDim;
            }
            else
            {
                fill = Pressed ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                     : Hot     ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)
                               : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
                border = Theme.FieldBorder;
                text = Theme.Text;
            }

            Theme.FillRound(g, r, Height / 2f, fill);
            Theme.DrawRound(g, r, Height / 2f, border, 1f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, Chrome.Center);
        }
    }

    /// <summary>Круглая кнопка с крестиком — закрыть окно.</summary>
    public class CloseButton : GlassControl
    {
        public CloseButton() { Size = new Size(30, 30); Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Color fill = Pressed ? Color.FromArgb(0xFF, 0xC0, 0x30, 0x28)
                       : Hot     ? Theme.Red
                                 : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
            Theme.FillRound(g, r, Height / 2f, fill);

            float c = Width / 2f, s = 4.5f;
            using (Pen p = new Pen(Hot ? Color.White : Theme.TextDim, 1.5f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, c - s, c - s, c + s, c + s);
                g.DrawLine(p, c + s, c - s, c - s, c + s);
            }
        }
    }

    /// <summary>Переключатель-«таблетка» для фильтров.</summary>
    public class PillToggle : GlassControl
    {
        bool _checked;
        public event EventHandler CheckedChanged;

        public PillToggle() { Cursor = Cursors.Hand; Height = 30; Font = Theme.FLabel; }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value; Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void FitToText() { Width = TextRenderer.MeasureText(Text, Font).Width + 30; }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Color fill = _checked
                ? Color.FromArgb(Hot ? 0x50 : 0x3A, Theme.Blue)
                : Color.FromArgb(Hot ? 0x16 : 0x0A, 0xFF, 0xFF, 0xFF);
            Color border = _checked ? Color.FromArgb(0x80, Theme.Blue) : Theme.FieldBorder;

            Theme.FillRound(g, r, Height / 2f, fill);
            Theme.DrawRound(g, r, Height / 2f, border, 1f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                                  _checked ? Theme.Text : Theme.TextDim, Chrome.Center);
        }
    }

    /// <summary>Сегментированный переключатель (EN | RU).</summary>
    public class Segmented : GlassControl
    {
        string[] _items = new string[0];
        int _index;
        int _hotIndex = -1;

        public event EventHandler SelectedChanged;

        public Segmented() { Height = 28; Cursor = Cursors.Hand; Font = Theme.FLabel; }

        public void SetItems(params string[] items)
        {
            _items = items;
            int w = 8;
            foreach (string s in items) w += Math.Max(34, TextRenderer.MeasureText(s, Font).Width + 18);
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
            float w = (Width - 8f) / Math.Max(1, _items.Length);
            return new RectangleF(4 + i * w, 4, w, Height - 8);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = -1;
            for (int i = 0; i < _items.Length; i++) if (SegRect(i).Contains(e.Location)) idx = i;
            if (idx != _hotIndex) { _hotIndex = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < _items.Length; i++)
                if (SegRect(i).Contains(e.Location)) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, r, Height / 2f, Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            Theme.DrawRound(g, r, Height / 2f, Theme.FieldBorder, 1f);

            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF sr = SegRect(i);
                bool sel = i == _index;
                if (sel)
                    Theme.FillRound(g, sr, sr.Height / 2f, Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                else if (i == _hotIndex)
                    Theme.FillRound(g, sr, sr.Height / 2f, Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

                TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(sr),
                                      sel ? Theme.Text : Theme.TextFaint, Chrome.Center);
            }
        }
    }

    /// <summary>Поле ввода со скруглённой рамкой; внутри живёт обычный TextBox без рамки.</summary>
    public class FieldBox : GlassControl
    {
        public readonly TextBox Box = new TextBox();
        public string Glyph;      // необязательный значок слева (например, лупа)
        bool _focused;

        public FieldBox()
        {
            Height = 34;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1E);
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.FBody;
            Box.GotFocus += delegate { _focused = true; Invalidate(); };
            Box.LostFocus += delegate { _focused = false; Invalidate(); };
            Controls.Add(Box);
        }

        protected override void OnResize(EventArgs e)
        {
            int left = string.IsNullOrEmpty(Glyph) ? 13 : 32;
            Box.SetBounds(left, (Height - Box.PreferredHeight) / 2 + 1,
                          Math.Max(20, Width - left - 12), Box.PreferredHeight);
            base.OnResize(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, r, 9f, Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1E));
            Theme.DrawRound(g, r, 9f, _focused ? Color.FromArgb(0x90, Theme.Blue) : Theme.FieldBorder, 1f);

            if (!string.IsNullOrEmpty(Glyph))
                TextRenderer.DrawText(g, Glyph, Theme.FBody, new Rectangle(11, 0, 20, Height),
                                      Theme.TextFaint, Chrome.Center);
        }
    }

    /// <summary>Выпадающий список: скруглённое поле + тёмное меню.</summary>
    public class DropField : GlassControl
    {
        readonly List<string> _items = new List<string>();
        int _index = -1;

        public event EventHandler SelectedChanged;
        public string Label;   // подпись слева тусклым

        public DropField() { Height = 34; Cursor = Cursors.Hand; Font = Theme.FBody; }

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
            Chrome.PaintGlass(this, g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, r, 9f, Hot ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                                         : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Theme.DrawRound(g, r, 9f, Theme.FieldBorder, 1f);

            int x = 13;
            if (!string.IsNullOrEmpty(Label))
            {
                Size ls = TextRenderer.MeasureText(Label, Theme.FLabel);
                TextRenderer.DrawText(g, Label, Theme.FLabel, new Rectangle(x, 0, ls.Width, Height),
                                      Theme.TextFaint, Chrome.Left);
                x += ls.Width + 8;
            }

            Rectangle tr = new Rectangle(x, 0, Math.Max(10, Width - x - 30), Height);
            TextRenderer.DrawText(g, SelectedItem, Font, tr, Theme.Text, Chrome.Left);
            Chrome.Chevron(g, Width - 16, Height / 2f - 1, 4f, Theme.TextDim);
        }
    }

    public static class DarkMenu
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.BackColor = Color.FromArgb(0xFF, 0x22, 0x22, 0x26);
            m.ForeColor = Theme.Text;
            m.Font = Theme.FBody;
            m.ShowImageMargin = false;
            m.DropShadowEnabled = true;
            return m;
        }

        class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF); } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
            public override Color MenuItemBorder { get { return MenuItemSelected; } }
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(0xFF, 0x22, 0x22, 0x26); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(0xFF, 0x22, 0x22, 0x26); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(0xFF, 0x22, 0x22, 0x26); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(0xFF, 0x22, 0x22, 0x26); } }
            public override Color MenuBorder { get { return Color.FromArgb(0xFF, 0x3A, 0x3A, 0x40); } }
        }

        class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? Color.White : Theme.Text;
                base.OnRenderItemText(e);
            }
        }
    }
}
