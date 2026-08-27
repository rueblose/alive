using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AbletonOptions
{
    public sealed class Row
    {
        public Opt O;
        public bool On;
        public string Val = "";
        public bool Shown = true;

        public int Y, H;
        public Rectangle Value;   // область поля значения, в координатах строки

        public bool HasValue { get { return O.Kind != Kind.Flag; } }

        /// <summary>Live на этой машине уже отверг эту опцию — видно из его Log.txt.</summary>
        public bool RejectedByLive;

        // Кеш обмера: пересчитывать 160 строк на каждое нажатие в поиске незачем.
        public int CacheW = -1;
        public bool CacheRu;
        public string CacheLine, Hint;
        public int CacheH;

        public string Line()
        {
            if (O.Kind == Kind.Flag || string.IsNullOrEmpty(Val)) return "-" + O.Name;
            return "-" + O.Name + "=" + Val;
        }
    }

    /// <summary>
    /// Список опций целиком рисуется своими руками: 161 строка как отдельные контролы
    /// WinForms — это тормоза и чужой внешний вид. Здесь же — только одно поле ввода,
    /// которое подставляется под строку в момент редактирования.
    /// </summary>
    public sealed class OptionListView : GlassControl
    {
        readonly List<Row> _rows = new List<Row>();
        readonly TextBox _edit = new TextBox();

        int _scroll, _contentH, _hot = -1, _editRow = -1, _lastWidth = -1;
        bool _draggingBar;
        int _dragOffset;

        public event EventHandler Changed;

        public List<Row> Rows { get { return _rows; } }

        public OptionListView()
        {
            Cursor = Cursors.Hand;

            _edit.BorderStyle = BorderStyle.None;
            _edit.BackColor = Color.FromArgb(0xFF, 0x24, 0x24, 0x29);
            _edit.ForeColor = Theme.Text;
            _edit.Font = Theme.FBody;
            _edit.TextAlign = HorizontalAlignment.Right;
            _edit.Visible = false;
            _edit.KeyDown += OnEditKey;
            _edit.LostFocus += delegate { CommitEdit(); };
            Controls.Add(_edit);

            for (int i = 0; i < Catalog.All.Count; i++)
            {
                Row r = new Row();
                r.O = Catalog.All[i];
                r.Val = r.O.Def == null ? "" : r.O.Def;
                _rows.Add(r);
            }
        }

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        // ------------------------------------------------------------ раскладка

        public void Relayout(bool force)
        {
            if (!force && Width == _lastWidth) return;
            _lastWidth = Width;
            CancelEdit();

            int padX = Sc(20), padY = Sc(15), gap = Sc(4);
            int checkSize = Sc(19);
            int textLeft = padX + checkSize + Sc(13);
            int barGutter = Sc(14);

            using (Graphics g = CreateGraphics())
            {
                int y = Sc(6);
                for (int i = 0; i < _rows.Count; i++)
                {
                    Row r = _rows[i];
                    if (!r.Shown) { r.H = 0; continue; }

                    int valueW = r.O.Kind == Kind.Flag ? 0
                               : r.O.Kind == Kind.Num ? Sc(104) : Sc(172);
                    int valueH = Sc(30);
                    int right = Width - padX - barGutter;

                    r.Value = valueW == 0 ? Rectangle.Empty
                            : new Rectangle(right - valueW, padY - Sc(4), valueW, valueH);

                    string line = r.Line();
                    if (r.CacheW != Width || r.CacheRu != L.Ru || r.CacheLine != line)
                    {
                        int titleH = TextRenderer.MeasureText(g, r.O.Title, Theme.FTitle).Height;
                        int head = padY + Math.Max(titleH, valueW == 0 ? 0 : valueH - Sc(4));

                        int textW = Math.Max(Sc(160), right - textLeft);
                        int h = head + gap;
                        h += Measure(g, line, Theme.FMono, textW) + Sc(5);
                        h += Measure(g, r.O.Desc, Theme.FBody, textW);

                        r.Hint = BuildHint(r.O);
                        if (r.Hint.Length > 0) h += Sc(5) + Measure(g, r.Hint, Theme.FSmall, textW);
                        if (!string.IsNullOrEmpty(r.O.Note))
                            h += Sc(5) + Measure(g, "!  " + r.O.Note, Theme.FSmall, textW);

                        r.CacheW = Width; r.CacheRu = L.Ru; r.CacheLine = line;
                        r.CacheH = h + padY;
                    }

                    r.Y = y;
                    r.H = r.CacheH;
                    y += r.H + Sc(2);
                }
                _contentH = y + Sc(6);
            }

            ClampScroll();
            Invalidate();
        }

        static int Measure(Graphics g, string text, Font f, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(g, text, f, new Size(width, int.MaxValue), Chrome.Wrap).Height;
        }

        static string BuildHint(Opt o)
        {
            if (o.Kind == Kind.Flag) return "";
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(o.Def)) parts.Add(L.S("default ", "по умолчанию ") + o.Def);
            if (!string.IsNullOrEmpty(o.Min) && !string.IsNullOrEmpty(o.Max))
                parts.Add(L.S("range ", "диапазон ") + o.Min + "–" + o.Max);
            else if (!string.IsNullOrEmpty(o.Min)) parts.Add(L.S("min ", "минимум ") + o.Min);
            else if (!string.IsNullOrEmpty(o.Max)) parts.Add(L.S("max ", "максимум ") + o.Max);
            if (o.Kind == Kind.Choice && o.Choices != null)
                parts.Add(L.S("options: ", "варианты: ") + string.Join(", ", o.Choices));
            return parts.Count == 0 ? "" : string.Join("  ·  ", parts);
        }

        protected override void OnResize(EventArgs e) { Relayout(false); base.OnResize(e); }

        // ------------------------------------------------------------ прокрутка

        void ClampScroll()
        {
            int max = Math.Max(0, _contentH - Height);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            CommitEdit();
            _scroll -= (int)(e.Delta / 120f * Sc(72));
            ClampScroll();
            Invalidate();
            base.OnMouseWheel(e);
        }

        Rectangle BarRect()
        {
            if (_contentH <= Height) return Rectangle.Empty;
            int track = Height - Sc(12);
            int h = Math.Max(Sc(40), (int)(track * (float)Height / _contentH));
            int max = Math.Max(1, _contentH - Height);
            int y = Sc(6) + (int)((track - h) * (_scroll / (float)max));
            return new Rectangle(Width - Sc(10), y, Sc(5), h);
        }

        // ---------------------------------------------------------------- ввод

        int RowAt(int clientY)
        {
            int y = clientY + _scroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (!r.Shown) continue;
                if (y >= r.Y && y < r.Y + r.H) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingBar)
            {
                int track = Height - Sc(12);
                Rectangle bar = BarRect();
                int max = Math.Max(1, _contentH - Height);
                float t = (e.Y - _dragOffset - Sc(6)) / (float)Math.Max(1, track - bar.Height);
                _scroll = (int)(t * max);
                ClampScroll();
                Invalidate();
                return;
            }

            int idx = RowAt(e.Y);
            if (idx != _hot)
            {
                // Перерисовываем только две затронутые строки, а не весь список.
                InvalidateRow(_hot);
                InvalidateRow(idx);
                _hot = idx;
            }

            bool overValue = false;
            if (idx >= 0)
            {
                Row r = _rows[idx];
                if (r.HasValue && r.On)
                {
                    Rectangle v = r.Value; v.Offset(0, r.Y - _scroll);
                    overValue = v.Contains(e.Location);
                }
            }
            Cursor = overValue ? Cursors.IBeam : Cursors.Hand;
            base.OnMouseMove(e);
        }

        void InvalidateRow(int idx)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            Row r = _rows[idx];
            Invalidate(new Rectangle(0, r.Y - _scroll - 1, Width, r.H + 2));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1) { InvalidateRow(_hot); _hot = -1; }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();

            Rectangle bar = BarRect();
            if (!bar.IsEmpty && new Rectangle(bar.X - Sc(6), bar.Y, bar.Width + Sc(10), bar.Height).Contains(e.Location))
            {
                _draggingBar = true;
                _dragOffset = e.Y - bar.Y;
                return;
            }

            int idx = RowAt(e.Y);
            if (idx < 0) { CommitEdit(); return; }
            Row r = _rows[idx];

            if (r.HasValue && r.On)
            {
                Rectangle v = r.Value; v.Offset(0, r.Y - _scroll);
                if (v.Contains(e.Location))
                {
                    if (r.O.Kind == Kind.Choice) ShowChoices(idx, v);
                    else BeginEdit(idx, v);
                    return;
                }
            }

            CommitEdit();
            r.On = !r.On;
            InvalidateRow(idx);
            Raise();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _draggingBar = false; base.OnMouseUp(e); }

        void ShowChoices(int idx, Rectangle screenRect)
        {
            Row r = _rows[idx];
            ContextMenuStrip menu = DarkMenu.Create();
            foreach (string choice in r.O.Choices)
            {
                string captured = choice;
                ToolStripMenuItem mi = new ToolStripMenuItem(choice);
                mi.Checked = choice == r.Val;
                mi.Click += delegate { r.Val = captured; Raise(); Invalidate(); };
                menu.Items.Add(mi);
            }
            menu.Show(this, new Point(screenRect.Left, screenRect.Bottom + 2));
        }

        void BeginEdit(int idx, Rectangle rect)
        {
            CommitEdit();
            _editRow = idx;
            _edit.Text = _rows[idx].Val;
            _edit.SetBounds(rect.X + Sc(9), rect.Y + (rect.Height - _edit.PreferredHeight) / 2,
                            rect.Width - Sc(18), _edit.PreferredHeight);
            _edit.Visible = true;
            _edit.BringToFront();
            _edit.Focus();
            _edit.SelectAll();
        }

        void OnEditKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { CommitEdit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CancelEdit(); e.SuppressKeyPress = true; }
        }

        void CommitEdit()
        {
            if (_editRow < 0) return;
            int idx = _editRow;
            _editRow = -1;
            _edit.Visible = false;
            string v = _edit.Text.Trim();
            if (_rows[idx].Val != v) { _rows[idx].Val = v; Raise(); }
            Invalidate();
        }

        void CancelEdit()
        {
            _editRow = -1;
            _edit.Visible = false;
            Invalidate();
        }

        void Raise() { if (Changed != null) Changed(this, EventArgs.Empty); }

        // ------------------------------------------------------------ проверка

        public string Validate()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (!r.On || r.O.Kind != Kind.Num) continue;

                if (r.Val.Length == 0)
                    return Fmt(L.S("“{0}” needs a number.", "«{0}»: нужно указать число."), r.O.Title);

                double d;
                if (!double.TryParse(r.Val, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return Fmt(L.S("“{0}”: “{1}” is not a number (use a dot as the decimal separator).",
                                   "«{0}»: «{1}» не число (десятичный разделитель — точка)."), r.O.Title, r.Val);

                double bound;
                if (!string.IsNullOrEmpty(r.O.Min) &&
                    double.TryParse(r.O.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out bound) && d < bound)
                    return Fmt(L.S("“{0}” is below the minimum of {1}.",
                                   "«{0}»: значение меньше минимума {1}."), r.O.Title, r.O.Min);

                if (!string.IsNullOrEmpty(r.O.Max) &&
                    double.TryParse(r.O.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out bound) && d > bound)
                    return Fmt(L.S("“{0}” is above the maximum of {1}.",
                                   "«{0}»: значение больше максимума {1}."), r.O.Title, r.O.Max);
            }
            return null;
        }

        static string Fmt(string f, params object[] a) { return string.Format(f, a); }

        // ------------------------------------------------------------ отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintGlass(this, g, e.ClipRectangle);
            Theme.Smooth(g);

            int padX = Sc(20), padY = Sc(15), gap = Sc(4);
            int checkSize = Sc(19);
            int textLeft = padX + checkSize + Sc(13);
            int barGutter = Sc(14);
            int right = Width - padX - barGutter;
            int textW = Math.Max(Sc(160), right - textLeft);

            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (!r.Shown) continue;

                int top = r.Y - _scroll;
                if (top + r.H < 0) continue;
                if (top > Height) break;

                Rectangle rr = new Rectangle(Sc(8), top, Width - Sc(16) - barGutter + Sc(8), r.H);
                if (r.On)
                {
                    Theme.FillRound(g, rr, Sc(12), Theme.RowOn);
                    Theme.DrawRound(g, rr, Sc(12), Color.FromArgb(0x3C, Theme.Blue), 1f);
                }
                else if (i == _hot)
                    Theme.FillRound(g, rr, Sc(12), Theme.RowHover);

                // чекбокс
                Rectangle cb = new Rectangle(padX, top + padY + Sc(1), checkSize, checkSize);
                if (r.On)
                {
                    Theme.FillRound(g, cb, Sc(6), Theme.Blue);
                    using (Pen p = new Pen(Color.White, Sc(2)))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawLines(p, new PointF[] {
                            new PointF(cb.Left + checkSize * 0.26f, cb.Top + checkSize * 0.52f),
                            new PointF(cb.Left + checkSize * 0.44f, cb.Top + checkSize * 0.70f),
                            new PointF(cb.Left + checkSize * 0.75f, cb.Top + checkSize * 0.31f) });
                    }
                }
                else
                {
                    Theme.FillRound(g, cb, Sc(6), Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
                    Theme.DrawRound(g, cb, Sc(6), i == _hot ? Theme.TextDim : Theme.TextFaint, 1.2f);
                }

                // заголовок и бейджи
                Size ts = TextRenderer.MeasureText(g, r.O.Title, Theme.FTitle);
                int titleMax = right - textLeft - (r.Value.IsEmpty ? 0 : r.Value.Width + Sc(16));
                Rectangle tr = new Rectangle(textLeft, top + padY - Sc(2),
                                             Math.Min(ts.Width, Math.Max(Sc(80), titleMax)), ts.Height + Sc(4));
                TextRenderer.DrawText(g, r.O.Title, Theme.FTitle, tr,
                                      r.On ? Theme.Text : Color.FromArgb(0xF0, Theme.Text), Chrome.Left);

                int bx = tr.Right + Sc(9);
                if (r.O.Stat != Stat.Ok)
                    bx = Badge(g, bx, tr.Top + Sc(3), Catalog.StatName(r.O.Stat), Theme.Accent(r.O.Stat));
                if (!string.IsNullOrEmpty(r.O.Plat))
                    bx = Badge(g, bx, tr.Top + Sc(3), r.O.Plat, Theme.Purple);
                if (r.RejectedByLive)
                    bx = Badge(g, bx, tr.Top + Sc(3),
                               L.S("your Live rejects this", "твой Live её не понимает"), Theme.Red);

                // поле значения
                if (!r.Value.IsEmpty)
                {
                    Rectangle v = r.Value; v.Offset(0, top);
                    Theme.FillRound(g, v, Sc(8), r.On ? Color.FromArgb(0xFF, 0x24, 0x24, 0x29)
                                                      : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
                    Theme.DrawRound(g, v, Sc(8), Theme.FieldBorder, 1f);
                    if (_editRow != i)
                    {
                        Rectangle vt = new Rectangle(v.X + Sc(9), v.Y, v.Width - Sc(18), v.Height);
                        TextRenderer.DrawText(g, r.Val, Theme.FBody, vt,
                                              r.On ? Theme.Text : Theme.TextFaint,
                                              TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                                              TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    }
                }

                int y = top + padY + Math.Max(ts.Height, r.Value.IsEmpty ? 0 : r.Value.Height - Sc(4)) + gap;

                y = DrawWrapped(g, r.Line(), Theme.FMono, textLeft, y, textW,
                                r.On ? Theme.Mono : Color.FromArgb(0xB0, Theme.Mono)) + Sc(5);
                y = DrawWrapped(g, r.O.Desc, Theme.FBody, textLeft, y, textW, Theme.TextDim);

                if (!string.IsNullOrEmpty(r.Hint))
                    y = DrawWrapped(g, r.Hint, Theme.FSmall, textLeft, y + Sc(5), textW, Theme.TextFaint);

                if (!string.IsNullOrEmpty(r.O.Note))
                    DrawWrapped(g, "!  " + r.O.Note, Theme.FSmall, textLeft, y + Sc(5), textW, Theme.Orange);
            }

            Rectangle bar = BarRect();
            if (!bar.IsEmpty)
                Theme.FillRound(g, bar, bar.Width / 2f, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }

        static int DrawWrapped(Graphics g, string text, Font f, int x, int y, int w, Color c)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Chrome.Wrap).Height;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), c, Chrome.Wrap);
            return y + h;
        }

        int Badge(Graphics g, int x, int y, string text, Color color)
        {
            Size s = TextRenderer.MeasureText(g, text, Theme.FBadge);
            Rectangle r = new Rectangle(x, y, s.Width + Sc(14), s.Height + Sc(6));
            Theme.FillRound(g, r, r.Height / 2f, Color.FromArgb(0x2E, color));
            Theme.DrawRound(g, r, r.Height / 2f, Color.FromArgb(0x60, color), 1f);
            TextRenderer.DrawText(g, text, Theme.FBadge, r, color, Chrome.Center);
            return r.Right + Sc(6);
        }
    }
}
