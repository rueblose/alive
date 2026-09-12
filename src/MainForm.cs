using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        // Действия — круглые значки: папка, настройки, новый проект.
        readonly IconButton _folders = new IconButton();
        readonly IconButton _settingsBtn = new IconButton();
        readonly GlassButton _newProject = new GlassButton();

        // Мини-транспорт плеера в футере — переключить сет и play/pause, не поднимая
        // окно плеера. Раскладка слева направо: закрыть, prev, play, next, seek+время+трек, громкость, развернуть, имя сета.
        readonly IconButton _playerClose = new IconButton();
        readonly IconButton _playerPrev = new IconButton();
        readonly IconButton _playerPlayPause = new IconButton();
        readonly IconButton _playerNext = new IconButton();
        readonly SeekSlider _playerSeek = new SeekSlider();
        readonly IconButton _playerVolBtn = new IconButton();
        readonly VolumePopupControl _playerVolPopup = new VolumePopupControl();
        readonly System.Windows.Forms.Timer _volPopupTimer = new System.Windows.Forms.Timer();
        float _preMuteVolume = 0.5f;
        readonly IconButton _playerExpand = new IconButton();
        readonly PlayerSetLink _playerSetLink = new PlayerSetLink();
        readonly System.Windows.Forms.Timer _playerTimer = new System.Windows.Forms.Timer();
        string _playerTimeLeftStr = "", _playerTimeRightStr = "";
        Rectangle _rPlayerTimeLeft, _rPlayerTimeRight, _rPlayerTrack;

        // Кнопки самого окна.
        readonly IconButton _min = new IconButton();
        readonly IconButton _max = new IconButton();
        readonly IconButton _close = new IconButton();

        readonly RowListView _list = new RowListView();
        readonly DetailPanel _detail = new DetailPanel();
        readonly PluginSummary _summary = new PluginSummary();
        readonly HomeView _home = new HomeView();

        readonly IconToggle _viewToggle = new IconToggle();
        readonly IconButton _dice = new IconButton();
        readonly Random _rng = new Random();

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

        string _status = "";

        // Путь последнего выбранного сета — уход на вкладку Plugins (например, клик по
        // плагину в панели сведений) пересобирает список сетов и снимает выделение;
        // при возврате на Sets по этому пути находим тот же сет и выделяем заново.
        string _lastSetPath;

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

        Rectangle _rCount, _rStatus;

        readonly HelpOverlay _help = new HelpOverlay();

        // Откуда взяты данные о плагинах — показываем в статус-баре справа, только на
        // вкладке Плагинов, чтобы числам в карточках можно было верить.

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
            AllowDrop = true;          // папку можно бросить прямо на окно — см. OnDragDrop
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _settings = Settings.Load();
            Settings.RootsChanged += OnGlobalRootsChanged;
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
            public string En;
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
            { "Set", "Modified", "BPM", "Key", "PluginCount", "FileCount"};

        static List<ColDef> BuildCatalog()
        {
            List<ColDef> c = new List<ColDef>();

            c.Add(new ColDef {
                Id = "Set", En = "Set", Width = 0, Font = Theme.FTitle, Color = Theme.Text,
                // «+3» — столько версий той же папки спрятано под этой строкой.
                Text = delegate (SetEntry s)
                    { return s.CollapsedCount > 0 ? s.Name + "   +" + s.CollapsedCount : s.Name; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Place", En = "Place", Width = 150,
                Text = delegate (SetEntry s) { return s.Place; },
                Sort = delegate (SetEntry a, SetEntry b)
                    { return string.Compare(a.Place, b.Place, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new ColDef {
                Id = "Modified", En = "Modified", Width = 150,
                Text = delegate (SetEntry s) { return s.Modified.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Modified.CompareTo(b.Modified); } });

            c.Add(new ColDef {
                Id = "Created", En = "Created", Width = 150,
                Text = delegate (SetEntry s)
                    { return s.Created == default(DateTime) ? "" : s.Created.ToLocalTime().ToString("yyyy-MM-dd"); },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Created.CompareTo(b.Created); } });

            c.Add(new ColDef {
                Id = "Live", En = "Live", Width = 87,
                Text = delegate (SetEntry s) { return s.ShortVersion; },
                Sort = delegate (SetEntry a, SetEntry b) { return CompareVersion(a.ShortVersion, b.ShortVersion); } });

            c.Add(new ColDef {
                Id = "BPM", En = "BPM", Width = 81, Right = true,
                Text = delegate (SetEntry s)
                    { return s.Tempo > 0 ? s.Tempo.ToString("0.##", CultureInfo.InvariantCulture) : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tempo.CompareTo(b.Tempo); } });

            c.Add(new ColDef {
                Id = "Key", En = "Key", Width = 143,
                Text = delegate (SetEntry s) { return s.Key; },
                Sort = delegate (SetEntry a, SetEntry b)
                    {
                        // Сеты без тональности — всегда в конце, а не вперемешку.
                        bool ea = a.Key.Length == 0, eb = b.Key.Length == 0;
                        if (ea != eb) return ea ? 1 : -1;
                        return string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
                    } });

            c.Add(new ColDef {
                Id = "Tracks", En = "Tracks", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.Tracks > 0 ? s.Tracks.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Tracks.CompareTo(b.Tracks); } });

            // Просто «сколько их в проекте» — без оценок и цвета. Обе по умолчанию
            // спрятаны и стоят перед своими «Missed»-соседками: сперва сколько всего,
            // потом сколько из них потеряно.
            c.Add(new ColDef {
                Id = "PluginCount", En = "Plugins", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.Plugins.Length > 0 ? s.Plugins.Length.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.Plugins.Length.CompareTo(b.Plugins.Length); } });

            c.Add(new ColDef {
                Id = "FileCount", En = "Files", Width = 100, Right = true,
                Text = delegate (SetEntry s) { return s.TotalRefs > 0 ? s.TotalRefs.ToString() : ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.TotalRefs.CompareTo(b.TotalRefs); } });

            // Plugins и Files — только цветная отметка, текст ячейки пуст.
            // Сортировка — именно по ПОТЕРЯННЫМ: колонка так и называется, а раньше она
            // молча сортировала по общему числу плагинов, то есть не по тому, что показывает.
            c.Add(new ColDef {
                Id = "Plugins", En = "Plugins Missed", Width = 165, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingPlugins.CompareTo(b.MissingPlugins); } });

            c.Add(new ColDef {
                Id = "Files", En = "Files Missed", Width = 130, Right = true,
                Text = delegate (SetEntry s) { return ""; },
                Sort = delegate (SetEntry a, SetEntry b) { return a.MissingFiles.CompareTo(b.MissingFiles); } });

            c.Add(new ColDef {
                Id = "Tags", En = "Tags", Width = 180, Chips = true,
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
                Id = "Size", En = "Project size", Width = 130, Right = true,
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
            public string En;
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
                Id = "Plugin", En = "Plugin", Width = 0,
                Font = Theme.FTitle, Color = Theme.Text,
                Text = delegate (PluginStat p) { return p.Name; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Developer", En = "Developer", Width = 180,
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
                Id = "FxType", En = "Type", Width = 150,
                Text = delegate (PluginStat p) { return p.FxType; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.FxType, b.FxType, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Format", En = "Format", Width = 120,
                Text = delegate (PluginStat p) { return p.Format; },
                Sort = delegate (PluginStat a, PluginStat b)
                    { return string.Compare(a.Format, b.Format, StringComparison.OrdinalIgnoreCase); } });

            c.Add(new PluginColDef {
                Id = "Sets", En = "Sets", Width = 80, Right = true,
                Text = delegate (PluginStat p) { return p.Sets > 0 ? p.Sets.ToString() : "—"; },
                Sort = delegate (PluginStat a, PluginStat b) { return a.Sets.CompareTo(b.Sets); } });

            // Версия и файл — из базы самой Live, поэтому есть только у установленных.
            // По умолчанию спрятаны: нужны, когда разбираешься с конкретным плагином,
            // а не когда просматриваешь список.
            c.Add(new PluginColDef {
                Id = "Version", En = "Version", Width = 110,
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
                Id = "File", En = "File", Width = 240,
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
                Id = "Status", En = "Status", Width = 160, Right = true,
                Text = delegate (PluginStat p) { return ""; },
                Sort = delegate (PluginStat a, PluginStat b)
                    {
                        int sa = PluginStatusRank(a), sb = PluginStatusRank(b);
                        return sa != sb ? sa.CompareTo(sb) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    } });

            return c;
        }

        static int PluginStatusRank(PluginStat p)
        {
            if (p.Match == MatchKind.Missing || (p.Installed != null && p.Installed.FileMissing)) return 2;
            if (p.Match == MatchKind.OtherFormat) return 1;
            return 0;
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
            _settingsBtn.Icon = Glyph.Settings;
            _min.Icon = Glyph.Minimize;
            _max.Icon = Glyph.Maximize;
            _close.Icon = Glyph.Close;
            _close.Danger = true;

            _settingsBtn.Click += delegate { ShowSettings(); };
            _min.Click += delegate { WindowState = FormWindowState.Minimized; };
            _max.Click += delegate { ToggleMaximize(); };
            _close.Click += delegate { Close(); };
            foreach (IconButton b in new IconButton[] { _folders, _settingsBtn, _min, _max, _close })
                Controls.Add(b);

            // Кнопка нового сета переехала в футер и стала подписанной — доставать
            // новый пустой проект по одной иконке было не очевидно.
            Controls.Add(_newProject);

            _playerClose.Icon = Glyph.Close;
            _playerClose.Quiet = true;
            _playerClose.Visible = false;
            _playerClose.Click += delegate { if (_player != null && !_player.IsDisposed) _player.ShutDown(); };
            Controls.Add(_playerClose);

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

            _playerVolBtn.Icon = Glyph.VolumeHigh;
            _playerVolBtn.Click += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    if (_player.Volume > 0.001f)
                    {
                        _preMuteVolume = _player.Volume;
                        _player.Volume = 0f;
                    }
                    else
                    {
                        _player.Volume = _preMuteVolume > 0.05f ? _preMuteVolume : 0.5f;
                    }
                    _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                }
            };
            _playerVolBtn.MouseEnter += delegate
            {
                _volPopupTimer.Stop();
                if (_player != null && !_player.IsDisposed)
                {
                    _playerVolPopup.Value = _player.Volume;
                    _playerVolPopup.Visible = true;
                    _playerVolPopup.BringToFront();
                }
            };
            _playerVolBtn.MouseLeave += delegate
            {
                StartVolPopupCloseTimer();
            };
            _playerVolBtn.MouseWheel += delegate (object sender, MouseEventArgs e)
            {
                if (_player != null && !_player.IsDisposed && e.Delta != 0)
                {
                    int steps = e.Delta / 120;
                    if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                    float newVal = (float)Math.Round((_player.Volume + steps * 0.05f) / 0.05f) * 0.05f;
                    _player.Volume = Math.Max(0f, Math.Min(1f, newVal));
                    _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                }
            };

            _playerVolPopup.MouseEnter += delegate
            {
                _volPopupTimer.Stop();
            };
            _playerVolPopup.MouseLeave += delegate
            {
                StartVolPopupCloseTimer();
            };
            _playerVolPopup.ValueChanged += delegate
            {
                if (_player != null && !_player.IsDisposed)
                {
                    _player.Volume = _playerVolPopup.Value;
                    UpdatePlayerVolumeIcon();
                }
            };

            _volPopupTimer.Interval = 300;
            _volPopupTimer.Tick += delegate
            {
                Point pt = PointToClient(Cursor.Position);
                if (!_playerVolBtn.Bounds.Contains(pt) && !_playerVolPopup.Bounds.Contains(pt))
                {
                    _playerVolPopup.Visible = false;
                    _volPopupTimer.Stop();
                }
            };

            _playerSetLink.Click += delegate
            {
                if (_player != null && !_player.IsDisposed && _player.CurrentSet != null)
                {
                    NavigateToSet(_player.CurrentSet);
                }
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
                    if (!_playerVolPopup.Visible)
                        _playerVolPopup.Value = _player.Volume;
                    UpdatePlayerVolumeIcon();
                    UpdatePlayerTransport();
                    string sname = _player.CurrentSet != null ? _player.CurrentSet.Name : "";
                    if (_playerSetLink.SetName != sname)
                    {
                        _playerSetLink.SetName = sname;
                        LayoutAll();
                    }
                    if (!_rPlayerTimeLeft.IsEmpty) Invalidate(_rPlayerTimeLeft);
                    if (!_rPlayerTimeRight.IsEmpty) Invalidate(_rPlayerTimeRight);
                    if (!_rPlayerTrack.IsEmpty) Invalidate(_rPlayerTrack);
                }
            };

            _playerClose.Visible = _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible =
                _playerExpand.Visible = _playerSeek.Visible = _playerVolBtn.Visible = _playerVolPopup.Visible =
                _playerSetLink.Visible = false;
            Controls.Add(_playerPrev); Controls.Add(_playerPlayPause);
            Controls.Add(_playerNext); Controls.Add(_playerSeek);
            Controls.Add(_playerVolBtn); Controls.Add(_playerExpand);
            Controls.Add(_playerSetLink); Controls.Add(_playerVolPopup);

            _mode.SelectedChanged += delegate
            {
                _list.ScrollOffsetX = 0;
                _list.ScrollOffset = 0;
                _pluginView = -1;
                _summary.Selected = -1;
                _setSortId = null; _pluginSortId = null; _sortDesc = false;
                _list.SortColumn = -1;
                UpdateFiltersButton();
                LayoutAll();
                Refill();

                if (_mode.SelectedIndex == ModeSets) RestoreLastSetSelection();
            };
            Controls.Add(_mode);

            _viewToggle.SelectedChanged += delegate
            {
                _list.ScrollOffsetX = 0;
                _list.ScrollOffset = 0;
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

            _dice.Icon = Glyph.Dice;
            _dice.SpinOnClick = true;
            _dice.Click += delegate { RollRandomSet(); };
            Controls.Add(_dice);

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
            _detail.RescueRequested += RescueSelected;
            _detail.CollectRequested += CollectSelected;
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
            _home.RescueRequested += RescueSet;
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
            _mode.SetItems("Sets", "Plugins");
            _viewToggle.SetGlyphs(Glyph.ViewTiles, Glyph.ViewList);
            _filtersBtn.Text = "Filters";
            _filtersBtn.Count = _filter.ActiveCount;
            _newProject.Text = "New Live Set";
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
                cue = "Search in plugins…";
            else
                cue = "Search in sets…";
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

            _folders.SetBounds(panelX + step, y, icon, icon);
            _settingsBtn.SetBounds(panelX + step * 2, y, icon, icon);

            _mode.Height = h;
            _mode.Location = new Point(left, y);
            bool setsMode = _mode.SelectedIndex == ModeSets;
            bool isTiles = setsMode && _viewToggle.SelectedIndex == ViewTiles;
            bool isList = setsMode && _viewToggle.SelectedIndex == ViewList;

            // Кнопку «New Live Set» из футера убрали: нижняя полоса теперь только плеер.
            // Создать сет по-прежнему можно первой плиткой в Recent.
            _newProject.Visible = false;

            _viewToggle.Height = h;
            _viewToggle.Location = new Point(_mode.Right + Sc(16), y);
            _dice.SetBounds(_viewToggle.Right + Sc(16), y, icon, icon);

            _viewToggle.Visible = setsMode;
            _dice.Visible = setsMode;

            _filtersBtn.SetBounds(_dice.Right + Sc(16), y, Sc(140), h);
            _filtersBtn.Visible = true;
            int searchX = _filtersBtn.Right + Sc(15);
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
            int listGap = Sc(20);

            // Нижняя полоса — только плеер. Нет плеера — нет и полосы: содержимое
            // забирает её высоту себе, а не оставляет пустой прогал во всю ширину.
            bool playerOpen = _player != null && !_player.IsDisposed;
            bool showTransport = playerOpen;
            bool scanLine = _mode.SelectedIndex == ModeSets && _status.Length > 0;
            int listBottom = showTransport || scanLine ? footerControlY - Sc(10) : footerBottom;

            _newProject.SetBounds(panelX - listGap - _newProject.Width, footerControlY, _newProject.Width, controlH);

            int statusY = footerControlY + (controlH - statusH) / 2;
            _rStatus = new Rectangle(left + Sc(2), statusY, Sc(700), statusH);

            bool plugins = _mode.SelectedIndex == ModePlugins;
            int listTop = top;

            // Мини-транспорт — везде, где есть что играть, включая вкладку плагинов:
            // подпись про источник плагинов оттуда убрана, полоса свободна.
            _playerClose.Visible = _playerPrev.Visible = _playerPlayPause.Visible = _playerNext.Visible
                = _playerExpand.Visible = _playerSeek.Visible = _playerVolBtn.Visible
                = _playerSetLink.Visible = showTransport;
            if (!showTransport)
            {
                _playerVolPopup.Visible = false;
                _rPlayerTimeLeft = _rPlayerTimeRight = _rPlayerTrack = Rectangle.Empty;
            }
            if (showTransport)
            {
                int trIcon = controlH;
                int trStep = trIcon + Sc(Theme.IconGap);
                // Кнопка нового сета из футера убрана, и её место — тоже место имени:
                // упираться в невидимый прямоугольник и резать имя многоточием незачем.
                int contentRight = (_newProject.Visible ? _newProject.Left : panelX - listGap) - Sc(16);

                // Ряд центрируем в полосе, оставшейся под таблицей: снизу поле окна
                // Sc(Pad), сверху всего Sc(10) до таблицы — прижатый к нижнему полю ряд
                // заметно уезжал вверх от середины этой полосы.
                int playerY = listBottom + ((ClientSize.Height - listBottom) - controlH) / 2;

                int startX = left + Sc(7);

                // 1. Крестик закрытия слева
                _playerClose.SetBounds(startX, playerY, trIcon, trIcon);
                int curX = _playerClose.Right + Sc(20);

                // 2. Кнопки Prev, Play/Pause, Next
                _playerPrev.SetBounds(curX, playerY, trIcon, trIcon);
                _playerPlayPause.SetBounds(curX + trStep, playerY, trIcon, trIcon);
                _playerNext.SetBounds(curX + trStep * 2, playerY, trIcon, trIcon);
                curX = _playerNext.Right + Sc(24);

                // 3. Прогресс-бар и подписи над ним: время по краям желобка,
                //    имя файла — крупным по центру между ними.
                int seekW = Sc(348);
                int seekPad = Sc(4);              // внутренний отступ желобка в SeekSlider
                int timeW = Sc(38);

                // Высоту строки подписей берём у самого крупного шрифта в ней: на
                // глазок поставленное число режет имени файла хвосты букв (p, y, g).
                int textH = TextRenderer.MeasureText("Agjpq", Theme.FTitle).Height;
                int seekH = Sc(14);
                int textY = playerY + (controlH - (textH + Sc(1) + seekH)) / 2;

                // Время мельче имени трека, и по верху коробки они встали бы на разные
                // линии — сажаем мелкую строку на базовую линию крупной.
                int timeH = TextRenderer.MeasureText("Agjpq", Theme.FMini).Height;
                int timeY = textY + Theme.Baseline(Theme.FTitle) - Theme.Baseline(Theme.FMini);

                _rPlayerTimeLeft = new Rectangle(curX + seekPad, timeY, timeW, timeH);
                _rPlayerTimeRight = new Rectangle(curX + seekW - seekPad - timeW, timeY, timeW, timeH);

                int trackLeft = _rPlayerTimeLeft.Right + Sc(6);
                int trackW = Math.Max(0, (_rPlayerTimeRight.Left - Sc(6)) - trackLeft);
                _rPlayerTrack = new Rectangle(trackLeft, textY, trackW, textH);

                _playerSeek.SetBounds(curX, textY + textH + Sc(1), seekW, seekH);
                curX += seekW + Sc(24);

                // 4. Кнопка громкости и всплывающий регулятор над ней
                _playerVolBtn.SetBounds(curX, playerY, trIcon, trIcon);
                int popupW = Sc(34);
                int popupH = Sc(140);
                int popupX = _playerVolBtn.Left + (trIcon - popupW) / 2;
                int popupY = _playerVolBtn.Top - popupH - Sc(12);
                _playerVolPopup.SetBounds(popupX, popupY, popupW, popupH);
                curX += trIcon + Sc(Theme.IconGap);

                // 5. Кнопка разворачивания плеера (OpenPlaylist)
                _playerExpand.SetBounds(curX, playerY, trIcon, trIcon);
                curX += trIcon + Sc(16);

                // 6. Кликабельное название сета
                string sname = (_player != null && !_player.IsDisposed && _player.CurrentSet != null) ? _player.CurrentSet.Name : "";
                _playerSetLink.SetName = sname;
                int maxNameW = Math.Max(Sc(100), contentRight - curX);
                int nameW = maxNameW;
                if (!string.IsNullOrEmpty(sname))
                {
                    int measured = TextRenderer.MeasureText(sname, Theme.FTitle).Width + Sc(8);
                    nameW = Math.Min(maxNameW, measured);
                }
                _playerSetLink.SetBounds(curX, playerY, nameW, controlH);
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
                _list.PillRightGap = listGap;
                _list.SetBounds(left, listTop, Math.Max(Sc(200), panelX - left),
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
        void Notify(string msg)
        {
            _toast.Post(msg, 2200);
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

            // Пока окно тянут за край, обходимся дешёвой перерисовкой себя: Invalidate(true)
            // проходит вдобавок по всем дочерним контролам, а их тут под два десятка, и
            // делать это на каждое сообщение ресайза незачем — они и так перерисуются от
            // собственной смены размера. Полную перерисовку делаем один раз, когда край
            // отпустили (WM_EXITSIZEMOVE).
            if (_inSizeMove) Invalidate(); else Invalidate(true);
        }

        /// <summary>
        /// Перерисовка на перемещение нужна только тогда, когда окно двигают не мышью
        /// (снап с клавиатуры, перенос на другой монитор). Пока идёт перетаскивание,
        /// пропускаем: содержимое от сдвига окна не меняется, а лишняя работа на каждое
        /// WM_MOVE — ровно то, из-за чего окно уезжало за курсором с задержкой.
        /// </summary>
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_inSizeMove) Invalidate(false);
        }

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

        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST     = 0x0084;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE  = 0x0232;

        [StructLayout(LayoutKind.Sequential)]
        struct MinMaxInfo
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        /// <summary>Окно сейчас тащат или тянут за край — идёт модальный цикл Windows
        /// между WM_ENTERSIZEMOVE и WM_EXITSIZEMOVE.</summary>
        bool _inSizeMove;

        protected override void WndProc(ref Message m)
        {
            // Клик мимо открытого модального окна — системный звук отсюда и берётся.
            if (Chrome.SwallowBlockedClick(ref m)) return;

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

            // Окно взяли в руки: на время перетаскивания перестаём перерисовываться на
            // каждое движение — см. OnMove/OnResize. Само размытие при этом не трогаем:
            // подмена акрила на ходу заметна глазом и выглядит хуже, чем сами тормоза.
            // Кому оно тяжело (Windows 10), тот выключает его насовсем — Settings.DisableGlass.
            if (m.Msg == WM_ENTERSIZEMOVE) _inSizeMove = true;
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                _inSizeMove = false;
                LayoutAll();
                Invalidate(true);
            }

            base.WndProc(ref m);

            if (m.Msg == WM_GETMINMAXINFO)
            {
                // Развёрнутое окно без рамки Windows растягивает на весь МОНИТОР, а не на
                // рабочую область, — и оно накрывает панель задач. Рамки у нас нет, значит
                // и «съезжания под панель» ждать неоткуда: предел развёртки задаём сами.
                //
                // Считаем от того монитора, на котором окно сейчас: на втором экране
                // панель может стоять с другой стороны или не стоять вовсе.
                Screen sc = Screen.FromHandle(Handle);
                Rectangle work = sc.WorkingArea, full = sc.Bounds;

                MinMaxInfo mmi = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                mmi.MaxPosition = new Point(work.Left - full.Left, work.Top - full.Top);
                mmi.MaxSize = new Point(work.Width, work.Height);
                Marshal.StructureToPtr(mmi, m.LParam, false);
                return;
            }

            if (m.Msg != WM_NCHITTEST || (int)m.Result != 1) return;

            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));

            // На развёрнутом окне краёв под растягивание нет вовсе. Иначе выходило так:
            // потянул за край развёрнутого окна — Windows молча вывела его из Maximized
            // в обычное состояние прямо во весь экран, и дальше «развернуть» уже ничего
            // не делало (оно и так во весь экран), а «свернуть в окно» возвращало не тот
            // размер, что был до этого. Тащить за панель инструментов при этом можно —
            // это штатный способ вернуть окно к прежнему размеру.
            if (WindowState != FormWindowState.Maximized)
            {
                int b = Sc(6);
                bool l = p.X <= b, r = p.X >= ClientSize.Width - b;
                bool t = p.Y <= b, d = p.Y >= ClientSize.Height - b;

                if (t && l) { m.Result = (IntPtr)13; return; }
                if (t && r) { m.Result = (IntPtr)14; return; }
                if (d && l) { m.Result = (IntPtr)16; return; }
                if (d && r) { m.Result = (IntPtr)17; return; }
                if (l) { m.Result = (IntPtr)10; return; }
                if (r) { m.Result = (IntPtr)11; return; }
                if (t) { m.Result = (IntPtr)12; return; }
                if (d) { m.Result = (IntPtr)15; return; }
            }

            // За панель инструментов тащим окно — но не за само имя программы: оно
            // За панель инструментов тащим окно.
            if (p.Y < Sc(Theme.ContentY) - Sc(10)) m.Result = (IntPtr)2;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Sc(Theme.ContentY) - Sc(10)) ToggleMaximize();
            base.OnMouseDoubleClick(e);
        }

        /// <summary>
        /// Открыть диалог настроек.
        /// </summary>
        void ShowSettings()
        {
            bool rescan, help;
            using (SettingsDialog d = new SettingsDialog(_settings))
            {
                d.ShowDialog(this);
                rescan = d.RescanWanted;
                help = d.ShortcutsWanted;
            }

            _settings.Save();
            if (rescan) StartScan(true);
            if (help) ShowHelp();
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
            // Ctrl+, — как в любой другой программе; на русской раскладке это та же
            // клавиша «б», код у неё от раскладки не зависит.
            else if (e.Control && e.KeyCode == Keys.Oemcomma)
            {
                ShowSettings();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.N) { NewProject(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.T && !typing && _mode.SelectedIndex == ModeSets)
            {
                EditNotes(SelectedSet());
                e.Handled = e.SuppressKeyPress = true;
            }

            else if (e.Control && e.KeyCode == Keys.R && !typing && _mode.SelectedIndex == ModeSets)
            {
                RescueSet(SelectedSet());
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
            else if (e.KeyCode == Keys.Enter && (_list.Selected != null || SelectedSet() != null))
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

            // Над окном держат папку — обводим его, чтобы было видно, что бросать
            // можно сюда. Обычным светлым, не акцентом — тот же принцип, что и у
            // выделения строки/плитки.
            if (_dragOverWindow)
            {
                RectangleF edge = new RectangleF(1.5f, 1.5f, ClientSize.Width - 3f, ClientSize.Height - 3f);
                using (GraphicsPath ep = Theme.Round(edge, Sc(Theme.WindowR) - 1.5f))
                using (Pen pen = new Pen(Color.FromArgb(0xE0, Theme.Light), 3f))
                    g.DrawPath(pen, ep);
            }

            // Транспорт рисуется везде, где он разложен, — включая вкладку плагинов.
            bool showTransport = _player != null && !_player.IsDisposed;

            if (showTransport)
            {
                // Строго по верху коробки: прямоугольники уже разведены так, чтобы
                // время и имя трека сели на одну базовую линию (см. LayoutAll).
                TextFormatFlags tfL = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextFormatFlags tfR = TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextFormatFlags tfC = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

                if (!string.IsNullOrEmpty(_playerTimeLeftStr) && !_rPlayerTimeLeft.IsEmpty)
                    Chrome.DrawText(g, _playerTimeLeftStr, Theme.FMini, _rPlayerTimeLeft, Theme.TextDim, tfL);
                if (!string.IsNullOrEmpty(_playerTimeRightStr) && !_rPlayerTimeRight.IsEmpty)
                    Chrome.DrawText(g, _playerTimeRightStr, Theme.FMini, _rPlayerTimeRight, Theme.TextDim, tfR);

                // Имя трека (файла) над прогресс-баром
                string trackName = (_player != null && !_player.IsDisposed) ? _player.CurrentFileName : "";
                if (!string.IsNullOrEmpty(trackName) && !_rPlayerTrack.IsEmpty)
                    Chrome.DrawText(g, trackName, Theme.FTitle, _rPlayerTrack, Theme.Text, tfC);
            }
            else if (_mode.SelectedIndex == ModeSets)
            {
                Chrome.DrawText(g, StatusText(), Theme.FLabel, _rStatus, Theme.TextDim, Chrome.Left);
            }

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
                     ? "Scanning " + _scanDone + " / " + _scanTotal
                     : "Looking for sets… " + _scanDone;
            // VisibleCount, а не VisibleSets().Count: счётчик рисуется на каждой
            // перерисовке окна (при открытом плеере — двадцать раз в секунду), и строить
            // ради него список сетов незачем.
            if (_mode.SelectedIndex == ModeSets && _viewToggle.SelectedIndex == ViewTiles)
                return _home.VisibleCount + " shown";
            return _list.Rows.Count + " shown";
        }

        string StatusText()
        {
            return _status.Length > 0 ? _status : "";
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
            int scrollX = _list.ScrollOffsetX;

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
            _list.ScrollOffsetX = scrollX;
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
                cols[i] = new Column(d.En, w)
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
                                         s.MissingPlugins == 0 ? "" : s.MissingPlugins + " "));

            if (fIdx >= 0)
            {
                if (s.Error.Length > 0)
                    r.Marks.Add(new CellMark(fIdx, Theme.Red, "unreadable"));
                else if (s.TotalRefs > 0 || s.MissingFiles > 0)
                    r.Marks.Add(new CellMark(fIdx, s.MissingFiles > 0 ? Theme.Red : Theme.Green,
                     s.MissingFiles == 0 ? ""
                                         : s.MissingFiles + " "));
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
            int scrollX = _list.ScrollOffsetX;
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
            _list.ScrollOffsetX = scrollX;
        }

        void FillPlugins(bool animate)
        {
            _list.ColumnsConfigurable = true;
            _list.ShowPlayButton = false;
            _list.ShowPinIndicator = false;

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
                pcols[i] = new Column(d.En, w)
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

                // Отметку ставим, только если колонку состояния сейчас видно:
                // три состояния — Installed (✔️), not installed (❌), other format.
                if (stIdx >= 0)
                {
                    if (st.Match == MatchKind.Missing || (st.Installed != null && st.Installed.FileMissing))
                        r.Marks.Add(new CellMark(stIdx, Theme.Red, "❌"));
                    else if (st.Match == MatchKind.OtherFormat)
                        r.Marks.Add(new CellMark(stIdx, Theme.TextDim, "other format"));
                    else
                        r.Marks.Add(new CellMark(stIdx, Theme.Green, "✔️"));
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
            SetEntry keepSet = SelectedSet();
            string keepSetPath = keepSet != null ? keepSet.Path : null;
            PluginStat keepPlugin = SelectedPlugin();
            string keepPluginName = keepPlugin != null ? keepPlugin.Name : null;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;

            if (_mode.SelectedIndex == ModeSets)
            {
                if (column < 0 || column >= _setVisible.Count) return;
                string id = _setVisible[column].Id;
                if (_setSortId == id) _sortDesc = !_sortDesc;
                else { _setSortId = id; _sortDesc = false; }
                Refill(false);     // FillSets проставит _list.SortColumn/Descending
                if (keepSetPath != null)
                {
                    _list.SelectRow(delegate (RowData r)
                    {
                        SetEntry s = r.Tag as SetEntry;
                        return s != null && string.Equals(s.Path, keepSetPath, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }
            else
            {
                if (column < 0 || column >= _pluginVisible.Count) return;
                string pid = _pluginVisible[column].Id;
                if (_pluginSortId == pid) _sortDesc = !_sortDesc;
                else { _pluginSortId = pid; _sortDesc = false; }
                Refill(false);         // FillPlugins проставит _list.SortColumn/Descending
                if (keepPluginName != null)
                {
                    _list.SelectRow(delegate (RowData r)
                    {
                        PluginStat p = r.Tag as PluginStat;
                        return p != null && string.Equals(p.Name, keepPluginName, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
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
                    "One row per folder");
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
                foreach (ColDef d in Catalog) { ids.Add(d.Id); titles.Add(d.En); }
            else
                foreach (PluginColDef d in PluginCatalog) { ids.Add(d.Id); titles.Add(d.En); }

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
            ToolStripMenuItem reset = new ToolStripMenuItem("Reset to defaults");
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
            int sel = _list.SelectedIndex;
            int scroll = _list.ScrollOffset;
            int scrollX = _list.ScrollOffsetX;
            Refill(false);
            _list.SelectIndex(sel);
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
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
            int scrollX = _list.ScrollOffsetX;
            Refill(false);                        // строки те же — влетать снизу им незачем
            _list.SelectIndex(sel);
            _list.ScrollOffset = scroll;
            _list.ScrollOffsetX = scrollX;
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
            if (s != null) _lastSetPath = s.Path;

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
            // По плагину активировать нечего: раньше двойной клик утаскивал на вкладку
            // Sets с фильтром по этому плагину — неожиданный прыжок вместо действия над
            // тем, по чему ткнули. Сеты плагина и так перечислены в панели сведений.
            if (_mode.SelectedIndex == ModePlugins) return;
            OpenSelected();
        }

        /// <summary>Клик по сету в списке «Sets» у плагина или «Show Details» на главной —
        /// переход к нему на вкладке Sets в табличный вид с открытием панели деталей.</summary>
        void OnSetRequested(SetEntry set)
        {
            if (set == null) return;
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

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public void SelectSetByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { SelectSetByPath(path); }); } catch { }
                return;
            }

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            _search.Box.Text = "";
            if (!_filter.IsEmpty)
            {
                _filter.Clear();
                UpdateFiltersButton();
            }

            bool modeChanged = _mode.SelectedIndex != ModeSets;
            bool viewChanged = _viewToggle.SelectedIndex != ViewList;

            _mode.SelectedIndex = ModeSets;
            _viewToggle.SelectedIndex = ViewList;

            if (modeChanged || viewChanged)
                Refill();

            SetEntry matched = null;
            _list.SelectRow(delegate (RowData r)
            {
                SetEntry s = r.Tag as SetEntry;
                if (s == null) return false;
                if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    matched = s;
                    return true;
                }
                if (!string.IsNullOrEmpty(s.ProjectDir) &&
                    (string.Equals(s.ProjectDir, path, StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith(s.ProjectDir, StringComparison.OrdinalIgnoreCase)))
                {
                    matched = s;
                    return true;
                }
                return false;
            });

            SetEntry specific = null;
            foreach (SetEntry s in _index.Sets)
            {
                if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    specific = s;
                    break;
                }
            }

            if (specific != null)
                _detail.Show(specific);
            else if (matched != null && _list.Selected == null)
                _detail.Show(matched);

            BringToFront();
            Activate();
            try { SetForegroundWindow(Handle); } catch { }
        }

        // ------------------------------------------------------------ действия

        SetEntry SelectedSet()
        {
            if (_mode.SelectedIndex == ModeSets && _viewToggle.SelectedIndex == ViewTiles)
                return _home.Selected;
            RowData sel = _list.Selected;
            return sel == null ? null : sel.Tag as SetEntry;
        }

        /// <summary>
        /// Возврат на вкладку Sets (например, с Plugins, куда увёл клик по плагину в
        /// панели сведений) пересобирает список и снимает выделение — здесь находим по
        /// пути тот же сет и выделяем его заново. Тихо ничего не делает, если сета с
        /// таким путём сейчас не видно — например, его исключил фильтр.
        /// </summary>
        void RestoreLastSetSelection()
        {
            if (_lastSetPath == null) return;
            string path = _lastSetPath;

            if (_viewToggle.SelectedIndex == ViewTiles)
            {
                foreach (SetEntry s in _home.VisibleSets())
                    if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                    { _home.Select(s); return; }
            }
            else
            {
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry s = r.Tag as SetEntry;
                    return s != null && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        /// <summary>
        /// Кубик рядом с переключателем вида — выбирает случайный сет из того же
        /// набора, что сейчас на экране (с учётом фильтров и поиска), и не трогает
        /// сам вид: плитки остаются плитками, список — списком.
        /// </summary>
        void RollRandomSet()
        {
            bool tiles = _viewToggle.SelectedIndex == ViewTiles;
            SetEntry pick;
            if (tiles)
            {
                List<SetEntry> pool = _home.VisibleSets();
                if (pool.Count == 0) return;
                pick = pool[_rng.Next(pool.Count)];
                _home.Select(pick);
            }
            else
            {
                List<SetEntry> pool = new List<SetEntry>();
                foreach (RowData row in _list.Rows)
                {
                    SetEntry s = row.Tag as SetEntry;
                    if (s != null) pool.Add(s);
                }
                if (pool.Count == 0) return;
                pick = pool[_rng.Next(pool.Count)];
                _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, pick); });
            }
        }

        void OpenSelected()
        {
            OpenSet(SelectedSet());
        }

        void RescueSelected()
        {
            RescueSet(SelectedSet());
        }

        /// <summary>Открыть конкретный сет в Live — общее для списка и плиток главной.</summary>
        void OpenSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true });
                // Live поднимается не сразу — то же самое «кнопка как будто не сработала»,
                // что и у запуска пустой Live, только тут ещё и не сразу ясно, ТОТ ли
                // именно сет открывается: имя сохраняет и в заголовке двойных строк.
                Notify("Opening “" + s.Name + ".als”…");
            }
            catch (Exception ex) { Status("Could not open: " + ex.Message); }
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

        /// <summary>
        /// Помощник по восстановлению: сет не открывается, и надо выяснить, какой плагин
        /// его роняет. Оригинал он не трогает — работает на пробных копиях, см. RescueDialog.
        /// </summary>
        void RescueSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }

            using (RescueDialog d = new RescueDialog(s, _index.Inventory))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                    Notify("Saved " + Path.GetFileName(d.Produced));
            }
        }

        void CollectSelected()
        {
            SetEntry s = SelectedSet();
            CollectSet(s);
        }

        /// <summary>
        /// Собрать проект: все нужные ему медиафайлы в одну папку рядом с ним, плюс копия
        /// сета с переписанными путями. Оригинал не трогается — см. CollectAll.
        /// </summary>
        void CollectSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }

            using (CollectDialog d = new CollectDialog(s, _index.Env, _settings))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                {
                    Notify(d.Failed > 0
                        ? string.Format("Collected to {0} — {1} file(s) could not be copied, see the log",
                                        Path.GetFileName(d.Produced), d.Failed)
                        : "Collected to " + Path.GetFileName(d.Produced));

                    // Не открывать проводник на наполовину собранной папке: тост про
                    // отказы уже отправил человека в журнал, а не смотреть на то, чего
                    // там не хватает.
                    if (d.Failed == 0)
                    {
                        try { Process.Start("explorer.exe", "\"" + d.Produced + "\""); }
                        catch (Exception ex) { Diag.Line("collect: explorer: " + ex.Message); }
                    }
                }
            }
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
                    else Status("The plugin file is gone: " + st.Installed.Path);
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
                // Фильтры применяются на лету: пока окно открыто, список и счётчик
                // «N shown» за ним меняются на глазах, а кнопка снизу просто закрывает.
                d.Changed += delegate
                {
                    _filter.CopyFrom(d.Result);
                    UpdateFiltersButton();
                    Refill(false);
                };
                d.ShowDialog(this);
                _filter.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill(false);
            }
        }

        void EditPluginFilters()
        {
            List<PluginStat> all = _index.PluginUsage();
            using (PluginFiltersDialog d = new PluginFiltersDialog(_pluginFilterObj, all))
            {
                // Фильтры применяются на лету: пока окно открыто, список плагинов
                // за ним меняется на глазах, а кнопка снизу просто закрывает.
                d.Changed += delegate
                {
                    _pluginFilterObj.CopyFrom(d.Result);
                    UpdateFiltersButton();
                    Refill(false);
                };
                d.ShowDialog(this);
                _pluginFilterObj.CopyFrom(d.Result);
                UpdateFiltersButton();
                Refill(false);
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
                    Status("No renders next to this set");
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
                int scroll = _list.ScrollOffset;
                int scrollX = _list.ScrollOffsetX;
                Refill(false);

                // Refill пересобирает список с нуля (SetRows всегда сбрасывает выделение —
                // так и панель справа, и подсветка строки гаснут посреди правки её же
                // тегов). Объект сета не меняется, поэтому просто выделяем его снова.
                if (_viewToggle.SelectedIndex == ViewTiles) _home.Selected = s;
                else
                {
                    _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, s); });
                    _list.ScrollOffset = scroll;
                    _list.ScrollOffsetX = scrollX;
                }
            }
        }

        void OpenPreview()
        {
            SetEntry s = SelectedSet();
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
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

            ToolStripMenuItem open = new ToolStripMenuItem("Open in Live");
            open.ShortcutKeyDisplayString = "Enter";
            open.Click += delegate { OpenSet(s); };
            m.Items.Add(open);

            if (s.HasRenders)
            {
                ToolStripMenuItem play = new ToolStripMenuItem("Play render");
                play.ShortcutKeyDisplayString = "Space";
                play.Click += delegate { OnRowPlay(idx); };
                m.Items.Add(play);
            }

            ToolStripMenuItem pin = new ToolStripMenuItem(
                HomeStore.IsPinned(s.Path) ? "Unpin" : "Pin project");
            pin.Checked = HomeStore.IsPinned(s.Path);
            pin.Click += delegate { TogglePinAndRefresh(s); };
            m.Items.Add(pin);

            ToolStripMenuItem notes = new ToolStripMenuItem(
                ProjectMeta.HasAnything(s.ProjectDir)
                    ? "Tags and notes…"
                    : "Add tags or a note…");
            notes.ShortcutKeyDisplayString = "Ctrl+T";
            notes.Click += delegate { EditNotes(s); };
            m.Items.Add(notes);

            ToolStripMenuItem rescue = new ToolStripMenuItem("Rescue project…");
            rescue.ShortcutKeyDisplayString = "Ctrl+R";
            rescue.Click += delegate { RescueSet(s); };
            m.Items.Add(rescue);

            ToolStripMenuItem reveal = new ToolStripMenuItem("Show in Explorer");
            reveal.ShortcutKeyDisplayString = "Shift+Enter";
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
            if (_home.Visible) _home.RebuildTransition();
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
                    _playerVolPopup.Visible = false;
                    _volPopupTimer.Stop();
                    _playerSetLink.SetName = "";
                    _list.PlayingTag = _home.PlayingTag = null;
                    _list.Playing = _home.Playing = false;
                    _list.Invalidate();
                    _home.Invalidate();
                    LayoutAll();       // прячет мини-транспорт — управлять больше нечем
                    Invalidate(true);
                };
                _playerTimer.Start();
                _status = "";          // подпись «нет рендеров» относилась к прошлому сету
                LayoutAll();           // показывает мини-транспорт теперь, когда плеер есть
                // Раскладка сама по себе не перерисовывает фон формы, а на месте
                // футера оставалась старая строка состояния — поверх неё вставали
                // кнопки транспорта. Полная перерисовка ровно один раз, при открытии.
                Invalidate(true);
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

        void StartVolPopupCloseTimer()
        {
            _volPopupTimer.Stop();
            _volPopupTimer.Start();
        }

        void UpdatePlayerVolumeIcon()
        {
            float vol = _player != null && !_player.IsDisposed ? _player.Volume : 0.5f;
            Glyph g = vol <= 0.001f ? Glyph.Volume0 : vol <= 0.5f ? Glyph.VolumeLow : Glyph.VolumeHigh;
            if (_playerVolBtn.Icon != g)
            {
                _playerVolBtn.Icon = g;
                _playerVolBtn.Invalidate();
            }
        }

        void UpdatePlayerTransport()
        {
            bool playing = _player != null && !_player.IsDisposed && _player.IsPlaying;
            Glyph want = playing ? Glyph.Pause : Glyph.Play;
            if (_playerPlayPause.Icon != want) { _playerPlayPause.Icon = want; _playerPlayPause.Invalidate(); }

            if (_list.Playing != playing) { _list.Playing = playing; _list.Invalidate(); }
            if (_home.Playing != playing) { _home.Playing = playing; _home.Invalidate(); }

            UpdatePlayerVolumeIcon();
            string sname = (_player != null && !_player.IsDisposed && _player.CurrentSet != null) ? _player.CurrentSet.Name : "";
            if (_playerSetLink.SetName != sname)
            {
                _playerSetLink.SetName = sname;
                LayoutAll();
            }
            if (!_rPlayerTrack.IsEmpty) Invalidate(_rPlayerTrack);
        }

        void NavigateToSet(SetEntry target)
        {
            if (target == null) return;
            if (_mode.SelectedIndex != ModeSets)
            {
                _mode.SelectedIndex = ModeSets;
            }
            _lastSetPath = target.Path;

            if (_viewToggle.SelectedIndex == ViewTiles)
            {
                bool found = false;
                foreach (SetEntry s in _home.VisibleSets())
                {
                    if (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found && (_search.Text.Length > 0 || !_filter.IsEmpty))
                {
                    _search.Text = "";
                    _filter.Clear();
                    UpdateFiltersButton();
                    Refill();
                }
                _home.Select(target);
            }
            else
            {
                bool found = false;
                foreach (RowData r in _list.Rows)
                {
                    SetEntry s = r.Tag as SetEntry;
                    if (s != null && (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found && (_search.Text.Length > 0 || !_filter.IsEmpty))
                {
                    _search.Text = "";
                    _filter.Clear();
                    UpdateFiltersButton();
                    Refill();
                }
                _list.SelectRow(delegate (RowData r)
                {
                    SetEntry s = r.Tag as SetEntry;
                    return s != null && (ReferenceEquals(s, target) || string.Equals(s.Path, target.Path, StringComparison.OrdinalIgnoreCase));
                });
            }
            OnSelectionChanged();
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
                Status("Could not find Ableton Live — is it installed?");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                // Live поднимается долгие секунды и до первого своего окна не подаёт
                // никаких признаков жизни — без этой строчки нажатие выглядит как
                // «кнопка не сработала», и её жмут ещё раз.
                Notify("Starting Live…");
            }
            catch (Exception ex) { Status(ex.Message); }
        }

        // ------------------------------------------------- папка перетаскиванием

        /// <summary>Подсветка окна, пока над ним держат папку.</summary>
        bool _dragOverWindow;

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (!HasFolder(e.Data)) return;
            e.Effect = DragDropEffects.Copy;
            if (!_dragOverWindow) { _dragOverWindow = true; Invalidate(); }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (_dragOverWindow) { _dragOverWindow = false; Invalidate(); }
        }

        /// <summary>
        /// Брошенная на окно папка становится новым корнем. Раньше корни добавлялись
        /// только через отдельное окно «Folders…», хотя перетаскивание — первое, что
        /// пробуют сделать с менеджером файлов.
        /// </summary>
        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            _dragOverWindow = false;
            Invalidate();

            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return;

            List<string> added = new List<string>();
            foreach (string path in paths)
            {
                string folder = path;
                if (File.Exists(path)) folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                if (HasRoot(folder)) continue;
                _settings.Roots.Add(folder);
                _settings.DisabledRoots.Remove(folder);
                added.Add(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)));
            }
            if (added.Count == 0) { Notify("Already watching that folder"); return; }

            _settings.Save();
            Settings.NotifyRootsChanged(this);
            Notify(added.Count == 1 ? "Added " + added[0] : "Added " + added.Count + " folders");
            StartScan(true);
            Rewatch();
        }

        bool HasRoot(string folder)
        {
            foreach (string r in _settings.Roots)
                if (string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool HasFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
            string[] paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return false;
            foreach (string p in paths)
                if (Directory.Exists(p) || File.Exists(p)) return true;
            return false;
        }

        bool EditRoots()
        {
            using (RootsDialog d = new RootsDialog(_settings.Roots, _settings.DisabledRoots))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                _settings.Roots.Clear();
                _settings.Roots.AddRange(d.Result);
                _settings.DisabledRoots.Clear();
                _settings.DisabledRoots.AddRange(d.DisabledRoots);
                _settings.Save();
                Settings.NotifyRootsChanged(this);
                StartScan(true);
                Rewatch();          // набор корней другой — переставляем наблюдение
                return true;
            }
        }

        void OnGlobalRootsChanged(object source)
        {
            if (source == this || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { OnGlobalRootsChanged(source); }); } catch { }
                return;
            }
            _settings.ReloadRoots();
            StartScan(true);
            Rewatch();
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
                Status("No folders selected yet — press “Folders…”.");
                return;
            }

            Diag.Line("scan: start, roots = " + string.Join(" | ", _settings.Roots.ToArray()));
            _scanning = true;
            _manualScan = force;
            _scanDone = 0; _scanTotal = 0;
            _cancel = new CancellationTokenSource();
            Status("Scanning…");
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
                        Status("");

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

        void Status(string msg)
        {
            // Появление и исчезновение строки состояния меняет высоту содержимого
            // (нижняя полоса теперь резервируется только под то, что в ней есть),
            // поэтому раскладку пересчитываем — но лишь когда строка реально
            // появилась или пропала, а не на каждое её обновление.
            bool had = _status.Length > 0;
            _status = msg;
            if (had != (_status.Length > 0)) LayoutAll();
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_minimizeHotkey)
            {
                try { UnregisterHotKey(Handle, HotkeyMinimize); } catch { }
                _minimizeHotkey = false;
            }
            Settings.RootsChanged -= OnGlobalRootsChanged;
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            if (_watch != null) { try { _watch.Dispose(); } catch { } _watch = null; }
            if (_player != null && !_player.IsDisposed) { try { _player.Close(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
