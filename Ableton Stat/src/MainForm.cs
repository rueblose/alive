using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    public sealed class MainForm : Form
    {
        readonly Segmented _mode = new Segmented();
        readonly FieldBox _search = new FieldBox();
        readonly FiltersButton _filtersBtn = new FiltersButton();

        // Действия — круглые значки: папка, пересканировать, новый проект.
        readonly IconButton _folders = new IconButton();
        readonly IconButton _rescan = new IconButton();
        readonly GlassButton _newProject = new GlassButton();

        // Мини-транспорт плеера в футере — переключить сет и play/pause, не поднимая
        // окно плеера. Прослушивание рендеров внутри сета там же, где всегда — в самом
        // плеере, сюда его дублировать незачем.
        readonly IconButton _playerPrev = new IconButton();
        readonly IconButton _playerPlayPause = new IconButton();
        readonly IconButton _playerNext = new IconButton();
        readonly IconButton _playerExpand = new IconButton();
        readonly SeekSlider _playerSeek = new SeekSlider();
        readonly VolumeSlider _playerVol = new VolumeSlider();
        readonly System.Windows.Forms.Timer _playerTimer = new System.Windows.Forms.Timer();
        string _playerTimeLeftStr = "", _playerTimeRightStr = "";
        Rectangle _rPlayerTimeLeft, _rPlayerTimeRight;

        // Кнопки самого окна.
        readonly IconButton _min = new IconButton();
        readonly IconButton _max = new IconButton();
        readonly IconButton _close = new IconButton();

        readonly RowListView _list = new RowListView();
        readonly DetailPanel _detail = new DetailPanel();
        readonly PluginSummary _summary = new PluginSummary();
        readonly HomeView _home = new HomeView();

        readonly IconToggle _viewToggle = new IconToggle();

        // Всплывающее сообщение в левом нижнем углу содержимого — «идёт запуск Live» и
        // прочее, о чём стоит сказать, но не стоит спрашивать.
        readonly Toast _toast = new Toast();

        const int ModeSets = 0, ModePlugins = 1;
        const int ViewTiles = 0, ViewList = 1;

        readonly Settings _settings;
        readonly ProjectIndex _index = new ProjectIndex();
        ArrangementLoader _arrangements;

        Thread _scanThread;
        CancellationTokenSource _cancel;
        volatile bool _scanning;
        bool _manualScan;
        int _scanDone, _scanTotal;
        int _lastScanInvalidate;

        string _pluginFilter = "";
        string _statusEn = "", _statusRu = "";

        readonly SetFilter _filter = new SetFilter();
        readonly PluginFilter _pluginFilterObj = new PluginFilter();
        readonly List<string> _versions = new List<string>();   // какие версии Live вообще есть

        // Быстрый отбор во вкладке плагинов: -1 — все, дальше индексы карточек сводки.
        int _pluginView = -1;

        // Сортировка списка: индекс колонки (-1 — сортировка по умолчанию) и направление.
        // Своя для каждого режима, поэтому сбрасывается при переключении Сеты/Плагины —
        // индексы колонок там не совпадают по смыслу.
        bool _sortDesc;

        // Настраиваемые колонки списка сетов: какие видны, в каком порядке и какой
        // ширины. Именно СПИСОК, а не множество: порядок задаёт сам пользователь,
        // перетаскивая заголовки, и восстановить его из канонического Catalog уже нельзя.
        readonly List<string> _setOrder = new List<string>();
        readonly Dictionary<string, int> _setColW =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // id -> логическая ширина

        // То же самое для таблицы плагинов — она настраивается наравне с сетами.
        readonly List<string> _pluginOrder = new List<string>();
        readonly Dictionary<string, int> _pluginColW =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static bool HasCol(List<string> order, string id)
        {
            foreach (string s in order)
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string _setSortId;
        string _pluginSortId;
        List<ColDef> _setVisible = new List<ColDef>();
        List<PluginColDef> _pluginVisible = new List<PluginColDef>();

        Rectangle _rCount, _rStatus, _rPluginSource, _rTitle;

        // Само имя в шапке — кнопка, открывающая справку. Прямоугольник текста считаем
        // при отрисовке: _rTitle это вся середина панели, и кликом по ней целиком
        // справка вылезала бы от тычка мимо всего остального.
        Rectangle _rTitleHit;
        bool _titleHot;

        readonly HelpOverlay _help = new HelpOverlay();

        // Откуда взяты данные о плагинах — показываем в статус-баре справа, только на
        // вкладке Плагинов, чтобы числам в карточках можно было верить.
        string _pluginSource = "";

        // Окно прослушивания рендеров — одно на всё приложение, живёт рядом с главным.
        PlayerDialog _player;

        public MainForm()
        {
            Text = "Alive — Ableton Live Manager b1.3";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1475, 950);
            MinimumSize = new Size(1320, 740);
            BackColor = Theme.Bg;
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _settings = Settings.Load();
            LoadColumns();

            Build();
            ApplyTexts();
        }

        // ------------------------------------------------------- каталог колонок

        delegate string CellText(SetEntry s);

        /// <summary>Описание одной возможной колонки списка сетов.</summary>
        sealed class ColDef
        {
            public string Id;
            public string En, Ru;
            public int Width;          // логический дефолт; 0 — колонка тянется
            public bool Right;
            public bool Chips;          // рисовать ячейку тегами-пилюлями, а не текстом
            public Font Font;
            public Color? Color;
            public CellText Text;
            public Comparison<SetEntry> Sort;
        }

        // Порядок здесь — это и порядок колонок на экране, и порядок пунктов в меню.
        static readonly List<ColDef> Catalog = BuildCatalog();

        // Что показываем при первом запуске и по «сбросить».
        static readonly string[] DefaultSetCols =
            { "Set", "Modified", "BPM", "Key", "Plugins", "Files"};

        static List<ColDef> BuildCatalog()
        {
            List<ColDef> c = new List<ColDef>();

            c.Add(new ColDef {
                Id = "Set", En = "Set", Ru = "Сет", Width = 0, Font = Theme.FTitle, Color = Theme.Text,
                // «+3» — столько версий той же папки спрятано под этой строкой.
                Text = delegate (SetEntry s)
                    { return s.CollapsedCount > 0 ? s.Name + "   +" + s.CollapsedCount : s.Name; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Place", En = "Place", Ru = "Место", Width = 150,
                Text = delegate (SetEntry s) { return s.Place; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Place, b.Place, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Modified", En = "Modified", Ru = "Изменён", Width = 150,
                Text = delegate (SetEntry s) { return s.Modified.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Modified.CompareTo(b.Modified); } });

            c.Add(new ColDef {
                Id = "Created", En = "Created", Ru = "Создан", Width = 150,
                Text = delegate (SetEntry s)
                    { return s.Created == default(DateTime) ? "" : s.Created.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Created.CompareTo(b.Created); } });

            c.Add(new ColDef {
                Id = "Live", En = "Live", Ru = "Live", Width = 87,
                Text = delegate (SetEntry s) { return s.ShortVersion; },
                Sort = delegate (SetEntry a, SetEntry b) { return CompareVersion(a.ShortVersion, b.ShortVersion); } });

            c.Add(new ColDef {
                Id = "BPM", En = "BPM", Ru = "Темп", Width = 81,
                Text = delegate (SetEntry s)
                    { return s.Tempo > 0 ? s.Tempo.ToString("0.##", CultureInfo.InvariantCulture) : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tempo.CompareTo(b.Tempo); } });

            c.Add(new ColDef {
                Id = "Key", En = "Key", Ru = "Тональность", Width = 143,
                Text = delegate (SetEntry s) { return s.Key; },
                Sort = delegate (SetEntry a, SetEntry b)
                    {
                        // Сеты без тональности — всегда в конце, а не вперемешку.
                        bool ea = a.Key.Length == 0, eb = b.Key.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
                    } });

            c.Add(new ColDef {
                Id = "Tracks", En = "Tracks", Ru = "Треки", Width = 100,
                Text = delegate (SetEntry s) { return s.Tracks > 0 ? s.Tracks.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tracks.CompareTo(b.Tracks); } });

            // Просто «сколько их в проекте» — без оценок и цвета. Обе по умолчанию
            // спрятаны и стоят перед своими «Missed»-соседками: сперва сколько всего,
            // потом сколько из них потеряно.
            c.Add(new ColDef {
                Id = "PluginCount", En = "Plugins", Ru = "Плагины", Width = 100,
                Text = delegate (SetEntry s) { return s.Plugins.Length > 0 ? s.Plugins.Length.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Plugins.Length.CompareTo(b.Plugins.Length); } });

            c.Add(new ColDef {
                Id = "FileCount", En = "Files", Ru = "Файлы", Width = 100,
                Text = delegate (SetEntry s) { return s.TotalRefs > 0 ? s.TotalRefs.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.TotalRefs.CompareTo(b.TotalRefs); } });

            // Plugins и Files — только цветная отметка, текст ячейки пуст.
            // Сортировка — именно по ПОТЕРЯННЫМ: колонка так и называется, а раньше она
            // молча сортировала по общему числу плагинов, то есть не по тому, что показывает.
            c.Add(new ColDef {
                Id = "Plugins", En = "Plugins Missed", Ru = "Плагины", Width = 165, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingPlugins.CompareTo(b.MissingPlugins); } });

            c.Add(new ColDef {
                Id = "Files", En = "Files Missed", Ru = "Файлы", Width = 130, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingFiles.CompareTo(b.MissingFiles); } });

            c.Add(new ColDef {
                Id = "Tags", En = "Tags", Ru = "Теги", Width = 180, Chips = true,
                Text = delegate (SetEntry s) { return ProjectMeta.JoinTags(ProjectMeta.TagsOf(s.ProjectDir)); },
                Sort = delegate (SetEntry a, SetEntry b)
                    {
                        // Непомеченные — в конец: колонка тегов нужна, чтобы видеть
                        // помеченное, а не любоваться пустотой в начале списка.
                        string ta = ProjectMeta.JoinTags(ProjectMeta.TagsOf(a.ProjectDir));
                        string tb = ProjectMeta.JoinTags(ProjectMeta.TagsOf(b.ProjectDir));
                        bool ea = ta.Length == 0, eb = tb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(ta, tb, StringComparison.CurrentCultureIgnoreCase);
                    } });

            // Вес всей папки проекта, а не одного .als: сам сет весит сотню-другую
            // килобайт всегда, а вопрос «что тут занимает место» — про сэмплы и
            // рендеры рядом с ним. Размер самого файла остался в панели сведений.
            c.Add(new ColDef {
                Id = "Size", En = "Project size", Ru = "Размер", Width = 130, Right = true,
                Text = delegate (SetEntry s) { return SizeMB(s.ProjectSize); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.ProjectSize.CompareTo(b.ProjectSize); } });

            return c;
        }

        static ColDef FindCol(string id)
        {
            foreach (ColDef d in Catalog) if (d.Id == id) return d;
            return null;
        }

        // ------------------------------------------- каталог колонок плагинов

        delegate string PluginCellText(PluginStat p);

        /// <summary>Описание одной возможной колонки таблицы плагинов — брат-близнец
        /// ColDef, только про другую сущность.</summary>
        sealed class PluginColDef
        {
            public string Id;
            public string En, Ru;
            public int Width;          // логический дефолт; 0 — колонка тянется
            public bool Right;
            public Font Font;
            public Color? Color;
            public PluginCellText Text;
            public Comparison<PluginStat> Sort;
        }

        static readonly List<PluginColDef> PluginCatalog = BuildPluginCatalog();

        static readonly string[] DefaultPluginCols =
            { "Plugin", "Developer", "FxType", "Format", "Sets", "Status" };

        static List<PluginColDef> BuildPluginCatalog()
        {
            List<PluginColDef> c = new List<PluginColDef>();

            c.Add(new PluginColDef {
                Id = "Plugin", En = "Plugin", Ru = "Плагин", Width = 0,
                Font = Theme.FTitle, Color = Theme.Text,
                Text = delegate (PluginStat p) { return p.Name; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Developer", En = "Developer", Ru = "Разработчик", Width = 180,
                Text = delegate (PluginStat p) { return p.Vendor; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        // Безымянные — в конец: колонка нужна, чтобы находить по автору,
                        // а не любоваться пустотой в начале списка.
                        bool ea = a.Vendor.Length == 0, eb = b.Vendor.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        int r = string.Compare(a.Vendor, b.Vendor, StringComparison.CurrentCultureIgnoreCase);
                        return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    } });

            c.Add(new PluginColDef {
                Id = "FxType", En = "Type", Ru = "Тип FX", Width = 150,
                Text = delegate (PluginStat p) { return p.FxType; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.FxType, b.FxType, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Format", En = "Format", Ru = "Формат", Width = 120,
                Text = delegate (PluginStat p) { return p.Format; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Format, b.Format, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Sets", En = "Sets", Ru = "Сетов", Width = 80,
                Text = delegate (PluginStat p) { return p.Sets > 0 ? p.Sets.ToString() : "—"; },
                Sort = delegate (PluginStat a, PluginStat b) { return a.Sets.CompareTo(b.Sets); } });

            // Версия и файл — из базы самой Live, поэтому есть только у установленных.
            // По умолчанию спрятаны: нужны, когда разбираешься с конкретным плагином,
            // а не когда просматриваешь список.
            c.Add(new PluginColDef {
                Id = "Version", En = "Version", Ru = "Версия", Width = 110,
                Text = delegate (PluginStat p) { return p.Installed != null ? p.Installed.Version : ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        string va = a.Installed != null ? a.Installed.Version : "";
                        string vb = b.Installed != null ? b.Installed.Version : "";
                        bool ea = va.Length == 0, eb = vb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return CompareVersion(va, vb);
                    } });

            c.Add(new PluginColDef {
                Id = "File", En = "File", Ru = "Файл", Width = 240,
                Text = delegate (PluginStat p) { return p.Installed != null ? p.Installed.Path : ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        string pa = a.Installed != null ? a.Installed.Path : "";
                        string pb = b.Installed != null ? b.Installed.Path : "";
                        bool ea = pa.Length == 0, eb = pb.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                    } });

            // Только цветная отметка, текст ячейки пуст — как Plugins/Files у сетов.
            c.Add(new PluginColDef {
                Id = "Status", En = "Status", Ru = "Состояние", Width = 160, Right = true,
                Text = delegate (PluginStat p) { return ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return ((int)a.Match).CompareTo((int)b.Match); } });

            return c;
        }

        static PluginColDef FindPluginCol(string id)
        {
            foreach (PluginColDef d in PluginCatalog) if (d.Id == id) return d;
            return null;
        }

        /// <summary>
        /// Папка проекта легко тянет на гигабайты, а «3841 MB» читается хуже, чем
        /// «3.75 GB» — поэтому после тысячи мегабайт переходим на гигабайты.
        /// </summary>
        internal static string SizeMB(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / 1024.0 / 1024.0;
            if (mb >= 1024)
            {
                double gb = mb / 1024.0;
                return gb.ToString(gb >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture) + " GB";
            }
            return mb.ToString(mb >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ---------------------------------------------------- состояние колонок

        void LoadColumns()
        {
            LoadColumnSpec(_settings.SetColumns, _setOrder, _setColW, DefaultSetCols, "Set", true);
            LoadColumnSpec(_settings.PluginColumns, _pluginOrder, _pluginColW, DefaultPluginCols, "Plugin", false);
        }

        /// <summary>
        /// Разбор строки вида «Set,Modified:150,BPM:81». Порядок токенов — это и есть
        /// порядок колонок на экране: с тех пор как заголовки можно перетаскивать,
        /// восстановить его из канонического каталога уже нельзя.
        ///
        /// mandatory — колонка, которую убрать нельзя (имя сета, имя плагина): без неё
        /// в строке не остаётся ничего, по чему её вообще опознать.
        /// </summary>
        void LoadColumnSpec(string spec, List<string> order, Dictionary<string, int> widths,
                            string[] defaults, string mandatory, bool sets)
        {
            order.Clear();
            widths.Clear();

            if (!string.IsNullOrEmpty(spec))
                foreach (string tok in spec.Split(','))
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;
                    string id = t;
                    int w = -1;
                    int colon = t.IndexOf(':');
                    if (colon > 0)
                    {
                        id = t.Substring(0, colon);
                        int.TryParse(t.Substring(colon + 1), out w);
                    }
                    // Колонка из другой версии программы либо уже перечисленная — мимо.
                    if (sets ? FindCol(id) == null : FindPluginCol(id) == null) continue;
                    if (HasCol(order, id)) continue;
                    order.Add(id);
                    if (w > 0) widths[id] = w;
                }

            if (!HasCol(order, mandatory)) order.Insert(0, mandatory);
            if (order.Count <= 1) { order.Clear(); order.AddRange(defaults); }
        }

        void SaveColumns()
        {
            _settings.SetColumns = ColumnSpec(_setOrder, _setColW, true);
            _settings.PluginColumns = ColumnSpec(_pluginOrder, _pluginColW, false);
            _settings.Save();
        }

        string ColumnSpec(List<string> order, Dictionary<string, int> widths, bool sets)
        {
            List<string> toks = new List<string>();
            foreach (string id in order)
            {
                // Тянущейся колонке (Width == 0) ширину не пишем: она всегда занимает
                // остаток, и запомненное число всё равно ни на что не влияло бы.
                int def = sets ? WidthOf(FindCol(id)) : WidthOf(FindPluginCol(id));
                int w;
                if (def != 0 && widths.TryGetValue(id, out w) && w > 0) toks.Add(id + ":" + w);
                else toks.Add(id);
            }
            return string.Join(",", toks.ToArray());
        }

        static int WidthOf(ColDef d) { return d != null ? d.Width : 0; }
        static int WidthOf(PluginColDef d) { return d != null ? d.Width : 0; }

        // ------------------------------------------------------------ построение

        void Build()
        {
            _folders.Icon = Glyph.Folder;
            _rescan.Icon = Glyph.Refresh;
            _min.Icon = Glyph.Minimize;
            _max.Icon = Glyph.Maximize;
            _close.Icon = Glyph.Close;
            _close.Danger = true;

            _min.Click += delegate { WindowState = FormWindowState.Minimized; };
            _max.Click += delegate { ToggleMaximize(); };
            _close.Click += delegate { Close(); };
            foreach (IconButton b in new IconButton[] { _folders, _rescan, _min, _max, _close })
                Controls.Add(b);

            // Кнопка нового сета переехала в футер и стала подписанной — доставать
            // новый пустой проект по одной иконке было не очевидно.
            Controls.Add(_newProject);

            _playerPrev.Icon = Glyph.PrevSet;
            _playerPlayPause.Icon = Glyph.Play;
            _playerNext.Icon = Glyph.NextSet;
            _playerExpand.Icon = Glyph.OpenPlaylist;
            _playerPrev.Click += delegate { if (_player != null && !_player.IsDisposed) _player.PrevSet(); };
            _playerNext.Click += delegate { if (_player != null && !_player.IsDisposed) _player.NextSet(); };
            _playerPlayPause.Click += delegate
            {
                if (_player != null && !_player.IsDisposed) _player.PlayPause();
            };
            _playerExpand.Click += delegate { ExpandPlayer(); };

            _playerSeek.Seeked += delegate (float pct)
            {
                if (_player != null && !_player.IsDisposed && _player.Audio != null && _player.Audio.IsOpen)
                {
                    int len = _player.Audio.Length;
                    if (len > 0) _player.Seek((int)(pct * len));
                }
            };

            _playerVol.Value = 0.5f;
            _playerVol.ValueChanged += delegate
            {
                if (_player != null && !_player.IsDisposed)
                    _player.Volume = _playerVol.Value;
            };

            _playerTimer.Interval = 50;
            _playerTimer.Tick += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    AudioPlayer audio = _player.Audio;
                    if (audio != null && audio.IsOpen)
                    {
                        int len = audio.Length, pos = audio.Position;
                        _playerTimeLeftStr = TimeStr(pos);
                        _playerTimeRightStr = len > 0 ? TimeStr(len) : "";
                        if (len > 0) _playerSeek.Progress = Math.Min(1f, pos / (float)len);
                        else _playerSeek.Progress = 0f;
                    }
                    else
                    {
                        _playerTimeLeftStr = "";
                        _playerTimeRightStr = "";
                        _playerSeek.Progress = 0f;
                    }
                    _playerVol.Value = _player.Volume;
                    UpdatePlayerTransport();
                    if (!_rPlayerTimeLeft.IsEmpty) Invalidate(_rPlayerTimeLeft);
                    if (!_rPlayerTimeRight.IsEmpty) Invalidate(_rPlayerTimeRight);
                }
            };

            _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible =
                _playerExpand.Visible = _playerSeek.Visible = _playerVol.Visible = false;
            Controls.Add(_playerPrev); Controls.Add(_playerPlayPause);
            Controls.Add(_playerNext); Controls.Add(_playerSeek);
            Controls.Add(_playerVol); Controls.Add(_playerExpand);

            _mode.SelectedChanged += delegate
            {
                _pluginFilter = "";
                _pluginView = -1;
                _summary.Selected = -1;
                _setSortId = null; _pluginSortId = null; _sortDesc = false;
                _list.SortColumn = -1;
                UpdateFiltersButton();
                LayoutAll();
                Refill();
            };
            Controls.Add(_mode);

            _viewToggle.SelectedChanged += delegate
            {
                SetEntry setFromTiles = _home.Selected;
                RowData listRowBefore = _list.Selected;
                SetEntry setFromList = listRowBefore != null ? listRowBefore.Tag as SetEntry : null;

                Refill();

                if (_viewToggle.SelectedIndex == ViewList)
                {
                    SetEntry target = setFromTiles ?? setFromList;
                    if (target != null)
                        _list.SelectRow(r => ReferenceEquals(r.Tag, target));
                }
                else
                {
                    SetEntry target = setFromList ?? setFromTiles;
                    if (target != null)
                        _home.Selected = target;
                }
                OnSelectionChanged();
            };
            Controls.Add(_viewToggle);

            _summary.CardClicked += OnSummaryCard;
            Controls.Add(_summary);

            _search.ShowClear = true;
            // Появление строк заново на каждую букву — это дёрганье, а не анимация:
            // содержимое не «пришло», оно просто отфильтровалось.
            _search.Box.TextChanged += delegate { Refill(false); };
            Controls.Add(_search);

            _filtersBtn.Click += delegate
            {
                if (_mode.SelectedIndex == ModeSets) EditFilters();
                else EditPluginFilters();
            };
            Controls.Add(_filtersBtn);

            _newProject.Click += delegate { NewProject(); };
            _folders.Click += delegate { EditRoots(); };
            _rescan.Click += delegate { StartScan(true); };

            _list.SelectionChanged += delegate { OnSelectionChanged(); };
            _list.ItemActivated += delegate { ActivateSelected(); };
            _list.HeaderClicked += OnHeaderClicked;
            _list.HeaderRightClicked += OnHeaderRightClick;
            _list.ColumnsResized += OnColumnsResized;
            _list.RowPlayClicked += OnRowPlay;
            _list.RowRightClicked += OnListRowRightClick;
            _list.RowCountClicked += OnRowCountClicked;
            _list.ColumnsReordered += OnColumnsReordered;
            _list.RowPinClicked += delegate (int idx)
            {
                if (idx >= 0 && idx < _list.Rows.Count) TogglePinAndRefresh(_list.Rows[idx].Tag as SetEntry);
            };
            _list.PinnedFirst = _settings.PinnedFirst;
            _list.PinnedFirstToggled += delegate
            {
                _settings.PinnedFirst = _list.PinnedFirst;
                _settings.Save();
                Refill();
            };
            Controls.Add(_list);

            _detail.Index = _index;
            _arrangements = new ArrangementLoader(this);
            _arrangements.Ready += _detail.OnArrangement;
            _detail.Loader = _arrangements;
            _detail.PreviewRequested += OpenPreview;
            _detail.RevealRequested += RevealSelected;
            _detail.OpenRequested += OpenSelected;
            _detail.SetRequested += OnSetRequested;
            _detail.PluginRequested += OnPluginRequested;
            _detail.NotesRequested += EditNotes;
            Controls.Add(_detail);

            _home.Index = _index;
            _home.SetFilter = _filter;
            _home.GroupByFolder = _settings.GroupByFolder;
            _home.Loader = _arrangements;
            _arrangements.Ready += _home.OnArrangement;
            _home.Activated += OnHomeActivated;
            _home.PlayRequested += OpenPlayer;
            _home.RevealRequested += RevealSet;
            _home.DetailsRequested += OnSetRequested;   // «Show details» — уйти к сету в Sets
            _home.SelectionChanged += delegate { OnSelectionChanged(); };
            _home.NewProjectRequested += delegate { NewProject(); };

            _help.CloseRequested += delegate { HideHelp(); };
            Controls.Add(_help);
            Controls.Add(_home);

            // Сообщение поверх содержимого — добавляем последним и держим впереди, чтобы
            // его не закрыл ни список, ни плитки.
            Controls.Add(_toast);
            _toast.BringToFront();
        }

        /// <summary>Плитка открыта двойным щелчком — это то же «Open in Live», что и в списке.</summary>
        void OnHomeActivated(SetEntry s)
        {
            OpenSet(s);
        }

        void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
            _max.Icon = WindowState == FormWindowState.Maximized ? Glyph.CloseFullscreen : Glyph.Maximize;
        }

        void ApplyTexts()
        {
            _mode.SetItems(L.S("Sets", "Сеты"), L.S("Plugins", "Плагины"));
            _viewToggle.SetGlyphs(Glyph.ViewTiles, Glyph.ViewList);
            _filtersBtn.Text = L.S("Filters", "Фильтры");
            _filtersBtn.Count = _filter.ActiveCount;
            _newProject.Text = L.S("New Live Set", "Новый сет Live");
            _newProject.FitToText(20);
            SetSearchCue();
        }

        void UpdateFiltersButton()
        {
            if (_mode.SelectedIndex == ModeSets)
                _filtersBtn.Count = _filter.ActiveCount;
            else
                _filtersBtn.Count = _pluginFilterObj.ActiveCount;
            _filtersBtn.Invalidate();
        }

        void SetSearchCue()
        {
            string cue;
            if (_mode.SelectedIndex == ModePlugins)
                cue = L.S("Search in plugins…", "Поиск по плагинам и разработчикам…");
            else
                cue = L.S("Search in sets…", "Поиск по сетам, проектам, плагинам…");
            // Своя подсказка, а не системная EM_SETCUEBANNER — см. комментарий у FieldBox.Cue.
            if (_search.Cue == cue) return;
            _search.Cue = cue;
            _search.Invalidate();
        }

        // ------------------------------------------------------------- раскладка

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        /// <summary>
        /// Раскладка целиком построена на величинах из макета: поле окна 30, высота
        /// органов управления 35, шаг круглых кнопок 45, содержимое с отметки 98.
        /// Панель подробностей задаёт правую границу таблицы, поэтому колонки и значки
        /// действий выстраиваются по одной вертикали.
        /// </summary>
        void LayoutAll()
        {
            int pad = Sc(Theme.Pad);
            int h = Sc(Theme.ControlH);
            int icon = Sc(Theme.IconSize);
            int step = icon + Sc(Theme.IconGap);
            int left = pad, right = ClientSize.Width - pad;
            int y = pad;

            // Кнопки окна прижаты к правому краю, действия — к левому краю панели.
            _close.SetBounds(right - icon, y, icon, icon);
            _max.SetBounds(_close.Left - step, y, icon, icon);
            _min.SetBounds(_max.Left - step, y, icon, icon);

            int panelW = Sc(Theme.PanelW);
            int panelX = right - panelW;

            _folders.SetBounds(panelX, y, icon, icon);
            _rescan.SetBounds(panelX + step, y, icon, icon);

            int titleX = _rescan.Right + Sc(8);
            int titleW = Math.Max(0, _min.Left - Sc(8) - titleX);
            _rTitle = new Rectangle(titleX, y, titleW, icon);

            _mode.Location = new Point(left, y - (_mode.Height - icon) / 2);
            bool setsMode = _mode.SelectedIndex == ModeSets;
            bool isTiles = setsMode && _viewToggle.SelectedIndex == ViewTiles;
            bool isList = setsMode && _viewToggle.SelectedIndex == ViewList;

            _newProject.Visible = isList;

            _viewToggle.Visible = setsMode;
            if (setsMode)
            {
                _viewToggle.Location = new Point(_mode.Right + Sc(16), y - (_viewToggle.Height - icon) / 2);
                _filtersBtn.SetBounds(_viewToggle.Right + Sc(16), y, Sc(140), h);
            }
            else
            {
                _filtersBtn.SetBounds(_mode.Right + Sc(16), y, Sc(140), h);
            }

            _filtersBtn.Visible = true;
            int searchX = _filtersBtn.Visible ? _filtersBtn.Right + Sc(15) : _mode.Right + Sc(16);
            _search.SetBounds(searchX, y, Sc(315), h);
            _rCount = new Rectangle(_search.Right + Sc(16), y,
                                    Math.Max(0, panelX - Sc(16) - _search.Right - Sc(16)), h);

            // Содержимое
            int top = Sc(Theme.ContentY);
            int statusH = Sc(28);

            int footerBottom = ClientSize.Height - Sc(Theme.Pad);
            int panelBottom = footerBottom + Sc(Theme.PanelPad);
            int controlH = Sc(Theme.ControlH);
            int footerControlY = footerBottom - controlH;
            int listBottom = footerControlY - Sc(10);

            _newProject.SetBounds(panelX - Sc(10) - _newProject.Width, footerControlY, _newProject.Width, controlH);

            int statusY = footerControlY + (controlH - statusH) / 2;
            _rStatus = new Rectangle(left + Sc(2), statusY, Sc(700), statusH);
            _rPluginSource = new Rectangle(left, statusY, Math.Max(0, panelX - Sc(10) - left), statusH);

            bool plugins = _mode.SelectedIndex == ModePlugins;
            int listTop = top;

            // Мини-транспорт — на Сетах (в любом виде), но не на Плагинах
            bool playerOpen = _player != null && !_player.IsDisposed;
            bool showTransport = !plugins && playerOpen;
            _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible
                = _playerExpand.Visible = _playerSeek.Visible = _playerVol.Visible = showTransport;
            if (showTransport)
            {
                int trIcon = controlH;
                int trStep = trIcon + Sc(Theme.IconGap);
                int contentRight = _newProject.Visible ? _newProject.Left - Sc(10) : panelX - Sc(10);

                int buttonsW = trIcon * 3 + Sc(Theme.IconGap) * 2;
                int timeLeftW = Sc(70);
                int timeRightW = Sc(70);
                int volW = Sc(120);
                int gapBtnsToTime = Sc(12);
                int gapTimeToSeek = Sc(6);
                int gapSeekToTime = Sc(6);
                int gapTimeToVol = Sc(18);
                int gapVolToExpand = Sc(14);

                int fixedW = buttonsW + gapBtnsToTime + timeLeftW + gapTimeToSeek + gapSeekToTime + timeRightW + gapTimeToVol + volW + gapVolToExpand + trIcon;
                int availableW = Math.Max(0, contentRight - left - fixedW);
                int seekW = Math.Max(Sc(60), Math.Min(Sc(320), availableW));

                int startX = left + Sc(7);

                _playerPrev.SetBounds(startX, footerControlY, trIcon, trIcon);
                _playerPlayPause.SetBounds(startX + trStep, footerControlY, trIcon, trIcon);
                _playerNext.SetBounds(startX + trStep * 2, footerControlY, trIcon, trIcon);

                int curX = startX + buttonsW + gapBtnsToTime;
                _rPlayerTimeLeft = new Rectangle(curX, footerControlY, timeLeftW, controlH);
                curX += timeLeftW + gapTimeToSeek;

                _playerSeek.SetBounds(curX, footerControlY, seekW, controlH);
                curX += seekW + gapSeekToTime;

                _rPlayerTimeRight = new Rectangle(curX, footerControlY, timeRightW, controlH);
                curX += timeRightW + gapTimeToVol;

                _playerVol.SetBounds(curX, footerControlY, volW, controlH);
                curX += volW + gapVolToExpand;

                _playerExpand.SetBounds(curX, footerControlY, trIcon, trIcon);
            }

            _summary.Visible = false;

            _home.Visible = isTiles;
            _list.Visible = isList || plugins;
            _detail.Visible = isList || plugins;

            if (isTiles)
                _home.SetBounds(left, top, Math.Max(Sc(200), right - left),
                                Math.Max(Sc(80), listBottom - top));

            if (isList || plugins)
            {
                _list.SetBounds(left, listTop, Math.Max(Sc(200), panelX - Sc(10) - left),
                                Math.Max(Sc(80), listBottom - listTop));
                _detail.SetBounds(panelX, top, panelW, Math.Max(Sc(120), panelBottom - top));
            }

            _toastArea = new Rectangle(left, top, Math.Max(Sc(200), right - left),
                                       Math.Max(Sc(80), listBottom - top));
            if (_toast.Visible) _toast.PlaceIn(_toastArea);
        }

        // Куда садится всплывающее сообщение — левый нижний угол содержимого. Считается
        // в раскладке, а не в момент показа: окно могли развернуть или растянуть, пока
        // сообщения не было, и координаты к этому моменту уже другие.
        Rectangle _toastArea;

        /// <summary>Показать сообщение в углу содержимого на пару секунд.</summary>
        void Notify(string en, string ru)
        {
            _toast.Post(L.S(en, ru), 2200);
            _toast.PlaceIn(_toastArea);
            _toast.BringToFront();
        }

        FormWindowState _lastWindowState;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // На развёрнутом окне рамка в точности совпадает с границами экрана, но
            // скруглённые углы DWM всё равно рисует поверх — в уголках сквозь них виден
            // рабочий стол. Гасим скругление ровно на переходе в Maximized и возвращаем
            // на Normal, а не всегда: обычное окно должно остаться скруглённым.
            if (_lastWindowState != WindowState)
            {
                _lastWindowState = WindowState;
                Glass.SetCornerRounding(this, WindowState != FormWindowState.Maximized);
                _max.Icon = WindowState == FormWindowState.Maximized ? Glyph.CloseFullscreen : Glyph.Maximize;
            }
            LayoutAll();
            if (_help.Visible) _help.Bounds = ClientRectangle;
            Invalidate(true);
        }
        protected override void OnMove(EventArgs e) { base.OnMove(e); Invalidate(false); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetSearchCue();
            LayoutAll();

            if (_index.LoadFromCache())
            {
                RefreshVersions();
                Refill();
            }

            Invalidate(true);

            if (_settings.IsFirstRun) { if (EditRoots()) return; }
            StartScan(false);
            Rewatch();
        }

        /// <summary>
        /// Стекло и скруглённые углы включаем по созданию хендла, а не по показу окна:
        /// иначе первый кадр успевает нарисоваться непрозрачным и это видно как вспышку.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Glass.Apply(this);
            RegisterMinimizeHotkey();
        }

        // ------------------------------------------------------- Win+M: свернуть окно

        const int WM_HOTKEY = 0x0312;
        const int HotkeyMinimize = 0xA11E;
        const uint MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

        bool _minimizeHotkey;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Win+M — сочетание системное, его держит проводник («свернуть все окна»), и
        /// RegisterHotKey на занятом сочетании честно возвращает false. Тогда остаётся
        /// поведение Windows: окно свернётся, но вместе со всеми остальными. Результат
        /// пишем в журнал — иначе разбираться, почему «не сворачивает только моё», не с чем.
        /// </summary>
        void RegisterMinimizeHotkey()
        {
            if (_minimizeHotkey) return;
            try
            {
                _minimizeHotkey = RegisterHotKey(Handle, HotkeyMinimize,
                                                 MOD_WIN | MOD_NOREPEAT, (uint)Keys.M);
                Diag.Line("hotkey Win+M: " + (_minimizeHotkey
                        ? "ours"
                        : "taken by Windows, falling back to the system 'minimize all'"));
            }
            catch (Exception ex) { Diag.Fail("hotkey Win+M", ex); }
        }

        /// <summary>
        /// Повтор по показу — не перестраховка: заданный до первого показа backdrop DWM
        /// иногда не подхватывает, и стекло появлялось только после того, как окно
        /// перетащат на другой рабочий стол (там визуал окна пересоздаётся заново).
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Glass.Apply(this);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyMinimize)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            MediaKeys.Cmd media = MediaKeys.Parse(m);
            if (media != MediaKeys.Cmd.None && HandleMedia(media))
            {
                m.Result = (IntPtr)1;      // взяли себе, дальше по цепочке не пускаем
                return;
            }

            base.WndProc(ref m);
            if (m.Msg != 0x0084 || (int)m.Result != 1) return;

            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            int b = Sc(6);
            bool l = p.X <= b, r = p.X >= ClientSize.Width - b;
            bool t = p.Y <= b, d = p.Y >= ClientSize.Height - b;

            if (t && l) m.Result = (IntPtr)13;
            else if (t && r) m.Result = (IntPtr)14;
            else if (d && l) m.Result = (IntPtr)16;
            else if (d && r) m.Result = (IntPtr)17;
            else if (l) m.Result = (IntPtr)10;
            else if (r) m.Result = (IntPtr)11;
            else if (t) m.Result = (IntPtr)12;
            else if (d) m.Result = (IntPtr)15;
            // За панель инструментов тащим окно — но не за само имя программы: оно
            // кнопка, и под HTCAPTION клики до OnMouseDown просто не доходят.
            else if (p.Y < Sc(Theme.ContentY) - Sc(10) && !_rTitleHit.Contains(p)) m.Result = (IntPtr)2;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_rTitleHit.Contains(e.Location)) return;
            if (e.Y < Sc(Theme.ContentY) - Sc(10)) ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _rTitleHit.Contains(e.Location))
            {
                ShowHelp();
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = _rTitleHit.Contains(e.Location);
            if (hot != _titleHot)
            {
                _titleHot = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                Invalidate(_rTitle);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_titleHot)
            {
                _titleHot = false;
                Cursor = Cursors.Default;
                Invalidate(_rTitle);
            }
            base.OnMouseLeave(e);
        }

        // ----------------------------------------------------------------- справка

        void ShowHelp()
        {
            if (_help.Visible) { HideHelp(); return; }
            _help.Open(this);
        }

        void HideHelp()
        {
            if (!_help.Visible) return;
            _help.Close();
            Focus();
            Invalidate(true);
        }

        /// <summary>
        /// Tab как хоткей переключения вкладок — специально ProcessCmdKey, а не
        /// OnKeyDown. Замерено: OnKeyDown для Tab не срабатывает вовсе, даже с
        /// KeyPreview — Control.PreProcessMessage сперва спрашивает IsInputKey у
        /// того, что сейчас в фокусе, и раз обычные кнопки на Tab отвечают «нет, это
        /// не моя клавиша», клавиша тут же уходит в фокус-навигацию (ProcessDialogKey)
        /// и молча переставляет фокус на соседний control — до OnKeyDown она просто
        /// не доходит. ProcessCmdKey — единственная точка, которая получает клавишу
        /// РАНЬШЕ фокус-навигации, независимо от того, что сейчас выделено.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Пока открыта справка, каталог под ней клавиш не слышит: стрелки листают
            // саму справку, а не выделение в спрятанном за ней списке. Ctrl+Q пропускаем —
            // выйти из программы должно быть можно откуда угодно.
            if (_help.Visible && keyData != (Keys.Control | Keys.Q))
            {
                Keys hk = keyData & Keys.KeyCode;
                if (hk == Keys.Escape || hk == Keys.F1) { HideHelp(); return true; }
                if (_help.HandleKey(hk)) return true;
                if ((keyData & Keys.Alt) == 0) return true;   // Alt+F4 и прочее системное — мимо нас
            }

            if ((keyData & Keys.KeyCode) == Keys.Tab && !_search.Box.Focused)
            {
                if (_mode.SelectedIndex == ModeSets)
                {
                    _viewToggle.SelectedIndex = _viewToggle.SelectedIndex == ViewTiles ? ViewList : ViewTiles;
                    return true;
                }
            }
            // Стрелки ведут по каталогу независимо от того, где сейчас фокус. Через
            // ProcessCmdKey, а не OnKeyDown: иначе их сперва разбирает навигация по
            // фокусу и выделение уезжает в соседний контрол, а не по сетам.
            if (!_search.Box.Focused && (keyData & Keys.Modifiers) == Keys.None)
            {
                Keys k = keyData & Keys.KeyCode;
                if ((k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                     || k == Keys.PageUp || k == Keys.PageDown) && MoveSelection(k))
                    return true;
            }

            if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.NumPad1))
            {
                _mode.SelectedIndex = ModeSets;
                return true;
            }
            if (keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.NumPad2))
            {
                _mode.SelectedIndex = ModePlugins;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool typing = _search.Box.Focused;

            // Esc окно больше не закрывает: слишком дорогая опечатка для клавиши, которой
            // закрывают диалоги. Программу закрывает только Ctrl+Q. Здесь Esc остался
            // выходом из поля поиска — то, зачем его в поле и жмут.
            if (e.KeyCode == Keys.Escape)
            {
                if (!typing) return;
                if (_home.Visible) _home.Focus(); else _list.Focus();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Q) { Close(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Box.Focus(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Shift && !e.Control && !e.Alt && !typing)
            {
                EditRoots();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F && !e.Control && !e.Alt && !e.Shift && !typing)
            {
                if (_mode.SelectedIndex == ModeSets) EditFilters();
                else EditPluginFilters();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F1) { ShowHelp(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.N) { NewProject(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.T && !typing && _mode.SelectedIndex == ModeSets)
            {
                EditNotes(SelectedSet());
                e.Handled = e.SuppressKeyPress = true;
            }

            // Свернуть окно. Win+M отобрать у Windows нельзя (см. RegisterMinimizeHotkey),
            // а сворачивать с клавиатуры надо чем-то, что работает всегда.
            else if (e.Control && e.KeyCode == Keys.M)
            {
                WindowState = FormWindowState.Minimized;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5) { StartScan(true); e.Handled = true; }
            else if (e.KeyCode == Keys.F11) { ToggleMaximize(); e.Handled = true; }
            else if (e.KeyCode == Keys.Apps && !typing)
            {
                ShowMenuForSelection();
                e.Handled = e.SuppressKeyPress = true;
            }
            // Ctrl+Пробел — аранжировка выбранного сета во весь экран. Проверяется до
            // голого пробела: тот на модификаторы не смотрит и иначе перехватил бы
            // сочетание себе, запустив прослушку вместо превью.
            else if (e.Control && e.KeyCode == Keys.Space && !typing && _mode.SelectedIndex == ModeSets)
            {
                OpenPreview();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Space && !typing && _mode.SelectedIndex == ModeSets)
            {
                // В поле поиска пробел остаётся пробелом — иначе искать станет нечем.
                TogglePlaySelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter && _list.Selected != null)
            {
                // Работает и из поля поиска: пока ничего не выбрано, Enter просто ничего
                // не делает, так что случайно открыть проект при наборе нельзя.
                if (e.Shift) RevealSelected(); else ActivateSelected();
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------ отрисовка

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, Theme.Backdrop);
            Theme.Smooth(g);

            Chrome.DrawText(g, CountText(), Theme.FButton, _rCount, Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            string title = "Alive " + Application.ProductVersion;
            Color titleColor = _titleHot ? Theme.Text : Theme.TextDim;
            Chrome.DrawText(g, title, Theme.FTitle, _rTitle, titleColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // Кликабельна ровно надпись, а не вся отведённая ей полоса.
            Size ts = TextRenderer.MeasureText(title, Theme.FTitle);
            _rTitleHit = new Rectangle(_rTitle.X + (_rTitle.Width - ts.Width) / 2, _rTitle.Y,
                                       ts.Width, _rTitle.Height);

            // Подчёркивание, указывающее на кликабельность (вызов справки)
            int lineY = _rTitleHit.Y + (_rTitleHit.Height + ts.Height) / 2 - Sc(2);
            using (Pen p = new Pen(titleColor))
                g.DrawLine(p, _rTitleHit.Left, lineY, _rTitleHit.Right - Sc(3), lineY);

            bool playerOpen = _player != null && !_player.IsDisposed;
            bool showTransport = _mode.SelectedIndex != ModePlugins && playerOpen;

            if (showTransport)
            {
                TextFormatFlags tfL = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextFormatFlags tfR = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                if (!string.IsNullOrEmpty(_playerTimeLeftStr))
                    Chrome.DrawText(g, _playerTimeLeftStr, Theme.FLabel, _rPlayerTimeLeft, Theme.Text, tfL);
                if (!string.IsNullOrEmpty(_playerTimeRightStr))
                    Chrome.DrawText(g, _playerTimeRightStr, Theme.FLabel, _rPlayerTimeRight, Theme.TextDim, tfR);
            }
            else if (_mode.SelectedIndex == ModeSets)
            {
                Chrome.DrawText(g, StatusText(), Theme.FLabel, _rStatus, Theme.TextDim, Chrome.Left);
            }

            if (_mode.SelectedIndex == ModePlugins && _pluginSource.Length > 0)
                Chrome.DrawText(g, _pluginSource, Theme.FLabel, _rPluginSource, Theme.TextDim, Chrome.Left);

            if (_scanning && _manualScan && _scanTotal > 0)
            {
                int pad = Sc(Theme.Pad);
                int left = pad;
                int right = ClientSize.Width - pad;
                int top = Sc(Theme.ContentY);
                int barX = left;
                int barW = right - left;
                int barH = Sc(3);
                int barY = top - Sc(12);

                Rectangle barRect = new Rectangle(barX, barY, barW, barH);
                Theme.FillRound(g, barRect, barH / 2f, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

                float pct = Math.Max(0.01f, Math.Min(1.0f, (float)_scanDone / _scanTotal));
                int fillW = (int)(barW * pct);
                if (fillW > 0)
                {
                    Rectangle fillRect = new Rectangle(barX, barY, fillW, barH);
                    Theme.FillRound(g, fillRect, barH / 2f, Color.FromArgb(0xFF, 0x00, 0x7A, 0xCC));
                }
            }
        }

        string CountText()
        {
            if (_scanning)
                // Пока корни только обходятся, общее число ещё неизвестно (total = 0), и
                // «0 / 0» тут врало: на большой папке это единственное, что видно
                // секундами, и читается как «программа ничего не делает».
                return _scanTotal > 0
                     ? L.S("Scanning ", "Сканирую ") + _scanDone + " / " + _scanTotal
                     : L.S("Looking for sets… ", "Ищу сеты… ") + _scanDone;
            // VisibleCount, а не VisibleSets().Count: счётчик рисуется на каждой
            // перерисовке окна (при открытом плеере — двадцать раз в секунду), и строить
            // ради него список сетов незачем.
            if (_mode.SelectedIndex == ModeSets && _viewToggle.SelectedIndex == ViewTiles)
                return _home.VisibleCount + L.S(" shown", " показано");
            return _list.Rows.Count + L.S(" shown", " показано");
        }

        string StatusText()
        {
            string s = L.S(_statusEn, _statusRu);
            return s.Length > 0 ? s : "";
        }

        // ------------------------------------------------------------ содержимое

        void Refill() { Refill(true); }

        /// <summary>
        /// Пересборка после сканирования, которое человек не заказывал. Обычный Refill
        /// строит список заново, а SetRows при этом всегда сбрасывает и выделение, и
        /// прокрутку — то есть самопроизвольное обновление выдёргивало бы читающего в
        /// начало каталога и снимало выбор посреди работы. Здесь и то, и другое
        /// возвращается на место, а строки не влетают снизу заново.
        ///
        /// Сет ищем по пути, а не по ссылке: после пересканирования объекты в каталоге
        /// другие, даже если файл на диске тот же самый.
        /// </summary>
        void RefillPreservingView()
        {
            SetEntry keep = SelectedSet();
            string keepPath = keep != null ? keep.Path : null;
            int scroll = _list.ScrollOffset;

            Refill(false);

            if (keepPath != null)
            {
                if (_viewToggle.SelectedIndex == ViewTiles && _mode.SelectedIndex == ModeSets)
                {
                    foreach (SetEntry s in _home.VisibleSets())
                        if (string.Equals(s.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                        { _home.Selected = s; break; }
                }
                else
                {
                    string path = keepPath;
                    _list.SelectRow(delegate (RowData r)
                    {
                        SetEntry s = r.Tag as SetEntry;
                        return s != null && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            // Строго после SelectRow: тот подкручивает список к найденной строке.
            _list.ScrollOffset = scroll;
        }

        /// <summary>
        /// animate=false — пересобрать содержимое, не запуская появление строк и плиток
        /// заново. Так пересобирается только набор в поиске: там Refill идёт на каждую
        /// букву, и список всё время влетал снизу вместо того, чтобы отфильтроваться.
        /// Все остальные поводы (скан закончился, сменили вкладку или вид, применили
        /// фильтры, переставили колонки) появление показывают, как и раньше.
        /// </summary>
        void Refill(bool animate)
        {
            SetSearchCue();
            LayoutAll();
            if (_mode.SelectedIndex == ModeSets)
            {
                if (_viewToggle.SelectedIndex == ViewTiles)
                {
                    _home.Filter = _search.Box.Text;
                    _home.Rebuild(animate);
                }
                else
                {
                    FillSets(animate);
                }
            }
            else
            {
                FillPlugins(animate);
            }
            Invalidate();
        }

        void FillSets(bool animate)
        {
            // Видимые колонки — в том порядке, в каком их расставил пользователь.
            _setVisible = new List<ColDef>();
            foreach (string id in _setOrder)
            {
                ColDef d = FindCol(id);
                if (d != null) _setVisible.Add(d);
            }

            Column[] cols = new Column[_setVisible.Count];
            for (int i = 0; i < _setVisible.Count; i++)
            {
                ColDef d = _setVisible[i];
                int w = d.Width;                                  // логическая ширина
                int ov;
                if (d.Width != 0 && _setColW.TryGetValue(d.Id, out ov) && ov > 0) w = ov;
                cols[i] = new Column(L.S(d.En, d.Ru), w)
                    { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color, Chips = d.Chips };
            }
            _list.ColumnsConfigurable = true;
            _list.ShowPlayButton = true;
            _list.ShowPinIndicator = true;
            _list.PlayingTag = _home.PlayingTag =
                _player != null && !_player.IsDisposed ? _player.CurrentSet : null;
            _list.Playing = _home.Playing = _player != null && !_player.IsDisposed && _player.IsPlaying;
            _list.SetColumns(cols);

            // Индекс сортируемой колонки для стрелки в шапке (или -1, если её спрятали).
            int si = -1;
            if (_setSortId != null)
                for (int i = 0; i < _setVisible.Count; i++)
                    if (_setVisible[i].Id == _setSortId) { si = i; break; }
            _list.SortColumn = si;
            _list.SortDescending = _sortDesc;

            string q = _search.Box.Text.Trim();

            List<SetEntry> matched = new List<SetEntry>();
            foreach (SetEntry s in _index.Sets)
            {
                if (!_filter.Matches(s)) continue;
                if (_pluginFilter.Length > 0 && !HasPlugin(s, _pluginFilter)) continue;
                if (q.Length > 0 && !MatchesSet(s, q)) continue;
                matched.Add(s);
            }
            // Спрятанные версии запоминаем ДО схлопывания: после него список голов уже
            // не помнит, кого он под собой держит, а «показать остальные» должно
            // показывать ровно то, что прошло текущий отбор, — не всю папку с диска.
            Dictionary<string, List<SetEntry>> hidden = null;
            if (_settings.GroupByFolder)
            {
                List<SetEntry> heads = ProjectIndex.CollapseByFolder(matched);

                Dictionary<SetEntry, bool> isHead = new Dictionary<SetEntry, bool>();
                foreach (SetEntry h in heads) isHead[h] = true;

                hidden = new Dictionary<string, List<SetEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (SetEntry s in matched)
                {
                    if (isHead.ContainsKey(s)) continue;
                    string dir = s.Directory ?? "";
                    List<SetEntry> bucket;
                    if (!hidden.TryGetValue(dir, out bucket)) hidden[dir] = bucket = new List<SetEntry>();
                    bucket.Add(s);
                }
                matched = heads;
            }
            else
            {
                // CollapsedCount живёт на самом SetEntry и сбрасывается только внутри
                // CollapseByFolder — сканирование его не трогает. Группировку могли
                // выключить уже ПОСЛЕ того, как она однажды посчитала «+N»: без сброса
                // здесь эта надпись осталась бы висеть на каждой строке даже сейчас,
                // когда версии больше не схлопываются и каждая показана отдельно.
                foreach (SetEntry s in matched) s.CollapsedCount = 0;
            }

            Comparison<SetEntry> chosen = null;
            if (_setSortId != null) { ColDef d = FindCol(_setSortId); if (d != null) chosen = d.Sort; }

            Comparison<SetEntry> order;
            if (chosen == null)
                order = delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); };
            else if (_sortDesc)
                // Разворачиваем сравнение, а не список после сортировки: с закреплёнными
                // сверху Reverse перевернул бы заодно и сами группы местами.
                order = delegate (SetEntry a, SetEntry b) { return chosen(b, a); };
            else
                order = chosen;

            // Закреплённые вперёд — но только как первый ключ сортировки: внутри своей
            // группы они упорядочены ровно так же, как и все остальные.
            HashSet<string> pins = null;
            if (_settings.PinnedFirst)
            {
                pins = new HashSet<string>(HomeStore.Pins, StringComparer.OrdinalIgnoreCase);
                Comparison<SetEntry> inner = order;
                HashSet<string> pinSet = pins;
                order = delegate (SetEntry a, SetEntry b)
                {
                    bool pa = pinSet.Contains(a.Path), pb = pinSet.Contains(b.Path);
                    if (pa != pb) return pa ? -1 : 1;
                    return inner(a, b);
                };
            }
            matched.Sort(order);

            int pIdx = VisibleIndex("Plugins"), fIdx = VisibleIndex("Files");

            List<RowData> rows = new List<RowData>();
            foreach (SetEntry s in matched)
            {
                string dir = s.Directory ?? "";
                bool open = s.CollapsedCount > 0 && _expandedDirs.Contains(dir);
                rows.Add(RowFor(s, pIdx, fIdx, open, false));
                if (!open || hidden == null) continue;

                List<SetEntry> kids;
                if (!hidden.TryGetValue(dir, out kids)) continue;

                // Внутри проекта версии всегда свежими вверх, независимо от того, как
                // отсортирован сам каталог: это уже не список проектов, а история одного.
                kids.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });
                foreach (SetEntry k in kids) rows.Add(RowFor(k, pIdx, fIdx, false, true));
            }
            _list.SetRows(rows, animate);

            // Разделитель — ровно там, где кончились закреплённые. Ни линии без группы,
            // ни линии в самом низу, когда незакреплённых не осталось.
            int lastPinned = -1;
            if (pins != null)
                for (int i = 0; i < matched.Count; i++)
                {
                    if (!pins.Contains(matched[i].Path)) break;
                    lastPinned = i;
                }
            _list.GroupSeparatorAfter = lastPinned >= 0 && lastPinned < matched.Count - 1 ? lastPinned : -1;

            OnSelectionChanged();
        }

        int VisibleIndex(string id)
        {
            for (int i = 0; i < _setVisible.Count; i++) if (_setVisible[i].Id == id) return i;
            return -1;
        }

        /// <summary>
        /// Одна строка каталога.
        ///
        /// expanded — эта папка сейчас раскрыта, и хвостик «+3» надо показать минусом:
        /// знак и есть единственное, чем раскрытая группа отличается от схлопнутой,
        /// поэтому меняем его прямо в готовой ячейке — сам Catalog о раскрытии не знает
        /// и знать не должен, он статический.
        ///
        /// childRow — это одна из версий, показанных под раскрытой строкой, а не сам
        /// проект. RowListView отступает её имя, чтобы вложенность была видна даже без
        /// подсветки группы, — см. RowData.ChildRow.
        /// </summary>
        RowData RowFor(SetEntry s, int pIdx, int fIdx, bool expanded, bool childRow)
        {
            RowData r = new RowData();
            string[] cells = new string[_setVisible.Count];
            for (int i = 0; i < _setVisible.Count; i++) cells[i] = _setVisible[i].Text(s);

            if (expanded)
                for (int i = 0; i < cells.Length; i++)
                {
                    if (_setVisible[i].Id != "Set" || cells[i] == null) continue;
                    int at = cells[i].LastIndexOf(RowListView.CountSep + RowListView.CountOpen,
                                                  StringComparison.Ordinal);
                    if (at < 0) continue;
                    cells[i] = cells[i].Substring(0, at) + RowListView.CountSep + RowListView.CountClose
                             + cells[i].Substring(at + RowListView.CountSep.Length + 1);
                }

            r.Cells = cells;
            r.ChildRow = childRow;
            r.Tag = s;
            // У версий под раскрытой строкой кнопку прослушивания не показываем: играет
            // всегда рендер главной версии проекта (см. RenderScan.Find — он смотрит
            // на папку целиком, а не на конкретный .als), так что play у каждой версии
            // играл бы один и тот же файл — кнопка без разницы в результате только сбивала бы с толку.
            r.CanPlay = s.HasRenders && !childRow;
            r.Pinned = HomeStore.IsPinned(s.Path);

            // Отметки ставим только если их колонки сейчас видно.
            if (pIdx >= 0 && s.Plugins.Length > 0)
                r.Marks.Add(new CellMark(pIdx, s.MissingPlugins > 0 ? Theme.Red : Theme.Green,
                                         s.MissingPlugins == 0 ? L.S("","") : s.MissingPlugins + L.S(" ", " нет")));

            if (fIdx >= 0)
            {
                if (s.Error.Length > 0)
                    r.Marks.Add(new CellMark(fIdx, Theme.Red, L.S("unreadable", "не читается")));
                else if (s.TotalRefs > 0 || s.MissingFiles > 0)
                    r.Marks.Add(new CellMark(fIdx, s.MissingFiles > 0 ? Theme.Red : Theme.Green,
                     s.MissingFiles == 0 ? L.S("", "")
                                         : s.MissingFiles + L.S(" ", " нет")));
            }
            return r;
        }

        /// <summary>
        /// Папки, чьи спрятанные версии сейчас показаны под своей строкой. Живёт только
        /// в памяти: раскрытие — это «посмотреть, что там», а не настройка, и тащить его
        /// через перезапуск незачем.
        /// </summary>
        readonly HashSet<string> _expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Щёлкнули по «+3» — показать или снова спрятать версии этой папки.</summary>
        void OnRowCountClicked(int idx)
        {
            if (_mode.SelectedIndex != ModeSets) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            string dir = s.Directory ?? "";
            if (!_expandedDirs.Remove(dir)) _expandedDirs.Add(dir);

            // Без анимации появления: раскрылась одна группа, а влетал бы снизу весь
            // каталог целиком — как будто список построили заново.
            SetEntry keep = SelectedSet();
            int scroll = _list.ScrollOffset;
            Refill(false);
            if (keep != null)
            {
                string path = keep.Path;
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry other = r.Tag as SetEntry;
                    return other != null && string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase);
                });
            }
            _list.ScrollOffset = scroll;
        }

        void FillPlugins(bool animate)
        {
            _list.ColumnsConfigurable = true;
            _list.ShowPlayButton = false;
            _list.ShowPinIndicator = false;
            _list.GroupSeparatorAfter = -1;

            _pluginVisible = new List<PluginColDef>();
            foreach (string id in _pluginOrder)
            {
                PluginColDef d = FindPluginCol(id);
                if (d != null) _pluginVisible.Add(d);
            }

            Column[] pcols = new Column[_pluginVisible.Count];
            for (int i = 0; i < _pluginVisible.Count; i++)
            {
                PluginColDef d = _pluginVisible[i];
                int w = d.Width;
                int ov;
                if (d.Width != 0 && _pluginColW.TryGetValue(d.Id, out ov) && ov > 0) w = ov;
                pcols[i] = new Column(L.S(d.En, d.Ru), w)
                    { Id = d.Id, Right = d.Right, Font = d.Font, Color = d.Color };
            }
            _list.SetColumns(pcols);

            int si2 = -1;
            if (_pluginSortId != null)
                for (int i = 0; i < _pluginVisible.Count; i++)
                    if (_pluginVisible[i].Id == _pluginSortId) { si2 = i; break; }
            _list.SortColumn = si2;
            _list.SortDescending = _sortDesc;

            List<PluginStat> all = _index.PluginUsage();
            _summary.Update(_index.Health(all));
            _pluginSource = _index.Inventory.Describe();

            string q = _search.Box.Text.Trim();
            List<PluginStat> matched = new List<PluginStat>();
            foreach (PluginStat st in all)
            {
                if (!PassesView(st)) continue;
                if (!_pluginFilterObj.Matches(st)) continue;
                if (q.Length > 0
                    && st.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.Vendor.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.FxType.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0
                    && st.Format.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                matched.Add(st);
            }

            // PluginUsage() уже отдаёт разумный порядок по умолчанию (по числу сетов, потом
            // по имени) — трогаем его, только если пользователь явно кликнул по заголовку.
            // Сортируем по id колонки, а не по её номеру: колонки теперь и переставляются,
            // и прячутся, так что номер сам по себе ничего не значит.
            if (_pluginSortId != null)
            {
                PluginColDef d = FindPluginCol(_pluginSortId);
                if (d != null && d.Sort != null)
                {
                    Comparison<PluginStat> cmp = d.Sort;
                    matched.Sort(_sortDesc
                        ? delegate (PluginStat a, PluginStat b) { return cmp(b, a); }
                        : cmp);
                }
            }

            int stIdx = PluginVisibleIndex("Status");

            List<RowData> rows = new List<RowData>();
            foreach (PluginStat st in matched)
            {
                RowData r = new RowData();
                string[] cells = new string[_pluginVisible.Count];
                for (int i = 0; i < _pluginVisible.Count; i++) cells[i] = _pluginVisible[i].Text(st);
                r.Cells = cells;
                r.Tag = st;

                // Отметку ставим, только если колонку состояния сейчас видно.
                if (stIdx >= 0)
                {
                    if (st.Match == MatchKind.Missing)
                        r.Marks.Add(new CellMark(stIdx, Theme.Red, L.S("❌", "не установлен")));
                    else if (st.Match == MatchKind.OtherFormat)
                        r.Marks.Add(new CellMark(stIdx, Theme.TextDim, L.S("other format", "другой формат")));
                    else if (st.Installed != null && st.Installed.FileMissing)
                        r.Marks.Add(new CellMark(stIdx, Theme.Red, L.S("file gone", "файла нет")));
                    else if (st.Sets == 0)
                        r.Marks.Add(new CellMark(stIdx, Theme.TextDim, L.S("never used", "не используется")));
                    else
                        r.Marks.Add(new CellMark(stIdx, Theme.Green, L.S("✔️", "установлен")));
                }
                rows.Add(r);
            }
            _list.SetRows(rows, animate);
            OnSelectionChanged();
        }

        int PluginVisibleIndex(string id)
        {
            for (int i = 0; i < _pluginVisible.Count; i++) if (_pluginVisible[i].Id == id) return i;
            return -1;
        }

        /// <summary>Отбор по выбранной карточке сводки.</summary>
        bool PassesView(PluginStat st)
        {
            switch (_pluginView)
            {
                case PluginSummary.Total: return st.Sets > 0;
                case PluginSummary.Missing: return st.Sets > 0 && st.Match == MatchKind.Missing;
                case PluginSummary.Installed: return st.IsInstalled;
                case PluginSummary.Unused: return st.Sets == 0;
                default: return true;
            }
        }

        void OnSummaryCard(int card)
        {
            _pluginView = _pluginView == card ? -1 : card;
            _summary.Selected = _pluginView;
            _summary.Invalidate();
            Refill();
        }

        /// <summary>
        /// Версии сравниваются по числам, а не как строки: посимвольно «12.4.3» оказывается
        /// меньше «9.7.2», потому что '1' идёт раньше '9'.
        /// </summary>
        static int CompareVersion(string a, string b)
        {
            string[] pa = (a ?? "").Split('.'), pb = (b ?? "").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length ? Num(pa[i]) : 0;
                int vb = i < pb.Length ? Num(pb[i]) : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ведущие цифры фрагмента версии: «6b3» -> 6, пустое -> 0.</summary>
        static int Num(string s)
        {
            int v = 0, i = 0;
            while (i < s.Length && char.IsDigit(s[i])) { v = v * 10 + (s[i] - '0'); i++; }
            return v;
        }

        /// <summary>
        /// Сортировка по id колонки, а не по её номеру, в обеих таблицах: набор и
        /// порядок колонок подвижны, и индекс сам по себе между перерисовками ничего
        /// не значит.
        /// </summary>
        void OnHeaderClicked(int column)
        {
            if (_mode.SelectedIndex == ModeSets)
            {
                if (column < 0 || column >= _setVisible.Count) return;
                string id = _setVisible[column].Id;
                if (_setSortId == id) _sortDesc = !_sortDesc;
                else { _setSortId = id; _sortDesc = false; }
                Refill();     // FillSets проставит _list.SortColumn/Descending
                return;
            }

            if (column < 0 || column >= _pluginVisible.Count) return;
            string pid = _pluginVisible[column].Id;
            if (_pluginSortId == pid) _sortDesc = !_sortDesc;
            else { _pluginSortId = pid; _sortDesc = false; }
            Refill();         // FillPlugins проставит _list.SortColumn/Descending
        }

        // ------------------------------------------------------- меню колонок

        /// <summary>
        /// Меню колонок по правой кнопке — одно и то же для обеих таблиц: набор колонок,
        /// сброс по умолчанию, а у сетов ещё и группировка по папкам. Раньше меню было
        /// только у сетов, и таблица плагинов оставалась намертво зашитой.
        /// </summary>
        void OnHeaderRightClick(Point pt)
        {
            bool sets = _mode.SelectedIndex == ModeSets;

            ContextMenuStrip menu = DarkMenu.Create();
            menu.ShowCheckMargin = true;   // видно, какие колонки уже включены — как в проводнике

            if (sets)
            {
                // Группировка живёт здесь же: это про то, что показывает список, ровно как
                // и набор колонок, и другого места для неё в интерфейсе нет.
                ToolStripMenuItem group = new ToolStripMenuItem(
                    L.S("One row per folder", "Одна строка на папку"));
                group.Checked = _settings.GroupByFolder;
                group.Click += delegate
                {
                    _settings.GroupByFolder = !_settings.GroupByFolder;
                    group.Checked = _settings.GroupByFolder;
                    _home.GroupByFolder = _settings.GroupByFolder;
                    _settings.Save();
                    Refill();
                };
                menu.Items.Add(group);
                menu.Items.Add(new ToolStripSeparator());
            }

            // Клик по пункту — это переключатель, не команда «сделал и уйди»: не закрываем
            // меню, чтобы можно было проставить сразу несколько галочек подряд. Закрывается
            // как обычно — кликом мимо или Esc (это уже не ItemClicked, а другая причина).
            menu.Closing += delegate (object s, ToolStripDropDownClosingEventArgs e)
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };

            List<string> order = sets ? _setOrder : _pluginOrder;
            string mandatory = sets ? "Set" : "Plugin";

            // Пункты — в каноническом порядке каталога, а не в пользовательском: меню
            // это перечень того, что вообще бывает, и переставлять его вслед за таблицей
            // значило бы каждый раз искать нужную строчку на новом месте.
            List<string> ids = new List<string>();
            List<string> titles = new List<string>();
            if (sets)
                foreach (ColDef d in Catalog) { ids.Add(d.Id); titles.Add(L.S(d.En, d.Ru)); }
            else
                foreach (PluginColDef d in PluginCatalog) { ids.Add(d.Id); titles.Add(L.S(d.En, d.Ru)); }

            List<ToolStripMenuItem> boxes = new List<ToolStripMenuItem>();
            for (int i = 0; i < ids.Count; i++)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(titles[i]);
                mi.Checked = HasCol(order, ids[i]);
                if (ids[i] == mandatory) mi.Enabled = false;   // главную колонку убрать нельзя
                else
                {
                    string id = ids[i];
                    List<string> captured = order;
                    ToolStripMenuItem box = mi;
                    mi.Click += delegate { ToggleColumn(id); box.Checked = HasCol(captured, id); };
                }
                boxes.Add(mi);
                menu.Items.Add(mi);
            }

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem reset = new ToolStripMenuItem(L.S("Reset to defaults", "Сбросить по умолчанию"));
            List<string> resetIds = ids;
            List<ToolStripMenuItem> resetBoxes = boxes;
            List<string> resetOrder = order;
            reset.Click += delegate
            {
                ResetColumns();
                for (int i = 0; i < resetBoxes.Count; i++)
                    resetBoxes[i].Checked = HasCol(resetOrder, resetIds[i]);
            };
            menu.Items.Add(reset);

            menu.Show(_list, pt);
        }

        void ToggleColumn(string id)
        {
            bool sets = _mode.SelectedIndex == ModeSets;
            List<string> order = sets ? _setOrder : _pluginOrder;
            if (id == (sets ? "Set" : "Plugin")) return;

            if (HasCol(order, id))
            {
                for (int i = 0; i < order.Count; i++)
                    if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase))
                    { order.RemoveAt(i); break; }
            }
            // Включённая колонка встаёт в конец, а не на своё «каноническое» место:
            // порядок теперь пользовательский, и вклиниваться в него самим неправильно.
            else order.Add(id);

            // Сортировка могла стоять по спрятанной колонке — вернёмся к порядку по умолчанию.
            if (sets) { if (_setSortId != null && !HasCol(order, _setSortId)) _setSortId = null; }
            else { if (_pluginSortId != null && !HasCol(order, _pluginSortId)) _pluginSortId = null; }

            SaveColumns();
            Refill();
        }

        void ResetColumns()
        {
            bool sets = _mode.SelectedIndex == ModeSets;
            List<string> order = sets ? _setOrder : _pluginOrder;
            Dictionary<string, int> widths = sets ? _setColW : _pluginColW;

            order.Clear();
            widths.Clear();
            order.AddRange(sets ? DefaultSetCols : DefaultPluginCols);
            if (sets) _setSortId = null; else _pluginSortId = null;

            SaveColumns();
            Refill();
        }

        void OnColumnsResized()
        {
            Dictionary<string, int> widths = _mode.SelectedIndex == ModeSets ? _setColW : _pluginColW;
            foreach (Column c in _list.ColumnList)
                if (c.Width > 0 && !string.IsNullOrEmpty(c.Id)) widths[c.Id] = c.Width;
            SaveColumns();
        }

        /// <summary>
        /// Заголовок перетащили на новое место. Переставляем id в пользовательском
        /// порядке — он и есть то, что рисует таблица и что уезжает в настройки.
        /// </summary>
        void OnColumnsReordered(int from, int to)
        {
            List<string> order = _mode.SelectedIndex == ModeSets ? _setOrder : _pluginOrder;
            if (from < 0 || from >= order.Count || to < 0 || to > order.Count || from == to) return;

            string moved = order[from];
            order.RemoveAt(from);
            if (to > from) to--;                 // после изъятия всё, что правее, съехало
            if (to > order.Count) to = order.Count;
            order.Insert(to, moved);
            SaveColumns();

            // Строки не изменились — переехала только колонка, поэтому выделение и
            // прокрутку возвращаем прямо по номеру строки, без поиска. Без этого
            // перестановка колонки стоила бы выбранного проекта и места в списке.
            int sel = _list.SelectedIndex;
            int scroll = _list.ScrollOffset;
            Refill(false);                        // строки те же — влетать снизу им незачем
            _list.SelectIndex(sel);
            _list.ScrollOffset = scroll;
        }

        static bool HasPlugin(SetEntry s, string name)
        {
            foreach (string p in s.Plugins)
                if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool MatchesSet(SetEntry s, string q)
        {
            if (s.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.ProjectName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.Path.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            foreach (string p in s.Plugins)
                if (p.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // Рендер — то же самое «имя проекта» для человека, который ищет по звуку,
            // а не по названию сета. Выборка та же, что у предпрослушки (без Samples),
            // см. RenderNames.
            foreach (string r in s.RenderNames)
                if (r.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // Свои теги и заметки — тоже поиск: иначе метка, которую человек поставил
            // руками, была бы видна только глазами в панели справа.
            foreach (string tag in ProjectMeta.TagsOf(s.ProjectDir))
                if (tag.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (ProjectMeta.NoteOf(s.ProjectDir).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                return true;

            return false;
        }

        void OnSelectionChanged()
        {
            if (_mode.SelectedIndex == ModePlugins)
            {
                RowData sel = _list.Selected;
                _detail.ShowPlugin(sel == null ? null : sel.Tag as PluginStat);
                return;
            }

            SetEntry s = SelectedSet();
            _detail.Show(s);

            if (_viewToggle.SelectedIndex == ViewTiles)
            {
                RowData cur = _list.Selected;
                if (s != null && (cur == null || !ReferenceEquals(cur.Tag, s)))
                    _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, s); });
            }
            else
            {
                if (s != null && !ReferenceEquals(_home.Selected, s))
                    _home.Selected = s;
            }
        }

        PluginStat SelectedPlugin()
        {
            RowData sel = _list.Selected;
            return sel == null ? null : sel.Tag as PluginStat;
        }

        void ActivateSelected()
        {
            if (_mode.SelectedIndex == ModePlugins)
            {
                PluginStat st = SelectedPlugin();
                if (st == null || st.Sets == 0) return;
                _pluginFilter = st.Name;              // двойной клик по плагину -> его сеты
                _mode.SelectedIndex = ModeSets;
                _search.Box.Text = "";
                Refill();
                Status("Filtered by plugin: " + _pluginFilter,
                       "Отобраны сеты с плагином: " + _pluginFilter);
                return;
            }
            OpenSelected();
        }

        /// <summary>Клик по сету в списке «Sets» у плагина или «Show Details» на главной —
        /// переход к нему на вкладке Sets в табличный вид с открытием панели деталей.</summary>
        void OnSetRequested(SetEntry set)
        {
            if (set == null) return;
            _pluginFilter = "";
            _search.Box.Text = "";
            _mode.SelectedIndex = ModeSets;
            if (_viewToggle.SelectedIndex != ViewList)
            {
                _viewToggle.SelectedIndex = ViewList;
                Refill();
            }
            _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, set); });

            // Версия, спрятанная под схлопнутой строкой, своей строки в списке не имеет —
            // SelectRow её не найдёт. Показываем её прямо в панели: клик по версии
            // должен показать версию, а не молча ничего не сделать.
            RowData sel = _list.Selected;
            if (sel == null || !ReferenceEquals(sel.Tag, set)) _detail.Show(set);
        }

        /// <summary>Клик по плагину в списке «Plugins» у сета — обратный переход,
        /// к самому плагину на вкладке Plugins. Ряды там пересобираются каждый Refill,
        /// поэтому ищем по имени, а не по ссылке на объект.</summary>
        void OnPluginRequested(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName)) return;
            _search.Box.Text = "";
            _mode.SelectedIndex = ModePlugins;
            _list.SelectRow(delegate (RowData r)
            {
                PluginStat st = r.Tag as PluginStat;
                return st != null && string.Equals(st.Name, pluginName, StringComparison.OrdinalIgnoreCase);
            });
        }

        // ------------------------------------------------------------ действия

        SetEntry SelectedSet()
        {
            if (_mode.SelectedIndex == ModeSets && _viewToggle.SelectedIndex == ViewTiles)
                return _home.Selected;
            RowData sel = _list.Selected;
            return sel == null ? null : sel.Tag as SetEntry;
        }

        void OpenSelected()
        {
            OpenSet(SelectedSet());
        }

        /// <summary>Открыть конкретный сет в Live — общее для списка и плиток главной.</summary>
        void OpenSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path, "Файла больше нет: " + s.Path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true });
                // Live поднимается не сразу — то же самое «кнопка как будто не сработала»,
                // что и у запуска пустой Live, только тут ещё и не сразу ясно, ТОТ ли
                // именно сет открывается: имя сохраняет и в заголовке двойных строк.
                Notify("Opening “" + s.Name + ".als”", "Открываю «" + s.Name + ".als»");
            }
            catch (Exception ex) { Status("Could not open: " + ex.Message, "Не удалось открыть: " + ex.Message); }
        }

        /// <summary>Показать сет в проводнике — общее для списка и плиток главной.</summary>
        void RevealSet(SetEntry s)
        {
            if (s == null) return;
            try
            {
                if (File.Exists(s.Path)) Process.Start("explorer.exe", "/select,\"" + s.Path + "\"");
                else if (Directory.Exists(s.Directory)) Process.Start("explorer.exe", "\"" + s.Directory + "\"");
            }
            catch { }
        }

        void RevealSelected()
        {
            if (_mode.SelectedIndex == ModePlugins)
            {
                PluginStat st = SelectedPlugin();
                if (st == null || st.Installed == null || st.Installed.Path.Length == 0) return;
                try
                {
                    if (File.Exists(st.Installed.Path) || Directory.Exists(st.Installed.Path))
                        Process.Start("explorer.exe", "/select,\"" + st.Installed.Path + "\"");
                    else Status("The plugin file is gone: " + st.Installed.Path,
                                "Файла плагина больше нет: " + st.Installed.Path);
                }
                catch { }
                return;
            }

            RevealSet(SelectedSet());
        }

        void EditFilters()
        {
            using (FiltersDialog d = new FiltersDialog(_filter, _index.Sets, _versions))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _filter.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill();
            }
        }

        void EditPluginFilters()
        {
            List<PluginStat> all = _index.PluginUsage();
            using (PluginFiltersDialog d = new PluginFiltersDialog(_pluginFilterObj, all))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _pluginFilterObj.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill();
            }
        }

        // --------------------------------------------------- клавиатура: навигация

        /// <summary>Стрелка — на соседний сет. Список и плитки ходят по-разному.</summary>
        bool MoveSelection(Keys k)
        {
            if (_mode.SelectedIndex == ModeSets && _viewToggle.SelectedIndex == ViewTiles)
            {
                switch (k)
                {
                    case Keys.Left: return _home.MoveSelection(-1, 0);
                    case Keys.Right: return _home.MoveSelection(+1, 0);
                    case Keys.Up: case Keys.PageUp: return _home.MoveSelection(0, -1);
                    case Keys.Down: case Keys.PageDown: return _home.MoveSelection(0, +1);
                }
                return false;
            }

            switch (k)
            {
                case Keys.Up: return _list.MoveSelection(-1);
                case Keys.Down: return _list.MoveSelection(+1);
                case Keys.PageUp: return _list.MoveSelection(-_list.PageStep);
                case Keys.PageDown: return _list.MoveSelection(+_list.PageStep);
            }
            return false;   // влево-вправо в списке водить некуда
        }

        /// <summary>Клавиша вызова меню — то же меню, что по правой кнопке мыши.</summary>
        void ShowMenuForSelection()
        {
            if (_mode.SelectedIndex != ModeSets) return;
            if (_viewToggle.SelectedIndex == ViewTiles) { _home.ShowMenuForSelected(); return; }

            int idx = _list.SelectedIndex;
            if (idx >= 0) OnListRowRightClick(idx, _list.RowMenuPoint(idx));
        }

        // -------------------------------------------------- клавиатура: прослушка

        /// <summary>
        /// Пробел — предпрослушка рендера выбранного сета. Если выбран тот же сет, что
        /// уже в плеере, это пауза/продолжить (за это отвечает PlayOrToggle); если
        /// другой — плеер переезжает на него.
        /// </summary>
        void TogglePlaySelected()
        {
            SetEntry s = SelectedSet();
            bool player = _player != null && !_player.IsDisposed && _player.CurrentSet != null;

            if (s == null || !s.HasRenders)
            {
                // Слушать у выбранного нечего — но если что-то уже играет, пробел
                // логичнее понять как паузу, чем не сделать ничего.
                if (player) { _player.PlayPause(); UpdatePlayerTransport(); }
                else if (s != null)
                    Status("No renders next to this set", "Рядом с этим сетом нет рендеров");
                return;
            }

            if (_viewToggle.SelectedIndex == ViewTiles) { OpenPlayer(s); return; }
            int idx = _list.SelectedIndex;
            if (idx >= 0) OnRowPlay(idx);
        }

        /// <summary>
        /// Медиаклавиши. «Следующий трек» здесь — следующий СЕТ в том плейлисте, что
        /// сейчас на экране, а не следующий рендер внутри одного сета: между рендерами
        /// одного проекта ходят кнопки в самом плеере.
        ///
        /// false — команду не берём, и Windows отдаст её дальше, обычному плееру: когда
        /// у нас ничего не играет, отбирать у человека паузу в Spotify незачем.
        /// </summary>
        bool HandleMedia(MediaKeys.Cmd cmd)
        {
            bool player = _player != null && !_player.IsDisposed && _player.CurrentSet != null;

            switch (cmd)
            {
                case MediaKeys.Cmd.Next:
                    if (!player) return false;
                    _player.NextSet();
                    break;

                case MediaKeys.Cmd.Prev:
                    if (!player) return false;
                    _player.PrevSet();
                    break;

                case MediaKeys.Cmd.Stop:
                case MediaKeys.Cmd.Pause:
                    if (!player || !_player.IsPlaying) return false;
                    _player.PlayPause();
                    break;

                case MediaKeys.Cmd.Play:
                case MediaKeys.Cmd.PlayPause:
                    if (player) _player.PlayPause();
                    else if (_mode.SelectedIndex == ModeSets) TogglePlaySelected();
                    else return false;
                    break;

                default: return false;
            }

            UpdatePlayerTransport();
            return true;
        }

        /// <summary>
        /// Теги и заметка проекта. Привязаны к папке, поэтому правка видна сразу всем
        /// версиям сета из неё — и список надо пересобрать целиком, а не только строку.
        /// </summary>
        void EditNotes(SetEntry s)
        {
            if (s == null) return;
            using (NotesDialog d = new NotesDialog(s))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Refill(false);

                // Refill пересобирает список с нуля (SetRows всегда сбрасывает выделение —
                // так и панель справа, и подсветка строки гаснут посреди правки её же
                // тегов). Объект сета не меняется, поэтому просто выделяем его снова.
                if (_viewToggle.SelectedIndex == ViewTiles) _home.Selected = s;
                else _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, s); });
            }
        }

        void OpenPreview()
        {
            SetEntry s = SelectedSet();
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path, "Файла больше нет: " + s.Path);
                return;
            }
            using (PreviewDialog d = new PreviewDialog(s, _arrangements))
                d.ShowDialog(this);
        }

        /// <summary>
        /// Прослушивание рендера без запуска Live. Плеер один на всё приложение: второй
        /// сет просто заезжает в то же окно, иначе после десятка нажатий экран будет
        /// завален плеерами, играющими друг поверх друга.
        /// </summary>
        void OnRowPlay(int idx)
        {
            if (_mode.SelectedIndex != ModeSets) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            // Плейлист — то, что сейчас видно в списке: следующий трек логично берётся
            // из того же отбора, который человек перед собой видит.
            List<SetEntry> playlist = new List<SetEntry>();
            foreach (RowData row in _list.Rows)
            {
                SetEntry candidate = row.Tag as SetEntry;
                if (candidate != null && candidate.HasRenders) playlist.Add(candidate);
            }
            PlayOrToggle(s, playlist);
        }

        /// <summary>
        /// Плитка главной: плейлистом берём то, что показано на главной, и в том же
        /// порядке, в каком оно там лежит — то есть сперва закреплённые в порядке
        /// закрепления, потом недавние. Раньше сюда уходил null, плейлист вырождался
        /// в один сет, и кнопки «предыдущий/следующий» в футере на главной не делали
        /// вообще ничего.
        /// </summary>
        void OpenPlayer(SetEntry s)
        {
            List<SetEntry> playlist = new List<SetEntry>();
            foreach (SetEntry t in _home.VisibleSets())
                if (t.HasRenders) playlist.Add(t);
            PlayOrToggle(s, playlist);
        }

        /// <summary>ПКМ по строке в списке сетов — то же меню, что и у плитки на главной,
        /// чтобы закреплять проекты можно было не выходя из основного каталога.</summary>
        void OnListRowRightClick(int idx, Point at)
        {
            if (_mode.SelectedIndex != ModeSets) return;
            if (idx < 0 || idx >= _list.Rows.Count) return;
            SetEntry s = _list.Rows[idx].Tag as SetEntry;
            if (s == null) return;

            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem open = new ToolStripMenuItem(L.S("Open in Live", "Открыть в Live"));
            open.Click += delegate { OpenSet(s); };
            m.Items.Add(open);

            if (s.HasRenders)
            {
                ToolStripMenuItem play = new ToolStripMenuItem(L.S("Play render", "Слушать рендер"));
                play.Click += delegate { OnRowPlay(idx); };
                m.Items.Add(play);
            }

            ToolStripMenuItem pin = new ToolStripMenuItem(
                HomeStore.IsPinned(s.Path) ? L.S("Unpin", "Открепить") : L.S("Pin project", "Закрепить"));
            pin.Checked = HomeStore.IsPinned(s.Path);
            pin.Click += delegate { TogglePinAndRefresh(s); };
            m.Items.Add(pin);

            ToolStripMenuItem notes = new ToolStripMenuItem(
                ProjectMeta.HasAnything(s.ProjectDir)
                    ? L.S("Tags and notes…", "Теги и заметки…")
                    : L.S("Add tags or a note…", "Добавить теги или заметку…"));
            notes.Click += delegate { EditNotes(s); };
            m.Items.Add(notes);

            ToolStripMenuItem reveal = new ToolStripMenuItem(L.S("Show in Explorer", "Показать в папке"));
            reveal.Click += delegate { RevealSet(s); };
            m.Items.Add(reveal);

            m.Show(_list, at);
        }

        /// <summary>Закрепление сета — общее для звёздочки в строке и пункта меню:
        /// сразу подкрашивает конкретную строку, не перестраивая весь список.</summary>
        void TogglePinAndRefresh(SetEntry s)
        {
            if (s == null) return;
            HomeStore.TogglePin(s.Path);
            foreach (RowData r in _list.Rows)
            {
                SetEntry rs = r.Tag as SetEntry;
                if (rs != null && rs.Path == s.Path) { r.Pinned = HomeStore.IsPinned(s.Path); break; }
            }
            _list.Invalidate();
        }

        void ShowPlayer(SetEntry s, List<SetEntry> playlist)
        {
            if (s == null) return;

            if (_player == null || _player.IsDisposed)
            {
                _player = new PlayerDialog();
                // Владельца задаём сами — Show(owner) тут не будет, а окно всё равно
                // должно центрироваться и позиционироваться относительно главного.
                _player.Owner = this;
                // Хендл форсируем без показа окна: предпрослушка — это звук и мини-
                // транспорт в футере, а не всплывающее окно. Без хендла BeginInvoke внутри
                // плеера (волна, автопереход) работать не станет. CreateControl() тут не
                // годится — при Visible=false он тихо ничего не делает; а сам по себе
                // Handle не считает раскладку дочерних контролов, поэтому следом лёгкий
                // пинок ресайзом — тот же приём, которым в этой сессии проверяли форму
                // офлайн, только без экрана он ничего не показывает.
                IntPtr forceHandle = _player.Handle;
                Size s0 = _player.Size;
                _player.Size = new Size(s0.Width + 1, s0.Height);
                _player.Size = s0;
                _player.SetChanged += delegate (SetEntry changed) {
                    _list.PlayingTag = _home.PlayingTag = changed;
                    UpdatePlayerTransport();   // трек сменился — Playing сверяем заново
                    _list.Invalidate();
                    _home.Invalidate();
                };
                _player.PlayStateChanged += UpdatePlayerTransport;
                _player.FormClosed += delegate
                {
                    _playerTimer.Stop();
                    _playerTimeLeftStr = _playerTimeRightStr = "";
                    _playerSeek.Progress = 0f;
                    _player = null;
                    _list.PlayingTag = _home.PlayingTag = null;
                    _list.Playing = _home.Playing = false;
                    _list.Invalidate();
                    _home.Invalidate();
                    LayoutAll();       // прячет мини-транспорт — управлять больше нечем
                    Invalidate(true);
                };
                _playerTimer.Start();
                LayoutAll();           // показывает мини-транспорт теперь, когда плеер есть
            }

            if (playlist == null) playlist = new List<SetEntry>();
            if (playlist.Count == 0) playlist.Add(s);

            int playlistIndex = -1;
            for (int i = 0; i < playlist.Count; i++)
                if (ReferenceEquals(playlist[i], s)) { playlistIndex = i; break; }
            if (playlistIndex < 0) playlistIndex = 0;

            _player.LoadSet(s, playlist, playlistIndex);
            _list.PlayingTag = _home.PlayingTag = s;
            _list.Invalidate();
            _home.Invalidate();
            UpdatePlayerTransport();

            // Окно само не поднимаем: если пользователь его уже разворачивал — пусть
            // остаётся видимым и просто обновится, а если нет — так и играет молча,
            // пока не нажмут «развернуть».
            if (_player.Visible)
            {
                if (_player.WindowState == FormWindowState.Minimized)
                    _player.WindowState = FormWindowState.Normal;
                _player.Activate();
            }
        }

        /// <summary>Кнопка «развернуть» в футере — единственный способ показать окно плеера.</summary>
        void ExpandPlayer()
        {
            if (_player == null || _player.IsDisposed) return;
            if (!_player.Visible) _player.Show(this);
            if (_player.WindowState == FormWindowState.Minimized) _player.WindowState = FormWindowState.Normal;
            _player.Activate();
        }

        /// <summary>
        /// Значок play/pause в футере — вслед за тем, что реально играет в плеере. Заодно
        /// единственное место, где список и плитки узнают, что это не просто «текущий
        /// трек», а именно ЗВУЧИТ ли он сейчас — иначе после паузы кнопка в строке или
        /// на плитке продолжала бы показывать паузу, хотя играть уже нечему.
        /// </summary>
        static string TimeStr(int ms)
        {
            if (ms < 0) ms = 0;
            int total = ms / 1000;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        void UpdatePlayerTransport()
        {
            bool playing = _player != null && !_player.IsDisposed && _player.IsPlaying;
            Glyph want = playing ? Glyph.Pause : Glyph.Play;
            if (_playerPlayPause.Icon != want) { _playerPlayPause.Icon = want; _playerPlayPause.Invalidate(); }

            if (_list.Playing != playing) { _list.Playing = playing; _list.Invalidate(); }
            if (_home.Playing != playing) { _home.Playing = playing; _home.Invalidate(); }
        }

        /// <summary>
        /// Клик по play/pause в строке или на плитке: если это уже тот трек, что сейчас
        /// в плеере, — просто переключаем паузу, не перезапуская его с начала. Если
        /// другой — грузим и играем заново, как раньше.
        /// </summary>
        void PlayOrToggle(SetEntry s, List<SetEntry> playlist)
        {
            if (_player != null && !_player.IsDisposed && ReferenceEquals(_player.CurrentSet, s))
            {
                _player.PlayPause();
                return;
            }
            ShowPlayer(s, playlist);
        }

        /// <summary>
        /// «New Live Set» просто открывает саму Live — как двойной щелчок по её иконке,
        /// без выбора папки и без подсовывания шаблона сета. Дальше пользователь решает
        /// сам, средствами самой Live: новый проект, недавние или мастер-шаблон.
        /// </summary>
        void NewProject()
        {
            string exe = LiveEnvironment.FindExecutable();
            if (exe.Length == 0)
            {
                Status("Could not find Ableton Live — is it installed?",
                       "Не нашёл Ableton Live — она установлена?");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                // Live поднимается долгие секунды и до первого своего окна не подаёт
                // никаких признаков жизни — без этой строчки нажатие выглядит как
                // «кнопка не сработала», и её жмут ещё раз.
                Notify("Starting Live…", "Запускаю Live…");
            }
            catch (Exception ex) { Status(ex.Message, ex.Message); }
        }

        bool EditRoots()
        {
            using (RootsDialog d = new RootsDialog(_settings.Roots, _settings.IncludeBackups, _settings.DisabledRoots))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                _settings.Roots.Clear();
                _settings.Roots.AddRange(d.Result);
                _settings.DisabledRoots.Clear();
                _settings.DisabledRoots.AddRange(d.DisabledRoots);
                _settings.IncludeBackups = d.IncludeBackups;
                _settings.Save();
                StartScan(true);
                Rewatch();          // набор корней другой — переставляем наблюдение
                return true;
            }
        }

        // -------------------------------------------------------- автообновление

        /// <summary>
        /// Пересканирование, которое началось само. Отличается от F5 двумя вещами:
        /// молчит (никакой полосы прогресса поверх каталога) и бережёт то, на что
        /// человек сейчас смотрит, — см. RefillPreservingView.
        /// </summary>
        FolderWatch _watch;
        bool _rescanPending;

        void Rewatch()
        {
            if (_watch == null)
            {
                _watch = new FolderWatch(this);
                _watch.Changed += OnFoldersChanged;
            }
            _watch.Watch(_settings.Roots, _settings.DisabledRoots);
        }

        void OnFoldersChanged()
        {
            // Пока идёт сканирование, второе поверх него не запускаем — но и не теряем:
            // изменения могли прийти как раз в те папки, которые уже прошли, и без
            // отметки они дождались бы только следующего F5.
            if (_scanning) { _rescanPending = true; return; }
            StartScan(false);
        }

        // ----------------------------------------------------------- сканирование

        void StartScan(bool force)
        {
            if (_scanning) return;
            if (_settings.Roots.Count == 0)
            {
                Status("No folders selected yet — press “Folders…”.",
                       "Папки ещё не выбраны — нажми «Папки…».");
                return;
            }

            Diag.Line("scan: start, roots = " + string.Join(" | ", _settings.Roots.ToArray()));
            _scanning = true;
            _manualScan = force;
            _scanDone = 0; _scanTotal = 0;
            _cancel = new CancellationTokenSource();
            Status("Scanning…", "Сканирую…");
            Invalidate();

            CancellationToken token = _cancel.Token;
            _scanThread = new Thread(delegate ()
            {
                try
                {
                    _index.Scan(_settings, delegate (int done, int total, string current)
                    {
                        _scanDone = done; _scanTotal = total;
                        int now = Environment.TickCount;
                        if (now - _lastScanInvalidate > 100)
                        {
                            _lastScanInvalidate = now;
                            try { BeginInvoke((MethodInvoker)delegate { Invalidate(); }); }
                            catch { }
                        }
                    }, token);
                    Diag.Line("scan: done, " + _index.Sets.Count + " sets");
                }
                catch (Exception ex) { Diag.Fail("scan", ex); }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        bool wasManual = _manualScan;
                        _scanning = false;
                        RefreshVersions();
                        if (wasManual) Refill(); else RefillPreservingView();
                        // Итоговую сводку («Indexed N sets · M with missing files») больше
                        // не пишем: сколько сетов показано, и так стоит наверху, а потери
                        // видны цветными отметками в самих строках. Строка внизу остаётся
                        // только под то, о чём иначе никак не узнать, — ошибки действий.
                        Status("", "");

                        // Пока сканировали, на диске успело измениться ещё что-то —
                        // проходим ещё раз, иначе те правки ждали бы следующего повода.
                        if (_rescanPending) { _rescanPending = false; StartScan(false); }
                    });
                }
                catch { }
            });
            _scanThread.IsBackground = true;
            _scanThread.Start();
        }

        void RefreshVersions()
        {
            _versions.Clear();
            foreach (SetEntry s in _index.Sets)
            {
                string v = s.ShortVersion;
                if (v.Length > 0 && !_versions.Contains(v)) _versions.Add(v);
            }
            _versions.Sort(delegate (string a, string b) { return CompareVersion(b, a); });   // новые сверху
        }

        void Status(string en, string ru) { _statusEn = en; _statusRu = ru; Invalidate(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_minimizeHotkey)
            {
                try { UnregisterHotKey(Handle, HotkeyMinimize); } catch { }
                _minimizeHotkey = false;
            }
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            if (_watch != null) { try { _watch.Dispose(); } catch { } _watch = null; }
            if (_player != null && !_player.IsDisposed) { try { _player.Close(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
