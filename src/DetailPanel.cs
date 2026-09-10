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
        readonly GlassButton _showInList = new GlassButton();

        SetEntry _set;
        PluginStat _plugin;
        bool _pluginMode;
        int _scroll;
        int _contentHeight;
        float _scrollTarget, _scrollCurrent;
        readonly SmoothScroller _scroller;

        Arrangement _arr;
        Bitmap _thumb;
        Size _thumbSize;
        Rectangle _thumbRect, _linkRect, _notesRect, _topRect;
        bool _thumbHot, _linkHot, _notesHot, _topHot;
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

        Action _showInListRequested;
        public event Action ShowInListRequested
        {
            add
            {
                _showInListRequested += value;
                ApplyAction();
            }
            remove
            {
                _showInListRequested -= value;
                ApplyAction();
            }
        }

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
            // нет, ни чинить, ни версионировать нечего. Обычные пилюли, не Quiet: рядом
            // с заливной Open in Live «тихая» кнопка читалась как подпись к ней, а не
            // как второе действие. Главная остаётся главной за счёт заливки, а не за
            // счёт того, что у соседей отняли обводку.
            // Surface = цвет карточки (CardFill), не фон окна: пилюля лежит на карточке
            // панели, и в непрозрачном режиме Backdrop красил её углы тёмным окном —
            // вокруг кнопки висел прямоугольник. Двухслойный Backdrop+overlay (как у
            // _action) не годится: на стекле SourceCopy в PaintGlassSurface пробивает
            // непрозрачную накладку в дыру при затухании ховера.
            _rescue.Text = "Rescue Project";
            _rescue.Surface = Theme.CardFill;
            _rescue.Click += delegate { if (RescueRequested != null) RescueRequested(); };
            Controls.Add(_rescue);

            _forks.Text = "Forks";
            _forks.Surface = Theme.CardFill;
            _forks.Click += delegate { if (ForksRequested != null) ForksRequested(); };
            Controls.Add(_forks);

            _showInList.Text = "Show in List";
            _showInList.Surface = Theme.CardFill;
            _showInList.Click += delegate { if (_showInListRequested != null) _showInListRequested(); };
            Controls.Add(_showInList);

            _scroller = new SmoothScroller(this,
                delegate (int s) { _scroll = s; _scrollCurrent = s; ClampScroll(); },
                delegate { return Math.Max(0, _contentHeight - BodyBottom); });
            ApplyAction();
        }

        int Pad { get { return Sc(Theme.PanelPad); } }

        static readonly TextFormatFlags PanelLeft =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        static readonly TextFormatFlags PanelRight =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

        /// <summary>
        /// С PanelLeft (TextFormatFlags.NoPadding) первый пиксель текста рисуется ровно от края rr.X.
        /// </summary>
        int UnderlinePad { get { return 0; } }

        // ------------------------------------------------------------- содержимое

        public void Show(SetEntry s)
        {
            bool same = _set != null && s != null && _set.Path == s.Path;
            _plugin = null;
            _pluginMode = false;
            _set = s;
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            if (_scroller != null) _scroller.SyncPosition(0);
            _topHot = false;
            _topRect = Rectangle.Empty;
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
            if (_scroller != null) _scroller.SyncPosition(0);
            _topHot = false;
            _topRect = Rectangle.Empty;
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
                // Кнопка «Show in Explorer» убрана: путь к плагину сам стал ссылкой,
                // и кнопка внизу повторяла то, на что и так хочется нажать.
                _action.Visible = false;
                _rescue.Visible = false;
                _forks.Visible = false;
                _showInList.Visible = false;
            }
            else
            {
                _action.Text = "Open in Live";
                _action.Visible = _set != null;
                bool inList = _showInListRequested != null;
                _showInList.Visible = !_pluginMode && _set != null && inList;
                _rescue.Visible = !_pluginMode && _set != null && !inList;
                _forks.Visible = !_pluginMode && _set != null && !inList && ForksRequested != null;
            }
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
            _showInList.SetBounds(Pad, _action.Top - Sc(8) - h, w, h);
            _rescue.SetBounds(Pad, _action.Top - Sc(8) - h, w, h);
            _forks.SetBounds(Pad, _rescue.Top - Sc(8) - h, w, h);
            ClampScroll();
        }

        /// <summary>
        /// Докуда можно рисовать содержимое. Место под кнопки резервируем всегда, чтобы
        /// список не прыгал, когда кнопка то есть, то нет, — но по-разному для двух
        /// режимов: у плагина кнопка всегда одна (Show in Explorer то есть, то нет,
        /// смотря установлен ли он), у сета их до трёх сразу (Open in Live, Rescue
        /// Project и Forks, либо Open in Live и Show in List). Переключение между режимами
        /// и так меняет содержимое панели целиком, так что разная высота резерва здесь не
        /// приводит к дёрганью, которого избегает сам резерв, — оно только внутри одного режима.
        /// </summary>
        int BodyBottom
        {
            get
            {
                int h = Sc(Theme.ControlH);
                int rows = _pluginMode ? 0 : (_showInListRequested != null ? 2 : (ForksRequested != null ? 3 : 2));
                int buttons = rows == 0 ? 0 : h * rows + Sc(8) * (rows - 1);
                return Height - Pad - buttons - Sc(16);
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
            _scroller.OnMouseWheel(e.Delta, Sc(60));
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------------- мышь

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool t = _arr != null && _arr.HasContent && _thumbRect.Contains(e.Location);
            bool l = !_linkRect.IsEmpty && _linkRect.Contains(e.Location);

            int rowHot = -1;
            for (int i = 0; i < _setRowRects.Count; i++)
                if (_setRowRects[i].Contains(e.Location)) { rowHot = i; break; }

            int pluginRowHot = -1;
            for (int i = 0; i < _pluginRowRects.Count; i++)
                if (_pluginRowRects[i].Contains(e.Location)) { pluginRowHot = i; break; }

            bool n = _set != null && !_pluginMode && _notesRect.Contains(e.Location);
            bool up = !_topRect.IsEmpty && _topRect.Contains(e.Location);
            if (up) { t = l = n = false; rowHot = pluginRowHot = -1; }

            if (t != _thumbHot || l != _linkHot || n != _notesHot || up != _topHot
                || rowHot != _setRowHot || pluginRowHot != _pluginRowHot)
            {
                _thumbHot = t; _linkHot = l; _notesHot = n; _topHot = up;
                _setRowHot = rowHot; _pluginRowHot = pluginRowHot;
                Cursor = (t || l || n || up || rowHot >= 0 || pluginRowHot >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_thumbHot || _linkHot || _notesHot || _topHot || _setRowHot >= 0 || _pluginRowHot >= 0)
            {
                _thumbHot = _linkHot = _notesHot = _topHot = false;
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
                if (_topHot)
                {
                    _scroll = 0;
                    _scrollTarget = _scrollCurrent = 0;
                    if (_scroller != null) _scroller.SyncPosition(0);
                    _topHot = false;
                    Invalidate();
                    return;
                }
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

            // Резиновый перелёт за край — тот же, что в списке и на плитках.
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            int y = Pad - _scroll - over;

            if (_pluginMode) { PaintPlugin(g, Pad, y, w, over); return; }
            _setRowRects.Clear();
            _setRowSets.Clear();
            _pluginRowRects.Clear();
            _pluginRowNames.Clear();

            if (_set == null)
            {
                _thumbRect = _linkRect = _topRect = Rectangle.Empty;

                // Раньше это была строка в левом верхнем углу пустой панели — читалась
                // как забытая подпись. Значок и две строки по центру: заголовок и что
                // делать дальше.
                int gl = Sc(30);
                float cy = Height / 2f - Sc(34);
                Icons.Draw(g, Glyph.ViewList,
                           new RectangleF(Pad + (w - gl) / 2f, cy, gl, gl),
                           Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 1.3f);
                Chrome.DrawText(g, "No set selected", Theme.FTitle,
                    new Rectangle(Pad, (int)(cy + gl + Sc(12)), w, Sc(24)),
                    Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), Chrome.CellCenter);
                Chrome.DrawText(g, "Pick one from the list", Theme.FLabel,
                    new Rectangle(Pad, (int)(cy + gl + Sc(34)), w, Sc(22)),
                    Theme.TextDim, Chrome.CellCenter);
                return;
            }

            // Заголовок и вес всей папки проекта в одной строке — то же число, что и в
            // колонке списка.
            int titleH = Sc(26);
            string size = MainForm.SizeMB(_set.ProjectSize);
            Size sw = TextRenderer.MeasureText(g, size, Theme.FLabel, new Size(w, titleH), PanelRight);
            Chrome.DrawText(g, _set.Name, Theme.FTitle,
                new Rectangle(Pad, y, w - sw.Width - Sc(10), titleH), Theme.Text, PanelLeft);
            Chrome.DrawText(g, size, Theme.FLabel,
                new Rectangle(Pad, y, w, titleH), Theme.TextDim, PanelRight);
            y += titleH + Sc(16);

            y = Thumb(g, Pad, y, w) + Sc(16);

            // Сам путь и есть ссылка: отдельная строка «Show in Explorer…» повторяла
            // то, на что и так хочется нажать. Не подчёркиваем — просто светлеет.
            int pathTop = y;
            int pathBottom = Wrapped(g, _set.Path, Theme.FLabel,
                                     _linkHot ? Theme.Text : Theme.TextDim, Pad, y, w);
            _linkRect = new Rectangle(Pad, pathTop, w, pathBottom - pathTop);
            y = pathBottom + Sc(24);

            y = TagsAndNote(g, y, w);
            y = Versions(g, y, w);

            // Файлы
            y = Line(g, "Files:", Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(8);
            Chrome.DrawText(g, Plural(_set.TotalRefs, "reference"), Theme.FLabel,
                new Rectangle(Pad, y, w, Sc(28)), Theme.Text, PanelLeft);
            // Цвет — только когда плохо. Зелёный ноль обещал событие, которого нет,
            // и красное среди него переставало бросаться в глаза.
            if (_set.MissingFiles > 0)
                Chrome.DrawText(g, _set.MissingFiles + " missing", Theme.FLabel,
                    new Rectangle(Pad, y, w, Sc(28)), Theme.Red, PanelRight);
            y += Sc(28) + Sc(24);

            if (_set.Error.Length > 0)
                y = Wrapped(g, _set.Error, Theme.FLabel, Theme.Red, Pad, y, w) + Sc(24);

            // Плагины — счётчик пропавших напротив заголовка, тем же приёмом, что у Files.
            Rectangle plugHead = new Rectangle(Pad, y, w, Sc(28));
            Chrome.DrawText(g, "Plugins" + " (" + _set.Plugins.Length + "):",
                            Theme.FLabel, plugHead, Theme.TextDim, PanelLeft);
            if (_set.MissingPlugins > 0)
                Chrome.DrawText(g, _set.MissingPlugins + " missing", Theme.FLabel,
                    plugHead, Theme.Red, PanelRight);
            y += Sc(28) + Sc(8);

            if (_set.Plugins.Length == 0)
                y = Line(g, "only Live's own devices",
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
                    Chrome.DrawText(g, _set.Plugins[i], Theme.FLabel, rr, c, PanelLeft);
                    if (m == MatchKind.OtherFormat)
                        Chrome.DrawText(g, "other format", Theme.FLabel,
                                        rr, Theme.TextDim, PanelRight);
                    if (hot)
                    {
                        // Подчёркиваем только эту строку и только по ширине текста — та же
                        // подача, что у кликабельных сетов в панели плагина.
                        Size ts = TextRenderer.MeasureText(g, _set.Plugins[i], Theme.FLabel, new Size(rr.Width, rr.Height), PanelLeft);
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
                        y = Line(g, "… " + (_set.Plugins.Length - shown) + " more",
                                 Theme.FLabel, Theme.TextDim, Pad, y, w);
                    else
                        y += Sc(28) * (_set.Plugins.Length - shown);
                }
            }

            _contentHeight = y + _scroll + over + Pad;
            ClampScroll();

            PaintScrollTop(g);
        }

        /// <summary>
        /// Кнопка «наверх» в правом нижнем углу тела панели. Список сетов у плагина
        /// бывает на сотню строк, и возвращаться к шапке колесом — долго.
        /// </summary>
        void PaintScrollTop(Graphics g)
        {
            if (_scroll < Sc(120)) { _topRect = Rectangle.Empty; return; }

            int d = Sc(30);
            _topRect = new Rectangle(Width - Pad - d, BodyBottom - d, d, d);
            Theme.PaintGlassSurface(this, g, _topRect, d / 2f,
                _topHot ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha);

            float cx = _topRect.X + d / 2f, cy = _topRect.Y + d / 2f, a = Sc(5);
            using (Pen pen = new Pen(_topHot ? Theme.Text : Theme.TextDim, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(cx - a, cy + a * 0.45f),
                    new PointF(cx, cy - a * 0.55f),
                    new PointF(cx + a, cy + a * 0.45f) });
            }
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
            int iconOffset = (int)Math.Round(icon * 0.16f);
            int iconLeft = Pad - iconOffset;
            int textX = Pad + icon - iconOffset * 2 + Sc(6);
            int textW = w - (textX - Pad);
            int top = y;

            if (tags.Count == 0 && note.Length == 0)
            {
                Rectangle line = new Rectangle(iconLeft, y, w + iconOffset, Sc(24));
                Icons.Draw(g, Glyph.Note,
                           new RectangleF(iconLeft, y + (Sc(24) - icon) / 2f - Sc(2), icon, icon),
                           _notesHot ? Theme.Text : Theme.TextDim, 1.3f);
                Chrome.DrawText(g, "Add tags or a note…",
                                Theme.FBadge, new Rectangle(textX, y, textW, Sc(24)),
                                _notesHot ? Theme.Text : Theme.TextDim, PanelLeft);
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
                    Icons.Draw(g, Glyph.Note, new RectangleF(iconLeft, y + Sc(1), icon, icon),
                               _notesHot ? Theme.Text : Theme.TextDim, 1.3f);
                y = Wrapped(g, note, Theme.FBadge,
                            _notesHot ? Theme.Text : Theme.TextDim,
                            tags.Count == 0 ? textX : Pad,
                            y, tags.Count == 0 ? textW : w);
            }

            _notesRect = new Rectangle(iconLeft, top, w + iconOffset, Math.Max(Sc(24), y - top));
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

            y = Line(g, "Versions" + " (" + all.Count + "):",
                     Theme.FLabel, Theme.TextDim, Pad, y, w) + Sc(4);

            foreach (SetEntry v in all)
            {
                if (Below(y + Sc(24))) break;
                bool current = ReferenceEquals(v, _set);
                bool hot = _setRowHot == _setRowRects.Count;
                Rectangle rr = new Rectangle(Pad, y, w, Sc(24));

                Color c = current ? Theme.Text : Theme.TextDim;
                Size ds = TextRenderer.MeasureText(g, v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge, new Size(rr.Width, rr.Height), PanelRight);
                Chrome.DrawText(g, v.Name, Theme.FBadge,
                                new Rectangle(rr.X, rr.Y, rr.Width - ds.Width - Sc(8), rr.Height),
                                hot ? Color.White : c, PanelLeft);
                Chrome.DrawText(g, v.Modified.ToLocalTime().ToString("yyyy-MM-dd"), Theme.FBadge,
                                rr, Theme.TextDim, PanelRight);

                // Текущую не подчёркиваем даже под курсором: щёлкать по ней незачем.
                if (hot && !current)
                {
                    Size ts = TextRenderer.MeasureText(g, v.Name, Theme.FBadge, new Size(rr.Width, rr.Height), PanelLeft);
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

        void PaintPlugin(Graphics g, int pad, int y, int w, int over)
        {
            _thumbRect = _linkRect = _topRect = Rectangle.Empty;
            _setRowRects.Clear();
            _setRowSets.Clear();
            _pluginRowRects.Clear();
            _pluginRowNames.Clear();
            PluginStat p = _plugin;
            if (p == null)
            {
                // Тот же пустой экран, что и у сетов: значок и две строки по центру.
                int gl = Sc(30);
                float cy = Height / 2f - Sc(34);
                Icons.Draw(g, Glyph.ViewList,
                           new RectangleF(pad + (w - gl) / 2f, cy, gl, gl),
                           Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 1.3f);
                Chrome.DrawText(g, "No plug-in selected", Theme.FTitle,
                    new Rectangle(pad, (int)(cy + gl + Sc(12)), w, Sc(24)),
                    Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF), Chrome.CellCenter);
                Chrome.DrawText(g, "Pick one to see where it is used", Theme.FLabel,
                    new Rectangle(pad, (int)(cy + gl + Sc(34)), w, Sc(22)),
                    Theme.TextDim, Chrome.CellCenter);
                return;
            }
            InstalledPlugin inst = p.Installed;

            y = Line(g, p.Name, Theme.FTitle, Theme.Text, pad, y, w) + Sc(2);
            y = Line(g, p.Vendor.Length > 0 ? p.Vendor : "unknown developer",
                     Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(16);

            string state;
            Color stateColor;
            if (p.Match == MatchKind.Exact && (inst == null || !inst.FileMissing))
            {
                state = "installed";
                stateColor = Theme.Green;
            }
            else if (p.Match == MatchKind.OtherFormat)
            {
                state = "installed as " + (inst != null ? inst.Format : "?");
                stateColor = Theme.TextDim;
            }
            else
            {
                state = "not installed";
                stateColor = Theme.Red;
            }

            y = Row(g, "Status:", state, stateColor, pad, y, w);
            y = Row(g, "Format:", p.Format, Theme.Text, pad, y, w);
            if (inst != null)
            {
                y = Row(g, "Version:", inst.Version, Theme.Text, pad, y, w);
                y = Row(g, "Category:", inst.Category.Replace("|", " · "), Theme.Text, pad, y, w);
            }
            // Скобки обязательны: «+» связывает раньше «?:», и без них выражение
            // сворачивалось в одно слово « sets», а число пропадало.
            y = Row(g, "Used in:", Plural(p.Sets, "set"),
                    p.Sets == 0 ? Theme.TextDim : Theme.Text, pad, y, w);
            y += Sc(16);

            // Путь к плагину — сам себе ссылка, как путь к сету у сетов.
            if (inst != null && inst.Path.Length > 0)
            {
                int pathTop = y;
                int pathBottom = Wrapped(g, inst.Path, Theme.FSmall,
                                         inst.FileMissing ? Theme.Red
                                                          : (_linkHot ? Theme.Text : Theme.TextDim),
                                         pad, y, w);
                _linkRect = new Rectangle(pad, pathTop, w, pathBottom - pathTop);
                y = pathBottom + Sc(24);
            }
            else _linkRect = Rectangle.Empty;

            List<SetEntry> users = _users;

            y = Line(g, "Sets" + " (" + p.Sets + "):", Theme.FLabel, Theme.TextDim, pad, y, w) + Sc(8);
            // if (users.Count == 0)
            //     y = Line(g, "not used anywhere — safe to uninstall",
            //              Theme.FLabel, Theme.TextDim, pad, y, w);
            int shown = 0;
            foreach (SetEntry s in users)
            {
                if (Below(y + Sc(28))) break;
                Rectangle rr = new Rectangle(pad, y, w, Sc(28));
                bool hot = _setRowHot == shown;
                Chrome.DrawText(g, s.Name, Theme.FLabel, rr, hot ? Color.White : Theme.Text, PanelLeft);
                if (hot)
                {
                    // Подчёркиваем только эту строку и только по ширине текста — не всю
                    // строку целиком, иначе выглядит как кнопка, а не как ссылка.
                    Size ts = TextRenderer.MeasureText(g, s.Name, Theme.FLabel, new Size(rr.Width, rr.Height), PanelLeft);
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
                    y = Line(g, "… " + (users.Count - shown) + " more",
                             Theme.FLabel, Theme.TextDim, pad, y, w);
                else
                    // Высоту недорисованных строк всё равно закладываем, иначе панель
                    // считает себя короче, чем есть, и до хвоста не долистать.
                    y += Sc(28) * (users.Count - shown);
            }

            _contentHeight = y + _scroll + over + pad;
            ClampScroll();

            PaintScrollTop(g);
        }

        // ------------------------------------------------------------------ куски

        int Thumb(Graphics g, int x, int y, int w)
        {
            int h = (int)Math.Round(w * 180f / 302f);      // пропорции из макета
            _thumbRect = new Rectangle(x, y, w, h);
            Theme.FillRound(g, _thumbRect, Sc(Theme.ThumbR), Theme.Bg);

            string hint = null;
            if (_arr == null) hint = "reading…";
            else if (_arr.Error != null) hint = "could not read the set";
            else if (!_arr.HasContent) hint = "arrangement is empty";

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

        /// <summary>«1 reference», «18 references» — согласование, а не «1 references».</summary>
        static string Plural(int n, string word)
        {
            return n + " " + word + (n == 1 ? "" : "s");
        }

        int Line(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, text, f, new Rectangle(x, y, w, h), c, PanelLeft);
            return y + h;
        }

        int Wrapped(Graphics g, string text, Font f, Color c, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = TextRenderer.MeasureText(g, text, f, new Size(w, int.MaxValue), Chrome.Wrap).Height;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), c, Chrome.Wrap);
            return y + h;
        }

        /// <summary>
        /// Строка «метка — значение». Значение прижато вправо, но не во всю ширину:
        /// длинное значение накрывало метку собой (на «Category: Fx · Dynamics ·
        /// Mastering» двоеточие исчезало под первым словом значения). Ширину под
        /// значение считаем как остаток после метки, и если не влезло — многоточие.
        /// </summary>
        int Row(Graphics g, string label, string value, Color valueColor, int x, int y, int w)
        {
            if (string.IsNullOrEmpty(value)) return y;
            int h = Sc(28);
            Chrome.DrawText(g, label, Theme.FLabel, new Rectangle(x, y, w, h), Theme.TextDim, PanelLeft);

            int lw = TextRenderer.MeasureText(g, label, Theme.FLabel, new Size(w, h), PanelLeft).Width;
            int vx = x + lw + Sc(12);
            int vw = Math.Max(Sc(24), x + w - vx);
            Chrome.DrawText(g, value, Theme.FLabel, new Rectangle(vx, y, vw, h), valueColor, PanelRight);
            return y + h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scroller != null) _scroller.Dispose();
                DropThumb();
            }
            base.Dispose(disposing);
        }
    }
}
