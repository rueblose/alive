using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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

        int _scroll, _scrollX, _hot = -1, _selected = -1;
        bool _draggingBar, _draggingHBar;
        int _dragOffset, _dragHOffset;
        float _scrollTarget, _scrollCurrent;
        float _scrollXTarget, _scrollXCurrent;
        readonly SmoothScroller _scroller;
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
        public event Action<int> RowTagsClicked;       // клик по тегам строки (или по «+» в пустой ячейке)

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

        /// <summary>
        /// Зазор справа от пилюли строки (px). Если задан, пилюли заканчиваются
        /// на Width - PillRightGap, а вертикальный скроллбар центрируется ровно
        /// в этом промежутке между пилюлей выделения и правым краем контрола.
        /// </summary>
        public int PillRightGap;

        /// <summary>
        /// Путь к файлу для строки — если задан (не null и не пустой), строку можно
        /// вытащить наружу как файл (в проводник, в другое приложение). null для
        /// строки без файла — например, версии проекта без ссылки на рендер.
        /// </summary>
        public Func<RowData, string> DragFilePath;

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

        // Ячейка тегов — тем же приёмом, что и хвостик «+N»: что нарисовали, по тому и
        // кликаем. Кликабельны именно пилюли (или «+» у пустой ячейки), а не вся ширина
        // колонки: пустое место справа от тегов должно просто выделять строку.
        Rectangle[] _tagsHit;

        /// <summary>Строка, чьи теги сейчас под курсором, иначе -1.</summary>
        int _tagsHot = -1;

        int TagsAtPoint(Point p)
        {
            if (_tagsHit == null) return -1;
            for (int i = 0; i < _tagsHit.Length; i++)
                if (!_tagsHit[i].IsEmpty && _tagsHit[i].Contains(p)) return i;
            return -1;
        }

        void RememberTags(int row, Rectangle r, int clipLeft)
        {
            if (_tagsHit == null || row < 0 || row >= _tagsHit.Length) return;
            if (r.Left < clipLeft) r = Rectangle.FromLTRB(clipLeft, r.Top, r.Right, r.Bottom);
            if (r.Width > 0) _tagsHit[row] = r;
        }

        /// <summary>Строка, которая сейчас загружена в плеер (необязательно играет —
        /// см. Playing), — её треугольник горит светлым.</summary>
        public object PlayingTag
        {
            get { return _playingTag; }
            set { if (!ReferenceEquals(_playingTag, value)) { _playingTag = value; SyncPulse(); } }
        }
        object _playingTag;

        /// <summary>Плеер сейчас действительно звучит, а не на паузе. Иконка PlayingTag
        /// становится паузой только при обоих условиях разом — иначе после паузы через
        /// футер строка продолжала бы показывать паузу, хотя играть уже нечему.</summary>
        public bool Playing
        {
            get { return _playing; }
            set { if (_playing != value) { _playing = value; SyncPulse(); } }
        }
        bool _playing;

        // Оверлейные полосы прокрутки: тонкие в покое, толще под курсором, гаснут
        // через секунду после последней прокрутки.
        ScrollFade _barFade, _hbarFade;

        /// <summary>Волосяная линия под шапкой. Отключается там, где шапка и так стоит
        /// на своей поверхности (диалоги с одной колонкой).</summary>
        public bool ShowHeaderRule = true;

        /// <summary>Пока звучит — перерисовываем не весь список, а только кружок play
        /// у играющей строки; она ездит с прокруткой, поэтому считаем каждый тик.</summary>
        void SyncPulse()
        {
            if (_playing && _playingTag != null) PlayPulse.Attach(this, PulseRect);
            else PlayPulse.Detach(this);
            Invalidate();
        }

        Rectangle PulseRect()
        {
            if (_playingTag == null || !ShowPlayButton) return Rectangle.Empty;
            int rowH = RowHeight;
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!ReferenceEquals(_rows[i].Tag, _playingTag)) continue;
                int top = HeaderHeight + i * rowH - _scroll - over;
                if (top + rowH < HeaderHeight || top > Height) return Rectangle.Empty;
                return PlayRect(top, rowH);
            }
            return Rectangle.Empty;
        }

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
        /// Растворять ли нижние строки в фон. На стекле эффекта не даёт: градиент
        /// подмешивает свой же полупрозрачный фон поверх уже размытых обоев, альфа
        /// копится слоями, и вместо мягкого исчезновения получается чёткий тёмный
        /// прямоугольник в самом низу списка. На непрозрачном окне это не проблема.
        /// </summary>
        public bool FadeBottom = true;

        // Перетаскивание правого края колонки: индекс колонки или -1.
        int _resizeCol = -1;
        // Где взялись и какой ширина была в тот момент. Ширина считается от этой пары,
        // а не от текущей раскладки: раскладка сама зависит от ширины, и счёт от неё
        // замыкался сам на себя — см. DoResize.
        int _resizeStartX, _resizeStartW;

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

        // Строку тоже нажимают раньше, чем становится ясно — клик это или перетаскивание
        // наружу (в проводник, в другое приложение), тем же порогом, что и колонки.
        int _rowDragIdx = -1;
        Point _rowDragStart;

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
            _barFade = new ScrollFade(this, BarRect);
            _hbarFade = new ScrollFade(this, HBarRect);
            _scroller = new SmoothScroller(this,
                delegate (int s) { _scroll = s; _scrollCurrent = s; ClampScroll(); },
                delegate { return Math.Max(0, _rows.Count * RowHeight - (ViewH - HeaderHeight)); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PlayPulse.Detach(this);
                _gripTimer.Dispose(); _scroller.Dispose();
                _barFade.Dispose(); _hbarFade.Dispose();
            }
            base.Dispose(disposing);
        }

        public List<Column> ColumnList { get { return _columns; } }

        int RowHeight { get { return Sc(Theme.RowH); } }
        int HeaderHeight { get { return Sc(45); } }
        int PadX { get { return Sc(Theme.CellPadX); } }
        int PadRight { get { return Math.Max(PadX, (int)Math.Ceiling(Sc(Theme.RowPillH) / 2f) + Sc(16)) + PillRightGap; } }

        // Чекбокс живёт в отдельной колонке слева от первой обычной колонки — левый
        // отступ содержимого от этого растёт. Правый отступ (PadRight) гарантирует,
        // что текст колонок не залезает под скругление пилюли выделения строки.
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
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;
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
            if (_scroller != null) _scroller.SyncPosition(0);
            _selected = -1;
            _hot = -1;
            ClampScroll();
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;

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
                if (_scroller != null) _scroller.SyncPosition(_scroll);
                Invalidate();
            }
        }

        public int ScrollOffsetX
        {
            get { return _scrollX; }
            set
            {
                _scrollX = value;
                ClampScrollX();
                _scrollXTarget = _scrollXCurrent = _scrollX;
                Invalidate();
            }
        }

        public int MaxHScroll
        {
            get
            {
                int[] widths = ComputeWidths();
                if (widths.Length <= 1) return 0;
                int scrollableW = 0;
                for (int c = 1; c < widths.Length; c++) scrollableW += widths[c];
                int visibleScrollableW = Width - (LeftX + widths[0]) - PadRight;
                return Math.Max(0, scrollableW - visibleScrollableW);
            }
        }

        /// <summary>Полоса горизонтальной прокрутки живёт в нижних Sc(10). Её высота
        /// вычитается из области строк целиком, а не добавляется к содержимому: иначе
        /// пустая полоска появлялась только в самом низу списка, а на любой другой
        /// прокрутке полоса по-прежнему лежала поперёк строки.</summary>
        int HBarSpace { get { return MaxHScroll > 0 ? Sc(14) : 0; } }

        /// <summary>Высота области строк — окно минус полоса горизонтальной прокрутки.</summary>
        int ViewH { get { return Height - HBarSpace; } }

        int ContentHeight { get { return _rows.Count * RowHeight + Sc(8) + HeaderHeight; } }

        void ClampScroll()
        {
            int max = Math.Max(0, ContentHeight - ViewH);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        void ClampScrollX()
        {
            int max = MaxHScroll;
            if (_scrollX > max) _scrollX = max;
            if (_scrollX < 0) _scrollX = 0;
        }

        protected override void OnResize(EventArgs e)
        {
            ClampScroll();
            ClampScrollX();
            base.OnResize(e);
        }

        int GetSnapTarget(int direction)
        {
            int[] widths = ComputeWidths();
            if (widths.Length <= 1) return 0;

            int maxScroll = MaxHScroll;
            if (maxScroll <= 0) return 0;

            List<int> snapOffsets = new List<int>();
            int acc = 0;
            snapOffsets.Add(0);
            for (int c = 1; c < widths.Length - 1; c++)
            {
                acc += widths[c];
                if (acc >= maxScroll)
                {
                    snapOffsets.Add(maxScroll);
                    break;
                }
                snapOffsets.Add(acc);
            }
            if (!snapOffsets.Contains(maxScroll))
                snapOffsets.Add(maxScroll);

            int current = (int)Math.Round(_scrollXTarget);

            if (direction > 0) // Вправо к следующей колонке
            {
                for (int i = 0; i < snapOffsets.Count; i++)
                {
                    if (snapOffsets[i] > current + 2)
                        return snapOffsets[i];
                }
                return maxScroll;
            }
            else // Влево к предыдущей колонке
            {
                for (int i = snapOffsets.Count - 1; i >= 0; i--)
                {
                    if (snapOffsets[i] < current - 2)
                        return snapOffsets[i];
                }
                return 0;
            }
        }

        int GetNearestSnapTarget(int target)
        {
            int[] widths = ComputeWidths();
            if (widths.Length <= 1) return 0;
            int maxScroll = MaxHScroll;
            if (maxScroll <= 0) return 0;

            List<int> snapOffsets = new List<int>();
            int acc = 0;
            snapOffsets.Add(0);
            for (int c = 1; c < widths.Length - 1; c++)
            {
                acc += widths[c];
                if (acc >= maxScroll)
                {
                    snapOffsets.Add(maxScroll);
                    break;
                }
                snapOffsets.Add(acc);
            }
            if (!snapOffsets.Contains(maxScroll))
                snapOffsets.Add(maxScroll);

            int closest = snapOffsets[0];
            int minDist = Math.Abs(target - closest);
            for (int i = 1; i < snapOffsets.Count; i++)
            {
                int dist = Math.Abs(target - snapOffsets[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = snapOffsets[i];
                }
            }
            return closest;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            if (isShift)
            {
                int steps = e.Delta / 120;
                if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                int direction = steps < 0 ? 1 : -1;
                int count = Math.Abs(steps);
                for (int s = 0; s < count; s++)
                {
                    _scrollXTarget = GetSnapTarget(direction);
                }
                ClampScrollXTarget();
                _scrollX = (int)_scrollXTarget;
                _scrollXCurrent = _scrollXTarget;
                ClampScrollX();
                _hbarFade.Ping();
                Invalidate();
            }
            else
            {
                _scroller.OnMouseWheel(e.Delta, RowHeight * 3);
                _barFade.Ping();
            }
            base.OnMouseWheel(e);
        }

        const int WM_MOUSEHWHEEL = 0x020E;

        // Список живёт с ПКМ: меню строки и меню шапки. Разбор — в OnMouseDown.
        protected override bool WantsRightClick { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (delta != 0)
                {
                    int steps = delta / 120;
                    if (steps == 0) steps = delta > 0 ? 1 : -1;
                    int direction = steps > 0 ? 1 : -1;
                    int count = Math.Abs(steps);
                    for (int s = 0; s < count; s++)
                    {
                        _scrollXTarget = GetSnapTarget(direction);
                    }
                    ClampScrollXTarget();
                    _scrollX = (int)_scrollXTarget;
                    _scrollXCurrent = _scrollXTarget;
                    ClampScrollX();
                    Invalidate();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void ClampScrollTarget()
        {
            int max = Math.Max(0, _rows.Count * RowHeight - (Height - HeaderHeight));
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scrollTarget < 0) _scrollTarget = 0;
        }

        void ClampScrollXTarget()
        {
            int max = MaxHScroll;
            if (_scrollXTarget > max) _scrollXTarget = max;
            if (_scrollXTarget < 0) _scrollXTarget = 0;
        }


        /// <summary>
        /// Смещение содержимого по вертикали: прокрутка плюс резиновый перелёт. Попадания
        /// мыши обязаны считаться по нему же, что и отрисовка, — иначе во время отскока
        /// звёздочка и play срабатывают там, где их уже не видно.
        /// </summary>
        int ScrollY
        {
            get { return _scroll + (_scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0); }
        }

        int RowAt(int y)
        {
            int top = HeaderHeight - ScrollY;
            if (y < HeaderHeight) return -1;
            int idx = (y - top) / RowHeight;
            return idx >= 0 && idx < _rows.Count ? idx : -1;
        }

        Rectangle BarRect()
        {
            int content = ContentHeight;
            if (content <= ViewH) return Rectangle.Empty;
            int track = ViewH - HeaderHeight - Sc(10);
            int h = Math.Max(Sc(40), (int)(track * (float)ViewH / content));
            int max = Math.Max(1, content - ViewH);
            int y = HeaderHeight + Sc(5) + (int)((track - h) * (_scroll / (float)max));
            int barW = Sc(4);
            int barX = PillRightGap > 0
                ? (Width - PillRightGap) + (PillRightGap - barW) / 2
                : Width - Sc(8);
            return new Rectangle(barX, y, barW, h);
        }

        Rectangle HBarRect()
        {
            int max = MaxHScroll;
            if (max <= 0) return Rectangle.Empty;
            int[] widths = ComputeWidths();
            int startX = LeftX + widths[0] + Sc(5);
            int track = Width - startX - PadRight;
            if (track <= Sc(20)) return Rectangle.Empty;

            int totalScrollableW = 0;
            for (int c = 1; c < widths.Length; c++) totalScrollableW += widths[c];
            int visibleW = Width - (LeftX + widths[0]) - PadRight;

            int w = Math.Max(Sc(30), (int)(track * (float)visibleW / Math.Max(1, totalScrollableW)));
            int x = startX + (int)((track - w) * (_scrollX / (float)max));
            return new Rectangle(x, Height - Sc(6), w, Sc(4));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_rowDragIdx >= 0 && e.Button == MouseButtons.Left
                && (Math.Abs(e.X - _rowDragStart.X) > DragThreshold || Math.Abs(e.Y - _rowDragStart.Y) > DragThreshold))
            {
                int dragIdx = _rowDragIdx;
                _rowDragIdx = -1;
                string path = dragIdx < _rows.Count ? DragFilePath(_rows[dragIdx]) : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    DoDragDrop(new DataObject(DataFormats.FileDrop, new string[] { path }), DragDropEffects.Copy);
                return;
            }

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
            // _pressCol > 0 — там же, где и DropIndexAt: первую колонку не таскают.
            if (_pressCol > 0 && e.Button == MouseButtons.Left
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
                int track = ViewH - HeaderHeight - Sc(10);
                int max = Math.Max(1, ContentHeight - ViewH);
                float t = (e.Y - _dragOffset - HeaderHeight - Sc(5)) / (float)Math.Max(1, track - bar.Height);
                _scroll = (int)(t * max);
                _scrollTarget = _scrollCurrent = _scroll;
                if (_scroller != null) _scroller.SyncPosition(_scroll);
                ClampScroll();
                Invalidate();
                return;
            }

            if (_draggingHBar)
            {
                int[] widths = ComputeWidths();
                int startX = LeftX + widths[0] + Sc(5);
                int track = Width - startX - PadRight;
                Rectangle hbar = HBarRect();
                int max = MaxHScroll;
                float t = (e.X - _dragHOffset - startX) / (float)Math.Max(1, track - hbar.Width);
                _scrollX = (int)Math.Max(0, Math.Min(max, t * max));
                _scrollXTarget = _scrollXCurrent = _scrollX;
                ClampScrollX();
                Invalidate();
                return;
            }

            // Полосы прокрутки просыпаются только когда курсор рядом с ними самими,
            // а не когда он где угодно над списком.
            _barFade.SetHot(e.X >= Width - Sc(22) && e.Y > HeaderHeight);
            _hbarFade.SetHot(MaxHScroll > 0 && e.Y >= Height - Sc(18));

            // В шапке у краёв изменяемых колонок курсор — «раздвинуть», в остальном рука.
            // Полоски-ручки показываем все разом, лишь только курсор зашёл в шапку.
            if (e.Y < HeaderHeight)
            {
                int grip = GripAt(e.X, ComputeWidths());
                SetHeaderHot(true);
                Cursor = grip >= 0 ? Cursors.VSplit : Cursors.Hand;
                bool headPin = OnHeaderPin(e.Location);
                if (headPin != _headPinHot) { _headPinHot = headPin; Invalidate(); }
                if (_hot != -1 || _playHot != -1 || _pinHot != -1 || _tagsHot != -1)
                { _hot = -1; _playHot = -1; _pinHot = -1; _tagsHot = -1; Invalidate(); }
                return;
            }
            if (_headPinHot) { _headPinHot = false; Invalidate(); }
            SetHeaderHot(false);
            if (Cursor != Cursors.Hand) Cursor = Cursors.Hand;

            int pillW = Math.Max(0, Width - PillRightGap);
            int idx = (PillRightGap > 0 && e.X >= pillW) ? -1 : RowAt(e.Y);
            int playHot = -1, pinHot = -1;
            if (idx >= 0)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
                if (ShowPlayButton && _rows[idx].CanPlay && PlayRect(top, RowHeight).Contains(e.Location))
                    playHot = idx;
                if (ShowPinIndicator && PinRect(top, RowHeight).Contains(e.Location))
                    pinHot = idx;
            }
            int countHot = CountAtPoint(e.Location);
            int tagsHot = TagsAtPoint(e.Location);
            if (tagsHot != idx) tagsHot = -1;
            if (idx != _hot || playHot != _playHot || pinHot != _pinHot
                || countHot != _countHot || tagsHot != _tagsHot)
            {
                _hot = idx; _playHot = playHot; _pinHot = pinHot; _countHot = countHot; _tagsHot = tagsHot;
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
            if (_columns.Count == 0) return 0;
            // Первая колонка прибита: это имя сета (плагина), единственное, по чему
            // строку вообще можно опознать, и тянется она на всю свободную ширину.
            // Вставлять перед ней некуда — самое левое место для остальных это 1.
            if (px < LeftX + widths[0]) return 1;
            for (int c = 1; c < _columns.Count; c++)
            {
                int x = ColX(widths, c);
                if (px < x + widths[c] / 2) return c;
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
            if (_columns.Count > 0)
            {
                int edge0 = LeftX + widths[0];
                if (Math.Abs(px - edge0) <= Sc(4)) return 0;
            }
            for (int c = 1; c < _columns.Count; c++)
            {
                if (_columns[c].Width > 0)
                {
                    int x = ColX(widths, c);
                    if (x >= LeftX + widths[0] - Sc(2) && x <= Width - PadRight + Sc(2) && Math.Abs(px - x) <= Sc(4)) return c;
                }
            }
            return -1;
        }

        void DoResize(int mouseX)
        {
            float scale = DeviceDpi / 96f;
            int[] before = ComputeWidths();
            int rightBefore = ColX(before, _resizeCol) + before[_resizeCol];

            if (_resizeCol == 0 && _columns.Count > 0)
            {
                int newScaled = _resizeStartW + (mouseX - _resizeStartX);
                int minW = Sc(100);
                int maxW = Width - LeftX - PadRight - Sc(100);
                if (maxW < minW) maxW = minW;
                if (newScaled < minW) newScaled = minW;
                if (newScaled > maxW) newScaled = maxW;
                _columns[0].Width = Math.Max(1, (int)Math.Round(newScaled / scale));
            }
            else if (_resizeCol > 0 && _resizeCol < _columns.Count)
            {
                // Ширина — от того, что было в момент захвата, плюс пройденный путь. Раньше
                // считали от правого края текущей раскладки, а он стоит на месте, только пока
                // колонка имени тянется и гасит разницу собой. Как только колонки переставали
                // влезать, имя упиралось в минимум, край начинал ехать вместе с шириной — и ширина
                // разгоняла сама себя на каждом MouseMove. От точки захвата обратной связи нет,
                // и тянется всегда одна и та же колонка — та, за чей левый край взялись.
                int newScaled = _resizeStartW + (_resizeStartX - mouseX);

                int minW = Sc(48);
                int maxW = Width - LeftX - PadRight - Sc(80);
                if (maxW < minW) maxW = minW;
                if (newScaled < minW) newScaled = minW;
                if (newScaled > maxW) newScaled = maxW;

                _columns[_resizeCol].Width = Math.Max(1, (int)Math.Round(newScaled / scale));
            }
            // Правее границы ничего шевелиться не должно. Пока колонки влезают, это выходит
            // само собой: тянущаяся колонка имени гасит разницу собой, и всё остальное стоит
            // прижатым к правому краю. Когда не влезают, гасить нечем — тогда ровно на ту же
            // разницу доворачиваем прокрутку, и картинка получается та же, что во весь экран.
            // В первом случае прокручивать нечего и ClampScrollX вернёт ноль — ветка не нужна.
            int[] after = ComputeWidths();
            _scrollX += ColX(after, _resizeCol) + after[_resizeCol] - rightBefore;
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1 || _playHot != -1 || _pinHot != -1 || _countHot != -1
                || _tagsHot != -1 || _headPinHot)
            {
                _hot = -1; _playHot = -1; _pinHot = -1; _countHot = -1; _tagsHot = -1; _headPinHot = false;
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
            if (_columns.Count == 0) return -1;
            int[] widths = ComputeWidths();
            if (px < LeftX || px >= Width - PadRight) return -1;
            if (px < LeftX + widths[0]) return 0;

            for (int c = 1; c < _columns.Count; c++)
            {
                int x = ColX(widths, c);
                if (px >= x && px < x + widths[c]) return c;
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
                if (grip >= 0)
                {
                    _resizeCol = grip;
                    _resizeStartX = e.X;
                    _resizeStartW = widths[grip];
                    return;
                }

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

            Rectangle hbar = HBarRect();
            if (!hbar.IsEmpty &&
                new Rectangle(hbar.X, hbar.Y - Sc(4), hbar.Width, hbar.Height + Sc(6)).Contains(e.Location))
            {
                _draggingHBar = true;
                _dragHOffset = e.X - hbar.X;
                return;
            }

            int pillW = Math.Max(0, Width - PillRightGap);
            int idx = (PillRightGap > 0 && e.X >= pillW) ? -1 : RowAt(e.Y);

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

            // Теги — тоже своя кнопка: клик по пилюлям (или по «+» у пустой ячейки)
            // открывает редактор тегов, а не просто выделяет строку.
            if (idx >= 0 && TagsAtPoint(e.Location) == idx)
            {
                if (RowTagsClicked != null) { RowTagsClicked(idx); return; }
            }

            if (idx >= 0 && ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - ScrollY;
                if (PinRect(pinTop, RowHeight).Contains(e.Location))
                {
                    // Закрепление — тоже независимое действие, как и play ниже.
                    if (RowPinClicked != null) RowPinClicked(idx);
                    return;
                }
            }

            if (idx >= 0 && ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
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

            // Тащить наружу можно, только если для строки вообще есть файл — курсор
            // подтверждает это раньше, чем движение мыши решит, клик это или перенос.
            if (idx >= 0 && e.Button == MouseButtons.Left && DragFilePath != null)
            {
                _rowDragIdx = idx;
                _rowDragStart = e.Location;
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _rowDragIdx = -1;

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
            if (_draggingHBar)
            {
                _draggingHBar = false;
                _scrollXTarget = GetNearestSnapTarget(_scrollX);
                ClampScrollXTarget();
                _scrollX = (int)_scrollXTarget;
                _scrollXCurrent = _scrollXTarget;
                ClampScrollX();
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        /// <summary>
        /// Двойной клик активирует строку — но только если он пришёлся на саму строку,
        /// а не на одну из её независимых кнопок (хвостик «+N», звёздочка, play,
        /// чекбокс). У Windows второй клик быстрого двойного тапа не идёт через
        /// OnMouseDown второй раз — он приходит сюда, минуя те же проверки. Кнопки строки
        /// все переключатели (пин, play/pause, раскрытие версий, чекбокс), поэтому второй
        /// клик тут просто проглатывается — иначе он повторял бы то же действие и гасил
        /// первое: пин закреплял и тут же открепял, play запускал и тут же ставил на паузу.
        /// </summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int idx = RowAt(e.Y);
            if (idx >= 0 && OnRowAccessory(idx, e.Location))
            {
                base.OnMouseDoubleClick(e);
                return;
            }
            if (idx < 0)
            {
                base.OnMouseDoubleClick(e);
                return;
            }
            if (ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        /// <summary>Курсор сейчас над одной из кнопок строки idx — хвостиком «+N»,
        /// тегами, звёздочкой, play или чекбоксом, — а не над самой строкой.</summary>
        bool OnRowAccessory(int idx, Point p)
        {
            if (CountAtPoint(p) == idx) return true;
            if (TagsAtPoint(p) == idx) return true;

            if (ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - ScrollY;
                if (PinRect(pinTop, RowHeight).Contains(p)) return true;
            }
            if (ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
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
            int top = HeaderHeight + idx * RowHeight - ScrollY + RowHeight / 2;
            return new Point(LeftX, Math.Max(0, Math.Min(Height - 1, top)));
        }

        void EnsureVisible(int idx)
        {
            int top = HeaderHeight + idx * RowHeight - _scroll;
            if (top < HeaderHeight) _scroll += top - HeaderHeight;
            else if (top + RowHeight > ViewH) _scroll += top + RowHeight - ViewH;
            _scrollTarget = _scrollCurrent = _scroll;
            if (_scroller != null) _scroller.SyncPosition(_scroll);
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
            int available = Width - LeftX - PadRight - fixedTotal;
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Width == 0)
                {
                    int minW = i == 0 ? Sc(220) : Sc(80);
                    w[i] = Math.Max(minW, available / Math.Max(1, flexCount));
                }
            }
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

            int scrollLeft = LeftX + widths[0];
            int scrollWidth = Math.Max(0, Width - PadRight - scrollLeft);

            // Пока ручки колонок проявляются/гаснут, таймер дёргает Invalidate только
            // по прямоугольнику шапки — тело списка в клип не попадает и всё равно
            // отсекается, так что весь проход по строкам ниже можно пропустить.
            bool headerOnly = e.ClipRectangle.Bottom <= HeaderHeight;

            if (!headerOnly)
            {
                Region baseClip = g.Clip;

                // Резиновый перелёт за край. Сдвигаем только тело списка: шапка
                // закреплена и уезжать вместе со строками не должна.
                int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
                int slack = Sc(8) + Math.Abs(over);

                // Прямоугольники хвостиков «+N» собираем заново за каждый полный проход:
                // строки уехали прокруткой, и вчерашние координаты кликать нельзя.
                if (_countHit == null || _countHit.Length != _rows.Count)
                    _countHit = new Rectangle[_rows.Count];
                Array.Clear(_countHit, 0, _countHit.Length);

                if (_tagsHit == null || _tagsHit.Length != _rows.Count)
                    _tagsHit = new Rectangle[_rows.Count];
                Array.Clear(_tagsHit, 0, _tagsHit.Length);

                int first = Math.Max(0, (_scroll - slack) / rowH);
                int last = Math.Min(_rows.Count - 1, (_scroll + ViewH + slack) / rowH);

                // 1. Проход: фон строк (пилюли выделения и ховера) на всю ширину
                g.SetClip(new Rectangle(0, HeaderHeight, Width, Math.Max(0, ViewH - HeaderHeight)));
                for (int i = first; i <= last; i++)
                {
                    int top = HeaderHeight + i * rowH - _scroll - over;
                    if (top > ViewH) break;

                    float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                    if (entrance <= 0.001f) continue;

                    int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                    int topAnim = top + offsetY;

                    int pillW = Math.Max(0, Width - PillRightGap);
                    Rectangle pill = new Rectangle(0, topAnim + (rowH - pillH) / 2, pillW, pillH);
                    float hoverFactor = (_rowHoverFactors != null && i < _rowHoverFactors.Length) ? _rowHoverFactors[i] : (i == _hot ? 1f : 0f);

                    // На уже выделенной строке заливка ховера не нужна — у неё и так
                    // есть свой контур, а сверху ещё и заливка спорила с текстом.
                    if (i != _selected && hoverFactor > 0.001f)
                    {
                        Color c = Color.FromArgb((int)Math.Round(hoverFactor * Theme.RowHover.A * entrance), Theme.RowHover);
                        Theme.FillRound(g, pill, pillH / 2f, c);
                    }
                    if (i == _selected)
                    {
                        // Выделение — обычным светлым, только контуром: заливка спорила
                        // с текстом строки, а сам цвет от выбора акцента не зависит —
                        // акцент выбирают кнопки, прогресс и переключатели.
                        RectangleF ring = RectangleF.Inflate(pill, -0.75f, -0.75f);
                        Color line = Color.FromArgb((int)Math.Round(0xC0 * entrance), Theme.Light);
                        using (GraphicsPath rp = Theme.Round(ring, pillH / 2f - 0.75f))
                        using (Pen pen = new Pen(line, 1.5f))
                            g.DrawPath(pen, rp);
                    }
                }

                // 2. Проход: прокручиваемые колонки (с 1 по N-1), строго обрезанные границами scrollLeft и PadRight
                if (_columns.Count > 1 && scrollWidth > 0)
                {
                    g.SetClip(new Rectangle(scrollLeft, HeaderHeight, scrollWidth, Math.Max(0, ViewH - HeaderHeight)));

                    for (int i = first; i <= last; i++)
                    {
                        int top = HeaderHeight + i * rowH - _scroll - over;
                        if (top > ViewH) break;

                        float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                        if (entrance <= 0.001f) continue;

                        int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                        int topAnim = top + offsetY;
                        RowData row = _rows[i];
                        bool dim = ShowCheckboxes && !row.Checked;
                        bool bright = i == _selected;

                        for (int c = 1; c < _columns.Count && c < row.Cells.Length; c++)
                        {
                            int cx = ColX(widths, c);
                            if (cx < scrollLeft || cx >= Width - PadRight) continue;
                            PaintCell(g, widths, topAnim, rowH, entrance, dim, bright, row, c, false, i);
                        }

                        foreach (CellMark m in row.Marks)
                        {
                            if (m.Column >= 1)
                            {
                                int mx = ColX(widths, m.Column);
                                if (mx < scrollLeft || mx >= Width - PadRight) continue;
                                PaintMark(g, widths, topAnim, rowH, m);
                            }
                        }
                    }
                }

                // 3. Проход: закреплённая колонка имени (0) и гутеры слева (0 .. scrollLeft)
                {
                    int col0W = Math.Min(scrollLeft, Width - PadRight);
                    g.SetClip(new Rectangle(0, HeaderHeight, col0W, Math.Max(0, ViewH - HeaderHeight)));

                    for (int i = first; i <= last; i++)
                    {
                        int top = HeaderHeight + i * rowH - _scroll - over;
                        if (top > ViewH) break;

                        float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                        if (entrance <= 0.001f) continue;

                        int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                        int topAnim = top + offsetY;
                        RowData row = _rows[i];
                        bool dim = ShowCheckboxes && !row.Checked;
                        bool bright = i == _selected;

                        if (ShowCheckboxes) PaintCheckbox(g, topAnim, rowH, row.Checked);
                        if (ShowPinIndicator && (row.Pinned || i == _hot))
                            PaintPin(g, topAnim, rowH, i == _pinHot, row.Pinned);
                        if (ShowPlayButton && row.CanPlay)
                            PaintPlay(g, topAnim, rowH, i == _playHot,
                                      Playing && PlayingTag != null && ReferenceEquals(PlayingTag, row.Tag));

                        if (_columns.Count > 0 && row.Cells.Length > 0)
                        {
                            PaintCell(g, widths, topAnim, rowH, entrance, dim, bright, row, 0, i == _countHot, i);
                        }

                        foreach (CellMark m in row.Marks)
                        {
                            if (m.Column == 0) PaintMark(g, widths, topAnim, rowH, m);
                        }
                    }
                }

                g.Clip = baseClip;

                // Разделителя между закреплёнными и остальными больше нет: закреплённые
                // и так видны звёздочкой, а линия внутри списка спорила с линией под
                // шапкой — на их пересечении с вертикальным разделителем получался
                // лишний светлый пиксель.

                // 5. Тонкий вертикальный разделитель между закреплённой колонкой и прокручиваемой областью
                if (_scrollX > 0 && _columns.Count > 1)
                {
                    using (Pen divPen = new Pen(Theme.Hairline))
                        g.DrawLine(divPen, scrollLeft - 1, HeaderHeight, scrollLeft - 1, Height);
                }

                // Нижние строки чуть растворяются в фоне — тонкая полоска, не прошлый
                // на треть экрана. На стекле это гасим целиком, см. комментарий у FadeBottom.
                if (FadeBottom && !Glass.Enabled && ContentHeight > Height)
                {
                    int fadeH = Sc(36);
                    int fadeW = Math.Max(0, Width - PillRightGap);
                    Rectangle fr = new Rectangle(0, Height - fadeH, fadeW, fadeH);
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new Rectangle(fr.X, fr.Y - 1, fr.Width, fr.Height + 2),
                        Color.FromArgb(0, Surface), Surface, LinearGradientMode.Vertical))
                        g.FillRectangle(lb, fr);
                }
            }

            PaintHeader(g, widths);

            if (!headerOnly)
            {
                // Полоса под строками — своя дорожка. Clip тут не помогает: текст рисует
                // TextRenderer мимо GDI+, и обрезку он игнорирует, поэтому нижнюю
                // полоску просто закрашиваем фоном, а уже поверх кладём полосу.
                if (HBarSpace > 0)
                    Chrome.PaintBase(this, g, new Rectangle(0, Height - HBarSpace, Width, HBarSpace), Surface);

                PaintFadingBar(g, BarRect(), _barFade, _draggingBar, true);
                PaintFadingBar(g, HBarRect(), _hbarFade, _draggingHBar, false);
            }
        }

        void PaintFadingBar(Graphics g, Rectangle bar, ScrollFade fade, bool dragging, bool vertical)
        {
            Chrome.PaintFadingBar(g, bar, dragging ? 1f : fade.Alpha, dragging ? 1f : fade.Thick, vertical, Sc(4));
        }

        void PaintCell(Graphics g, int[] widths, int topAnim, int rowH, float entrance, bool dim, bool bright, RowData row, int c, bool countHot, int rowIndex)
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
            int x = ColX(widths, c);

            if (childHere)
            {
                Rectangle rail = new Rectangle(x + Sc(6), topAnim + Sc(4), Sc(2), rowH - Sc(8));
                Color railColor = Color.FromArgb((int)Math.Round(120 * entrance), Theme.TextDim);
                g.FillRectangle(Theme.GetBrush(railColor), rail);
            }
            int maxRight = Width - PadRight;
            int cellX = x + indent;
            int cellW = Math.Max(0, widths[c] - Sc(10) - indent);
            if (cellX + cellW > maxRight) cellW = Math.Max(0, maxRight - cellX);
            if (cellW <= 0 && cellX >= maxRight) return;
            Rectangle cr = new Rectangle(cellX, topAnim, cellW, rowH);
            // Последним аргументом — левая граница кликабельной зоны: прокручиваемая
            // колонка может заехать под закреплённую первую, рисунок там обрезан клипом,
            // а вот попадание мышью надо обрезать самим.
            if (col.Chips)
                PaintChips(g, cr, row.Cells[c], color, rowIndex, rowIndex == _hot, rowIndex == _tagsHot,
                           c >= 1 ? LeftX + widths[0] : 0);
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
                    Color countColor = countHot ? Theme.Text : Theme.TextDim;
                    if (entrance < 1.0f) countColor = Color.FromArgb((int)Math.Round(countColor.A * entrance), countColor);

                    Size countSz = TextRenderer.MeasureText(countText, f);
                    Size mainSz = TextRenderer.MeasureText(mainText, f);

                    int countX = cr.X + mainSz.Width - Sc(4);
                    if (countX + countSz.Width <= cr.Right)
                    {
                        Rectangle mainR = new Rectangle(cr.X, cr.Y, mainSz.Width, cr.Height);
                        Chrome.DrawText(g, mainText, f, mainR, color, Chrome.CellLeft);

                        Rectangle countR = new Rectangle(countX, cr.Y, Math.Max(0, cr.Right - countX), cr.Height);
                        Chrome.DrawText(g, countText, f, countR, countColor, Chrome.CellLeft);
                        RememberCount(rowIndex, countX, cr.Y, countSz.Width, cr.Height);
                    }
                    else
                    {
                        int maxMainW = Math.Max(0, cr.Width - countSz.Width - Sc(4));
                        Rectangle mainR = new Rectangle(cr.X, cr.Y, maxMainW, cr.Height);
                        Chrome.DrawText(g, mainText, f, mainR, color, Chrome.CellLeft);

                        int cX = cr.X + maxMainW + Sc(4);
                        if (cX < cr.Right)
                        {
                            Rectangle countR = new Rectangle(cX, cr.Y, Math.Max(0, cr.Right - cX), cr.Height);
                            Chrome.DrawText(g, countText, f, countR, countColor, Chrome.CellLeft);
                            RememberCount(rowIndex, cX, cr.Y, Math.Min(countSz.Width, cr.Right - cX), cr.Height);
                        }
                    }
                }
                else
                {
                    Chrome.DrawText(g, row.Cells[c], f, cr, color,
                                   col.Right ? Chrome.CellRight : Chrome.CellLeft);
                }
            }
        }

        int ColX(int[] widths, int col)
        {
            if (col <= 0) return LeftX;
            int x = LeftX + widths[0] - _scrollX;
            for (int c = 1; c < col; c++) x += widths[c];
            return x;
        }

        // Заголовок и отметка в данных под ним всегда делят один и тот же прямоугольник —
        // левую границу собственной колонки, без залезания в соседнюю.
        Rectangle ColSlot(int[] widths, int top, int rowH, int col)
        {
            int x = ColX(widths, col);
            int maxRight = Width - PadRight;
            int w = Math.Max(0, widths[col] - Sc(10));
            if (x + w > maxRight) w = Math.Max(0, maxRight - x);
            return new Rectangle(x, top, w, rowH);
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

            // Пока строка звучит и на неё не наведён курсор — вместо значка живой
            // пульс: видно, что играет именно эта строка. Под курсором возвращается
            // пауза, иначе остановить нечем.
            if (playing && !hot)
            {
                RectangleF pr = RectangleF.Inflate(r, -Sc(5), -Sc(6));
                PlayPulse.Paint(g, pr, Theme.Light);
                return;
            }

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
        /// Теги пилюлями, тем же приёмом, что в панели сведений — только мельче.
        /// В одну дорожку, а если в неё всё не влезло — в две: строка таблицы держит
        /// ровно две пилюли по высоте, а больше и не нужно. Что не поместилось и во
        /// вторую, сворачивается в многоточие: обрезанная наполовину пилюля читалась бы
        /// как брак вёрстки, а не как «тегов больше, чем видно».
        ///
        /// Сами пилюли — кнопка правки: по ним кликают, чтобы открыть редактор тегов.
        /// У пустой ячейки кликать нечего, поэтому под курсором на строке появляется
        /// «+» — постоянно держать его во всех строках значило бы засеять плюсами
        /// всю таблицу, у большинства проектов тегов нет.
        /// </summary>
        const string AddTagsLabel = "Add tags";

        void PaintChips(Graphics g, Rectangle cell, string joined, Color textColor,
                        int rowIndex, bool rowHot, bool hot, int clipLeft)
        {
            // Кегль мельче, чем у остального текста строки: одиннадцатым пилюли выходят
            // по 21 px, две дорожки съедают строку почти целиком, и пилюли соседних строк
            // оказываются друг от друга на том же расстоянии, что и две дорожки внутри
            // одной, — теги перестают читаться как теги одного проекта.
            Font f = Theme.FMini;
            int vgap = Sc(3);

            // Высота пилюли — по шрифту, а не по остатку строки: текст, которому нужно
            // 17 px, в пилюлю 16 не влезет, и выносные элементы упрутся в края. Две
            // пилюли с зазором занимают 39 px из 53, остаток строки сам становится
            // полями сверху и снизу (блок центрируется ниже).
            int h = Math.Min(Chrome.PillHeight(f, 4), (cell.Height - vgap) / 2);

            string[] tags = string.IsNullOrEmpty(joined)
                ? new string[0]
                : joined.Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

            if (tags.Length == 0)
            {
                if (!rowHot) return;

                // Бирка и подпись, как в панели сведений, — голый плюс не говорил, что
                // именно он добавит. Иконка и текст делят одну коробку высотой с пилюлю:
                // иконка стоит по её центру, текст — по тому же правилу, что и в пилюлях
                // (Chrome.PillTop), так что буквы и значок выровнены друг с другом.
                Color ink = hot ? Theme.Text : Theme.TextDim;
                // Бирка вписана в квадрат, а сама она широкая и низкая (14×10 в исходнике),
                // поэтому по ширине занимает весь квадрат, а по высоте — две трети.
                int icon = Math.Max(Sc(12), h - Sc(4));
                int iconGap = Sc(6);
                int textW = TextRenderer.MeasureText(AddTagsLabel, f, new Size(short.MaxValue, h),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                int hintW = icon + iconGap + textW;
                if (hintW > cell.Width) return;

                int hintY = cell.Y + (cell.Height - h) / 2;
                Icons.Draw(g, Glyph.Tag,
                           new RectangleF(cell.X, hintY + (h - icon) / 2f, icon, icon), ink, 1.2f);
                // NoClipping в Chrome.PillText: прямоугольник тут ровно по мерке текста,
                // и без него хвост «g» обрезался бы своей же коробкой.
                Chrome.DrawText(g, AddTagsLabel, f,
                                new Rectangle(cell.X + icon + iconGap, hintY + Chrome.PillTop(g, f, h), textW, h),
                                ink, Chrome.PillText);
                RememberTags(rowIndex, new Rectangle(cell.X - Sc(3), cell.Y, hintW + Sc(6), cell.Height), clipLeft);
                return;
            }

            int gap = Sc(5);
            int padX = Sc(8);

            int[] chipW = new int[tags.Length];
            int total = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                chipW[i] = TextRenderer.MeasureText(tags[i], f, new Size(short.MaxValue, h),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + padX * 2;
                total += chipW[i] + (i > 0 ? gap : 0);
            }

            // Вторую дорожку заводим, только когда в одну и правда не влезло: ради пары
            // коротких тегов раздёргивать строку по вертикали незачем. А если не влезает
            // даже первый тег, второй дорожке тем более нечего показать — только многоточие.
            int lines = (total <= cell.Width || chipW[0] > cell.Width) ? 1 : 2;
            int top = cell.Y + (cell.Height - (lines * h + (lines - 1) * vgap)) / 2;

            Color fill = Color.FromArgb(hot ? 0x3D : 0x22, 0xFF, 0xFF, 0xFF);
            Color chipInk = hot ? Theme.Text : textColor;
            int textTop = Chrome.PillTop(g, f, h);

            int next = 0, right = cell.X;
            for (int line = 0; line < lines; line++)
            {
                int x = cell.X;
                int y = top + line * (h + vgap);

                while (next < tags.Length && x + chipW[next] <= cell.Right)
                {
                    Rectangle chip = new Rectangle(x, y, chipW[next], h);
                    Theme.FillRound(g, chip, h / 2f, fill);
                    Chrome.DrawText(g, tags[next], f,
                                    new Rectangle(chip.X, chip.Y + textTop, chip.Width, chip.Height),
                                    chipInk, Chrome.PillText);
                    x += chipW[next] + gap;
                    next++;
                }

                if (line == lines - 1 && next < tags.Length)
                {
                    int dot = Sc(3), ew = Chrome.DotsWidth(dot);
                    int ex = Math.Min(x, cell.Right - ew);
                    if (ex >= cell.X)
                    {
                        Chrome.DrawDots(g, new Rectangle(ex, y, ew, h), chipInk, dot);
                        x = ex + ew + gap;
                    }
                }

                right = Math.Max(right, Math.Min(cell.Right, x - gap));
            }

            if (right > cell.X)
                RememberTags(rowIndex, new Rectangle(cell.X, cell.Y, right - cell.X, cell.Height), clipLeft);
        }

        void PaintMark(Graphics g, int[] widths, int top, int rowH, CellMark m)
        {
            if (m.Column < 0 || m.Column >= _columns.Count || string.IsNullOrEmpty(m.Text)) return;

            Rectangle slot = ColSlot(widths, top, rowH, m.Column);
            if (slot.Width <= 0) return;

            int yOffset = 0;
            // Символ галочки (Segoe UI/Emoji) из-за метрик шрифта садится ниже оптического центра строки
            if (m.Text.IndexOfAny(new char[] { '\u2714', '\u2713', '\u2705' }) >= 0)
                yOffset = -Sc(2);

            // Цветной полоски у правого края больше нет: сам текст уже покрашен, и
            // полоска только удваивала одно и то же сообщение в самой правой колонке.
            Chrome.DrawText(g, m.Text, Theme.FBody, new Rectangle(slot.Left, top + yOffset, slot.Width, rowH),
                            m.Color, Chrome.CellRight);
        }

        void PaintHeader(Graphics g, int[] widths)
        {
            Chrome.PaintBase(this, g, new Rectangle(0, 0, Width, HeaderHeight), Surface);

            int scrollLeft = LeftX + widths[0];
            int scrollWidth = Math.Max(0, Width - PadRight - scrollLeft);

            // 1. Отрисовка заголовков прокручиваемых колонок (с 1 по N-1)
            if (_columns.Count > 1 && scrollWidth > 0)
            {
                Region oldClip = g.Clip;
                g.SetClip(new Rectangle(scrollLeft, 0, scrollWidth, HeaderHeight));

                for (int c = 1; c < _columns.Count; c++)
                {
                    int cx = ColX(widths, c);
                    if (cx < scrollLeft || cx >= Width - PadRight) continue;
                    PaintHeaderCell(g, widths, c);
                }

                g.Clip = oldClip;
            }

            // 2. Закреплённая область заголовка (колонка 0 и левый гутер)
            {
                Region oldClip = g.Clip;
                int head0W = Math.Min(scrollLeft, Width - PadRight);
                g.SetClip(new Rectangle(0, 0, head0W, HeaderHeight));

                if (ShowPinIndicator && ShowHeaderPin)
                {
                    RectangleF hp = PinRect(0, HeaderHeight);
                    Color ink = PinnedFirst ? Theme.Light
                              : (_headPinHot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
                    Icons.Draw(g, PinnedFirst ? Glyph.StarFill : Glyph.Star,
                               RectangleF.Inflate(hp, -Sc(4), -Sc(4)), ink, 1.3f);
                }

                if (_columns.Count > 0)
                {
                    PaintHeaderCell(g, widths, 0);
                }

                g.Clip = oldClip;
            }

            // 3. Тонкий вертикальный разделитель в шапке
            if (_scrollX > 0 && _columns.Count > 1)
            {
                using (Pen divPen = new Pen(Theme.Hairline))
                    g.DrawLine(divPen, scrollLeft - 1, 0, scrollLeft - 1, HeaderHeight);
            }

            // 4. Линия под шапкой: без неё заголовки набраны тем же кеглем и цветом,
            // что и тело, и шапка от списка не отделяется вовсе.
            if (ShowHeaderRule && _columns.Count > 0)
            {
                using (Pen rule = new Pen(Theme.Hairline))
                    g.DrawLine(rule, PadX, HeaderHeight - 1, Width - PadRight, HeaderHeight - 1);
            }

            PaintDropMark(g, widths);
            PaintGrip(g, widths);
        }

        void PaintHeaderCell(Graphics g, int[] widths, int c)
        {
            bool active = c == SortColumn;
            Column col = _columns[c];

            Rectangle hr = ColSlot(widths, 0, HeaderHeight, c);
            if (hr.Width <= 0) return;

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
                hr.Width = Math.Max(0, hr.Width - arrow - Sc(4));
            }

            // Заголовок, который сейчас тащат, гаснет: он «взят в руку», а место, куда
            // он встанет, показывает вертикальная черта ниже.
            Color ink = active ? Theme.Text : Theme.TextDim;
            if (c == _dragCol) ink = Color.FromArgb(ink.A / 3, ink);

            Chrome.DrawText(g, col.Title, Theme.FLabel, hr, ink,
                            col.Right ? Chrome.CellRight : Chrome.CellLeft);
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

            int x = _dropAt == 0 ? LeftX : (_dropAt < _columns.Count ? ColX(widths, _dropAt) : ColX(widths, _columns.Count - 1) + widths[_columns.Count - 1]);
            if (x > Width - PadRight) x = Width - PadRight;

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
                if (_columns.Count > 0)
                {
                    int x0 = LeftX + widths[0];
                    if (x0 < Width - PadRight)
                        g.DrawLine(p, x0, top, x0, bottom);
                }
                for (int c = 1; c < _columns.Count; c++)
                {
                    if (_columns[c].Width > 0)
                    {
                        int x = ColX(widths, c);
                        if (x >= LeftX + widths[0] - Sc(2) && x < Width - PadRight)
                            g.DrawLine(p, x, top, x, bottom);
                    }
                }
            }
        }
    }
}
