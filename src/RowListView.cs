using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    public sealed class Column
    {
        public string Id = "";            // устойчивый ключ колонки — для настроек и меню
        public string Title = "";
        public int Width;                 // 0 — колонка растягивается на остаток; иначе логические px
        public bool Right;                // текст прижат вправо
        public bool Sortable = true;
        public Font Font;
        public Color? Color;

        /// <summary>
        /// Ячейка — не строка, а список тегов через «, » (см. ProjectMeta.JoinTags),
        /// и рисовать её нужно пилюлями, как в панели сведений, а не сплошным текстом.
        /// Отдельного поля под сам список нет: строка и так уже разбирается на теги
        /// одним Split — заводить рядом ещё один массив ради того же самого незачем.
        /// </summary>
        public bool Chips;

        public Column(string title, int width) { Title = title; Width = width; }
    }

    /// <summary>
    /// Отметка в колонке: цветной текст и полоска того же цвета у правого края —
    /// так «сколько потеряно» читается одним взглядом по цвету, не вчитываясь в число.
    /// </summary>
    public struct CellMark
    {
        public int Column;
        public Color Color;
        public string Text;
        public CellMark(int column, Color color, string text)
        { Column = column; Color = color; Text = text; }
    }

    public sealed class RowData
    {
        public string[] Cells;
        public object Tag;
        public readonly List<CellMark> Marks = new List<CellMark>();

        /// <summary>Только когда у списка включён RowListView.ShowCheckboxes.</summary>
        public bool Checked = true;

        /// <summary>Есть ли что проигрывать: у строки без этого кнопка play не рисуется.</summary>
        public bool CanPlay = true;

        /// <summary>Закреплён ли проект — только когда у списка включён ShowPinIndicator.</summary>
        public bool Pinned;

        /// <summary>Одна из версий под раскрытой строкой, а не сам проект — имя в
        /// первой колонке отступает и получает короткий рельс перед собой,
        /// показывая вложенность.</summary>
        public bool ChildRow;
    }

    /// <summary>
    /// Таблица с собственной отрисовкой: строк тысячи, рисуются только видимые.
    /// Строка одноэтажная, выделение — пилюля во всю ширину, как в макете.
    /// </summary>
    public sealed class RowListView : GlassControl
    {
        readonly List<RowData> _rows = new List<RowData>();
        readonly List<Column> _columns = new List<Column>();

        int _scroll, _hot = -1, _selected = -1;
        bool _draggingBar;
        int _dragOffset;
        float _scrollTarget, _scrollCurrent;
        readonly Timer _scrollTimer;
        float[] _rowHoverFactors;
        float[] _rowEntrance;
        int _entranceStartTick;

        public void TriggerEntrance()
        {
            _rowEntrance = new float[_rows.Count];
            _entranceStartTick = Environment.TickCount;
            AnimEngine.Register(this);
            Invalidate();
        }

        public override bool OnAnimTick()
        {
            bool parentStill = base.OnAnimTick();
            bool anim = false;

            if (_rowHoverFactors == null || _rowHoverFactors.Length != _rows.Count)
                _rowHoverFactors = new float[_rows.Count];

            for (int i = 0; i < _rows.Count; i++)
            {
                float target = (i == _hot) ? 1.0f : 0.0f;
                float diff = target - _rowHoverFactors[i];
                if (Math.Abs(diff) > 0.01f)
                {
                    _rowHoverFactors[i] += diff * 0.35f;
                    anim = true;
                }
                else
                {
                    _rowHoverFactors[i] = target;
                }
            }

            int now = Environment.TickCount;
            if (_rowEntrance == null || _rowEntrance.Length != _rows.Count)
                _rowEntrance = new float[_rows.Count];

            for (int i = 0; i < _rows.Count; i++)
            {
                int delay = Math.Min(i, 30) * 15;
                int elapsed = now - _entranceStartTick - delay;
                if (elapsed > 0)
                {
                    float diff = 1.0f - _rowEntrance[i];
                    if (diff > 0.005f)
                    {
                        _rowEntrance[i] += diff * 0.25f;
                        anim = true;
                    }
                    else
                    {
                        _rowEntrance[i] = 1.0f;
                    }
                }
                else
                {
                    anim = true;
                }
            }

            if (anim) Invalidate();
            return parentStill || anim;
        }

        public event EventHandler SelectionChanged;
        public event EventHandler ItemActivated;      // двойной клик
        public event Action<int> HeaderClicked;
        public event Action<Point> HeaderRightClicked; // ПКМ по шапке — меню колонок
        public event Action ColumnsResized;            // отпустили край колонки — сохранить ширины
        public event Action<int, int> ColumnsReordered; // колонку перетащили: откуда, куда
        public event Action<int> RowCheckedChanged;    // клик по чекбоксу строки
        public event Action<int> RowPlayClicked;       // клик по кнопке прослушивания
        public event Action<int> RowPinClicked;        // клик по звёздочке закрепления
        public event Action<int, Point> RowRightClicked;
        public event Action<int> RowCountClicked;      // клик по хвостику «+3» / «−3»

        public int SortColumn = -1;
        public bool SortDescending;

        /// <summary>Можно ли менять ширину колонок и вызывать меню колонок правой кнопкой.</summary>
        public bool ColumnsConfigurable;

        /// <summary>
        /// Чекбокс перед первой колонкой — «включена ли эта строка» (например, папка
        /// временно исключена из сканирования, но остаётся в списке). Клик по нему не
        /// трогает выделение — это независимое переключение, а не выбор строки.
        /// </summary>
        public bool ShowCheckboxes;

        /// <summary>
        /// Треугольник «послушать» слева от первой колонки. Живёт в своём гутере, а не
        /// поверх имени: иначе он либо наезжает на текст, либо появляется только под
        /// курсором, и тогда о нём просто не узнают.
        /// </summary>
        public bool ShowPlayButton;

        /// <summary>Строка, чью кнопку play сейчас держит курсор, иначе -1.</summary>
        int _playHot = -1;

        /// <summary>
        /// Хвостик «+3» в конце ячейки — сколько версий проекта спрятано под строкой, и
        /// одновременно кнопка их раскрыть. Отделяется от имени тремя пробелами: рисуется
        /// он своим цветом и своим прямоугольником, поэтому его нужно уметь находить в
        /// готовом тексте ячейки. У раскрытой строки знак меняется на минус.
        /// </summary>
        public const string CountSep = "   ";
        public const string CountOpen = "+";
        public const string CountClose = "−";

        static int CountAt(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return -1;
            int i = cell.LastIndexOf(CountSep + CountOpen, StringComparison.Ordinal);
            if (i < 0) i = cell.LastIndexOf(CountSep + CountClose, StringComparison.Ordinal);
            return i;
        }

        // Куда попал этот хвостик при последней отрисовке — по строке на запись. Считать
        // его заново в обработчике мыши значило бы повторить весь разбор ячейки вместе с
        // замерами текста; проще запомнить то, что уже нарисовано.
        Rectangle[] _countHit;

        /// <summary>Строка, чей «+N» сейчас под курсором, иначе -1.</summary>
        int _countHot = -1;

        int CountAtPoint(Point p)
        {
            if (_countHit == null) return -1;
            for (int i = 0; i < _countHit.Length; i++)
                if (!_countHit[i].IsEmpty && _countHit[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>Запомнить нарисованный хвостик, чуть расширив его по горизонтали:
        /// «+3» — это три-четыре символа, и попадать в них впритык неудобно.</summary>
        void RememberCount(int row, int x, int y, int w, int h)
        {
            if (_countHit == null || row < 0 || row >= _countHit.Length) return;
            int pad = Sc(6);
            _countHit[row] = new Rectangle(x - pad, y, Math.Max(1, w) + pad * 2, h);
        }

        /// <summary>Строка, которая сейчас загружена в плеер (необязательно играет —
        /// см. Playing), — её треугольник горит светлым.</summary>
        public object PlayingTag;

        /// <summary>Плеер сейчас действительно звучит, а не на паузе. Иконка PlayingTag
        /// становится паузой только при обоих условиях разом — иначе после паузы через
        /// футер строка продолжала бы показывать паузу, хотя играть уже нечему.</summary>
        public bool Playing;

        /// <summary>
        /// Звёздочка закрепления в гутере, тем же приёмом, что и play: у закреплённой
        /// строки видна всегда, у остальных — пока курсор на самой строке, а не как у
        /// play (тот виден всегда, но приглушён) — иначе на длинном списке рябит.
        /// </summary>
        public bool ShowPinIndicator;
        public bool ShowHeaderPin = true;
        int _pinHot = -1;

        /// <summary>
        /// Звёздочка-переключатель в шапке, ровно над гутером со звёздами строк:
        /// «держать закреплённые сверху». Живёт здесь, а не в диалоге фильтров, потому
        /// что это не отбор (ничего не прячет), а порядок — и стоять ей логично там же,
        /// где стоят сами звёзды, которыми закрепляют.
        /// </summary>
        public bool PinnedFirst;
        public event Action PinnedFirstToggled;
        bool _headPinHot;

        /// <summary>
        /// После какой строки провести разделитель групп (-1 — не проводить). Сам список
        /// не знает, что такое «закреплённые», и знать не должен: где граница — решает
        /// тот, кто собрал строки.
        /// </summary>
        public int GroupSeparatorAfter = -1;

        /// <summary>
        /// Растворять ли нижние строки в фон — как в макете у длинного списка. На стекле
        /// эффекта не даёт: градиент подмешивает свой же полупрозрачный фон поверх уже
        /// размытых обоев, альфа копится слоями, и вместо мягкого исчезновения получается
        /// чёткий тёмный прямоугольник в самом низу списка. На непрозрачном окне это не
        /// проблема — там гасим как раньше.
        /// </summary>
        public bool FadeBottom = true;

        // Перетаскивание правого края колонки: индекс колонки или -1.
        int _resizeCol = -1;

        // Перетаскивание самого заголовка — как в проводнике.
        //
        // Нажатие и перетаскивание разведены намеренно: по заголовку и сортируют, и
        // таскают, и отличить одно от другого можно только по тому, поехала мышь или
        // нет. Поэтому на нажатии лишь запоминаем колонку (_pressCol), тащить начинаем
        // после порога в несколько пикселей (_dragCol), а сортируем на отпускании — и
        // только если перетаскивания так и не случилось.
        int _pressCol = -1;
        int _pressX;
        int _dragCol = -1;
        int _dragX;
        int _dropAt = -1;      // куда встанет колонка, если отпустить сейчас

        int DragThreshold { get { return Sc(5); } }

        // Полоски-ручки между всеми заголовками: видны все разом, как только курсор
        // зашёл в шапку — чтобы сразу было понятно, где вообще можно тянуть, а не
        // нащупывать границу вслепую. Непрозрачность общая для всех, 0..1, короткая
        // анимация — вся дорожка за два тика таймера по 10 мс, то есть около 20 мс.
        bool _headerHot;
        float _gripAlpha;
        readonly Timer _gripTimer;

        public RowListView()
        {
            Cursor = Cursors.Hand;
            _gripTimer = new Timer();
            _gripTimer.Interval = 10;
            _gripTimer.Tick += delegate { StepGripFade(); };
            _scrollTimer = new Timer();
            _scrollTimer.Interval = 16;
            _scrollTimer.Tick += delegate { ScrollTick(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _gripTimer.Dispose(); _scrollTimer.Dispose(); }
            base.Dispose(disposing);
        }

        public List<Column> ColumnList { get { return _columns; } }

        int RowHeight { get { return Sc(Theme.RowH); } }
        int HeaderHeight { get { return Sc(45); } }
        int PadX { get { return Sc(Theme.CellPadX); } }

        // Чекбокс живёт в отдельной колонке слева от первой обычной колонки — левый
        // отступ содержимого от этого растёт, правый (PadX) не меняется.
        int CheckW { get { return ShowCheckboxes ? Sc(30) : 0; } }
        int PinW { get { return ShowPinIndicator ? Sc(32) : 0; } }
        int PlayW { get { return ShowPlayButton ? Sc(32) : 0; } }
        int LeftX { get { return PadX + CheckW + PinW + PlayW; } }

        /// <summary>Звёздочка закрепления — гутер между чекбоксом и кнопкой play, того
        /// же размера, что и сама play.</summary>
        Rectangle PinRect(int top, int rowH)
        {
            int s = Sc(26);
            return new Rectangle(PadX + CheckW + (PinW - s) / 2, top + (rowH - s) / 2, s, s);
        }

        /// <summary>Курсор на звёздочке шапки? Она занимает тот же гутер, что и звёзды строк.</summary>
        bool OnHeaderPin(Point p)
        {
            return ShowPinIndicator && ShowHeaderPin && p.Y < HeaderHeight && PinRect(0, HeaderHeight).Contains(p);
        }

        /// <summary>Кнопка play строки — гутер между звёздочкой и первой колонкой.</summary>
        Rectangle PlayRect(int top, int rowH)
        {
            int s = Sc(26);
            return new Rectangle(PadX + CheckW + PinW + (PlayW - s) / 2, top + (rowH - s) / 2, s, s);
        }

        public List<RowData> Rows { get { return _rows; } }

        public void SetColumns(params Column[] cols)
        {
            _columns.Clear();
            _columns.AddRange(cols);
            Invalidate();
        }

        public void SetRows(List<RowData> rows) { SetRows(rows, true); }

        /// <summary>
        /// animate=false — показать строки сразу, без появления снизу. Нужно набору в
        /// поиске: список пересобирается на каждую букву, и появление запускалось заново
        /// от каждого нажатия, вместо того чтобы строки просто отфильтровались.
        /// </summary>
        public void SetRows(List<RowData> rows, bool animate)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            _selected = -1;
            _hot = -1;
            ClampScroll();

            if (animate) { TriggerEntrance(); return; }

            // Массив нулей означал бы «строки ещё не появились», и без запущенной
            // анимации список остался бы пустым.
            _rowEntrance = new float[_rows.Count];
            for (int i = 0; i < _rowEntrance.Length; i++) _rowEntrance[i] = 1f;
            Invalidate();
        }

        public RowData Selected
        {
            get { return _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null; }
        }

        /// <summary>
        /// Программное выделение строки — например, переход к конкретному сету из
        /// панели деталей плагина, а не клик по самому списку. Тихо ничего не делает,
        /// если подходящей строки сейчас нет (её могли отфильтровать).
        /// </summary>
        public void SelectRow(Predicate<RowData> match)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!match(_rows[i])) continue;
                _selected = i;
                EnsureVisible(i);
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                return;
            }
        }

        /// <summary>
        /// Прокрутка в пикселях. Нужна автообновлению каталога: SetRows всегда ставит
        /// список в начало, а обновление приходит само, без нажатия, — и выдёргивать
        /// человека наверх посреди чтения оно права не имеет. Присваивание ставит
        /// список сразу, без доводки: это восстановление прежнего вида, а не прокрутка.
        /// </summary>
        public int ScrollOffset
        {
            get { return _scroll; }
            set
            {
                _scroll = value;
                ClampScroll();
                _scrollTarget = _scrollCurrent = _scroll;
                Invalidate();
            }
        }

        int ContentHeight { get { return _rows.Count * RowHeight + Sc(8) + HeaderHeight; } }

        void ClampScroll()
        {
            int max = Math.Max(0, ContentHeight - Height);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnResize(EventArgs e) { ClampScroll(); base.OnResize(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scrollTarget -= (int)(e.Delta / 120f * RowHeight * 3);
            ClampScrollTarget();
            if (!_scrollTimer.Enabled) _scrollTimer.Start();
            base.OnMouseWheel(e);
        }

        void ClampScrollTarget()
        {
            int max = Math.Max(0, _rows.Count * RowHeight - (Height - HeaderHeight));
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

        int RowAt(int y)
        {
            int top = HeaderHeight - _scroll;
            if (y < HeaderHeight) return -1;
            int idx = (y - top) / RowHeight;
            return idx >= 0 && idx < _rows.Count ? idx : -1;
        }

        Rectangle BarRect()
        {
            int content = ContentHeight;
            if (content <= Height) return Rectangle.Empty;
            int track = Height - HeaderHeight - Sc(10);
            int h = Math.Max(Sc(40), (int)(track * (float)Height / content));
            int max = Math.Max(1, content - Height);
            int y = HeaderHeight + Sc(5) + (int)((track - h) * (_scroll / (float)max));
            return new Rectangle(Width - Sc(8), y, Sc(4), h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizeCol >= 0) { DoResize(e.X); return; }

            // Заголовок уже тащат — ведём его и считаем, куда он встанет.
            if (_dragCol >= 0)
            {
                _dragX = e.X;
                int at = DropIndexAt(e.X);
                if (at != _dropAt) _dropAt = at;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                return;
            }

            // Взялись за заголовок и повели в сторону — это перетаскивание, а не клик
            // по сортировке. Порог нужен, чтобы дрожь руки на обычном щелчке не
            // считалась переносом колонки.
            if (_pressCol >= 0 && e.Button == MouseButtons.Left
                && Math.Abs(e.X - _pressX) > DragThreshold
                && ColumnsConfigurable && _columns.Count > 1)
            {
                _dragCol = _pressCol;
                _dragX = e.X;
                _dropAt = DropIndexAt(e.X);
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                return;
            }

            if (_draggingBar)
            {
                Rectangle bar = BarRect();
                int track = Height - HeaderHeight - Sc(10);
                int max = Math.Max(1, ContentHeight - Height);
                float t = (e.Y - _dragOffset - HeaderHeight - Sc(5)) / (float)Math.Max(1, track - bar.Height);
                _scroll = (int)(t * max);
                _scrollTarget = _scrollCurrent = _scroll;
                ClampScroll();
                Invalidate();
                return;
            }

            // В шапке у краёв изменяемых колонок курсор — «раздвинуть», в остальном рука.
            // Полоски-ручки показываем все разом, лишь только курсор зашёл в шапку.
            if (e.Y < HeaderHeight)
            {
                int grip = GripAt(e.X, ComputeWidths());
                SetHeaderHot(true);
                Cursor = grip >= 0 ? Cursors.VSplit : Cursors.Hand;
                bool headPin = OnHeaderPin(e.Location);
                if (headPin != _headPinHot) { _headPinHot = headPin; Invalidate(); }
                if (_hot != -1 || _playHot != -1 || _pinHot != -1)
                { _hot = -1; _playHot = -1; _pinHot = -1; Invalidate(); }
                return;
            }
            if (_headPinHot) { _headPinHot = false; Invalidate(); }
            SetHeaderHot(false);
            if (Cursor != Cursors.Hand) Cursor = Cursors.Hand;

            int idx = RowAt(e.Y);
            int playHot = -1, pinHot = -1;
            if (idx >= 0)
            {
                int top = HeaderHeight + idx * RowHeight - _scroll;
                if (ShowPlayButton && _rows[idx].CanPlay && PlayRect(top, RowHeight).Contains(e.Location))
                    playHot = idx;
                if (ShowPinIndicator && PinRect(top, RowHeight).Contains(e.Location))
                    pinHot = idx;
            }
            int countHot = CountAtPoint(e.Location);
            if (idx != _hot || playHot != _playHot || pinHot != _pinHot || countHot != _countHot)
            {
                _hot = idx; _playHot = playHot; _pinHot = pinHot; _countHot = countHot;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        /// <summary>
        /// Между какими колонками встанет перетаскиваемый заголовок, если отпустить его
        /// в точке px. Считается по СЕРЕДИНАМ колонок, а не по их краям: пока курсор не
        /// перевалил за середину соседа, менять их местами рано — иначе колонки прыгают
        /// туда-сюда от малейшего движения на границе.
        ///
        /// Возвращает позицию вставки в списке колонок: 0 — перед первой, Count — после
        /// последней.
        /// </summary>
        int DropIndexAt(int px)
        {
            int[] widths = ComputeWidths();
            int x = LeftX;
            for (int c = 0; c < _columns.Count; c++)
            {
                if (px < x + widths[c] / 2) return c;
                x += widths[c];
            }
            return _columns.Count;
        }

        /// <summary>
        /// Индекс колонки, за левый край которой можно тянуть, если курсор рядом с ним.
        /// Тянущаяся колонка имени слева, а сумма ширин прижата к правому краю списка,
        /// поэтому у фиксированных колонок двигается именно левая граница, а не правая.
        /// </summary>
        int GripAt(int px, int[] widths)
        {
            if (!ColumnsConfigurable) return -1;
            int x = LeftX;
            for (int c = 0; c < _columns.Count; c++)
            {
                if (_columns[c].Width > 0 && Math.Abs(px - x) <= Sc(4)) return c;
                x += widths[c];
            }
            return -1;
        }

        void DoResize(int mouseX)
        {
            int[] widths = ComputeWidths();

            // Правый край колонки прижат к правому краю списка и при перетаскивании
            // не двигается; новую ширину задаёт положение левого края под курсором.
            int rightX = LeftX;
            for (int c = 0; c <= _resizeCol; c++) rightX += widths[c];
            int newScaled = rightX - mouseX;

            // Колонка не схлопывается, и у тянущейся колонки имени остаётся минимум места.
            int minW = Sc(48);
            bool hasFlex = false;
            int otherFixed = 0;
            for (int c = 0; c < _columns.Count; c++)
            {
                if (_columns[c].Width == 0) hasFlex = true;
                else if (c != _resizeCol) otherFixed += widths[c];
            }
            int maxW = Width - LeftX - PadX - otherFixed - (hasFlex ? Sc(80) : 0);
            if (maxW < minW) maxW = minW;
            if (newScaled < minW) newScaled = minW;
            if (newScaled > maxW) newScaled = maxW;

            // В настройках ширина логическая (96 dpi) — так она переживает смену монитора.
            float scale = DeviceDpi / 96f;
            _columns[_resizeCol].Width = Math.Max(1, (int)Math.Round(newScaled / scale));
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1 || _playHot != -1 || _pinHot != -1 || _countHot != -1 || _headPinHot)
            {
                _hot = -1; _playHot = -1; _pinHot = -1; _countHot = -1; _headPinHot = false;
                AnimEngine.Register(this);
                Invalidate();
            }
            SetHeaderHot(false);
            base.OnMouseLeave(e);
        }

        void SetHeaderHot(bool hot)
        {
            if (_headerHot == hot) return;
            _headerHot = hot;
            if (!_gripTimer.Enabled) _gripTimer.Start();
        }

        void StepGripFade()
        {
            float target = _headerHot ? 1f : 0f;
            const float step = 0.5f;    // два тика по 10 мс = вся дорожка 0..1 за ~20 мс
            if (_gripAlpha < target) _gripAlpha = Math.Min(target, _gripAlpha + step);
            else if (_gripAlpha > target) _gripAlpha = Math.Max(target, _gripAlpha - step);

            Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
            if (_gripAlpha == target) _gripTimer.Stop();
        }

        int ColumnAt(int px)
        {
            int[] widths = ComputeWidths();
            int x = LeftX;
            for (int c = 0; c < _columns.Count; c++)
            {
                if (px >= x && px < x + widths[c]) return c;
                x += widths[c];
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();

            if (e.Y < HeaderHeight)
            {
                if (e.Button == MouseButtons.Right)
                {
                    if (ColumnsConfigurable && HeaderRightClicked != null) HeaderRightClicked(e.Location);
                    return;
                }

                // Звёздочка шапки перехватывает клик раньше сортировки: она стоит в
                // гутере, левее первой колонки, так что ColumnAt её всё равно не видит,
                // но проверить надо до GripAt — ручка первой колонки рядом.
                if (OnHeaderPin(e.Location))
                {
                    PinnedFirst = !PinnedFirst;
                    Invalidate();
                    if (PinnedFirstToggled != null) PinnedFirstToggled();
                    return;
                }

                int[] widths = ComputeWidths();
                int grip = GripAt(e.X, widths);
                if (grip >= 0) { _resizeCol = grip; return; }

                // Ни сортировки, ни перетаскивания прямо сейчас — только запоминаем, за
                // что взялись: что это было, станет ясно по движению мыши. См. _pressCol.
                _pressCol = ColumnAt(e.X);
                _pressX = e.X;
                return;
            }

            Rectangle bar = BarRect();
            if (!bar.IsEmpty &&
                new Rectangle(bar.X - Sc(6), bar.Y, bar.Width + Sc(10), bar.Height).Contains(e.Location))
            {
                _draggingBar = true;
                _dragOffset = e.Y - bar.Y;
                return;
            }

            int idx = RowAt(e.Y);

            if (e.Button == MouseButtons.Right)
            {
                // ПКМ по строке выделяет её и отдаёт меню наружу — как в проводнике.
                if (idx >= 0 && idx != _selected)
                {
                    _selected = idx;
                    Invalidate();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                }
                if (RowRightClicked != null) RowRightClicked(idx, e.Location);
                return;
            }

            // Хвостик «+3» — независимая кнопка «показать остальные версии», как play и
            // звёздочка: выделение строки он не трогает, иначе панель подробностей
            // прыгала бы от каждого раскрытия.
            if (idx >= 0 && CountAtPoint(e.Location) == idx)
            {
                if (RowCountClicked != null) { RowCountClicked(idx); return; }
            }

            if (idx >= 0 && ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - _scroll;
                if (PinRect(pinTop, RowHeight).Contains(e.Location))
                {
                    // Закрепление — тоже независимое действие, как и play ниже.
                    if (RowPinClicked != null) RowPinClicked(idx);
                    return;
                }
            }

            if (idx >= 0 && ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - _scroll;
                if (PlayRect(top, RowHeight).Contains(e.Location))
                {
                    // Прослушивание — независимое действие: выделение строки не трогаем,
                    // иначе панель подробностей будет прыгать от каждого нажатия play.
                    if (RowPlayClicked != null) RowPlayClicked(idx);
                    return;
                }
            }

            if (idx >= 0 && ShowCheckboxes && e.X < PadX + CheckW)
            {
                // Чекбокс — самостоятельное переключение, выделение строки не трогаем.
                RowData row = _rows[idx];
                row.Checked = !row.Checked;
                Invalidate();
                if (RowCheckedChanged != null) RowCheckedChanged(idx);
                return;
            }
            if (idx >= 0 && idx != _selected)
            {
                _selected = idx;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_resizeCol >= 0)
            {
                _resizeCol = -1;
                if (ColumnsResized != null) ColumnsResized();
            }

            if (_dragCol >= 0)
            {
                int from = _dragCol, to = _dropAt;
                _dragCol = -1; _dropAt = -1; _pressCol = -1;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                // to == from и to == from+1 — обе «поставить туда же, откуда взяли».
                if (to >= 0 && to != from && to != from + 1 && ColumnsReordered != null)
                    ColumnsReordered(from, to);
            }
            else if (_pressCol >= 0)
            {
                // Заголовок нажали и отпустили, никуда не уводя, — это сортировка.
                int col = _pressCol;
                _pressCol = -1;
                if (e.Button == MouseButtons.Left && col < _columns.Count
                    && _columns[col].Sortable && HeaderClicked != null)
                    HeaderClicked(col);
            }

            _draggingBar = false;
            base.OnMouseUp(e);
        }

        /// <summary>
        /// Двойной клик активирует строку — но только если он пришёлся на саму строку,
        /// а не на одну из её независимых кнопок (хвостик «+N», звёздочка, play,
        /// чекбокс). У Windows второй клик быстрого двойного тапа не идёт через
        /// OnMouseDown второй раз — он приходит сюда, минуя те же проверки, и без этой
        /// защиты быстрый повторный тап по «+N» успевал не только раскрыть строку, но
        /// и открыть сет в Live между первым и вторым кликом.
        /// </summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int idx = RowAt(e.Y);
            if (idx < 0 || OnRowAccessory(idx, e.Location))
            {
                base.OnMouseDoubleClick(e);
                return;
            }
            if (ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        /// <summary>Курсор сейчас над одной из кнопок строки idx — хвостиком «+N»,
        /// звёздочкой, play или чекбоксом, — а не над самой строкой.</summary>
        bool OnRowAccessory(int idx, Point p)
        {
            if (CountAtPoint(p) == idx) return true;

            if (ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - _scroll;
                if (PinRect(pinTop, RowHeight).Contains(p)) return true;
            }
            if (ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - _scroll;
                if (PlayRect(top, RowHeight).Contains(p)) return true;
            }
            if (ShowCheckboxes && p.X < PadX + CheckW) return true;

            return false;
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = 0;
            if (e.KeyCode == Keys.Down) step = 1;
            else if (e.KeyCode == Keys.Up) step = -1;
            else if (e.KeyCode == Keys.PageDown) step = PageStep;
            else if (e.KeyCode == Keys.PageUp) step = -PageStep;

            if (step != 0 && MoveSelection(step)) e.Handled = true;
            base.OnKeyDown(e);
        }

        public int SelectedIndex { get { return _selected; } }

        /// <summary>
        /// Вернуть выделение на строку с тем же номером. Нужно перестановке колонок:
        /// строки там те же самые, меняется только их разметка, а SetRows всё равно
        /// сбрасывает выделение — и без этого колонка переезжала бы ценой потери того,
        /// что было выбрано.
        /// </summary>
        public void SelectIndex(int idx)
        {
            if (idx < 0 || idx >= _rows.Count || idx == _selected) return;
            _selected = idx;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        /// <summary>Насколько прыгает PageUp/PageDown — на экран строк.</summary>
        public int PageStep { get { return Math.Max(1, (Height - HeaderHeight) / RowHeight); } }

        /// <summary>
        /// Сдвинуть выделение на step строк. Отдельно от OnKeyDown, потому что стрелки
        /// приходят не только сюда: главное окно ведёт ими по каталогу независимо от
        /// того, где сейчас фокус.
        ///
        /// true и на краю списка — клавишу мы всё равно съели, и отдавать её дальше в
        /// навигацию по фокусу нельзя: она уведёт выделение в соседний контрол.
        /// </summary>
        public bool MoveSelection(int step)
        {
            if (_rows.Count == 0) return false;
            int want = Math.Max(0, Math.Min(_rows.Count - 1, (_selected < 0 ? 0 : _selected) + step));
            if (want != _selected)
            {
                _selected = want;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            EnsureVisible(want);
            return true;
        }

        /// <summary>
        /// Куда ставить контекстное меню, вызванное с клавиатуры: у левого края строки,
        /// по её середине. Мыши тут нет, а меню должно выйти у той строки, о которой
        /// речь, — не там, где случайно оставили курсор.
        /// </summary>
        public Point RowMenuPoint(int idx)
        {
            int top = HeaderHeight + idx * RowHeight - _scroll + RowHeight / 2;
            return new Point(LeftX, Math.Max(0, Math.Min(Height - 1, top)));
        }

        void EnsureVisible(int idx)
        {
            int top = HeaderHeight + idx * RowHeight - _scroll;
            if (top < HeaderHeight) _scroll += top - HeaderHeight;
            else if (top + RowHeight > Height) _scroll += top + RowHeight - Height;
            _scrollTarget = _scrollCurrent = _scroll;
            ClampScroll();
        }

        // ------------------------------------------------------------- отрисовка

        int[] ComputeWidths()
        {
            int[] w = new int[_columns.Count];
            int fixedTotal = 0, flexCount = 0;
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Width > 0) { w[i] = Sc(_columns[i].Width); fixedTotal += w[i]; }
                else flexCount++;
            }
            int available = Width - LeftX - PadX - fixedTotal;
            for (int i = 0; i < _columns.Count; i++)
                if (_columns[i].Width == 0) w[i] = Math.Max(Sc(80), available / Math.Max(1, flexCount));
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            int[] widths = ComputeWidths();
            int rowH = RowHeight;
            int pillH = Sc(Theme.RowPillH);

            // Пока ручки колонок проявляются/гаснут, таймер дёргает Invalidate только
            // по прямоугольнику шапки — тело списка в клип не попадает и всё равно
            // отсекается, так что весь проход по строкам ниже можно пропустить.
            bool headerOnly = e.ClipRectangle.Bottom <= HeaderHeight;

            if (!headerOnly)
            {
                Region oldClip = g.Clip;
                g.SetClip(new Rectangle(0, HeaderHeight, Width, Math.Max(0, Height - HeaderHeight)));

                // Прямоугольники хвостиков «+N» собираем заново за каждый полный проход:
                // строки уехали прокруткой, и вчерашние координаты кликать нельзя.
                if (_countHit == null || _countHit.Length != _rows.Count)
                    _countHit = new Rectangle[_rows.Count];
                Array.Clear(_countHit, 0, _countHit.Length);

                int first = Math.Max(0, (_scroll - Sc(8)) / rowH);
                for (int i = first; i < _rows.Count; i++)
                {
                    int top = HeaderHeight + i * rowH - _scroll;
                    if (top > Height) break;

                    float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                    if (entrance <= 0.001f) continue;

                    int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                    int topAnim = top + offsetY;

                    RowData row = _rows[i];

                    Rectangle pill = new Rectangle(0, topAnim + (rowH - pillH) / 2, Width, pillH);
                    float hoverFactor = (_rowHoverFactors != null && i < _rowHoverFactors.Length) ? _rowHoverFactors[i] : (i == _hot ? 1f : 0f);

                    if (i == _selected) Theme.PaintGlassSurface(this, g, pill, pillH / 2f, (int)Math.Round(Theme.GlassSurfacePressedAlpha * entrance));
                    else if (hoverFactor > 0.001f)
                    {
                        Color c = Color.FromArgb((int)Math.Round(hoverFactor * Theme.RowHover.A * entrance), Theme.RowHover);
                        Theme.FillRound(g, pill, pillH / 2f, c);
                    }

                    if (ShowCheckboxes) PaintCheckbox(g, topAnim, rowH, row.Checked);
                    if (ShowPinIndicator && (row.Pinned || i == _hot))
                        PaintPin(g, topAnim, rowH, i == _pinHot, row.Pinned);
                    if (ShowPlayButton && row.CanPlay)
                        PaintPlay(g, topAnim, rowH, i == _playHot,
                                  Playing && PlayingTag != null && ReferenceEquals(PlayingTag, row.Tag));

                    // Выключенная строка (чекбокс снят) читается приглушённой — сама по себе,
                    // без нужды лезть в глаза, пока не включена обратно.
                    bool dim = ShowCheckboxes && !row.Checked;

                    // У выделенной строки приглушённые ячейки читаются в полную силу:
                    // выделение — это «сейчас смотрим сюда», и дата с темпом там нужны
                    // так же, как имя. Шрифт при этом не меняется — только цвет, иначе
                    // строка дёргалась бы по ширине от одного щелчка по ней.
                    bool bright = i == _selected;

                    int x = LeftX;
                    for (int c = 0; c < _columns.Count && c < row.Cells.Length; c++)
                    {
                        Column col = _columns[c];
                        Font f = col.Font ?? Theme.FBody;
                        Color color = dim ? Theme.TextDim
                                          : (col.Color ?? (c == 0 || bright ? Theme.Text : Theme.TextDim));
                        if (entrance < 1.0f) color = Color.FromArgb((int)Math.Round(color.A * entrance), color);

                        // Версия под раскрытой строкой отступает в колонке имени, а прямо
                        // в этом отступе — короткий рельс: только у дочерних строк и только
                        // рядом с текстом, а не через всю строку, — как «|» перед именем в
                        // дереве файлов.
                        bool childHere = row.ChildRow && col.Id == "Set";
                        int indent = childHere ? Sc(20) : 0;
                        if (childHere)
                        {
                            Rectangle rail = new Rectangle(x + Sc(6), topAnim + Sc(4), Sc(2), rowH - Sc(8));
                            Color railColor = Color.FromArgb((int)Math.Round(120 * entrance), Theme.TextDim);
                            g.FillRectangle(Theme.GetBrush(railColor), rail);
                        }
                        Rectangle cr = new Rectangle(x + indent, topAnim, Math.Max(0, widths[c] - Sc(10) - indent), rowH);
                        if (col.Chips) PaintChips(g, cr, row.Cells[c], color);
                        else
                        {
                            string cellText = row.Cells[c];
                            int plusIdx = !col.Right ? CountAt(cellText) : -1;
                            if (plusIdx > 0)
                            {
                                string mainText = cellText.Substring(0, plusIdx);
                                string countText = cellText.Substring(plusIdx + CountSep.Length);

                                // Под курсором хвостик светлеет — иначе о том, что по нему
                                // можно щёлкнуть и раскрыть версии, никто не догадается.
                                Color countColor = i == _countHot ? Theme.Text : Theme.TextDim;
                                if (entrance < 1.0f) countColor = Color.FromArgb((int)Math.Round(countColor.A * entrance), countColor);

                                Size countSz = TextRenderer.MeasureText(countText, f);
                                Size mainSz = TextRenderer.MeasureText(mainText, f);

                                int countX = cr.X + mainSz.Width - Sc(4);
                                if (countX + countSz.Width <= cr.Right)
                                {
                                    Rectangle mainR = new Rectangle(cr.X, cr.Y, mainSz.Width, cr.Height);
                                    Chrome.DrawText(g, mainText, f, mainR, color, Chrome.Left);

                                    Rectangle countR = new Rectangle(countX, cr.Y, Math.Max(0, cr.Right - countX), cr.Height);
                                    Chrome.DrawText(g, countText, f, countR, countColor, Chrome.Left);
                                    RememberCount(i, countX, cr.Y, countSz.Width, cr.Height);
                                }
                                else
                                {
                                    int maxMainW = Math.Max(0, cr.Width - countSz.Width - Sc(4));
                                    Rectangle mainR = new Rectangle(cr.X, cr.Y, maxMainW, cr.Height);
                                    Chrome.DrawText(g, mainText, f, mainR, color, Chrome.Left);

                                    int cX = cr.X + maxMainW + Sc(4);
                                    if (cX < cr.Right)
                                    {
                                        Rectangle countR = new Rectangle(cX, cr.Y, Math.Max(0, cr.Right - cX), cr.Height);
                                        Chrome.DrawText(g, countText, f, countR, countColor, Chrome.Left);
                                        RememberCount(i, cX, cr.Y, Math.Min(countSz.Width, cr.Right - cX), cr.Height);
                                    }
                                }
                            }
                            else
                            {
                                Chrome.DrawText(g, row.Cells[c], f, cr, color,
                                               col.Right ? Chrome.Right : Chrome.Left);
                            }
                        }
                        x += widths[c];
                    }

                    foreach (CellMark m in row.Marks) PaintMark(g, widths, top, rowH, m);

                    // Граница между закреплёнными и остальными: волосяная линия в тот же
                    // цвет, что и прочие линейки, во всю ширину списка.
                    if (i == GroupSeparatorAfter)
                    {
                        int sy = top + rowH - 1;
                        using (Pen sep = new Pen(Theme.Hairline))
                            g.DrawLine(sep, PadX, sy, Width - PadX, sy);
                    }
                }

                g.Clip = oldClip;

                // Нижние строки растворяются в фоне — список не обрывается ровным срезом.
                // На стекле это гасим целиком, см. комментарий у FadeBottom.
                if (FadeBottom && !Glass.Enabled && ContentHeight > Height)
                {
                    int fadeH = Sc(110);
                    Rectangle fr = new Rectangle(0, Height - fadeH, Width, fadeH);
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new Rectangle(fr.X, fr.Y - 1, fr.Width, fr.Height + 2),
                        Color.FromArgb(0, Surface), Surface, LinearGradientMode.Vertical))
                        g.FillRectangle(lb, fr);
                }
            }

            PaintHeader(g, widths);

            if (!headerOnly)
            {
                Rectangle bar = BarRect();
                if (!bar.IsEmpty && (Hot || _draggingBar))
                    Theme.FillRound(g, bar, bar.Width / 2f, Theme.SurfacePressed);
            }
        }

        // Заголовок и отметка в данных под ним всегда делят один и тот же прямоугольник —
        // левую границу собственной колонки, без залезания в соседнюю. Ширину колонок,
        // где живут отметки (Plugins/Files и т.п.), поэтому задаём с запасом на стороне
        // вызова (MainForm), а не растим их здесь за счёт соседей.
        Rectangle ColSlot(int[] widths, int top, int rowH, int col)
        {
            int x = LeftX;
            for (int c = 0; c < col; c++) x += widths[c];
            return new Rectangle(x, top, Math.Max(0, widths[col] - Sc(10)), rowH);
        }

        /// <summary>Квадратный чекбокс в гутере слева от первой колонки.</summary>
        void PaintCheckbox(Graphics g, int top, int rowH, bool on)
        {
            float cs = Sc(16);
            RectangleF cb = new RectangleF(PadX, top + (rowH - cs) / 2f, cs, cs);
            if (on)
            {
                Theme.FillRound(g, cb, Sc(4), Theme.Light);
                Icons.Draw(g, Glyph.Check, RectangleF.Inflate(cb, -Sc(3), -Sc(3)), Theme.OnLight, 1.6f);
            }
            else
            {
                Theme.DrawRound(g, cb, Sc(4), Theme.TextDim, 1.3f);
            }
        }

        /// <summary>
        /// Треугольник «послушать», той же палитрой, что и звёздочка: в покое —
        /// приглушённый серый, у играющей строки — акцентный, как у закреплённой
        /// звезды. Ховер пока не выделяем отдельным видом.
        /// </summary>
        void PaintPlay(Graphics g, int top, int rowH, bool hot, bool playing)
        {
            RectangleF r = PlayRect(top, rowH);
            Color ink = playing ? Theme.Light : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);
            // Меньше отступа, чем у звезды: сам треугольник/пауза рисуются мельче
            // своего бокса (собственные пропорции глифа), и с тем же отступом, что у
            // звезды, выглядели бы заметно мельче неё при одинаковом размере кнопки.
            Icons.Draw(g, playing ? Glyph.Pause : Glyph.Play,
                       RectangleF.Inflate(r, -Sc(1), -Sc(1)), ink, 1.5f);
        }

        /// <summary>
        /// Звёздочка закрепления. У закреплённой строки видна постоянно (иначе как
        /// узнать, что проект закреплён, не наводясь на каждую строку), у остальных —
        /// пока курсор где-то на строке, чтобы не рябило на длинном списке.
        /// </summary>
        void PaintPin(Graphics g, int top, int rowH, bool hot, bool pinned)
        {
            RectangleF r = PinRect(top, rowH);
            Color ink = pinned ? Theme.Light : (hot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
            Icons.Draw(g, pinned ? Glyph.StarFill : Glyph.Star,
                       RectangleF.Inflate(r, -Sc(4), -Sc(4)), ink, 1.3f);
        }

        /// <summary>
        /// Теги пилюлями в один ряд, тем же приёмом, что в панели сведений — только
        /// мельче, строка таблицы вдвое ниже блока в панели. Дорожку не переносим:
        /// в неё и так лезет три-четыре тега, а перенос раздул бы строку таблицы вдвое
        /// ради довеска, который у большинства проектов вообще пуст. Тег, который не
        /// поместился целиком, просто не начинаем рисовать — обрезанная наполовину
        /// пилюля выглядела бы как брак верстки, а не как «тегов больше, чем видно».
        /// </summary>
        void PaintChips(Graphics g, Rectangle cell, string joined, Color textColor)
        {
            if (string.IsNullOrEmpty(joined)) return;
            string[] tags = joined.Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            if (tags.Length == 0) return;

            Font f = Theme.FBadge;
            int h = Sc(18);
            int y = cell.Y + (cell.Height - h) / 2;
            int x = cell.X;
            int gap = Sc(5);
            int padX = Sc(8);

            for (int i = 0; i < tags.Length; i++)
            {
                Size ts = TextRenderer.MeasureText(tags[i], f, new Size(short.MaxValue, h),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                int w = ts.Width + padX * 2;
                if (x + w > cell.Right) break;

                Rectangle chip = new Rectangle(x, y, w, h);
                Theme.FillRound(g, chip, h / 2f, Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                Chrome.DrawText(g, tags[i], f, chip, textColor, Chrome.Center);
                x += w + gap;
            }
        }

        void PaintMark(Graphics g, int[] widths, int top, int rowH, CellMark m)
        {
            if (m.Column < 0 || m.Column >= _columns.Count || string.IsNullOrEmpty(m.Text)) return;

            Rectangle slot = ColSlot(widths, top, rowH, m.Column);

            int barW = Sc(Theme.StatusBarW), barH = Sc(Theme.StatusBarH);
            Rectangle bar = new Rectangle(slot.Right - barW, top + (rowH - barH) / 2, barW, barH);
            Theme.FillRound(g, bar, barW / 2f, m.Color);

            int textW = Math.Max(0, slot.Width - barW - Sc(8));
            Rectangle tr = new Rectangle(slot.Left, top, textW, rowH);
            Chrome.DrawText(g, m.Text, Theme.FBody, tr, m.Color, Chrome.Right);
        }

        void PaintHeader(Graphics g, int[] widths)
        {
            Chrome.PaintBase(this, g, new Rectangle(0, 0, Width, HeaderHeight), Surface);

            // Звёздочка «закреплённые сверху» — ровно над гутером со звёздами строк,
            // чтобы связь между ними читалась без подписи.
            if (ShowPinIndicator && ShowHeaderPin)
            {
                RectangleF hp = PinRect(0, HeaderHeight);
                Color ink = PinnedFirst ? Theme.Light
                          : (_headPinHot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
                Icons.Draw(g, PinnedFirst ? Glyph.StarFill : Glyph.Star,
                           RectangleF.Inflate(hp, -Sc(4), -Sc(4)), ink, 1.3f);
            }

            for (int c = 0; c < _columns.Count; c++)
            {
                bool active = c == SortColumn;
                Column col = _columns[c];

                Rectangle hr = ColSlot(widths, 0, HeaderHeight, c);

                Size tsz = TextRenderer.MeasureText(col.Title, Theme.FLabel);
                if (active)
                {
                    // Треугольник рисуем отдельным элементом, иначе он обрезается вместе
                    // с текстом в узкой колонке.
                    int arrow = Sc(14);
                    RectangleF ar = col.Right
                        ? new RectangleF(hr.Right - arrow, (HeaderHeight - arrow) / 2f, arrow, arrow)
                        : new RectangleF(Math.Min(hr.X + tsz.Width + Sc(6), hr.Right - arrow),
                                         (HeaderHeight - arrow) / 2f, arrow, arrow);
                    Icons.Draw(g, SortDescending ? Glyph.SortDown : Glyph.SortUp, ar, Theme.Text, 1f);
                    hr.Width -= arrow + Sc(4);
                }

                // Заголовок, который сейчас тащат, гаснет: он «взят в руку», а место, куда
                // он встанет, показывает вертикальная черта ниже.
                Color ink = active ? Theme.Text : Theme.TextDim;
                if (c == _dragCol) ink = Color.FromArgb(ink.A / 3, ink);

                Chrome.DrawText(g, col.Title, Theme.FLabel, hr, ink,
                                col.Right ? Chrome.Right : Chrome.Left);
            }

            PaintDropMark(g, widths);
            PaintGrip(g, widths);
        }

        /// <summary>
        /// Куда встанет колонка, если отпустить её сейчас, — вертикальная черта на
        /// границе между заголовками, как в проводнике. Рисуется только во время
        /// перетаскивания.
        /// </summary>
        void PaintDropMark(Graphics g, int[] widths)
        {
            if (_dragCol < 0 || _dropAt < 0) return;
            if (_dropAt == _dragCol || _dropAt == _dragCol + 1) return;   // вернуть на место

            int x = LeftX;
            for (int c = 0; c < _dropAt && c < widths.Length; c++) x += widths[c];

            int top = Sc(10), bottom = HeaderHeight - Sc(10);
            using (Pen p = new Pen(Theme.Text, Sc(2)))
                g.DrawLine(p, x, top, x, bottom);
        }

        /// <summary>
        /// Тонкие ручки на всех границах между заголовками — появляются все разом, как
        /// только курсор зашёл в шапку, чтобы сразу было видно, где вообще можно тянуть,
        /// а не нащупывать границы вслепую. В остальное время шапка чистая.
        /// </summary>
        void PaintGrip(Graphics g, int[] widths)
        {
            if (_gripAlpha <= 0.001f || !ColumnsConfigurable) return;

            int top = Sc(13), bottom = HeaderHeight - Sc(13);
            if (bottom <= top) return;

            int a = (int)(180 * Math.Min(1f, Math.Max(0f, _gripAlpha)));
            using (Pen p = new Pen(Color.FromArgb(a, Theme.TextDim)))
            {
                int x = LeftX;
                for (int c = 0; c < _columns.Count; c++)
                {
                    // Граница резайзится, только если у неё справа колонка с фиксированной
                    // шириной — ровно то же условие, что и в GripAt.
                    if (_columns[c].Width > 0) g.DrawLine(p, x, top, x, bottom);
                    x += widths[c];
                }
            }
        }
    }
}
