using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Поле тегов проекта: уже поставленные лежат пилюлями, каретка стоит сразу за
    /// последней, новый тег набирают прямо здесь и закрывают запятой. От TagField
    /// отличается тем, что значения не выбирают из готового списка, а придумывают, —
    /// поэтому внутри живёт настоящий TextBox: каретка, выделение и раскладки достаются
    /// даром, а рисовать всё это своими руками пришлось бы заново.
    ///
    /// Поле нативное, поэтому диалог, который его показывает, стекло выключает — см.
    /// комментарий у NotesDialog.
    /// </summary>
    public sealed class TagEditor : GlassControl
    {
        public readonly List<string> Tags = new List<string>();
        public readonly TextBox Box = new TextBox();

        /// <summary>Подсказка в пустом поле.</summary>
        public string Cue = "";

        /// <summary>Список тегов изменился — снаружи это повод пересобрать раскладку.</summary>
        public event EventHandler Changed;

        readonly List<Rectangle> _chips = new List<Rectangle>();
        Rectangle _boxRect;
        int _hotChip = -1;

        /// <summary>
        /// Сколько рядов пилюль показываем, прежде чем поле перестаёт расти и начинает
        /// прокручиваться. Высота окна фиксированная, а тегов у проекта может быть
        /// сколько угодно — без потолка поле выдавливало из диалога и заметку, и кнопки.
        /// </summary>
        public int MaxRows = 3;

        int _scroll;      // на сколько пикселей содержимое уехало вверх
        int _contentH;    // полная высота всех рядов, независимо от потолка

        int Pad { get { return Sc(8); } }
        int Gap { get { return Sc(6); } }
        int RowGap { get { return Sc(6); } }
        int ChipH { get { return Chrome.PillHeight(Font); } }
        int MinBoxW { get { return Sc(90); } }

        // Отступ текста от края пилюли и отдельная зона под крестик справа — как в
        // TagField: без этого длинный тег наезжает на крестик.
        int ChipPadX { get { return Sc(10); } }
        int ChipCrossZone { get { return Sc(16); } }

        public TagEditor()
        {
            Font = Theme.FBadge;

            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Sunken;
            Box.ForeColor = Theme.Text;
            Box.Font = Font;
            // Отмотал колесом вверх и начал печатать — возвращаем каретку на экран:
            // она в самом низу содержимого, так что просто мотаем до упора.
            Box.TextChanged += delegate { TakeCommas(); SetScroll(int.MaxValue); Invalidate(); };
            Box.GotFocus += delegate { SetScroll(int.MaxValue); };
            Box.KeyDown += OnBoxKeyDown;
            // Колесо Windows отдаёт тому, у кого фокус, а фокус здесь всегда у TextBox:
            // без переадресации прокрутить поле мышью было бы нельзя.
            Box.MouseWheel += delegate (object s, MouseEventArgs e) { DoWheel(e.Delta); };
            Controls.Add(Box);
        }

        public void SetTags(IEnumerable<string> tags)
        {
            Tags.Clear();
            if (tags != null) foreach (string t in tags) Add(t);
            Relayout();
            Invalidate();
        }

        /// <summary>
        /// Дописать тег, который набран, но ещё не закрыт запятой. Зовётся перед
        /// сохранением: набрал слово, нажал Save — тег должен сохраниться, а не пропасть.
        /// </summary>
        public void CommitPending()
        {
            if (Box.Text.Trim().Length == 0) return;
            Add(Box.Text);
            Box.Text = "";
            Fire();
        }

        public void AddTag(string tag)
        {
            if (!Add(tag)) return;
            Fire();
        }

        bool Add(string tag)
        {
            string t = (tag ?? "").Trim();
            if (t.Length == 0) return false;
            foreach (string have in Tags)
                if (string.Equals(have, t, StringComparison.CurrentCultureIgnoreCase)) return false;
            Tags.Add(t);
            return true;
        }

        /// <summary>Запятая закрывает тег: всё до неё уходит в пилюлю, хвост остаётся набранным.</summary>
        void TakeCommas()
        {
            string text = Box.Text;
            if (text.IndexOf(',') < 0) return;

            string[] parts = text.Split(',');
            bool any = false;
            for (int i = 0; i < parts.Length - 1; i++) any |= Add(parts[i]);

            // Присваивание снова поднимет TextChanged, но запятых там уже нет — заходов
            // в этот метод ровно два, без рекурсии.
            Box.Text = parts[parts.Length - 1].TrimStart();
            Box.SelectionStart = Box.Text.Length;
            if (any) Fire(); else { Relayout(); Invalidate(); }
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            // Backspace в пустом поле съедает последнюю пилюлю — так ведут себя все
            // поля-теги, и это единственный способ убрать тег с клавиатуры.
            if (e.KeyCode == Keys.Back && Box.Text.Length == 0 && Tags.Count > 0)
            {
                Tags.RemoveAt(Tags.Count - 1);
                Fire();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            // Enter закрывает тег наравне с запятой. Ctrl+Enter не трогаем: он уже забран
            // диалогом под сохранение (KeyPreview отдаёт его форме раньше нас).
            if (e.KeyCode == Keys.Enter && !e.Control)
            {
                CommitPending();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        void DoWheel(int delta)
        {
            if (_contentH <= Height) return;
            SetScroll(_scroll - Math.Sign(delta) * (ChipH + RowGap));
        }

        void SetScroll(int value)
        {
            int max = Math.Max(0, _contentH - Height);
            int v = Math.Max(0, Math.Min(max, value));
            if (v == _scroll) return;
            _scroll = v;
            PlaceBox();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e) { DoWheel(e.Delta); }

        void Fire()
        {
            Relayout();
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- раскладка

        int ChipWidth(string tag)
        {
            int textW = TextRenderer.MeasureText(tag, Font, new Size(short.MaxValue, ChipH),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            return Math.Min(textW + ChipPadX * 2 + ChipCrossZone, Math.Max(Sc(60), Width - Pad * 2));
        }

        bool _laying;

        /// <summary>
        /// Раскладывает пилюли и поле ввода, возвращает нужную высоту. Высоту меняет
        /// прямо здесь, а это OnResize и ещё один заход сюда же — отсюда флаг.
        /// </summary>
        public int Relayout()
        {
            if (_laying) return Height;
            _laying = true;
            try { return DoLayout(); }
            finally { _laying = false; }
        }

        int DoLayout()
        {
            _chips.Clear();
            if (Width <= 0) return Height;

            int right = Width - Pad;
            int x = Pad, y = Pad;

            foreach (string tag in Tags)
            {
                int w = ChipWidth(tag);
                if (x > Pad && x + w > right) { x = Pad; y += ChipH + RowGap; }
                _chips.Add(new Rectangle(x, y, w, ChipH));
                x += w + Gap;
            }

            // Каретка идёт за последней пилюлей, а если до правого края осталась щель —
            // с новой строки: набирать тег в двадцати пикселях невозможно.
            int boxW = right - x;
            if (boxW < MinBoxW && _chips.Count > 0)
            {
                x = Pad;
                y += ChipH + RowGap;
                boxW = right - x;
            }
            _boxRect = new Rectangle(x, y, Math.Max(MinBoxW, boxW), ChipH);

            _contentH = y + ChipH + Pad;
            int maxH = Pad * 2 + MaxRows * ChipH + (MaxRows - 1) * RowGap;
            int viewH = Math.Min(_contentH, maxH);

            // Каретка всегда на виду: она в самом низу содержимого, туда и прокручиваем.
            // Посмотреть, что выше, можно колесом.
            _scroll = Math.Max(0, _contentH - viewH);
            Height = viewH;      // вернётся сюда же через OnResize, отсюда и _laying
            PlaceBox();
            return viewH;
        }

        /// <summary>
        /// Высоту однострочного TextBox задаёт шрифт, менять её бесполезно — просто
        /// ставим по центру своего ряда, с учётом прокрутки. Уехавшее за край поле
        /// прятать не надо: дочерний контрол WinForms и так обрезает по клиентской
        /// области родителя, а Visible=false отобрал бы у него фокус.
        /// </summary>
        void PlaceBox()
        {
            int top = _boxRect.Y - _scroll;
            Box.SetBounds(_boxRect.X + Sc(4), top + (ChipH - Box.Height) / 2,
                          Math.Max(Sc(20), _boxRect.Width - Sc(8)), Box.Height);
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        // -------------------------------------------------------------------- мышь

        int ChipAt(Point p)
        {
            Point q = new Point(p.X, p.Y + _scroll);
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Contains(q)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ChipAt(e.Location);
            if (i != _hotChip)
            {
                _hotChip = i;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.IBeam;
                Invalidate();
            }
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
            if (chip >= 0 && chip < Tags.Count)
            {
                Tags.RemoveAt(chip);
                _hotChip = -1;
                Fire();
                return;
            }
            Box.Focus();
        }

        // --------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            if (_chips.Count != Tags.Count) Relayout();

            Theme.FillRound(g, new Rectangle(0, 0, Width, Height), Sc(12), Theme.Sunken);

            for (int i = 0; i < _chips.Count && i < Tags.Count; i++)
            {
                Rectangle c = _chips[i];
                c.Y -= _scroll;
                if (c.Bottom < 0 || c.Y > Height) continue;
                bool hot = i == _hotChip;
                Theme.FillRound(g, c, c.Height / 2f, hot ? Theme.Red : Theme.Light);

                Rectangle tr = new Rectangle(c.X + ChipPadX, c.Y + Chrome.PillTop(g, Font, c.Height),
                                             c.Width - ChipPadX - ChipCrossZone, c.Height);
                Chrome.DrawText(g, Tags[i], Font, tr, hot ? Color.White : Theme.OnLight,
                                Chrome.PillTextClipped);

                // Крестик у правого края: клик по пилюле её же и убирает.
                float cx = c.Right - Sc(10), cy = c.Y + c.Height / 2f, s = Sc(3);
                using (Pen p = new Pen(hot ? Color.White : Theme.OnLight, 1.4f))
                {
                    g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                }
            }

            if (Tags.Count == 0 && Box.Text.Length == 0 && Cue.Length > 0)
                Chrome.DrawText(g, Cue, Font,
                                new Rectangle(_boxRect.X + Sc(4), _boxRect.Y - _scroll,
                                              _boxRect.Width, _boxRect.Height),
                                Theme.TextDim, Chrome.CellLeft);

            // Полоска прокрутки ложится в правое поле, поэтому раскладку пилюль не
            // трогает; видна только когда рядов больше, чем влезает.
            if (_contentH > Height)
            {
                int barH = Math.Max(Sc(20), Height * Height / _contentH);
                int barY = (Height - barH) * _scroll / Math.Max(1, _contentH - Height);
                Rectangle bar = new Rectangle(Width - Sc(5), barY, Sc(3), barH);
                Theme.FillRound(g, bar, bar.Width / 2f, Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    /// <summary>
    /// Ряд уже существующих где-то тегов: клик по пилюле добавляет её в поле. Заменяет
    /// прежнюю кнопку «Existing…» с выпадающим меню — теги видно сразу, не открывая
    /// ничего, а это и есть весь смысл списка уже придуманных тегов.
    /// </summary>
    public sealed class TagSuggest : GlassControl
    {
        readonly List<string> _items = new List<string>();
        readonly List<Rectangle> _chips = new List<Rectangle>();
        int _hot = -1;

        public event Action<string> Picked;

        // ponytail: лишние ряды просто не показываем — прокрутка тут появится, только
        // если тегов станет заметно за десяток.
        public int MaxRows = 2;

        int Gap { get { return Sc(6); } }
        int ChipH { get { return Chrome.PillHeight(Font); } }
        int ChipPadX { get { return Sc(10); } }

        public TagSuggest()
        {
            Font = Theme.FBadge;
            Cursor = Cursors.Hand;
        }

        public void SetItems(IEnumerable<string> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            Relayout();
            Invalidate();
        }

        public bool IsEmpty { get { return _items.Count == 0; } }

        public int Relayout()
        {
            _chips.Clear();
            if (Width <= 0) return 0;

            int x = 0, y = 0, row = 0;
            foreach (string item in _items)
            {
                int w = TextRenderer.MeasureText(item, Font, new Size(short.MaxValue, ChipH),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + ChipPadX * 2;
                if (x > 0 && x + w > Width)
                {
                    if (row + 1 >= MaxRows) break;
                    x = 0; y += ChipH + Gap; row++;
                }
                _chips.Add(new Rectangle(x, y, w, ChipH));
                x += w + Gap;
            }
            return _chips.Count == 0 ? 0 : y + ChipH;
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        int ChipAt(Point p)
        {
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ChipAt(e.Location);
            if (i != _hot) { _hot = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot >= 0) { _hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = ChipAt(e.Location);
            if (e.Button != MouseButtons.Left || i < 0 || i >= _items.Count) return;
            if (Picked != null) Picked(_items[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            if (_chips.Count == 0 && _items.Count > 0) Relayout();

            for (int i = 0; i < _chips.Count && i < _items.Count; i++)
            {
                Rectangle c = _chips[i];
                bool hot = i == _hot;
                // Приглушённые, в отличие от светлых пилюль самого поля: это ещё не теги
                // проекта, а всего лишь то, что можно добавить.
                Theme.FillRound(g, c, c.Height / 2f,
                                Color.FromArgb(hot ? 0x3D : 0x22, 0xFF, 0xFF, 0xFF));
                Chrome.DrawText(g, _items[i], Font,
                                new Rectangle(c.X, c.Y + Chrome.PillTop(g, Font, c.Height), c.Width, c.Height),
                                hot ? Theme.Text : Theme.TextDim, Chrome.PillText);
            }
        }
    }
}
