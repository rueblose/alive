using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace AbletonManager
{
    /// <summary>
    /// Главная страница: проекты плитками с превью аранжировки, как стартовый экран
    /// DaVinci Resolve. Смысл не в аналитике, а в узнавании — по картинке аранжировки
    /// сет вспоминается быстрее, чем по имени в таблице.
    /// </summary>
    public sealed class HomeView : GlassControl
    {
        public ProjectIndex Index;
        public ArrangementLoader Loader;

        public event Action<SetEntry> Activated;       // двойной клик — открыть в Live
        public event Action<SetEntry> PlayRequested;
        public event Action<SetEntry> RevealRequested;
        public event Action<SetEntry> DetailsRequested; // ПКМ → уйти к сету во вкладку Sets
        public event Action NewProjectRequested;       // первая карточка в Recent

        public SetFilter SetFilter;

        /// <summary>Фильтр из общего поля поиска — по имени сета и названию папки.</summary>
        public string Filter = "";

        /// <summary>Одна плитка на папку, а не на каждую версию сета. См. настройки.</summary>
        public bool GroupByFolder = true;

        const int RecentMax = 39;
        const int PinnedMax = 16;

        // Рамка вокруг картинки внутри превью и сама картинка внутри рамки — снизу
        // рамка идёт вплотную к тексту (без своего отступа), поэтому итоговый зазор от
        // t.Thumb до настоящего края картинки справа/сверху и снизу разный. Кнопка play
        // должна отступать от самой картинки, а не от рамки, — иначе от одного края её
        // видимый отступ, от другого нет.
        const int ThumbInset = 8;
        const int PicInset = 4;

        // Превью держим готовыми картинками фиксированного размера, а не разобранными
        // аранжировками: у большого сета сотни тысяч нот, и десяток таких в памяти —
        // это десятки мегабайт. Картинку же можно просто отмасштабировать под плитку
        // любого размера, поэтому перечитывать сет при изменении окна не нужно.
        const int ThumbW = 512, ThumbH = 288;

        /// <summary>
        /// Потолок на картинки в памяти. Одна — 512×288×4 = 576 КБ, и без него на
        /// библиотеке в тысячу проектов это полгигабайта: «Показать все» прокручивают до
        /// конца, и каждая увиденная плитка оставалась бы в памяти навсегда.
        ///
        /// Потолок обязан быть НЕ МЕНЬШЕ, чем плиток в полосе, которую OnPaint успевает
        /// запросить (видимое плюс по экрану сверху и снизу). Иначе полоса не влезает
        /// в себя же: плитки вытесняют друг друга, следующая отрисовка запрашивает их
        /// заново, и круг «загрузил — вытеснил — загрузил» забивает поток интерфейса
        /// так, что колесо мыши перестаёт прокручивать. Ловится это только на широком
        /// окне с большой библиотекой, поэтому считаем от раскладки, а не числом.
        /// </summary>
        int _thumbBudget = MinThumbs;
        const int MinThumbs = 64;

        readonly Dictionary<string, Bitmap> _thumbs =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        // Когда картинку последний раз рисовали: по этому счётчику выбираем, кого
        // вытеснить. Видимые плитки отмечаются на каждом кадре, поэтому вытесняется
        // всегда что-то давно уехавшее за край.
        readonly Dictionary<string, int> _thumbUsed =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int _thumbClock;

        // Прэ-масштабированные превью: DrawImage по размеру плитки на каждый кадр при
        // прокрутке — дорогая операция (20 rescale/frame при HQ-интерполяции). Здесь
        // держим уже отмасштабированные копии, сбрасываем при ресайзе окна.
        readonly Dictionary<string, Bitmap> _scaledThumbs =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        Size _scaledSize;

        /// <summary>true во время анимации прокрутки — OnPaint использует упрощённый
        /// рендер (PaintGlassSurfaceFast, NearestNeighbor, пропускает декоративные
        /// элементы вроде обводок и бликов).</summary>
        bool _scrolling;

        // Очередь превью ведём сами: загрузчик отдаёт ответы всем подписчикам разом и
        // не знает, что из этого нужно именно нам. В работе держим несколько штук —
        // разбор сета целиком процессорный, и на одном потоке два десятка плиток
        // заполняются больше трёх секунд.
        const int MaxInFlight = 3;

        readonly List<string> _queue = new List<string>();
        readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _rendering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _diskLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sealed class Tile
        {
            public SetEntry Set;
            public Rectangle Bounds;      // без учёта прокрутки
            public Rectangle Thumb;
            public Rectangle Play;
            public Rectangle Pin;
            public bool HasPlay;
            public bool IsNewProject;     // первая карточка в Recent — «New Live Set»
            public string Subtitle;
        }

        sealed class Header
        {
            public string Text;
            public string Note;
            public Rectangle Bounds;
            public int NoteShift;
        }

        readonly List<Tile> _tiles = new List<Tile>();
        readonly List<Header> _heads = new List<Header>();
        Rectangle _allBar;                 // полоса «All projects» в самом низу

        int _scroll, _contentHeight;
        float _scrollTarget, _scrollCurrent;
        Timer _scrollTimer;
        bool _draggingBar;
        int _dragOffset;
        int _hot = -1;
        bool _playHot, _pinHot, _allHot;
        bool _showAllRecent;
        SetEntry _selected;

        Rectangle BarRect()
        {
            if (_contentHeight <= Height) return Rectangle.Empty;
            int track = Height - Sc(10);
            int h = Math.Max(Sc(40), (int)(track * (float)Height / _contentHeight));
            int max = Math.Max(1, _contentHeight - Height);
            int y = Sc(5) + (int)((track - h) * (_scroll / (float)max));
            return new Rectangle(Width - Sc(8), y, Sc(4), h);
        }

        float[] _tileHoverFactors;
        float[] _tileEntrance;
        int _entranceStartTick;
        float _allBarHoverFactor;
        float _playHoverFactor;
        float _pinHoverFactor;

        public void TriggerEntrance()
        {
            _tileEntrance = new float[_tiles.Count];
            _entranceStartTick = Environment.TickCount;
            AnimEngine.Register(this);
            Invalidate();
        }

        public override bool OnAnimTick()
        {
            bool parentStill = base.OnAnimTick();
            bool anim = false;

            if (_tileHoverFactors == null || _tileHoverFactors.Length != _tiles.Count)
                _tileHoverFactors = new float[_tiles.Count];

            for (int i = 0; i < _tiles.Count; i++)
            {
                float target = (i == _hot) ? 1.0f : 0.0f;
                float diff = target - _tileHoverFactors[i];
                if (Math.Abs(diff) > 0.01f)
                {
                    _tileHoverFactors[i] += diff * 0.35f;
                    anim = true;
                }
                else
                {
                    _tileHoverFactors[i] = target;
                }
            }

            int now = Environment.TickCount;
            if (_tileEntrance == null || _tileEntrance.Length != _tiles.Count)
                _tileEntrance = new float[_tiles.Count];

            for (int i = 0; i < _tiles.Count; i++)
            {
                int delay = Math.Min(i, 24) * 20;
                int elapsed = now - _entranceStartTick - delay;
                if (elapsed > 0)
                {
                    float diff = 1.0f - _tileEntrance[i];
                    if (diff > 0.005f)
                    {
                        _tileEntrance[i] += diff * 0.25f;
                        anim = true;
                    }
                    else
                    {
                        _tileEntrance[i] = 1.0f;
                    }
                }
                else
                {
                    anim = true;
                }
            }

            float targetAll = _allHot ? 1.0f : 0.0f;
            float diffAll = targetAll - _allBarHoverFactor;
            if (Math.Abs(diffAll) > 0.01f)
            {
                _allBarHoverFactor += diffAll * 0.35f;
                anim = true;
            }
            else _allBarHoverFactor = targetAll;

            float targetPlay = _playHot ? 1.0f : 0.0f;
            float diffPlay = targetPlay - _playHoverFactor;
            if (Math.Abs(diffPlay) > 0.01f)
            {
                _playHoverFactor += diffPlay * 0.35f;
                anim = true;
            }
            else _playHoverFactor = targetPlay;

            float targetPin = _pinHot ? 1.0f : 0.0f;
            float diffPin = targetPin - _pinHoverFactor;
            if (Math.Abs(diffPin) > 0.01f)
            {
                _pinHoverFactor += diffPin * 0.35f;
                anim = true;
            }
            else _pinHoverFactor = targetPin;

            if (anim) Invalidate();
            return parentStill || anim;
        }

        /// <summary>Выделена карточка «New Live Set» — у неё нет своего SetEntry.</summary>
        bool _newSelected;

        /// <summary>Что сейчас загружено в плеер (необязательно играет — см. Playing) —
        /// тот же приём, что и PlayingTag у RowListView: сравниваем по ссылке на
        /// SetEntry, а не по пути.</summary>
        public object PlayingTag;

        /// <summary>Плеер сейчас действительно звучит, а не на паузе.</summary>
        public bool Playing;

        public HomeView()
        {
            Cursor = Cursors.Default;
            Surface = Theme.Backdrop;
            _scrollTimer = new Timer();
            _scrollTimer.Interval = 16;
            _scrollTimer.Tick += delegate { ScrollTick(); };
        }

        // ------------------------------------------------------------- содержимое

        public void Rebuild() { Rebuild(true); }

        /// <summary>
        /// animate=false — пересобрать, не запуская заново появление плиток. Нужно набору
        /// в поиске: там пересборка идёт на каждую букву, и сетка всё время заново
        /// «влетала» снизу, вместо того чтобы просто отфильтроваться.
        /// </summary>
        public void Rebuild(bool animate)
        {
            BuildLayout(animate);
            Invalidate();
        }

        /// <summary>Сколько плиток с проектами сейчас на экране — для счётчика в шапке.
        /// Отдельно от VisibleSets(), который собирает список: счётчик спрашивают на
        /// каждой перерисовке окна, и строить ради него список незачем.</summary>
        public int VisibleCount
        {
            get
            {
                int n = 0;
                foreach (Tile t in _tiles) if (t.Set != null) n++;
                return n;
            }
        }

        /// <summary>
        /// Сеты в том порядке, в каком они лежат на экране: сперва закреплённые — в
        /// порядке закрепления, а не по дате, — потом недавние. Нужен плееру: «дальше»
        /// в мини-транспорте должно идти туда же, куда ведёт глаз, а не в какой-то
        /// свой внутренний порядок.
        /// </summary>
        public event EventHandler SelectionChanged;

        public SetEntry Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                _newSelected = false;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Перемещение выделения с клавиатуры. По горизонтали — просто по порядку
        /// плиток; по вертикали — геометрически, в ближайшую плитку соседнего ряда под
        /// тем же X. Прыжок «на ширину сетки» тут не годится: ряды разной длины и
        /// разделены заголовками секций, и на стыке Recent и All он промахивался бы.
        ///
        /// true и когда двигаться некуда — на краю сетки клавишу всё равно съедаем,
        /// иначе она уйдёт в навигацию по фокусу и выделение уедет из каталога.
        /// </summary>
        public bool MoveSelection(int dx, int dy)
        {
            List<int> cells = new List<int>();
            for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Set != null) cells.Add(i);
            if (cells.Count == 0) return false;

            int cur = -1;
            if (_selected != null)
                for (int k = 0; k < cells.Count; k++)
                    if (ReferenceEquals(_tiles[cells[k]].Set, _selected)) { cur = k; break; }

            if (cur < 0) { SelectTile(cells[0]); return true; }

            if (dx != 0)
            {
                SelectTile(cells[Math.Max(0, Math.Min(cells.Count - 1, cur + dx))]);
                return true;
            }

            Rectangle from = _tiles[cells[cur]].Bounds;
            int best = -1;
            long bestScore = long.MaxValue;
            foreach (int i in cells)
            {
                Rectangle b = _tiles[i].Bounds;
                if (dy > 0 ? b.Y <= from.Y : b.Y >= from.Y) continue;
                // Сперва ближайший ряд, и только внутри него — ближайшая колонка.
                long score = (long)Math.Abs(b.Y - from.Y) * 100000 + Math.Abs(b.X - from.X);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) SelectTile(best);
            return true;
        }

        void SelectTile(int i)
        {
            Selected = _tiles[i].Set;
            EnsureTileVisible(_tiles[i].Bounds);
        }

        void EnsureTileVisible(Rectangle b)
        {
            int pad = Sc(16);
            float target = _scrollTarget;
            if (b.Y - pad < target) target = b.Y - pad;
            else if (b.Bottom + pad > target + Height) target = b.Bottom + pad - Height;
            if (target == _scrollTarget) return;

            _scrollTarget = target;
            ClampScrollTarget();
            _scrolling = true;
            if (!_scrollTimer.Enabled) _scrollTimer.Start();
        }

        /// <summary>Меню выбранной плитки — для клавиши вызова меню на клавиатуре.</summary>
        public bool ShowMenuForSelected()
        {
            if (_selected == null) return false;
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (!ReferenceEquals(_tiles[i].Set, _selected)) continue;
                Rectangle b = _tiles[i].Bounds;
                ShowMenu(_selected, new Point(b.X + Sc(28), b.Y - _scroll + Sc(28)));
                return true;
            }
            return false;
        }

        public List<SetEntry> VisibleSets()
        {
            List<SetEntry> result = new List<SetEntry>();
            foreach (Tile t in _tiles) if (t.Set != null) result.Add(t.Set);
            return result;
        }

        bool Matches(SetEntry s)
        {
            if (SetFilter != null && !SetFilter.Matches(s)) return false;

            string q = (Filter ?? "").Trim();
            if (q.Length == 0) return true;
            if (s.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.Place.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // Рендер — то же самое «имя проекта» для человека, который ищет по звуку,
            // а не по названию сета. Та же выборка, что у предпрослушки (без Samples).
            foreach (string r in s.RenderNames)
                if (r.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            return false;
        }

        List<SetEntry> Pinned()
        {
            List<SetEntry> result = new List<SetEntry>();
            if (Index == null) return result;

            // Идём по списку закреплений, а не по всем сетам: порядок закрепления —
            // это и есть порядок на экране, он не должен скакать.
            foreach (string path in HomeStore.Pins)
            {
                foreach (SetEntry s in Index.Sets)
                    if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Matches(s)) result.Add(s);
                        break;
                    }
                if (result.Count >= PinnedMax) break;
            }
            return result;
        }

        List<SetEntry> Recent(List<SetEntry> exclude)
        {
            List<SetEntry> all = new List<SetEntry>();
            if (Index == null) return all;

            foreach (SetEntry s in Index.Sets)
            {
                if (s.IsBackup || !Matches(s)) continue;
                bool skip = false;
                foreach (SetEntry p in exclude)
                    if (ReferenceEquals(p, s)) { skip = true; break; }
                if (!skip) all.Add(s);
            }

            // Схлопывание — до обрезки по RecentMax: иначе девять «недавних» окажутся
            // девятью версиями одного и того же проекта, сохранёнными за один вечер.
            if (GroupByFolder) all = ProjectIndex.CollapseByFolder(all);

            all.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });

            if (!_showAllRecent && all.Count > RecentMax) all.RemoveRange(RecentMax, all.Count - RecentMax);
            return all;
        }

        // --------------------------------------------------------------- раскладка

        int Gap { get { return Sc(18); } }

        void BuildLayout() { BuildLayout(true); }

        void BuildLayout(bool animate)
        {
            _tiles.Clear();
            _heads.Clear();
            _allBar = Rectangle.Empty;
            if (Width <= 0) return;

            // Колонки подбираем под ширину: плитка около 250 логических точек, но
            // растягивается до ровного заполнения строки — дырки справа выглядят браком.
            int gap = Gap;
            int target = Sc(250);
            int cols = Math.Min(8, Math.Max(1, (Width + gap) / (target + gap)));
            int tileW = (Width - gap * (cols - 1)) / cols;
            int thumbH = (int)(tileW * 0.56f);
            int tileH = thumbH + Sc(68);

            // Сколько картинок разрешаем себе держать — ровно столько, сколько плиток
            // помещается в запрашиваемую полосу (три экрана), плюс запас на неполные
            // ряды по краям. См. _thumbBudget.
            int bandRows = Height * 3 / Math.Max(1, tileH + gap) + 2;
            _thumbBudget = Math.Max(MinThumbs, cols * bandRows);

            int y = 0;
            List<SetEntry> pinned = Pinned();
            if (pinned.Count > 0)
            {
                y = Section(L.S("Pinned", "Закреплённые"), "", pinned, y, cols, tileW, tileH, thumbH, gap, false);
                y += Sc(10);
            }

            // Первый слот в Recent — не проект, а кнопка «New Live Set»: место, где
            // обычно смотрят первым делом, и остаётся один клик до пустого сета.
            // Сама пачка — просто 9 последних по дате изменения, без учёта того,
            // открывали ли сет через Manager.
            List<SetEntry> recent = Recent(pinned);
            string note = HomeStore.Pins.Count > 0 || recent.Count > 0
                ? L.S("recently changed", "недавно изменённые") : "";
            y = Section(L.S("Recent", "Недавние"), note, recent, y, cols, tileW, tileH, thumbH, gap, true);

            y += Sc(14);
            _allBar = new Rectangle(0, y, Width, Sc(58));
            y += _allBar.Height;

            _contentHeight = y;
            ClampScroll();

            _tileEntrance = new float[_tiles.Count];
            if (animate)
            {
                _entranceStartTick = Environment.TickCount;
                AnimEngine.Register(this);
            }
            else
            {
                // Плитки сразу на месте: массив нулей означал бы «ещё не появились»,
                // и без запущенной анимации сетка осталась бы пустой.
                for (int i = 0; i < _tileEntrance.Length; i++) _tileEntrance[i] = 1f;
            }
        }

        int Section(string title, string note, List<SetEntry> sets, int y, int cols,
                    int tileW, int tileH, int thumbH, int gap, bool leadingNewTile)
        {
            Header h = new Header();
            h.Text = title;
            h.Note = note;
            h.Bounds = new Rectangle(0, y, Width, Sc(38));
            h.NoteShift = title.Length > 0 ? TextRenderer.MeasureText(title, Theme.FHead).Width + Sc(12) : 0;
            _heads.Add(h);
            y += h.Bounds.Height + Sc(14);

            if (sets.Count == 0 && !leadingNewTile)
            {
                _heads.Add(new Header {
                    Text = "", Note = L.S("nothing here yet", "пока пусто"),
                    Bounds = new Rectangle(0, y, Width, Sc(30)) });
                return y + Sc(30);
            }

            int slot = 0;
            if (leadingNewTile)
            {
                Tile nt = new Tile();
                nt.IsNewProject = true;
                nt.Bounds = new Rectangle(0, y, tileW, tileH);
                _tiles.Add(nt);
                slot = 1;
            }

            for (int i = 0; i < sets.Count; i++)
            {
                int col = (slot + i) % cols, row = (slot + i) / cols;
                Rectangle b = new Rectangle(col * (tileW + gap), y + row * (tileH + gap), tileW, tileH);

                Tile t = new Tile();
                t.Set = sets[i];
                t.Bounds = b;
                t.Thumb = new Rectangle(b.X, b.Y, b.Width, thumbH);
                t.HasPlay = sets[i].HasRenders;

                string date = t.Set.Modified == default(DateTime)
                    ? "" : t.Set.Modified.ToLocalTime().ToString("yyyy-MM-dd");
                string place = t.Set.Place;
                t.Subtitle = place.Length > 0 ? date + "   ·   " + place : date;

                // Отступы считаем от настоящего края картинки (Thumb минус рамка минус
                // отступ картинки в рамке), а не от рамки превью, — справа и снизу у
                // рамки разный внутренний отступ, и от рамки кнопка легла бы криво.
                int picRight = Sc(ThumbInset + PicInset);
                int picBottom = Sc(PicInset);
                int picTop = Sc(ThumbInset + PicInset);
                int margin = Sc(8);

                int btn = Sc(34);
                t.Play = new Rectangle(t.Thumb.Right - picRight - btn - margin,
                                       t.Thumb.Bottom - picBottom - btn - margin, btn, btn);
                int pin = Sc(28);
                t.Pin = new Rectangle(t.Thumb.Right - picRight - margin - pin, t.Thumb.Y + picTop + margin, pin, pin);
                _tiles.Add(t);
            }

            int total = slot + sets.Count;
            int rows = (total + cols - 1) / cols;
            return y + rows * tileH + (rows - 1) * gap + Sc(18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DropScaledThumbs();
            BuildLayout();
        }

        void DropScaledThumbs()
        {
            foreach (Bitmap b in _scaledThumbs.Values) if (b != null) b.Dispose();
            _scaledThumbs.Clear();
            _scaledSize = Size.Empty;
        }

        void ClampScroll()
        {
            int max = Math.Max(0, _contentHeight - Height);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_contentHeight <= Height) return;
            _scrollTarget -= (int)(e.Delta / 120f * Sc(90));
            ClampScrollTarget();
            _scrolling = true;
            if (!_scrollTimer.Enabled) _scrollTimer.Start();
            base.OnMouseWheel(e);
        }

        void ClampScrollTarget()
        {
            int max = Math.Max(0, _contentHeight - Height);
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
                _scrolling = false;
                Invalidate();
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

        int TileAt(Point p, out bool onPlay, out bool onPin)
        {
            onPlay = onPin = false;
            Point q = new Point(p.X, p.Y + _scroll);
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (!_tiles[i].Bounds.Contains(q)) continue;
                onPlay = _tiles[i].HasPlay && _tiles[i].Play.Contains(q);
                onPin = _tiles[i].Pin.Contains(q);
                return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingBar)
            {
                Rectangle bar = BarRect();
                int track = Height - Sc(10);
                int max = Math.Max(1, _contentHeight - Height);
                float t = (e.Y - _dragOffset - Sc(5)) / (float)Math.Max(1, track - bar.Height);
                _scrollTarget = _scrollCurrent = _scroll = (int)Math.Round(t * max);
                ClampScrollTarget();
                ClampScroll();
                Invalidate();
                return;
            }

            bool play, pin;
            int hot = TileAt(e.Location, out play, out pin);
            bool all = !_allBar.IsEmpty
                       && _allBar.Contains(new Point(e.X, e.Y + _scroll));

            if (hot != _hot || play != _playHot || pin != _pinHot || all != _allHot)
            {
                _hot = hot; _playHot = play; _pinHot = pin; _allHot = all;
                Cursor = (hot >= 0 || all) ? Cursors.Hand : Cursors.Default;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot >= 0 || _allHot)
            {
                _hot = -1; _playHot = _pinHot = _allHot = false;
                Cursor = Cursors.Default;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        int _lastPinTime;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                Rectangle bar = BarRect();
                if (!bar.IsEmpty && (bar.Contains(e.Location) || e.X >= Width - Sc(12)))
                {
                    _draggingBar = true;
                    _dragOffset = e.Y - bar.Y;
                    if (_dragOffset < 0 || _dragOffset > bar.Height) _dragOffset = bar.Height / 2;
                    return;
                }
            }

            bool play, pin;
            int hit = TileAt(e.Location, out play, out pin);

            if (e.Button == MouseButtons.Right)
            {
                if (hit >= 0 && !_tiles[hit].IsNewProject) ShowMenu(_tiles[hit].Set, e.Location);
                return;
            }

            if (_allHot)
            {
                _showAllRecent = !_showAllRecent;
                Rebuild();
                return;
            }
            if (hit < 0) return;

            // «New Live Set» ведёт себя как остальные карточки: одиночный клик только
            // выделяет, само действие — по двойному. Раньше она срабатывала сразу, и на
            // поле из одинаковых с виду плиток одна вела себя не как все — по ней
            // случайно запускали Live, просто ткнув мимо.
            if (_tiles[hit].IsNewProject)
            {
                _selected = null;
                _newSelected = true;
                Invalidate();
                return;
            }

            SetEntry s = _tiles[hit].Set;
            if (play) { if (PlayRequested != null) PlayRequested(s); return; }
            if (pin)
            {
                _lastPinTime = Environment.TickCount;
                HomeStore.TogglePin(s.Path);
                Rebuild();
                _hot = -1; _playHot = _pinHot = false;
                return;
            }

            Selected = s;
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingBar)
            {
                _draggingBar = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // Если только что открепили/закрепили проект, второй клик двойного щелчка
            // — это либо повторный клик открепления, либо прилетает в переехавшую плитку.
            // Открывать проект или запускать Live в этом случае нельзя.
            if (Math.Abs(Environment.TickCount - _lastPinTime) < SystemInformation.DoubleClickTime + 150)
                return;

            bool play, pin;
            int hit = TileAt(e.Location, out play, out pin);
            // По кнопкам внутри плитки двойной клик не должен ещё и открывать Live.
            if (hit >= 0 && !play && !pin)
            {
                if (_tiles[hit].IsNewProject)
                {
                    if (NewProjectRequested != null) NewProjectRequested();
                }
                else if (Activated != null) Activated(_tiles[hit].Set);
            }
            base.OnMouseDoubleClick(e);
        }

        void ShowMenu(SetEntry s, Point at)
        {
            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem open = new ToolStripMenuItem(L.S("Open in Live", "Открыть в Live"));
            open.Click += delegate { if (Activated != null) Activated(s); };
            m.Items.Add(open);

            if (s.HasRenders)
            {
                ToolStripMenuItem play = new ToolStripMenuItem(L.S("Play render", "Слушать рендер"));
                play.Click += delegate { if (PlayRequested != null) PlayRequested(s); };
                m.Items.Add(play);
            }

            ToolStripMenuItem pin = new ToolStripMenuItem(
                HomeStore.IsPinned(s.Path) ? L.S("Unpin", "Открепить") : L.S("Pin project", "Закрепить"));
            pin.Checked = HomeStore.IsPinned(s.Path);
            pin.Click += delegate { _lastPinTime = Environment.TickCount; HomeStore.TogglePin(s.Path); Rebuild(); };
            m.Items.Add(pin);

            ToolStripMenuItem details = new ToolStripMenuItem(L.S("Show details", "Подробности"));
            details.Click += delegate { if (DetailsRequested != null) DetailsRequested(s); };
            m.Items.Add(details);

            ToolStripMenuItem reveal = new ToolStripMenuItem(L.S("Show in Explorer", "Показать в папке"));
            reveal.Click += delegate { if (RevealRequested != null) RevealRequested(s); };
            m.Items.Add(reveal);

            m.Show(this, at);
        }

        // -------------------------------------------------------------- превью

        void Want(string path)
        {
            if (Loader == null || string.IsNullOrEmpty(path)) return;
            if (_thumbs.ContainsKey(path) || _queued.Contains(path)
                || _rendering.Contains(path) || _diskLoading.Contains(path)) return;

            // Готовая картинка с прошлого запуска — самый дешёвый путь: пара миллисекунд
            // против сотни на полный разбор сета.
            if (ThumbCache.Has(path)) { LoadFromDisk(path); return; }

            Arrangement cached = Loader.Cached(path);
            if (cached != null) { StoreThumb(cached); return; }

            _queued.Add(path);
            _queue.Add(path);
            PumpQueue();
        }

        void LoadFromDisk(string path)
        {
            _diskLoading.Add(path);
            string p = path;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool known;
                Bitmap bmp = ThumbCache.Load(p, out known);
                try
                {
                    if (IsDisposed || !IsHandleCreated) { if (bmp != null) bmp.Dispose(); return; }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _diskLoading.Remove(p);
                        if (!known)
                        {
                            // Запись пропала или испортилась — идём обычным путём.
                            if (bmp != null) bmp.Dispose();
                            Want(p);
                            return;
                        }
                        if (_thumbs.ContainsKey(p)) { if (bmp != null) bmp.Dispose(); return; }
                        PutThumb(p, bmp);
                        Invalidate();
                    });
                }
                catch { if (bmp != null) bmp.Dispose(); }
            });
        }

        void PumpQueue()
        {
            while (_inFlight.Count < MaxInFlight && _queue.Count > 0)
            {
                string p = _queue[0];
                _queue.RemoveAt(0);
                if (_thumbs.ContainsKey(p) || _rendering.Contains(p)) { _queued.Remove(p); continue; }
                _inFlight.Add(p);
                Loader.Request(p);
            }
        }

        /// <summary>Ответ загрузчика; приходит в потоке интерфейса.</summary>
        public void OnArrangement(Arrangement a)
        {
            if (IsDisposed) return;

            // Разбор всегда возвращает объект с заполненным Path — даже когда сет не
            // прочитался, — поэтому по нему и закрываем свой запрос. Ответы на чужие
            // запросы (панель подробностей, полноэкранное превью) сюда тоже приходят:
            // раз сет уже разобран, грех не сделать из него плитку.
            if (a != null && !string.IsNullOrEmpty(a.Path))
            {
                _inFlight.Remove(a.Path);
                _queued.Remove(a.Path);
                StoreThumb(a);
            }

            PumpQueue();
            Invalidate();
        }

        // --------------------------------------------------- хранение картинок

        /// <summary>Отметить, что картинку сейчас показывали, — она не кандидат на вытеснение.</summary>
        void TouchThumb(string path)
        {
            _thumbUsed[path] = ++_thumbClock;
        }

        void PutThumb(string path, Bitmap bmp)
        {
            _thumbs[path] = bmp;
            TouchThumb(path);
            TrimThumbs();
        }

        /// <summary>
        /// Считаем и вытесняем только записи с картинкой: «аранжировка пуста» и «сет не
        /// читается» — это null, памяти они не занимают вовсе, а вот выбросить такую
        /// запись значило бы разбирать безнадёжный сет заново на каждой прокрутке.
        /// </summary>
        void TrimThumbs()
        {
            int heavy = 0;
            foreach (Bitmap held in _thumbs.Values) if (held != null) heavy++;

            while (heavy > _thumbBudget)
            {
                string oldest = null;
                int best = int.MaxValue;
                foreach (KeyValuePair<string, int> kv in _thumbUsed)
                {
                    if (kv.Value >= best) continue;
                    Bitmap held;
                    if (!_thumbs.TryGetValue(kv.Key, out held) || held == null) continue;
                    best = kv.Value; oldest = kv.Key;
                }
                if (oldest == null) break;

                Bitmap b;
                if (_thumbs.TryGetValue(oldest, out b) && b != null) b.Dispose();
                _thumbs.Remove(oldest);
                _thumbUsed.Remove(oldest);
                heavy--;

                Bitmap s;
                if (_scaledThumbs.TryGetValue(oldest, out s))
                {
                    if (s != null) s.Dispose();
                    _scaledThumbs.Remove(oldest);
                }
            }
        }

        void StoreThumb(Arrangement a)
        {
            if (a == null || string.IsNullOrEmpty(a.Path)) return;
            if (_thumbs.ContainsKey(a.Path) || _rendering.Contains(a.Path)) return;

            _queued.Remove(a.Path);

            if (a.Error != null || !a.HasContent)
            {
                // Пустую аранжировку запоминаем и на диск: такой ответ стоит ровно
                // столько же, сколько картинка. Ошибку чтения — только в памяти: она
                // бывает временной (диск отключили), и записать её значило бы объявить
                // сет пустым до следующей его правки.
                if (a.Error == null) ThumbCache.SaveEmptyAsync(a.Path);
                PutThumb(a.Path, null);
                return;
            }

            _rendering.Add(a.Path);
            string path = a.Path;
            Arrangement arr = a;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap bmp = null;
                try
                {
                    RenderOptions o = new RenderOptions();
                    o.Dpi = 1f;                 // рисуем в фиксированный размер, потом масштабируем
                    o.MaxLane = 8;
                    o.ShowRuler = false;
                    bmp = ArrangementRender.ToBitmap(arr, ThumbW, ThumbH, o);
                    if (bmp != null) ThumbCache.Save(path, bmp);
                }
                catch { }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _rendering.Remove(path);
                            if (!_thumbs.ContainsKey(path))
                            {
                                PutThumb(path, bmp);
                                Invalidate();
                            }
                            else
                            {
                                if (bmp != null) bmp.Dispose();
                            }
                        });
                    }
                    else
                    {
                        if (bmp != null) bmp.Dispose();
                    }
                }
                catch
                {
                    if (bmp != null) bmp.Dispose();
                }
            });
        }

        void DropThumbs()
        {
            foreach (Bitmap b in _thumbs.Values) if (b != null) b.Dispose();
            _thumbs.Clear();
            _thumbUsed.Clear();
            DropScaledThumbs();
            _queued.Clear();
            _queue.Clear();
            _rendering.Clear();
            _inFlight.Clear();
            _diskLoading.Clear();
        }

        Bitmap GetScaledThumb(string path, Bitmap src, Size targetSize)
        {
            if (src == null || targetSize.Width <= 0 || targetSize.Height <= 0) return null;

            // Сброс кэша при изменении размера плиток (ресайз окна)
            if (_scaledSize != targetSize)
            {
                DropScaledThumbs();
                _scaledSize = targetSize;
            }

            Bitmap result;
            if (_scaledThumbs.TryGetValue(path, out result)) return result;

            // Масштабируем один раз, дальше DrawImage рисует 1:1 без ресемплинга
            result = new Bitmap(targetSize.Width, targetSize.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics tg = Graphics.FromImage(result))
            {
                tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                tg.DrawImage(src, 0, 0, targetSize.Width, targetSize.Height);
            }
            _scaledThumbs[path] = result;
            return result;
        }

        // ------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            if (_scrolling)
                Theme.SmoothFast(g);
            else
                Theme.Smooth(g);

            if (_tiles.Count == 0 && _heads.Count == 0)
            {
                Chrome.DrawText(g, L.S("Nothing indexed yet", "Пока ничего не проиндексировано"),
                    Theme.FBody, new Rectangle(0, Sc(40), Width, Sc(30)), Theme.TextDim, Chrome.Left);
                return;
            }

            foreach (Header h in _heads)
            {
                Rectangle r = new Rectangle(h.Bounds.X, h.Bounds.Y - _scroll, h.Bounds.Width, h.Bounds.Height);
                if (r.Bottom < 0 || r.Top > Height) continue;
                if (h.Text.Length > 0)
                    Chrome.DrawText(g, h.Text, Theme.FHead, r, Theme.Text,
                                    Chrome.Left | TextFormatFlags.NoClipping);
                if (h.Note.Length > 0)
                {
                    Chrome.DrawText(g, h.Note, Theme.FLabel,
                        new Rectangle(r.X + h.NoteShift, r.Y, Math.Max(0, r.Width - h.NoteShift), r.Height),
                        Theme.TextDim, Chrome.Left | TextFormatFlags.NoClipping);
                }
            }

            int bufferTop = -Height;
            int bufferBottom = Height * 2;

            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile t = _tiles[i];
                Rectangle b = new Rectangle(t.Bounds.X, t.Bounds.Y - _scroll, t.Bounds.Width, t.Bounds.Height);

                // Плитка далеко за пределами видимости (более 1 экрана от края) — пропускаем полностью
                if (b.Bottom < bufferTop || b.Top > bufferBottom) continue;

                // Плитка в упреждающей буферной зоне (в пределах 1 экрана за краем) — предзагружаем превью.
                // Уже загруженную отмечаем как нужную: она вот-вот приедет на экран, и
                // вытеснять её раньше того, что уехало далеко вверх, незачем.
                if (b.Bottom < 0 || b.Top > Height)
                {
                    if (t.Set != null)
                    {
                        if (_thumbs.ContainsKey(t.Set.Path)) TouchThumb(t.Set.Path);
                        else Want(t.Set.Path);
                    }
                    continue;
                }

                // Видимая плитка — отрисовываем
                float entrance = (_tileEntrance != null && i < _tileEntrance.Length) ? _tileEntrance[i] : 1.0f;
                if (entrance <= 0.001f) continue;

                int offsetY = (int)Math.Round((1.0f - entrance) * Sc(30));
                Rectangle animB = new Rectangle(b.X, b.Y + offsetY, b.Width, b.Height);

                float hover = (_tileHoverFactors != null && i < _tileHoverFactors.Length) ? _tileHoverFactors[i] : (i == _hot ? 1f : 0f);
                if (t.IsNewProject) PaintNewProjectTile(g, animB, i == _hot, hover, entrance);
                else PaintTile(g, t, animB, i == _hot, hover, entrance);
            }

            if (!_allBar.IsEmpty) PaintAllBar(g);

            Rectangle bar = BarRect();
            if (!bar.IsEmpty)
            {
                bool barHot = _draggingBar || bar.Contains(PointToClient(Cursor.Position));
                Color c = _draggingBar ? Theme.Text : (barHot ? Theme.Light : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                Theme.FillRound(g, bar, bar.Width / 2f, c);
            }
        }

        /// <summary>
        /// Подложка карточки — всегда полная, с обводкой и бликом.
        ///
        /// Пробовал рисовать при прокрутке упрощённо (PaintGlassSurfaceFast, без обводки
        /// и блика — это два GraphicsPath на плитку на кадр): экономия есть, но она видна
        /// глазом. Карточки на едущей сетке теряют края и получают их обратно, стоит
        /// прокрутке замереть, — читается как мельтешение, а не как плавность. Поэтому
        /// не надо: два лишних пути на плитку дешевле, чем дёргающаяся картинка.
        /// </summary>
        void PaintCard(Graphics g, RectangleF r, float radius, int alpha)
        {
            Theme.PaintGlassSurface(this, g, r, radius, alpha);
        }

        /// <summary>Серая карточка-кнопка: плюс и подпись посередине, функция та же,
        /// что у футерной кнопки «New Live Set» — просто открыть саму Live.</summary>
        void PaintNewProjectTile(Graphics g, Rectangle b, bool hot, float hoverFactor, float entrance)
        {
            int alpha = (int)Math.Round(Theme.Lerp(Theme.GlassSurfaceAlpha, Theme.GlassSurfaceHotAlpha, hoverFactor) * entrance);
            PaintCard(g, b, Sc(Theme.CardR), alpha);
            if (_newSelected) Theme.DrawRound(g, b, Sc(Theme.CardR), Theme.Light, 1.5f);

            Color ink = hoverFactor > 0.01f ? Theme.Interpolate(Theme.TextDim, Theme.Text, hoverFactor) : Theme.TextDim;
            if (entrance < 1.0f) ink = Color.FromArgb((int)Math.Round(ink.A * entrance), ink);
            string label = L.S("New Live Set", "Новый сет Live");
            int iconSize = Sc(30);
            int labelH = Sc(24);
            int gap = Sc(10);

            int groupH = iconSize + gap + labelH;
            float iconY = b.Y + (b.Height - groupH) / 2f;

            RectangleF iconBox = new RectangleF(b.X + (b.Width - iconSize) / 2f, iconY, iconSize, iconSize);
            Icons.Draw(g, Glyph.Plus, iconBox, ink, 1.8f);

            Rectangle textR = new Rectangle(b.X, (int)(iconY + iconSize + gap), b.Width, labelH);
            Chrome.DrawText(g, label, Theme.FTitle, textR, ink, Chrome.Center | TextFormatFlags.NoClipping);
        }

        void PaintTile(Graphics g, Tile t, Rectangle b, bool hot, float hoverFactor, float entrance)
        {
            bool selected = _selected != null && ReferenceEquals(_selected, t.Set);
            int alpha = (int)Math.Round(Theme.Lerp(Theme.GlassSurfaceAlpha, Theme.GlassSurfaceHotAlpha, hoverFactor) * entrance);
            float cardR = Sc(Theme.CardR);

            PaintCard(g, b, cardR, alpha);
            if (selected) Theme.DrawRound(g, b, cardR, Theme.Light, 1.5f);

            Rectangle thumb = new Rectangle(t.Thumb.X, t.Thumb.Y - _scroll, t.Thumb.Width, t.Thumb.Height);
            Rectangle inner = Rectangle.Inflate(thumb, -Sc(ThumbInset), -Sc(ThumbInset));
            inner.Height = thumb.Height - Sc(ThumbInset);   // снизу картинка идёт вплотную к тексту
            Theme.FillRound(g, inner, Sc(Theme.ThumbR), Theme.Bg);

            Bitmap art;
            bool known = _thumbs.TryGetValue(t.Set.Path, out art);
            if (!known)
            {
                Want(t.Set.Path);
                Chrome.DrawText(g, L.S("reading…", "читаю…"), Theme.FSmall, inner,
                                Theme.TextDim, Chrome.Center);
            }
            else if (art == null)
            {
                Chrome.DrawText(g, L.S("no arrangement", "пусто на линейке"), Theme.FSmall, inner,
                                Theme.TextDim, Chrome.Center);
            }
            else
            {
                // Картинку показали — значит она не кандидат на вытеснение, см. MaxThumbs.
                TouchThumb(t.Set.Path);
                Rectangle fit = Rectangle.Inflate(inner, -Sc(PicInset), -Sc(PicInset));
                if (fit.Width > 0 && fit.Height > 0)
                {
                    // Используем прэ-масштабированную копию: DrawImage 1:1 без ресемплинга
                    Bitmap scaled = GetScaledThumb(t.Set.Path, art, fit.Size);
                    if (scaled != null)
                        g.DrawImage(scaled, fit.X, fit.Y);
                    else
                        g.DrawImage(art, fit);
                }
            }

            // Имя и дата под превью — с запасом от краёв плитки и друг от друга, но
            // сама пара строк держится ближе к превью, а не к середине пустого низа.
            int textY = thumb.Bottom + Sc(4);
            Rectangle nameR = new Rectangle(b.X + Sc(16), textY, b.Width - Sc(32), Sc(26));
            Chrome.DrawText(g, t.Set.Name, Theme.FTitle, nameR, Theme.Text,
                            Chrome.Left | TextFormatFlags.NoClipping);

            Rectangle dateR = new Rectangle(b.X + Sc(16), nameR.Bottom + Sc(4), b.Width - Sc(32), Sc(22));
            Chrome.DrawText(g, t.Subtitle ?? "", Theme.FLabel, dateR, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);

            // Кнопка прослушивания — только если рядом с проектом есть рендер.
            if (t.HasPlay)
            {
                Rectangle pb = new Rectangle(t.Play.X, t.Play.Y - _scroll, t.Play.Width, t.Play.Height);
                bool ph = hot && _playHot;
                bool playing = Playing && PlayingTag != null && ReferenceEquals(PlayingTag, t.Set);
                Theme.FillRound(g, pb, pb.Height / 2f, ph ? Color.White : Theme.Light);
                Icons.Draw(g, playing ? Glyph.Pause : Glyph.Play,
                           RectangleF.Inflate(pb, -Sc(9), -Sc(9)), Theme.OnLight, 1.4f);
            }

            // Звёздочка: у закреплённого видна всегда, у остальных — под курсором.
            bool pinned = HomeStore.IsPinned(t.Set.Path);
            if (pinned || hot)
            {
                Rectangle nb = new Rectangle(t.Pin.X, t.Pin.Y - _scroll, t.Pin.Width, t.Pin.Height);
                bool nh = hot && _pinHot;
                if (nh) Theme.PaintGlassSurface(this, g, nb, nb.Height / 2f, Theme.GlassSurfacePressedAlpha);
                Icons.Draw(g, pinned ? Glyph.StarFill : Glyph.Star, RectangleF.Inflate(nb, -Sc(6), -Sc(6)),
                           pinned ? Theme.Light : (nh ? Theme.Text : Theme.TextDim), 1.2f);
            }
        }

        void PaintAllBar(Graphics g)
        {
            Rectangle r = new Rectangle(_allBar.X, _allBar.Y - _scroll, _allBar.Width, _allBar.Height);
            if (r.Bottom < 0 || r.Top > Height) return;

            int barAlpha = _allHot ? Theme.GlassSurfaceHotAlpha : Theme.GlassSurfaceAlpha;
            PaintCard(g, r, Sc(Theme.CardR), barAlpha);

            Rectangle text = new Rectangle(r.X + Sc(20), r.Y, r.Width - Sc(70), r.Height);
            string title = _showAllRecent
                ? L.S("Show less", "Свернуть")
                : L.S("Show all projects", "Показать все проекты");
            Chrome.DrawText(g, title, Theme.FTitle, text,
                            _allHot ? Color.White : Theme.Text, Chrome.Left);

            int total = Index != null ? Index.Sets.Count : 0;
            Chrome.DrawText(g, total + L.S(" sets indexed", " сетов в индексе"), Theme.FLabel, text,
                            Theme.TextDim, Chrome.Right);

            float cx = r.Right - Sc(26), cy = r.Y + r.Height / 2f, s = Sc(5);
            using (Pen p = new Pen(_allHot ? Color.White : Theme.TextDim, 1.6f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (_showAllRecent)
                {
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx - s, cy + s * 0.4f),
                        new PointF(cx, cy - s * 0.4f),
                        new PointF(cx + s, cy + s * 0.4f) });
                }
                else
                {
                    g.DrawLine(p, cx - s, cy, cx + s, cy);
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx + s - s * 0.8f, cy - s * 0.8f),
                        new PointF(cx + s, cy),
                        new PointF(cx + s - s * 0.8f, cy + s * 0.8f) });
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scrollTimer != null) _scrollTimer.Dispose();
                DropThumbs();
            }
            base.Dispose(disposing);
        }
    }
}
