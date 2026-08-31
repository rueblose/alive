using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Панель справа: подробности выбранного сета или плагина. Карточка одного цвета,
    /// главное действие прибито к низу, всё остальное — колонка блоков с одинаковыми
    /// отступами.
    /// </summary>
    public sealed class DetailPanel : GlassControl
    {
        readonly GlassButton _action = new GlassButton();
        readonly GlassButton _rescue = new GlassButton();
        readonly GlassButton _forks = new GlassButton();

        SetEntry _set;
        PluginStat _plugin;
        bool _pluginMode;
        int _scroll;
        int _contentHeight;
        float _scrollTarget, _scrollCurrent;
        Timer _scrollTimer;

        Arrangement _arr;
        Bitmap _thumb;
        Size _thumbSize;
        Rectangle _thumbRect, _linkRect, _notesRect;
        bool _thumbHot, _linkHot, _notesHot;
        bool _thumbRendering;

        // Список «Sets» у плагина: строки кликабельны — переход к сету, но подчёркиваем
        // только ту, что сейчас под курсором, а не все разом.
        readonly List<Rectangle> _setRowRects = new List<Rectangle>();
        readonly List<SetEntry> _setRowSets = new List<SetEntry>();
        int _setRowHot = -1;

        // Список «Plugins» у сета: та же логика в обратную сторону — переход к плагину.
        readonly List<Rectangle> _pluginRowRects = new List<Rectangle>();
        readonly List<string> _pluginRowNames = new List<string>();
        int _pluginRowHot = -1;

        public ProjectIndex Index;
        public ArrangementLoader Loader;

        public event Action PreviewRequested;
        public event Action RevealRequested;
        public event Action OpenRequested;
        public event Action RescueRequested;

        /// <summary>
        /// Версии этого проекта. Живут не в самом Alive, а в сборке AliveReel, поэтому
        /// кнопки нет, пока на событие никто не подписался: в Alive.exe открывать
        /// нечего, и показывать кнопку, которая ничего не делает, — врать интерфейсом.
        /// </summary>
        public event Action ForksRequested;

        /// <summary>Клик по блоку тегов и заметки — открыть редактор.</summary>
        public event Action<SetEntry> NotesRequested;
        public event Action<SetEntry> SetRequested;
        public event Action<string> PluginRequested;

        public DetailPanel()
        {
            Cursor = Cursors.Default;
            Surface = Theme.Backdrop;

            _action.Primary = true;
            // Кнопка лежит на карточке панели, а не на голом окне — повторяем оба слоя.
            _action.Surface = Theme.Backdrop;
            _action.SurfaceOverlay = Theme.Surface;
            _action.Click += delegate
            {
                if (_pluginMode) { if (RevealRequested != null) RevealRequested(); }
                else if (OpenRequested != null) OpenRequested();
            };
            Controls.Add(_action);

            // Второстепенные действия для сета — только для сета: у плагина своего .als
            // нет, ни чинить, ни версионировать нечего. Оба Quiet, без заливки: панель
            // уже говорит «Open in Live» громче всего, а эти две нужны далеко не на
            // каждом сете, и пилюли рядом с главной кнопкой спорили бы с ней за взгляд.
            // Никакой SurfaceOverlay: Quiet-кнопка — это текст-ссылка без заливки, а
            // непрозрачная накладка при затухании ховера (PaintGlassSurface рисует
            // SourceCopy) на миг пробивала кнопку в дыру и тут же возвращала плашку.
            _rescue.Text = L.S("Rescue Project", "Восстановить проект");
            _rescue.Quiet = true;
            _rescue.Surface = Theme.Backdrop;
            _rescue.Click += delegate { if (RescueRequested != null) RescueRequested(); };
            Controls.Add(_rescue);

            _forks.Text = L.S("Forks", "Версии");
            _forks.Quiet = true;
            _forks.Surface = Theme.Backdrop;
            _forks.Click += delegate { if (ForksRequested != null) ForksRequested(); };
            Controls.Add(_forks);

            _scrollTimer = new Timer();
            _scrollTimer.Interval = 16;
            _scrollTimer.Tick += delegate { ScrollTick(); };
            ApplyAction();
        }

        int Pad { get { return Sc(Theme.PanelPad); } }

        /// <summary>
        /// TextRenderer.DrawText без NoPadding сдвигает первый видимый пиксель буквы
        /// примерно на 4px правее начала прямоугольника (замерено на FLabel) — подчёркивание,
        /// проведённое от самого rr.X, из-за этого торчит левее текста, который оно подчёркивает.
        /// </summary>
        int UnderlinePad { get { return Sc(4); } }

        // ------------------------------------------------------------- содержимое

        public void Show(SetEntry s)
        {
            bool same = _set != null && s != null && _set.Path == s.Path;
            _plugin = null;
            _pluginMode = false;
            _set = s;
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            _pluginRowHot = -1;
            if (!same)
            {
                _arr = null;
                DropThumb();
                if (s != null && Loader != null)
                {
                    _arr = Loader.Cached(s.Path);
                    if (_arr == null) Loader.Request(s.Path);
                }
            }
            ApplyAction();
            Invalidate();
        }

        /// <summary>
        /// Сеты, где стоит показанный плагин. Считаем один раз при выборе плагина, а не
        /// в OnPaint: обход тут — все сеты на все их плагины, а перерисовок у панели
        /// много (наведение, прокрутка). Ограничения на длину нет намеренно — раньше
        /// список обрывался на шестидесятом сете молча, и «не все проекты появляются»
        /// было именно этим.
        /// </summary>
        readonly List<SetEntry> _users = new List<SetEntry>();

        public void ShowPlugin(PluginStat p)
        {
            _plugin = p;
            _pluginMode = true;
            _set = null;
            _arr = null;
            DropThumb();
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            _setRowHot = -1;
            _pluginRowHot = -1;

            _users.Clear();
            if (p != null && Index != null && p.Sets > 0)
                foreach (SetEntry s in Index.Sets)
                    foreach (string n in s.Plugins)
                        if (string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))
                        { _users.Add(s); break; }

            ApplyAction();
            Invalidate();
        }

        public void OnArrangement(Arrangement a)
        {
            if (a == null || _set == null || a.Path != _set.Path) return;
            _arr = a;
            DropThumb();
            Invalidate();
        }

        void ApplyAction()
        {
            if (_pluginMode)
            {
                _action.Text = L.S("Show in Explorer", "Показать в папке");
                _action.Visible = _plugin != null && _plugin.Installed != null
                                  && _plugin.Installed.Path.Length > 0;
            }
            else
            {
                _action.Text = L.S("Open in Live", "Открыть в Live");
                _action.Visible = _set != null;
            }

            _rescue.Visible = !_pluginMode && _set != null;
            _forks.Visible = !_pluginMode && _set != null && ForksRequested != null;
        }

        void DropThumb()
        {
            if (_thumb != null) { _thumb.Dispose(); _thumb = null; }
            _thumbSize = Size.Empty;
        }

        // --------------------------------------------------------------- раскладка

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Sc(Theme.ControlH);
            int w = Math.Max(Sc(40), Width - Pad * 2);
            _action.SetBounds(Pad, Height - Pad - h, w, h);
            _rescue.SetBounds(Pad, _action.Top - Sc(6) - h, w, h);
            _forks.SetBounds(Pad, _rescue.Top - Sc(2) - h, w, h);
            ClampScroll();
        }

        /// <summary>
        /// Докуда можно рисовать содержимое. Место под кнопки резервируем всегда, чтобы
        /// список не прыгал, когда кнопка то есть, то нет, — но по-разному для двух
        /// режимов: у плагина кнопка всегда одна (Show in Explorer то есть, то нет,
        /// смотря установлен ли он), у сета их до трёх сразу (Open in Live, Rescue
        /// Project и Forks). Переключение между режимами и так меняет содержимое панели
        /// целиком, так что разная высота резерва здесь не приводит к дёрганью, которого
        /// избегает сам резерв, — оно только внутри одного режима.
        /// </summary>
        int BodyBottom
        {
            get
            {
                int h = Sc(Theme.ControlH);
                int rows = _pluginMode ? 1 : (ForksRequested != null ? 3 : 2);
                int buttons = h * rows + (rows > 1 ? Sc(8) : 0);
                return Height - Pad - buttons - Sc(12);
            }
        }

        /// <summary>
        /// Строки списка просто перестаём рисовать за нижней границей: обрезка через
        /// Graphics.Clip не годится — TextRenderer рисует мимо GDI+ и клип игнорирует.
        /// </summary>
        bool Below(int y) { return y > BodyBottom; }

        void ClampScroll()
        {
            int max = Math.Max(0, _contentHeight - BodyBottom);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_contentHeight <= BodyBottom) return;
            _scrollTarget -= (int)(e.Delta / 120f * Sc(60));
            ClampScrollTarget();
            if (!Theme.SmoothScroll)
            {
                _scroll = (int)_scrollTarget;
                _scrollCurrent = _scrollTarget;
                ClampScroll();
                Invalidate();
            }
            else
            {
                if (!_scrollTimer.Enabled) _scrollTimer.Start();
            }
            base.OnMouseWheel(e);
        }

        void ClampScrollTarget()
        {
            int max = Math.Max(0, _contentHeight - BodyBottom);
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scrollTarget < 0) _scrollTarget = 0;
        }

        void ScrollTick()
        {
            _scrollCurrent += (_scrollTarget - _scrollCurrent) * 0.35f;
            if (Math.Abs(_scrollTarget - _scrollCurrent) < 0.5f)
            {
                _scrollCurrent = _scrollTarget;
                _scrollTimer.Stop();
            }
            int newScroll = (int)Math.Round(_scrollCurrent);
            if (newScroll != _scroll)
            {
                _scroll = newScroll;
                ClampScroll();
                Invalidate();
            }
        }

        // ------------------------------------------------------------------- мышь

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool t = _arr != null && _arr.HasContent && _thumbRect.Contains(e.Location);
            bool l = _set != null && _linkRect.Contains(e.Location);

            int rowHot = -1;
            for (int i = 0; i < _setRowRects.Count; i++)
                if (_setRowRects[i].Contains(e.Location)) { rowHot = i; break; }

            int pluginRowHot = -1;
            for (int i = 0; i < _pluginRowRects.Count; i++)
                if (_pluginRowRects[i].Contains(e.Location)) { pluginRowHot = i; break; }

            bool n = _set != null && !_pluginMode && _notesRect.Contains(e.Location);

            if (t != _thumbHot || l != _linkHot || n != _notesHot
                || rowHot != _setRowHot || pluginRowHot != _pluginRowHot)
            {
                _thumbHot = t; _linkHot = l; _notesHot = n;
                _setRowHot = rowHot; _pluginRowHot = pluginRowHot;
                Cursor = (t || l || n || rowHot >= 0 || pluginRowHot >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_thumbHot || _linkHot || _notesHot || _setRowHot >= 0 || _pluginRowHot >= 0)
            {
                _thumbHot = _linkHot = _notesHot = false;
                _setRowHot = -1;
                _pluginRowHot = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_thumbHot && PreviewRequested != null) { PreviewRequested(); return; }
                if (_linkHot && RevealRequested != null) { RevealRequested(); return; }
                if (_notesHot && _set != null && NotesRequested != null)
                {
                    NotesRequested(_set);
                    return;
                }
                if (_setRowHot >= 0 && _setRowHot < _setRowSets.Count && SetRequested != null)
                {
                    SetRequested(_setRowSets[_setRowHot]);
                    return;
                }
                if (_pluginRowHot >= 0 && _pluginRowHot < _pluginRowNames.Count && PluginRequested != null)
                {
                    PluginRequested(_pluginRowNames[_pluginRowHot]);
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        // -------------------------------------------------------------- отрисовка

        void PaintCard(Graphics g)
        {
            RectangleF card = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, card, Sc(Theme.CardR), Theme.GlassAlpha);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Уголки control'а снаружи скруглённой карточки красим фоном окна — на
            // стекле это Backdrop, так и уголки бесшовно сливаются с тем же стеклом
            // вокруг панели, как у обычных пилюль.
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            PaintCard(g);

            int w = Width - Pad * 2;
            int y = Pad - _scroll;

            if (_pluginMode) { PaintPlugin(g, Pad, y, w); return; }
            _setRowRects.Clear();
            _setRowSets.Clear();
            _pluginRowRects.Clear();
            _pluginRowNames.Clear();

            if (_set == null)
            {
                _thumbRect = _linkRect = Rectangle.Empty;
                Chrome.DrawText(g, L.S("Select a set to see details", "Выбери сет, чтобы увидеть детали"),
                    Theme.FLabel, new Rectangle(Pad, Pad, w, Sc(60)), Theme.TextDim, Chrome.Wrap);
                return;
            }

            // Заголовок и вес всей папки проекта в одной строке — то же число, что и в
            // колонке списка. Вес самого .als уехал строкой ниже: он почти всегда
            // одинаковый и интересен куда реже, чем «сколько занимает проект».
            int titleH = Sc(26);
            string size = MainForm.SizeMB(_set.ProjectSize);
            Size sw = TextRenderer.MeasureText(size, Theme.FLabel);
            Chrome.DrawText(g, _set.Name, Theme.FTitle,
                new Rectangle(Pad, y, w - sw.Width - Sc(10), titleH), Theme.Text, Chrome.Left);
            Chrome.DrawText(g, size, Theme.FLabel,
                new Rectangle(Pad, y, w, titleH), Theme.TextDim, Chrome.Right);
            y += titleH + Sc(2);

            string weights = _set.ProjectFiles > 0
                ? _set.ProjectFiles + L.S(" files in the project folder · set ", " файлов в папке · сет ")
                  + MainForm.SizeMB(_set.Size)
                : L.S("set ", "сет ") + MainForm.SizeMB(_set.Size);
            y = Wrapped(g, weights, Theme.FBadge, Theme.TextDim, Pad, y, w) + Sc(10);

            y = Thumb(g, Pad, y, w) + Sc(16);

            y = Wrapped(g, _set.Path, Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(4);

            // Ссылка «показать в папке» — подчёркнутая строка, как в макете.
            Size ls = TextRenderer.MeasureText(L.S("Show in Explorer…", "Показать в папке…"), Theme.FLabel);
            _linkRect = new Rectangle(Pad, y, ls.Width, ls.Height);
            Chrome.DrawText(g, L.S("Show in Explorer…", "Показать в папке…"), Theme.FLabel,
                            _linkRect, _linkHot ? Color.White : Theme.Text, Chrome.Left);
            using (Pen p = new Pen(_linkHot ? Color.White : Theme.Text))
                g.DrawLine(p, _linkRect.Left, _linkRect.Bottom - Sc(2),
                              _linkRect.Right - Sc(3), _linkRect.Bottom - Sc(2));
            y += ls.Height + Sc(20);

            y = TagsAndNote(g, y, w);
            y = Versions(g, y, w);

            // Файлы
            y = Line(g, L.S("Files:", "Файлы:"), Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(4);
            Chrome.DrawText(g, _set.TotalRefs + L.S(" references", " ссылок"), Theme.FLabel,
                new Rectangle(Pad, y, w, Sc(28)), Theme.Text, Chrome.Left);
            if (_set.TotalRefs > 0 || _set.MissingFiles > 0)
                Chrome.DrawText(g, _set.MissingFiles + L.S(" missing", " потеряно"), Theme.FLabel,
                    new Rectangle(Pad, y, w, Sc(28)),
                    _set.MissingFiles > 0 ? Theme.Red : Theme.Green, Chrome.Right);
            y += Sc(28) + Sc(16);

            if (_set.Error.Length > 0)
                y = Wrapped(g, _set.Error, Theme.FLabel, Theme.Red, Pad, y, w) + Sc(16);

            // Плагины — счётчик пропавших напротив заголовка, тем же приёмом, что у Files.
            Rectangle plugHead = new Rectangle(Pad, y, w, Sc(28));
            Chrome.DrawText(g, L.S("Plugins", "Плагины") + " (" + _set.Plugins.Length + "):",
                            Theme.FLabel, plugHead, Theme.TextDim, Chrome.Left);
            if (_set.Plugins.Length > 0)
                Chrome.DrawText(g, _set.MissingPlugins + L.S(" missing", " нет"), Theme.FLabel,
                    plugHead, _set.MissingPlugins > 0 ? Theme.Red : Theme.Green, Chrome.Right);
            y += Sc(28) + Sc(6);

            if (_set.Plugins.Length == 0)
                y = Line(g, L.S("only Live's own devices", "только встроенные устройства Live"),
                         Theme.FLabel, Theme.TextDim, Pad, y, w);
            else
            {
                int shown = 0;
                for (int i = 0; i < _set.Plugins.Length; i++)
                {
                    if (Below(y + Sc(28))) break;
                    MatchKind m = MatchKind.Exact;
                    if (Index != null)
                        m = Index.Inventory.Match(
                                i < _set.PluginUids.Length ? _set.PluginUids[i] : "", _set.Plugins[i]).Kind;

                    // Пропавший плагин и так виден по красному тексту — подпись рядом
                    // с каждым из них только дублирует то, что уже сказано числом
                    // напротив заголовка «Plugins».
                    // Цвет под курсором не меняем: красный у неустановленного — это
                    // сообщение, а не оформление, и подсветка «белым при наведении»
                    // стирала его ровно в тот момент, когда на строку смотрят. Что
                    // строка кликабельна, говорит подчёркивание ниже.
                    bool hot = _pluginRowHot == shown;
                    Color c = m == MatchKind.Missing ? Theme.Red : Theme.Text;
                    Rectangle rr = new Rectangle(Pad, y, w, Sc(28));
                    Chrome.DrawText(g, _set.Plugins[i], Theme.FLabel, rr, c, Chrome.Left);
                    if (m == MatchKind.OtherFormat)
                        Chrome.DrawText(g, L.S("other format", "другой формат"), Theme.FLabel,
                                        rr, Theme.TextDim, Chrome.Right);
                    if (hot)
                    {
                        // Подчёркиваем только эту строку и только по ширине текста — та же
                        // подача, что у кликабельных сетов в панели плагина.
                        Size ts = TextRenderer.MeasureText(_set.Plugins[i], Theme.FLabel);
                        int ly = rr.Y + (rr.Height + ts.Height) / 2;
                        int lx = rr.X + UnderlinePad;
                        using (Pen ln = new Pen(c))
                            g.DrawLine(ln, lx, ly, lx + Math.Min(ts.Width, rr.Width - UnderlinePad), ly);
                    }
                    _pluginRowRects.Add(rr);
                    _pluginRowNames.Add(_set.Plugins[i]);
                    y += Sc(28);
                    shown++;
                }
                // Список бывает длиннее, чем помещается до кнопки снизу - как и в списке
                // сетов у плагина, явно говорим, сколько ещё скрыто, а не обрываем молча.
                if (_set.Plugins.Length > shown)
                {
                    if (!Below(y + Sc(28)))
                        y = Line(g, "… " + (_set.Plugins.Length - shown) + L.S(" more", " ещё"),
                                 Theme.FLabel, Theme.TextDim, Pad, y, w);
                    else
                        y += Sc(28) * (_set.Plugins.Length - shown);
                }
            }

            _contentHeight = y + _scroll + Pad;
            ClampScroll();
        }

        /// <summary>
        /// Теги и заметка проекта. Пока их нет — одна тусклая строка с иконкой, чтобы
        /// про эту возможность вообще можно было узнать; как только что-то написали,
        /// строка превращается в чипы тегов и текст заметки. Клик по всему блоку
        /// открывает редактор.
        /// </summary>
        int TagsAndNote(Graphics g, int y, int w)
        {
            string dir = _set.ProjectDir;
            List<string> tags = ProjectMeta.TagsOf(dir);
            string note = ProjectMeta.NoteOf(dir);

            int icon = Sc(15);
            int textX = Pad + icon + Sc(8);
            int textW = w - icon - Sc(8);
            int top = y;

            if (tags.Count == 0 && note.Length == 0)
            {
                Rectangle line = new Rectangle(Pad, y, w, Sc(24));
                Icons.Draw(g, Glyph.Note,
                           new RectangleF(Pad, y + (Sc(24) - icon) / 2f, icon, icon),
                           _notesHot ? Theme.Text : Theme.TextDim, 1.3f);
                Chrome.DrawText(g, L.S("Add tags or a note…", "Добавить теги или заметку…"),
                                Theme.FBadge, new Rectangle(textX, y, textW, Sc(24)),
                                _notesHot ? Theme.Text : Theme.TextDim, Chrome.Left);
                _notesRect = line;
                return y + Sc(24) + Sc(14);
            }

            if (tags.Count > 0)
            {
                // Без иконки: пилюли сами по себе читаются как теги, значок только
                // отъедал место у первой строки.
                int cx = Pad, cy = y;
                foreach (string tag in tags)
                {
                    Size ts = TextRenderer.MeasureText(tag, Theme.FBadge);
                    int cw = Math.Min(w, ts.Width + Sc(18));
                    if (cx > Pad && cx + cw > Pad + w) { cx = Pad; cy += Sc(24); }
                    Rectangle chip = new Rectangle(cx, cy, cw, Sc(21));
                    Theme.FillRound(g, chip, chip.Height / 2f, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                    Chrome.DrawText(g, tag, Theme.FBadge, chip, Theme.Text, Chrome.Center);
                    cx += cw + Sc(6);
                }
                y = cy + Sc(21) + Sc(8);
            }

            if (note.Length > 0)
            {
                if (tags.Count == 0)
                    Icons.Draw(g, Glyph.Note, new RectangleF(Pad, y + Sc(3), icon, icon),
                               _notesHot ? Theme.Text : Theme.TextDim, 1.3f);
                y = Wrapped(g, note, Theme.FBadge,
                            _notesHot ? Theme.Text : Theme.TextDim,
                            tags.Count == 0 ? textX : Pad,
                            y, tags.Count == 0 ? textW : w);
            }

            _notesRect = new Rectangle(Pad, top, w, Math.Max(Sc(24), y - top));
            return y + Sc(14);
        }

        /// <summary>
        /// Другие .als той же папки. Список сетов схлопывает их в одну строку, и без
        /// этого блока увидеть, что именно спрятано под «+3», было бы негде. Строки
        /// кликабельны: показывают выбранную версию, даже если своей строки в списке
        /// у неё нет.
        /// </summary>
        int Versions(Graphics g, int y, int w)
        {
            if (Index == null) return y;
            List<SetEntry> all = Index.InSameFolder(_set);
            if (all.Count < 2) return y;

            y = Line(g, L.S("Versions", "Версии") + " (" + all.Count + "):",
                     Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(4);

            foreach (SetEntry v in all)
            {
                if (Below(y + Sc(24))) break;
                bool current = ReferenceEquals(v, _set);
                bool hot = _setRowHot == _setRowRects.Count;
                Rectangle rr = new Rectangle(Pad, y, w, Sc(24));

                Color c = current ? Theme.Text : Theme.TextDim;
                Size ds = TextRenderer.MeasureText(v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge);
                Chrome.DrawText(g, v.Name, Theme.FBadge,
                                new Rectangle(rr.X, rr.Y, rr.Width - ds.Width - Sc(8), rr.Height),
                                hot ? Color.White : c, Chrome.Left);
                Chrome.DrawText(g, v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge,
                                rr, Theme.TextDim, Chrome.Right);

                // Текущую не подчёркиваем даже под курсором: щёлкать по ней незачем.
                if (hot && !current)
                {
                    Size ts = TextRenderer.MeasureText(v.Name, Theme.FBadge);
                    int ly = rr.Y + (rr.Height + ts.Height) / 2;
                    using (Pen ln = new Pen(Color.White))
                        g.DrawLine(ln, rr.X + UnderlinePad, ly,
                                   rr.X + UnderlinePad + Math.Min(ts.Width, rr.Width - UnderlinePad), ly);
                }

                _setRowRects.Add(rr);
                _setRowSets.Add(v);
                y += Sc(24);
            }
            return y + Sc(14);
        }

        void PaintPlugin(Graphics g, int pad, int y, int w)
        {
            _thumbRect = _linkRect = Rectangle.Empty;
            _setRowRects.Clear();
            _setRowSets.Clear();
            _pluginRowRects.Clear();
            _pluginRowNames.Clear();
            PluginStat p = _plugin;
            if (p == null)
            {
                TextRenderer.DrawText(g, L.S("Select a plugin to see where it is used",
                                             "Выбери плагин, чтобы увидеть, где он стоит"),
                    Theme.FLabel, new Rectangle(pad, pad, w, Sc(60)), Theme.TextDim, Chrome.Wrap);
                return;
            }
            InstalledPlugin inst = p.Installed;

            y = Line(g, p.Name, Theme.FTitle, Theme.Text, pad, y, w) + Sc(2);
            y = Line(g, p.Vendor.Length > 0 ? p.Vendor : L.S("unknown developer", "разработчик неизвестен"),
                     Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(18);

            string state;
            Color stateColor;
            if (p.Match == MatchKind.Exact)
            {
                state = L.S("installed", "установлен");
                stateColor = Theme.Green;
                if (inst != null && inst.FileMissing)
                {
                    state = L.S("file is gone", "файла нет на диске");
                    stateColor = Theme.Red;
                }
            }
            else if (p.Match == MatchKind.OtherFormat)
            {
                state = L.S("installed as ", "установлен как ") + (inst != null ? inst.Format : "?");
                stateColor = Theme.TextDim;
            }
            else
            {
                state = L.S("not installed", "не установлен");
                stateColor = Theme.Red;
            }

            y = Row(g, L.S("Status:", "Состояние:"), state, stateColor, pad, y, w);
            y = Row(g, L.S("Format:", "Формат:"), p.Format, Theme.Text, pad, y, w);
            if (inst != null)
            {
                y = Row(g, L.S("Version:", "Версия:"), inst.Version, Theme.Text, pad, y, w);
                y = Row(g, L.S("Category:", "Категория:"), inst.Category.Replace("|", " · "), Theme.Text, pad, y, w);
            }
            y = Row(g, L.S("Used in:", "Используется:"),
                    p.Sets + L.S(p.Sets == 1 ? " set" : " sets", " сетах"),
                    p.Sets == 0 ? Theme.TextDim : Theme.Text, pad, y, w);
            y += Sc(10);

            if (inst != null && inst.Path.Length > 0)
                y = Wrapped(g, inst.Path, Theme.FSmall,
                            inst.FileMissing ? Theme.Red : Theme.TextDim, pad, y, w) + Sc(16);

            List<SetEntry> users = _users;

            y = Line(g, L.S("Sets", "Сеты") + " (" + p.Sets + "):", Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(6);
            // if (users.Count == 0)
            //     y = Line(g, L.S("not used anywhere — safe to uninstall",
            //                     "нигде не используется — можно сносить"),
            //              Theme.FLabel, Theme.TextDim, pad, y, w);
            int shown = 0;
            foreach (SetEntry s in users)
            {
                if (Below(y + Sc(28))) break;
                Rectangle rr = new Rectangle(pad, y, w, Sc(28));
                bool hot = _setRowHot == shown;
                Chrome.DrawText(g, s.Name, Theme.FLabel, rr, hot ? Color.White : Theme.Text, Chrome.Left);
                if (hot)
                {
                    // Подчёркиваем только эту строку и только по ширине текста — не всю
                    // строку целиком, иначе выглядит как кнопка, а не как ссылка.
                    Size ts = TextRenderer.MeasureText(s.Name, Theme.FLabel);
                    int ly = rr.Y + (rr.Height + ts.Height) / 2;
                    int lx = rr.X + UnderlinePad;
                    using (Pen ln = new Pen(Color.White))
                        g.DrawLine(ln, lx, ly, lx + Math.Min(ts.Width, rr.Width - UnderlinePad), ly);
                }
                _setRowRects.Add(rr);
                _setRowSets.Add(s);
                y += Sc(28);
                shown++;
            }
            // Считаем по фактической длине списка, а не по p.Sets: они обязаны совпадать,
            // но если разойдутся — врать про «ещё N» хуже, чем не показать ничего.
            if (users.Count > shown)
            {
                if (!Below(y + Sc(28)))
                    y = Line(g, "… " + (users.Count - shown) + L.S(" more", " ещё"),
                             Theme.FLabel, Theme.TextDim, pad, y, w);
                else
                    // Высоту недорисованных строк всё равно закладываем, иначе панель
                    // считает себя короче, чем есть, и до хвоста не долистать.
                    y += Sc(28) * (users.Count - shown);
            }

            _contentHeight = y + _scroll + pad;
            ClampScroll();
        }

        // ------------------------------------------------------------------ куски

        int Thumb(Graphics g, int x, int y, int w)
        {
            int h = (int)Math.Round(w * 180f / 302f);      // пропорции из макета
            _thumbRect = new Rectangle(x, y, w, h);
            Theme.FillRound(g, _thumbRect, Sc(Theme.ThumbR), Theme.Bg);

            string hint = null;
            if (_arr == null) hint = L.S("reading…", "читаю…");
            else if (_arr.Error != null) hint = L.S("could not read the set", "сет не читается");
            else if (!_arr.HasContent) hint = L.S("arrangement is empty", "аранжировка пуста");

            if (hint != null)
            {
                Chrome.DrawText(g, hint, Theme.FSmall, _thumbRect, Theme.TextDim, Chrome.Center);
                return y + h;
            }

            int inset = Sc(6);
            Rectangle inner = Rectangle.Inflate(_thumbRect, -inset, -inset);
            if (inner.Width > 0 && inner.Height > 0)
            {
                if (_thumb == null || _thumbSize != inner.Size)
                {
                    if (!_thumbRendering)
                    {
                        _thumbRendering = true;
                        DropThumb();
                        Size sz = inner.Size;
                        float dpi = DeviceDpi / 96f;
                        Arrangement arr = _arr;
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            RenderOptions o = new RenderOptions();
                            o.Dpi = dpi;
                            o.MaxLane = 10;
                            Bitmap bmp = ArrangementRender.ToBitmap(arr, sz.Width, sz.Height, o);
                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    DropThumb();
                                    _thumb = bmp;
                                    _thumbSize = sz;
                                    _thumbRendering = false;
                                    Invalidate(_thumbRect);
                                });
                            }
                            catch { if (bmp != null) bmp.Dispose(); }
                        });
                    }
                }
                if (_thumb != null) g.DrawImageUnscaled(_thumb, inner.Location);
            }

            // Лупа в углу — намёк, что превью открывается во весь экран.
            int mg = Sc(18);
            RectangleF mr = new RectangleF(_thumbRect.Right - mg - Sc(8), _thumbRect.Bottom - mg - Sc(8), mg, mg);
            Icons.Draw(g, Glyph.Magnifier, mr, _thumbHot ? Color.White : Theme.TextDim, 1.6f);
            return y + h;
        }

        int Line(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, text, f, new Rectangle(x, y, w, h), c, Chrome.Left);
            return y + h;
        }

        int Wrapped(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Chrome.Wrap).Height;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), c, Chrome.Wrap);
            return y + h;
        }

        int Row(Graphics g, string label, string value, Color valueColor, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(value)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, label, Theme.FLabel, new Rectangle(x, y, w, h), Theme.TextDim, Chrome.Left);
            Chrome.DrawText(g, value, Theme.FLabel, new Rectangle(x, y, w, h), valueColor, Chrome.Right);
            return y + h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scrollTimer != null) _scrollTimer.Dispose();
                DropThumb();
            }
            base.Dispose(disposing);
        }
    }
}
