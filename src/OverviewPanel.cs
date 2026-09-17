using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Сводка по библиотеке над списком проектов: плитки с числами и календарь работы.
    ///
    /// Не контрол, а рисовалка. Панель обязана ехать вместе с плитками — она стоит НАД
    /// ними в одном потоке прокрутки, а не отдельной шапкой; отдельный контрол пришлось
    /// бы двигать за чужой прокруткой вручную, и он всё равно перекрывал бы плитки при
    /// перелёте за край. Поэтому HomeView просит у панели высоту, отдаёт ей кусок своей
    /// поверхности и пересылает мышь — как делает со своими заголовками секций.
    ///
    /// Своих данных панель не считает: и каталог, и история приходят из ProjectIndex,
    /// сводка пересчитывается, только когда там подменили список (сканирование
    /// публикует новый — см. ProjectIndex.Sets).
    /// </summary>
    public sealed class OverviewPanel
    {
        public ProjectIndex Index;

        /// <summary>Развёрнута ли панель. Свёрнутая — одна строка с заголовком.</summary>
        public bool Open = true;

        public float Dpi = 1f;

        /// <summary>Изменилась высота — владельцу пора пересчитать раскладку.</summary>
        public event Action LayoutChanged;

        /// <summary>Что-то поменялось на вид — достаточно перерисовать.</summary>
        public event Action Repaint;

        /// <summary>Пользователь свернул панель — это стоит запомнить.</summary>
        public event Action StateChanged;

        /// <summary>Место панели без учёта прокрутки — как Bounds у заголовка секции.</summary>
        public Rectangle Bounds;

        int Sc(int v) { return (int)Math.Round(v * Dpi); }

        /// <summary>
        /// Значение в плитке. Отдельный кегль, а не FTitle: подпись и число обязаны
        /// читаться как пара «мелкое серое / крупное белое», а на тринадцатом они
        /// сливаются в две одинаковые строки.
        /// </summary>
        static readonly Font FValue = Theme.UISemibold(15f);

        // ------------------------------------------------------------------ размеры

        const int CardGap = 8;
        const int Cols = 4;        // плиток в ряду
        const int CellGap = 3;     // между клетками календаря
        const int CellMax = 14;

        /// <summary>
        /// Панель занимает две трети ширины. На всю ширину плитка растягивалась в
        /// полосу шесть к одному: подпись жмётся к левому краю, справа полкарточки
        /// пустоты, и весь блок читается разъехавшимся. Правая треть свободна
        /// намеренно — там будет чему появиться, а пока пусть лучше будет воздух, чем
        /// растянутое.
        /// </summary>
        int ContentWidth(int width)
        {
            return width <= Sc(560) ? width : Math.Max(Sc(560), width * 2 / 3);
        }

        // --------------------------------------------------------------- раскладка

        Rectangle _chevron, _title, _grid, _splash;

        /// <summary>Вся строка заголовка кликабельна — по ней панель сворачивается.</summary>
        Rectangle _toggle;
        bool _headHot;
        readonly List<Rectangle> _cards = new List<Rectangle>();
        readonly List<string> _cardLabel = new List<string>();
        readonly List<string> _cardValue = new List<string>();

        DateTime _gridFrom, _gridTo;
        int _cell, _gridMax;

        /// <summary>
        /// Высоты строк берём у самих шрифтов, а не назначаем числом. Коробка ниже
        /// строки — это не «плотнее», это обрезанные сверху и снизу буквы: GDI при
        /// VerticalCenter центрирует строку в коробке и всё, что не влезло, срезает.
        /// Первая версия панели ровно так и срезала подписям хвосты у «y» и «j».
        /// </summary>
        int _labH, _valH, _headH, _monthH;

        void MeasureRows()
        {
            _labH = TextRenderer.MeasureText("Ag", Theme.FBadge).Height;
            _valH = TextRenderer.MeasureText("Ag", FValue).Height;
            _monthH = TextRenderer.MeasureText("Ag", Theme.FMini).Height;
            _headH = Math.Max(Sc(30), TextRenderer.MeasureText("Ag", Theme.FHead).Height + Sc(8));
        }

        /// <summary>Сколько воздуха между подписью месяца и первой клеткой под ней.</summary>
        int MonthGap { get { return Sc(7); } }

        /// <summary>
        /// Своего зазора между подписью и числом нет: у измеренной строки сверху и
        /// снизу уже сидит внутренний лидинг шрифта, и любой отступ поверх него
        /// разносит пару на треть плитки — она перестаёт читаться как одна.
        /// </summary>
        int CardHeight { get { return Sc(6) + _labH + _valH + Sc(6); } }

        int Step { get { return _cell + Sc(CellGap); } }

        int _gridW;

        /// <summary>
        /// Подобрать клетку под отведённую ширину и вернуть ширину получившейся сетки —
        /// по ней равняется вся панель. Само место сетки задаётся позже, в LayoutGrid:
        /// оно зависит от того, сколько над ней заняли плитки.
        /// </summary>
        int PrepareGrid(int avail)
        {
            _gridTo = DateTime.Today;
            _gridFrom = _gridTo.AddDays(-364);

            int gap = Sc(CellGap);
            int cols = Weeks(_gridFrom, _gridTo);
            _cell = Math.Max(Sc(7), Math.Min(Sc(CellMax), (avail - gap * (cols - 1)) / Math.Max(1, cols)));
            _gridW = cols * Step - gap;

            // В совсем узком окне клетка упирается в нижний предел и сетка вылезает за
            // край. Тогда равняемся по окну: съехавшая панель хуже несошедшихся краёв.
            return Math.Min(avail, _gridW);
        }

        /// <summary>
        /// Разложить панель по ширине и вернуть её нижний край. Считается заново на
        /// каждую перестройку сетки — как и всё остальное в HomeView.
        /// </summary>
        public int Layout(int width, int top)
        {
            _cards.Clear();
            _cardLabel.Clear();
            _cardValue.Clear();

            MeasureRows();

            // Ширину задаёт календарь. Клетка целочисленная, и сетка почти всегда чуть
            // уже отведённого места — остаток от деления просто некуда деть. Если
            // равнять плитки по доступной ширине, их правый край не сходится с
            // последним столбцом на десяток пикселей, и это видно.
            int w = PrepareGrid(ContentWidth(width));
            int y = top;
            int headH = _headH;

            int chev = Sc(18);
            _chevron = new Rectangle(0, y + (headH - chev) / 2, chev, chev);
            _title = new Rectangle(_chevron.Right + Sc(8), y, Sc(240), headH);

            int titleW = TextRenderer.MeasureText("Overview", Theme.FHead).Width;
            _toggle = new Rectangle(0, y, _title.X + titleW + Sc(10), headH);

            if (!Open)
            {
                Bounds = new Rectangle(0, top, w, headH);
                return Bounds.Bottom;
            }

            y += headH + Sc(14);
            y = LayoutCards(w, y);
            y += Sc(24);
            y = LayoutGrid(y);

            _splash = new Rectangle(0, y + Sc(12), w,
                                    TextRenderer.MeasureText("Ag", Theme.FLabel).Height);
            y = _splash.Bottom;

            Bounds = new Rectangle(0, top, w, y - top);
            return Bounds.Bottom;
        }

        int LayoutCards(int w, int y)
        {
            Stats st = Ensure();

            string[] labels = { "Active days", "Streak", "Record", "Peak hour" };
            string[] values =
            {
                Num(st.ActiveDays), Days(st.Streak), Days(st.Record), Hour(st.PeakHour),
            };

            int gap = Sc(CardGap);
            int ch = CardHeight;

            // Границы считаем от края, а не «ширина плитки × номер»: при делении
            // нацело остаток съедала бы последняя плитка, и её правый край не дотягивал
            // бы до конца календаря на пару пикселей.
            for (int i = 0; i < labels.Length; i++)
            {
                int col = i % Cols;
                int x = col * (w + gap) / Cols;
                int right = (col + 1) * (w + gap) / Cols - gap;
                _cards.Add(new Rectangle(x, y + (i / Cols) * (ch + gap), right - x, ch));
                _cardLabel.Add(labels[i]);
                _cardValue.Add(values[i]);
            }

            int rows = (labels.Length + Cols - 1) / Cols;
            return y + rows * ch + (rows - 1) * gap;
        }

        /// <summary>
        /// Календарь за последний год: столбец — неделя, строка — день недели, как у
        /// GitHub. Срок ровно один и не выбирается — переключатель сроков убран, а
        /// вместе с ним и вторая форма календаря, которая была нужна только месяцу.
        /// </summary>
        int LayoutGrid(int y)
        {
            _grid = new Rectangle(0, y + _monthH + MonthGap, _gridW, GridRows * Step - Sc(CellGap));

            _gridMax = 1;
            if (Index != null)
                for (DateTime d = _gridFrom; d <= _gridTo; d = d.AddDays(1))
                {
                    int n = Index.History.SavesOn(d);
                    if (n > _gridMax) _gridMax = n;
                }

            return _grid.Bottom;
        }

        const int GridRows = 7;

        static int Weeks(DateTime from, DateTime to)
        {
            return (int)((to - from.AddDays(-Weekday(from))).TotalDays) / 7 + 1;
        }

        /// <summary>Понедельник — ноль: неделя начинается с него, а не с воскресенья.</summary>
        static int Weekday(DateTime d)
        {
            int w = (int)d.DayOfWeek;      // 0 — воскресенье
            return w == 0 ? 6 : w - 1;
        }

        /// <summary>Понедельник первого столбца — от него отсчитываются все клетки.</summary>
        DateTime FirstCol { get { return _gridFrom.AddDays(-Weekday(_gridFrom)); } }

        /// <summary>Какой день стоит в этой клетке. Обратное — CellOf.</summary>
        DateTime DayAt(int col, int row) { return FirstCol.AddDays(col * 7 + row); }

        void CellOf(DateTime day, out int col, out int row)
        {
            int n = (int)(day - FirstCol).TotalDays;
            col = n / 7; row = n % 7;
        }

        // --------------------------------------------------------------- отрисовка

        public void Paint(Graphics g, Control owner, int scroll)
        {
            int dy = -scroll;
            if (owner != null && (Bounds.Bottom + dy < 0 || Bounds.Y + dy > owner.Height)) return;

            PaintChevron(g, RectangleF.Inflate(Shift(_chevron, dy), -Sc(3), -Sc(3)),
                         _headHot ? Theme.Text : Theme.TextDim);
            Chrome.DrawText(g, "Overview", Theme.FHead, Shift(_title, dy), Theme.Text, Chrome.Left);

            if (!Open) return;

            for (int i = 0; i < _cards.Count; i++) PaintCard(g, owner, Shift(_cards[i], dy), i);
            PaintGrid(g, dy);
            Chrome.DrawText(g, _hover.Length > 0 ? _hover : Footer(), Theme.FLabel,
                            Shift(_splash, dy), Theme.TextDim, Chrome.CellLeft);
        }

        /// <summary>
        /// Свёрнутая панель — та же галка, повёрнутая на четверть. Отдельного значка
        /// «вправо» в наборе нет, и заводить его ради одного состояния незачем.
        /// </summary>
        void PaintChevron(Graphics g, RectangleF box, Color ink)
        {
            if (Open) { Icons.Draw(g, Glyph.ChevronDown, box, ink, 1.5f); return; }

            System.Drawing.Drawing2D.GraphicsState saved = g.Save();
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-90f);
            g.TranslateTransform(-cx, -cy);
            Icons.Draw(g, Glyph.ChevronDown, box, ink, 1.5f);
            g.Restore(saved);
        }

        static Rectangle Shift(Rectangle r, int dy)
        {
            return new Rectangle(r.X, r.Y + dy, r.Width, r.Height);
        }

        /// <summary>
        /// Подпись и число внутри плитки — ОБЯЗАТЕЛЬНО с обрезкой (CellLeft, а не Left):
        /// в Chrome.Left зашит NoClipping, и длинное значение вроде «F Phrygian»
        /// рисовалось прямо поверх соседней карточки, за собственной рамкой.
        /// </summary>
        void PaintCard(Graphics g, Control owner, Rectangle r, int i)
        {
            Theme.PaintGlassSurface(owner, g, r, Sc(10), Theme.GlassSurfaceAlpha);

            int pad = Sc(12);
            int inner = r.Width - pad * 2;
            Chrome.DrawText(g, _cardLabel[i], Theme.FBadge,
                            new Rectangle(r.X + pad, r.Y + Sc(6), inner, _labH),
                            Theme.TextDim, Chrome.CellLeft);
            Chrome.DrawText(g, _cardValue[i], FValue,
                            new Rectangle(r.X + pad, r.Y + Sc(6) + _labH, inner, _valH),
                            Theme.Text, Chrome.CellLeft);
        }

        /// <summary>
        /// Плотность вместо цвета: у окна вся палитра серая, и единственное цветное
        /// пятно на экране читалось бы как ошибка, а не как график. Пустой день —
        /// чуть светлее фона, самый плотный — почти белый.
        /// </summary>
        Color Level(int saves)
        {
            if (saves <= 0) return Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

            // Корень, а не прямая пропорция: один день на 28 сохранений сплющивал бы
            // всю остальную шкалу в самый бледный уровень, и календарь читался пустым.
            int step = (int)(3.999 * Math.Sqrt(saves / (double)Math.Max(1, _gridMax)));
            int[] alpha = { 0x4C, 0x80, 0xB8, 0xF4 };
            return Color.FromArgb(alpha[Math.Max(0, Math.Min(3, step))], Theme.Light);
        }

        Bitmap _gridCache;
        string _gridKey;
        object _gridHist;

        /// <summary>
        /// Календарь держим готовой картинкой. Клеток под четыре сотни, и каждая — это
        /// GraphicsPath со скруглением; рисовать их заново на кадр значит собирать
        /// двадцать тысяч путей в секунду, пока идёт прокрутка. Меняются они только от
        /// диапазона, размера клетки и самой истории — по ним и сбрасываем.
        /// </summary>
        void PaintGrid(Graphics g, int dy)
        {
            if (Index == null || _grid.Width <= 0 || _grid.Height <= 0) return;

            // Дата в ключе не для красоты: окно в год едет каждую полночь, и без неё
            // работающая ночь напролёт программа показывала бы вчерашнюю картинку.
            string key = _gridFrom.ToString("yyyyMMdd") + "|" + _cell
                       + "|" + _grid.Width + "x" + _grid.Height + "|" + _gridMax;
            if (_gridCache == null || _gridKey != key || !ReferenceEquals(_gridHist, Index.History))
            {
                if (_gridCache != null) _gridCache.Dispose();
                _gridCache = RenderGrid();
                _gridKey = key;
                _gridHist = Index.History;
            }
            g.DrawImageUnscaled(_gridCache, _grid.X, _grid.Y + dy);

            // Обводка под курсором — поверх картинки: она одна и меняется каждый кадр.
            if (_hoverDay != default(DateTime))
            {
                int hc, hr;
                CellOf(_hoverDay, out hc, out hr);
                Theme.FillRound(g, Rectangle.Inflate(
                    new Rectangle(_grid.X + hc * Step, _grid.Y + dy + hr * Step, _cell, _cell),
                    Sc(1), Sc(1)), _cell * 0.3f, Theme.LightTop);
            }

            PaintMonths(g, dy);
        }

        /// <summary>
        /// Подписи месяцев над столбцами, где месяц начинается.
        ///
        /// Идём СПРАВА НАЛЕВО и пропускаем те, что наезжают на уже нарисованную. Иначе
        /// у годового вида первый столбец — это ещё декабрь прошлого года, а второй уже
        /// январь: две подписи на расстоянии одной клетки складывались в «Deлan».
        /// Справа налево пропускается именно обрезок слева, а не полный месяц справа.
        /// </summary>
        void PaintMonths(Graphics g, int dy)
        {
            List<int> cols = new List<int>();
            int lastMonth = -1;
            for (int c = 0; _grid.X + c * Step < _grid.Right; c++)
            {
                DateTime top = DayAt(c, 0);
                if (top.Month == lastMonth) continue;
                lastMonth = top.Month;
                cols.Add(c);
            }

            int keptX = int.MaxValue;
            for (int i = cols.Count - 1; i >= 0; i--)
            {
                string name = Months[DayAt(cols[i], 0).Month - 1];
                int x = _grid.X + cols[i] * Step;
                if (x + TextRenderer.MeasureText(name, Theme.FMini).Width + Sc(8) > keptX) continue;
                keptX = x;
                Chrome.DrawText(g, name, Theme.FMini,
                                new Rectangle(x, _grid.Y + dy - _monthH - MonthGap, Sc(40), _monthH),
                                Theme.TextDim, Chrome.Left);
            }
        }

        Bitmap RenderGrid()
        {
            Bitmap bmp = new Bitmap(_grid.Width, _grid.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            float r = _cell * 0.3f;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Theme.Smooth(g);
                for (DateTime day = _gridFrom; day <= _gridTo; day = day.AddDays(1))
                {
                    int col, row;
                    CellOf(day, out col, out row);
                    Theme.FillRound(g, new Rectangle(col * Step, row * Step, _cell, _cell),
                                    r, Level(Index.History.SavesOn(day)));
                }
            }
            return bmp;
        }

        static readonly string[] Months =
            { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        // ------------------------------------------------------------------- мышь

        string _hover = "";
        DateTime _hoverDay;

        /// <summary>Точка уже с поправкой на прокрутку. true — надо перерисовать.</summary>
        public bool MouseMove(Point p)
        {
            bool was = _headHot;
            string wasHover = _hover;

            _headHot = _toggle.Contains(p);
            _hover = "";
            _hoverDay = default(DateTime);
            if (Open && Index != null && _grid.Contains(p))
            {
                DateTime day = DayAt((p.X - _grid.X) / Math.Max(1, Step),
                                     (p.Y - _grid.Y) / Math.Max(1, Step));
                if (day >= _gridFrom && day <= _gridTo)
                {
                    int n = Index.History.SavesOn(day);
                    _hoverDay = day;
                    _hover = (n == 0 ? "nothing saved" : n + (n == 1 ? " save" : " saves"))
                           + "  ·  " + Date(day);
                }
            }
            return was != _headHot || wasHover != _hover;
        }

        public bool MouseLeave()
        {
            if (!_headHot && _hover.Length == 0) return false;
            _headHot = false; _hover = ""; _hoverDay = default(DateTime);
            return true;
        }

        /// <summary>true — клик наш, дальше его нести некуда.</summary>
        public bool MouseDown(Point p)
        {
            if (!_toggle.Contains(p)) return false;

            Open = !Open;
            if (StateChanged != null) StateChanged();
            if (LayoutChanged != null) LayoutChanged();
            else if (Repaint != null) Repaint();
            return true;
        }

        // -------------------------------------------------------------- подсчёты

        sealed class Stats
        {
            public int ActiveDays, Streak, Record, PeakHour;
            public long Disk;
        }

        Stats _stats;
        object _statsOf, _histOf;

        Stats Ensure()
        {
            if (Index == null) return _stats ?? (_stats = new Stats());
            if (_stats != null && ReferenceEquals(_statsOf, Index.Sets)
                               && ReferenceEquals(_histOf, Index.History)) return _stats;

            _statsOf = Index.Sets;
            _histOf = Index.History;
            _stats = Compute();
            return _stats;
        }

        Stats Compute()
        {
            Stats s = new Stats();
            Activity h = Index.History;
            s.ActiveDays = h.ActiveDays;
            s.Streak = h.CurrentStreak;
            s.Record = h.LongestStreak;
            s.PeakHour = h.PeakHour;

            // Вес считаем по папкам: у проекта рядом с десяток версий .als, и по сетам
            // одна и та же папка сложилась бы десять раз.
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetEntry e in Index.Sets)
                if (!e.IsBackup && e.ProjectDir.Length > 0 && dirs.Add(e.ProjectDir))
                    s.Disk += e.ProjectSize;
            return s;
        }

        // ------------------------------------------------------------ форматирование

        static string Days(int n) { return n == 0 ? "—" : n + "d"; }

        /// <summary>
        /// Дата всегда по-английски: весь интерфейс английский, а ToString без культуры
        /// берёт системную — и посреди «nothing saved · » вылезало «21 авг 2025».
        /// </summary>
        static string Date(DateTime d)
        {
            return d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }
        static string Hour(int h) { return h < 0 ? "—" : h.ToString("00") + ":00"; }

        static string Num(int n)
        {
            return n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
        }

        static string Bytes(long b)
        {
            if (b >= 1L << 40) return (b / (double)(1L << 40)).ToString("0.0") + " TB";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            return b / 1024 + " KB";
        }

        /// <summary>
        /// Строчка под календарём: вес библиотеки. Плитки её больше не показывают, а
        /// место под курсором она всё равно занимает — там появляется день из календаря.
        /// </summary>
        string Footer()
        {
            Stats s = Ensure();
            return s.Disk > 0 ? Bytes(s.Disk) + " on disk" : "";
        }

        public void Dispose()
        {
            if (_gridCache != null) { _gridCache.Dispose(); _gridCache = null; }
        }
    }
}
