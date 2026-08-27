using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AbletonOptions;

using Opt = AbletonOptions.Opt;
using OptKind = AbletonOptions.Kind;
using OptStat = AbletonOptions.Stat;

namespace AbletonManager
{
    /// <summary>
    /// Редактор Options.txt самой Live: галочки вместо ручной правки текстового файла.
    ///
    /// Каталог опций, чтение и запись файла и сверка с журналом Live взяты как есть из
    /// отдельной программы Ableton Options (src\OptionsCatalog.cs, OptionsFile.cs,
    /// OptionsLiveLog.cs) — там 153 опции с описаниями, и переписывать их незачем.
    /// Переписан только внешний вид: своя тема и свой список заменены на оснастку
    /// Alive, чтобы окно не выглядело чужой программой внутри программы.
    ///
    /// Ключевое, ради чего это вообще стоит открывать: Live сама сообщает, какие опции
    /// она не понимает, — пишет об этом в Preferences\Log.txt. Такие опции помечены
    /// красным, и это точнее любого списка из интернета.
    /// </summary>
    public sealed class OptionsDialog : GlassDialog
    {
        readonly DropField _version = new DropField();
        readonly FieldBox _search = new FieldBox();
        readonly DropField _category = new DropField();
        readonly PillToggle _internal = new PillToggle();
        readonly PillToggle _legacy = new PillToggle();

        readonly OptionListView _list = new OptionListView();
        readonly GlassButton _cancel = new GlassButton();
        readonly GlassButton _save = new GlassButton();

        readonly List<LiveProfile> _profiles = LiveProfile.Detect();
        readonly List<string> _categories = new List<string>();

        LiveProfile _live;
        OptionsDoc _doc = new OptionsDoc();
        string _status = "";

        public OptionsDialog()
        {
            Caption = L.S("Live Options", "Опции Live");
            ClientSize = new Size(Sc(1000), Sc(700));
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;

            List<string> names = new List<string>();
            foreach (LiveProfile p in _profiles) names.Add(p.DisplayName);
            if (names.Count == 0) names.Add(L.S("no Live found", "Live не найдена"));
            _version.SetItems(names, 0);
            _version.SelectedChanged += delegate { UseProfile(_version.SelectedIndex); };
            Controls.Add(_version);

            _search.ShowClear = true;
            _search.Cue = L.S("Search options", "Поиск по опциям");
            _search.Box.TextChanged += delegate { Refill(); };
            Controls.Add(_search);

            _categories.Add(L.S("All categories", "Все категории"));
            foreach (string c in Catalog.Categories) _categories.Add(Catalog.CatName(c));
            _category.SetItems(_categories, 0);
            _category.SelectedChanged += delegate { Refill(); };
            Controls.Add(_category);

            // Служебные и устаревшие спрятаны по умолчанию: первые нужны Ableton для
            // отладки, вторые в Live 12 не действуют вовсе. Показывать их наравне с
            // рабочими — приглашать наступить на грабли.
            Small(_internal, L.S("Internal", "Служебные"));
            Small(_legacy, L.S("Deprecated", "Устаревшие"));

            _list.Changed += delegate { OnToggled(); };
            _list.SelectionChanged += delegate { Invalidate(); };
            Controls.Add(_list);

            _cancel.Text = L.S("Close", "Закрыть");
            _cancel.FitToText(16);
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);

            _save.Text = L.S("Save", "Сохранить");
            _save.Primary = true;
            _save.FitToText(20);
            _save.Click += delegate { Save(); };
            Controls.Add(_save);

            UseProfile(0);
        }

        void Small(PillToggle t, string text)
        {
            t.Text = text;
            t.FitToText();
            t.CheckedChanged += delegate { Refill(); };
            Controls.Add(t);
        }

        // ------------------------------------------------------------------ данные

        void UseProfile(int index)
        {
            _live = index >= 0 && index < _profiles.Count ? _profiles[index] : null;
            _doc = _live != null ? OptionsDoc.Load(_live.OptionsPath) : new OptionsDoc();
            // Полное имя: LiveLog есть и у Alive, и у каталога опций — это разные
            // журналы (падения Live против её же жалоб на Options.txt), и путать их
            // нельзя. Здесь нужен второй.
            _list.Rejected = _live != null
                ? AbletonOptions.LiveLog.RejectedBy(_live.PreferencesDir)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _list.Doc = _doc;
            Refill();
        }

        void Refill()
        {
            string q = _search.Box.Text.Trim();
            string cat = _category.SelectedIndex > 0
                ? Catalog.Categories[_category.SelectedIndex - 1] : null;

            List<Opt> shown = new List<Opt>();
            foreach (Opt o in Catalog.All)
            {
                if (o.Stat == OptStat.Dev && !_internal.Checked && !_doc.Enabled.ContainsKey(o.Name)) continue;
                if (o.Stat == OptStat.Legacy && !_legacy.Checked && !_doc.Enabled.ContainsKey(o.Name)) continue;
                if (cat != null && o.Cat != cat) continue;
                if (q.Length > 0 && !Matches(o, q)) continue;
                shown.Add(o);
            }

            _list.SetItems(shown);
            UpdateStatus();
            Invalidate();
        }

        /// <summary>Ищем и по человеческому названию, и по имени самой опции: помнят обычно одно из двух.</summary>
        static bool Matches(Opt o, string q)
        {
            return o.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || o.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || o.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void OnToggled()
        {
            UpdateStatus();
            Invalidate();
        }

        void UpdateStatus()
        {
            int on = _doc.Enabled.Count;
            _status = on + L.S(" option", " опция") + (on == 1 ? "" : "s") + L.S(" on", " вкл")
                    + (_doc.Custom.Count > 0
                       ? "   ·   " + _doc.Custom.Count + L.S(" kept as-is (not in the catalogue)",
                                                             " своих, сохранятся как есть")
                       : "")
                    + (_live != null ? "   ·   " + _live.OptionsPath : "");
        }

        void Save()
        {
            if (_live == null) return;
            try
            {
                string backup = _doc.Save(_live.OptionsPath);
                _status = backup == null
                    ? L.S("Nothing changed.", "Изменений нет.")
                    : L.S("Saved. Previous file kept as ", "Сохранено. Прежний файл — ")
                      + Path.GetFileName(backup)
                      + L.S("   ·   restart Live to apply.", "   ·   перезапустите Live.");
            }
            catch (Exception ex)
            {
                _status = L.S("Could not write the file: ", "Не удалось записать файл: ") + ex.Message;
                Diag.Fail("options: save", ex);
            }
            Invalidate();
        }

        // ------------------------------------------------------------------ раскладка

        Rectangle _statusRect, _lineRect;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_save == null) return;

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int right = Card.Right - pad;
            int h = Sc(Theme.ControlH);
            int y = Card.Top + Sc(62);

            _version.SetBounds(right - Sc(200), y, Sc(200), h);
            _search.SetBounds(x, y, Sc(300), h);
            _category.SetBounds(_search.Right + Sc(10), y, Sc(200), h);
            _legacy.SetBounds(_version.Left - Sc(16) - _legacy.Width, y, _legacy.Width, h);
            _internal.SetBounds(_legacy.Left - Sc(8) - _internal.Width, y, _internal.Width, h);

            y += h + Sc(16);

            int by = Card.Bottom - pad - _save.Height;
            _save.Location = new Point(right - _save.Width, by);
            _cancel.Location = new Point(_save.Left - Sc(10) - _cancel.Width, by);

            _lineRect = new Rectangle(x, by - Sc(2) - Theme.FSmall.Height * 2,
                                      right - x - _save.Width - _cancel.Width - Sc(30),
                                      Theme.FSmall.Height * 2);
            _statusRect = new Rectangle(x, by, _lineRect.Width, _save.Height);

            // Высота списка — по целым строкам: половина строки, срезанная нижним
            // краем, читается как обрыв отрисовки, а не как «дальше прокрути».
            int bottom = _lineRect.Top - Sc(12);
            int room = Math.Max(_list.RowHeight, bottom - y);
            _list.SetBounds(x, y, right - x, room - room % _list.RowHeight);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            // Строка, которая уедет в файл, — прямо под списком. Смысл в том, чтобы
            // видеть настоящий синтаксис Live, а не только галочку: именно эту строку
            // потом ищут в чужих советах и на форуме.
            Opt sel = _list.Selected;
            if (sel != null)
            {
                string val;
                bool on = _doc.Enabled.TryGetValue(sel.Name, out val);
                string line = "-" + sel.Name + (on && !string.IsNullOrEmpty(val) ? "=" + val : "");
                Chrome.DrawText(g, on ? line : "(" + line + ")", Theme.FSmall, _lineRect,
                                on ? Theme.Text : Theme.TextDim, Chrome.Wrap);
            }

            Chrome.DrawText(g, _status, Theme.FSmall, _statusRect, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>
    /// Список опций: галочка, название, имя опции, значение и бейдж надёжности.
    /// Своя отрисовка, а не полторы сотни контролов WinForms, — по той же причине, что
    /// и у списка плагинов в Rescue: иначе окно заметно тормозило бы на прокрутке.
    ///
    /// Значение правится на месте: у числовых и текстовых опций по щелчку в поле
    /// значения поверх строки встаёт настоящее поле ввода, у Choice — меню.
    /// </summary>
    public sealed class OptionListView : GlassControl
    {
        readonly List<Opt> _items = new List<Opt>();
        readonly GlassTextBox _edit = new GlassTextBox();

        public OptionsDoc Doc = new OptionsDoc();
        public HashSet<string> Rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;
        public event EventHandler SelectionChanged;

        int _scroll, _hover = -1, _selected = -1, _editing = -1;

        public OptionListView()
        {
            Surface = Theme.Backdrop;
            Cursor = Cursors.Hand;

            _edit.BorderStyle = BorderStyle.FixedSingle;
            _edit.BackColor = Theme.Bg;
            _edit.ForeColor = Theme.Text;
            _edit.Font = Theme.FBody;
            _edit.Visible = false;
            _edit.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) CommitEdit();
                else if (e.KeyCode == Keys.Escape) CancelEdit();
            };
            _edit.LostFocus += delegate { CommitEdit(); };
            Controls.Add(_edit);
        }

        /// <summary>
        /// Строка высотой в три текстовых: название сверху, под ним имя опции и
        /// описание в два переноса. Описание — то, ради чего этот список вообще
        /// существует, и обрывать его многоточием значит выбрасывать главное.
        /// </summary>
        public int RowHeight { get { return Theme.FBody.Height + Theme.FSmall.Height * 2 + Sc(22); } }
        int Inner { get { return _items.Count * RowHeight; } }

        public Opt Selected
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : null; }
        }

        public void SetItems(List<Opt> items)
        {
            CancelEdit();
            _items.Clear();
            _items.AddRange(items);
            _selected = _items.Count > 0 ? 0 : -1;
            _scroll = 0;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        // --------------------------------------------------------------- координаты

        int Pad { get { return Sc(12); } }
        int Box { get { return Sc(15); } }

        /// <summary>Поле значения — справа, ширина под «-6.0 dB» с запасом.</summary>
        Rectangle ValueRect(int top)
        {
            int w = Sc(120);
            return new Rectangle(Width - Pad - w, top + (RowHeight - Sc(26)) / 2, w, Sc(26));
        }

        int RowAt(int y)
        {
            int i = (y + _scroll) / RowHeight;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        int TopOf(int index) { return index * RowHeight - _scroll; }

        // --------------------------------------------------------------- ввод

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            int i = RowAt(e.Y);
            if (i < 0) return;

            if (_selected != i)
            {
                _selected = i;
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }

            Opt o = _items[i];
            bool on = Doc.Enabled.ContainsKey(o.Name);

            // Клик по полю значения правит значение, а не переключает опцию: у флагов
            // значения нет, и там весь ряд — переключатель.
            if (o.Kind != OptKind.Flag && on && ValueRect(TopOf(i)).Contains(e.Location))
            {
                if (o.Kind == OptKind.Choice) ChooseValue(i);
                else BeginEdit(i);
                return;
            }

            if (on) Doc.Enabled.Remove(o.Name);
            else Doc.Enabled[o.Name] = o.Kind == OptKind.Flag ? "" : (o.Def ?? "");

            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        void ChooseValue(int i)
        {
            Opt o = _items[i];
            if (o.Choices == null) return;

            ContextMenuStrip menu = DarkMenu.Create();
            string cur;
            Doc.Enabled.TryGetValue(o.Name, out cur);
            foreach (string choice in o.Choices)
            {
                string captured = choice;
                ToolStripMenuItem mi = new ToolStripMenuItem(choice);
                mi.Checked = choice == cur;
                mi.Click += delegate
                {
                    Doc.Enabled[o.Name] = captured;
                    Invalidate();
                    if (Changed != null) Changed(this, EventArgs.Empty);
                };
                menu.Items.Add(mi);
            }
            menu.Show(this, new Point(ValueRect(TopOf(i)).X, TopOf(i) + RowHeight));
        }

        void BeginEdit(int i)
        {
            _editing = i;
            Rectangle r = ValueRect(TopOf(i));
            _edit.SetBounds(r.X, r.Y, r.Width, r.Height);
            string v;
            Doc.Enabled.TryGetValue(_items[i].Name, out v);
            _edit.Text = v ?? "";
            _edit.Visible = true;
            _edit.BringToFront();
            _edit.Focus();
            _edit.SelectAll();
        }

        void CancelEdit()
        {
            if (_editing < 0) return;
            _editing = -1;
            _edit.Visible = false;
            Invalidate();
        }

        /// <summary>
        /// Записываем введённое, но не молча: у числовой опции есть диапазон, и значение
        /// вне его Live либо не поймёт, либо поймёт не так. Выходящее за границы
        /// подтягиваем к ближайшей — это заметно и обратимо, в отличие от тихой записи.
        /// </summary>
        void CommitEdit()
        {
            if (_editing < 0) return;
            int i = _editing;
            _editing = -1;
            _edit.Visible = false;

            Opt o = _items[i];
            string v = _edit.Text.Trim();

            if (o.Kind == OptKind.Num)
            {
                double d, lo, hi;
                if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out d))
                    v = o.Def ?? "";
                else
                {
                    if (o.Min != null && double.TryParse(o.Min, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out lo) && d < lo) d = lo;
                    if (o.Max != null && double.TryParse(o.Max, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out hi) && d > hi) d = hi;
                    v = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            Doc.Enabled[o.Name] = v;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Y);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            CancelEdit();
            Scroll(_scroll - e.Delta / 120 * RowHeight);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Scroll(_scroll);
        }

        void Scroll(int to)
        {
            int max = Math.Max(0, Inner - Height);
            int v = Math.Max(0, Math.Min(max, to));
            if (v == _scroll) return;
            _scroll = v;
            Invalidate();
        }

        // --------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            for (int i = 0; i < _items.Count; i++)
            {
                int top = TopOf(i);
                if (top + RowHeight < 0 || top > Height) continue;
                PaintRow(g, i, top);
            }

            if (_items.Count == 0)
                Chrome.DrawText(g, L.S("nothing matches", "ничего не найдено"), Theme.FBody,
                                new Rectangle(0, 0, Width, Height), Theme.TextDim, Chrome.Center);

            int over = Inner - Height;
            if (over > 0)
            {
                int h = Math.Max(Sc(30), (int)(Height * (float)Height / Inner));
                int y = (int)((Height - h) * (_scroll / (float)over));
                Theme.FillRound(g, new Rectangle(Width - Sc(5), y, Sc(3), h), Sc(2), Theme.Hairline);
            }
        }

        void PaintRow(Graphics g, int i, int top)
        {
            Opt o = _items[i];
            bool on = Doc.Enabled.ContainsKey(o.Name);
            bool rejected = Rejected.Contains(o.Name);

            Rectangle row = new Rectangle(0, top, Width, RowHeight);
            if (i == _selected) Theme.FillRound(g, row, Sc(8), Theme.RowHover);
            else if (i == _hover) Theme.FillRound(g, row, Sc(8), Theme.RowHover);

            Rectangle mark = new Rectangle(Pad, top + (RowHeight - Box) / 2, Box, Box);
            if (on)
            {
                Theme.FillRound(g, mark, Sc(4), Theme.Light);
                Icons.Draw(g, Glyph.Check,
                           new RectangleF(mark.X + Sc(2), mark.Y + Sc(2), Box - Sc(4), Box - Sc(4)),
                           Theme.OnLight, 1.8f);
            }
            else
            {
                using (System.Drawing.Drawing2D.GraphicsPath p =
                       Theme.Round(new RectangleF(mark.X, mark.Y, mark.Width, mark.Height), Sc(4)))
                using (Pen pen = new Pen(Theme.Hairline, 1.4f))
                    g.DrawPath(pen, p);
            }

            int x = mark.Right + Sc(14);
            Rectangle value = ValueRect(top);
            int textW = Math.Max(Sc(120), value.Left - Sc(14) - x);

            int line = Theme.FBody.Height;
            int descH = Theme.FSmall.Height * 2;
            int ty = top + (RowHeight - line - descH) / 2;

            // Первая строка — человеческое название и бейдж; вторая — имя опции и
            // описание. Имя опции стоит рядом с описанием, а не с названием: искать по
            // нему приходится редко, а мешает оно постоянно.
            string badge = rejected
                ? L.S("your Live does not know it", "ваша Live её не понимает")
                : Catalog.StatName(o.Stat);
            int badgeW = badge.Length > 0 ? Sc(170) : 0;

            Chrome.DrawText(g, o.Title, Theme.FBody,
                            new Rectangle(x, ty, textW - badgeW, line),
                            on ? Theme.Text : Theme.TextDim, Chrome.Left);

            if (badgeW > 0)
                Chrome.DrawText(g, badge, Theme.FBadge,
                                new Rectangle(x + textW - badgeW, ty, badgeW, line),
                                rejected ? Theme.Red : Theme.TextDim, Chrome.Right);

            Chrome.DrawText(g, "-" + o.Name + "   " + o.Desc, Theme.FSmall,
                            new Rectangle(x, ty + line + Sc(2), textW, descH),
                            Theme.TextDim, Chrome.Wrap);

            if (o.Kind == OptKind.Flag || _editing == i) return;

            // Поле значения рисуем всегда, когда опция включена: пустое поле честнее
            // отсутствия поля — сразу видно, что значение вообще бывает.
            if (!on) return;
            string v;
            Doc.Enabled.TryGetValue(o.Name, out v);
            Theme.FillRound(g, value, value.Height / 2f, Theme.Surface);
            Chrome.DrawText(g, string.IsNullOrEmpty(v) ? L.S("set…", "задать…") : v,
                            Theme.FSmall, value,
                            string.IsNullOrEmpty(v) ? Theme.TextDim : Theme.Text, Chrome.Center);
        }
    }
}
