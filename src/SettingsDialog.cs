using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Настройки программы. Раньше по имени программы в шапке открывались только
    /// горячие клавиши, а единственная настройка пряталась в меню правой кнопки —
    /// найти её там мог только тот, кто и так знал, что она есть.
    ///
    /// Сюда попадает то, что относится к программе целиком: как выглядит окно и откуда
    /// брать список плагинов. Настройки самого каталога сюда не переезжают — они уже
    /// живут там, где на них смотрят: группировка и колонки в меню шапки таблицы,
    /// закреплённые в звёздочке, папки в «Folders…».
    ///
    /// Раскладка строк одна на всё окно: подпись слева, контрол справа — как в самой
    /// Live в Preferences, чтобы читалось по вертикали одной колонкой значений.
    /// </summary>
    public sealed class SettingsDialog : GlassDialog
    {
        readonly Settings _s;

        readonly PillToggle _compat = new PillToggle();

        readonly Segmented _source = new Segmented();
        readonly DropField _install = new DropField();
        readonly PillToggle _vst2On = new PillToggle();
        readonly GlassButton _vst2Browse = new GlassButton();
        readonly PillToggle _vst3SysOn = new PillToggle();
        readonly PillToggle _vst3On = new PillToggle();
        readonly GlassButton _vst3Browse = new GlassButton();
        readonly GlassButton _rescan = new GlassButton();

        readonly GlassButton _options = new GlassButton();
        readonly GlassButton _shortcuts = new GlassButton();
        readonly GlassButton _close = new GlassButton();

        /// <summary>Пересобрать каталог: настройки плагинов поменялись.</summary>
        public bool RescanWanted;

        /// <summary>Показать горячие клавиши после закрытия окна.</summary>
        public bool ShortcutsWanted;

        /// <summary>Открыть редактор Options.txt после закрытия окна.</summary>
        public bool OptionsWanted;

        readonly List<string> _installs = new List<string>();

        public SettingsDialog(Settings s)
        {
            _s = s;
            Caption = L.S("Settings", "Настройки");
            ClientSize = new Size(Sc(700), Sc(660));

            _compat.Checked = _s.DisableGlass;
            _compat.CheckedChanged += delegate { ToggleCompat(); };
            State(_compat);

            _source.SetItems(L.S("Live's database", "База Live"), L.S("Scan folders", "Обход папок"));
            _source.SelectedIndex = _s.PluginsFromFolders ? 1 : 0;
            _source.SelectedChanged += delegate
            {
                _s.PluginsFromFolders = _source.SelectedIndex == 1;
                RescanWanted = true;
                Relayout();
                DescribeInventoryAsync();
            };
            Controls.Add(_source);

            // «All installs» первым пунктом: список плагинов складывается из всех сразу,
            // и это правильное умолчание — см. PluginInventory.Load. Список пунктов тут
            // provisорный (PluginInventory.Installs() не разбирает содержимое — см. её
            // комментарий); настоящий, посчитанный полным разбором, подставляет
            // RefreshInstallsAsync ниже, как только досчитает.
            _installs.Add(L.S("All installs", "Все установки"));
            _installs.AddRange(PluginInventory.Installs());
            int at = _installs.IndexOf(_s.PluginSource);
            _install.SetItems(_installs, at > 0 ? at : 0);
            _install.Width = Sc(240);
            _install.SelectedChanged += delegate
            {
                _s.PluginSource = _install.SelectedIndex <= 0 ? "" : _install.SelectedItem;
                RescanWanted = true;
                DescribeInventoryAsync();
            };
            Controls.Add(_install);

            Toggle(_vst2On, _s.Vst2CustomOn, delegate { _s.Vst2CustomOn = _vst2On.Checked; });
            Toggle(_vst3SysOn, _s.Vst3SystemOn, delegate { _s.Vst3SystemOn = _vst3SysOn.Checked; });
            Toggle(_vst3On, _s.Vst3CustomOn, delegate { _s.Vst3CustomOn = _vst3On.Checked; });

            Browse(_vst2Browse, delegate
            {
                string p = Pick(L.S("VST2 plug-in folder", "Папка VST2"), _s.Vst2CustomPath);
                if (p != null) { _s.Vst2CustomPath = p; _s.Vst2CustomOn = true; _vst2On.Checked = true; }
            });
            Browse(_vst3Browse, delegate
            {
                string p = Pick(L.S("VST3 plug-in folder", "Папка VST3"), _s.Vst3CustomPath);
                if (p != null) { _s.Vst3CustomPath = p; _s.Vst3CustomOn = true; _vst3On.Checked = true; }
            });

            _rescan.Text = L.S("Rescan", "Пересканировать");
            _rescan.FitToText(16);
            _rescan.Click += delegate { RescanWanted = true; Close(); };
            Controls.Add(_rescan);

            _options.Text = L.S("Options.txt…", "Options.txt…");
            _options.FitToText(16);
            _options.Click += delegate { OptionsWanted = true; Close(); };
            Controls.Add(_options);

            _shortcuts.Text = L.S("Shortcuts…", "Горячие клавиши…");
            _shortcuts.FitToText(16);
            _shortcuts.Click += delegate { ShortcutsWanted = true; Close(); };
            Controls.Add(_shortcuts);

            _close.Text = L.S("Close", "Закрыть");
            _close.Primary = true;
            _close.FitToText(20);
            _close.Click += delegate { Close(); };
            Controls.Add(_close);

            DescribeInventoryAsync();
            RefreshInstallsAsync();
        }

        void Toggle(PillToggle t, bool on, EventHandler changed)
        {
            t.Checked = on;
            t.CheckedChanged += changed;
            t.CheckedChanged += delegate { RescanWanted = true; Relayout(); DescribeInventoryAsync(); };
            State(t);
        }

        /// <summary>
        /// Переключатель пишет словом, что сейчас, — «On» или «Off», как в самой Live в
        /// Preferences. Ширина фиксированная по длинному из двух слов: иначе кнопка
        /// дёргалась бы шириной на каждое нажатие.
        /// </summary>
        void State(PillToggle t)
        {
            t.Width = Math.Max(TextRenderer.MeasureText(L.S("On", "Вкл"), t.Font).Width,
                               TextRenderer.MeasureText(L.S("Off", "Выкл"), t.Font).Width) + Sc(32);
            t.Text = t.Checked ? L.S("On", "Вкл") : L.S("Off", "Выкл");
            t.CheckedChanged += delegate { t.Text = t.Checked ? L.S("On", "Вкл") : L.S("Off", "Выкл"); };
            Controls.Add(t);
        }

        void Browse(GlassButton b, MethodInvoker click)
        {
            b.Text = L.S("Browse", "Обзор");
            b.FitToText(16);
            b.Click += delegate { click(); RescanWanted = true; Relayout(); DescribeInventoryAsync(); };
            Controls.Add(b);
        }

        static string Pick(string title, string current)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = title;
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) d.SelectedPath = current;
                return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
            }
        }

        /// <summary>
        /// Прозрачность окна переключается только через перезапуск, и это не лень:
        /// часть цветов темы — static readonly и считаются один раз по Glass.Enabled
        /// (см. MainForm.ToggleGlass, откуда это и переехало).
        /// </summary>
        void ToggleCompat()
        {
            _s.DisableGlass = _compat.Checked;
            _s.Save();

            string q = _compat.Checked
                ? L.S("Windows 10 compatible mode is on — transparency off. Restart now to apply?",
                      "Режим совместимости с Windows 10 включён — прозрачность выключена. Перезапустить сейчас?")
                : L.S("Transparency is back on. Restart now to apply?",
                      "Прозрачность включена обратно. Перезапустить сейчас?");

            if (MessageBox.Show(this, q, "Alive", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                == DialogResult.Yes)
                Application.Restart();
        }

        // ------------------------------------------------------------------ состояние

        string _inventory = "";
        int _job;

        /// <summary>
        /// Сколько плагинов сейчас видно и откуда. Читается в фоне: разбор базы Live —
        /// это мегабайт текста, а обход папок и вовсе ходит на диск. Пока считается,
        /// человек успевает щёлкнуть ещё раз, поэтому ответы старых заходов
        /// отбрасываются по номеру — иначе на экране осело бы число от прошлых настроек.
        /// </summary>
        void DescribeInventoryAsync()
        {
            _inventory = L.S("Reading…", "Читаю…");
            Invalidate();

            int mine = ++_job;
            Settings snapshot = _s;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                PluginInventory inv = PluginInventory.Load(snapshot);
                // Установок Live на машине бывает полтора десятка, и списком они в
                // строку не влезают — важно не какие именно, а сколько их сложилось.
                string where = inv.Sources.Count == 1
                    ? inv.Sources[0]
                    : inv.Sources.Count > 1
                      ? inv.Sources.Count + L.S(" installs of Live", " установок Live")
                      : "";
                string text = inv.All.Count == 0
                    ? (inv.Error ?? L.S("nothing found", "ничего не нашлось"))
                    : inv.All.Count + L.S(" plug-ins", " плагинов")
                      + (where.Length > 0 ? "   ·   " + where : "");
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (mine != _job) return;
                        _inventory = text;
                        Invalidate();
                    });
                }
                catch { }
            });
        }

        // ------------------------------------------------------------------ установки

        int _installJob;

        /// <summary>
        /// Точный список установок с хотя бы одним плагином — тем же полным разбором,
        /// которым Load считает и строку статуса (PluginInventory.Sources). Провизорный
        /// PluginInventory.Installs() один раз уже наврал: предложил Live 12.0.10
        /// из-за строки «found: Serum» в накопительном журнале сканера, а файла Serum
        /// на диске давно нет — LoadFrom эту запись сам же и выбрасывает при разборе.
        /// Отсюда следующий баг — выбор такой установки молча укорачивает список
        /// плагинов, о чём в дропдауне ничего не видно.
        ///
        /// Считается всегда по умолчанию (Settings свежий, не текущий _s): дропдаун
        /// должен отвечать «что вообще есть на машине», а не зависеть от того, что
        /// сейчас выбрано в фильтре.
        /// </summary>
        void RefreshInstallsAsync()
        {
            int mine = ++_installJob;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                PluginInventory inv = PluginInventory.Load(new Settings());
                List<string> names = new List<string>();
                foreach (string src in inv.Sources)
                {
                    int dot = src.IndexOf(" · ", StringComparison.Ordinal);
                    names.Add(dot < 0 ? src : src.Substring(0, dot));
                }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (mine != _installJob) return;
                        ApplyInstalls(names);
                    });
                }
                catch { }
            });
        }

        void ApplyInstalls(List<string> names)
        {
            string current = _install.SelectedItem;
            _installs.Clear();
            _installs.Add(L.S("All installs", "Все установки"));
            _installs.AddRange(names);

            int at = _installs.IndexOf(current);
            if (at <= 0 && _s.PluginSource.Length > 0)
            {
                // Выбранная установка не даёт ни одного настоящего плагина (или её уже
                // нет в списке) — забываем её, а не оставляем каталог молча коротким.
                _s.PluginSource = "";
                RescanWanted = true;
                DescribeInventoryAsync();
            }
            _install.SetItems(_installs, Math.Max(0, at));
            Invalidate();
        }

        // ------------------------------------------------------------------ раскладка

        bool Folders { get { return _source.SelectedIndex == 1; } }

        readonly List<Row> _rows = new List<Row>();

        sealed class Row
        {
            public string Title = "", Detail = "";
            public Rectangle Rect;
            public bool Section;
        }

        void Relayout() { OnResize(EventArgs.Empty); Invalidate(true); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_close == null) return;

            _rows.Clear();

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int right = Card.Right - pad;
            int w = right - x;
            int h = Sc(Theme.ControlH);
            int y = Card.Top + Sc(66);

            Section(x, ref y, w, L.S("WINDOW", "ОКНО"));
            Line(x, ref y, w, h, _compat,
                 L.S("Windows 10 compatible mode", "Режим совместимости с Windows 10"),
                 L.S("Turns the transparent background off. On Windows 10 the blur is redrawn on every "
                   + "move of the window, and dragging it lags behind the cursor. Applies after a restart.",
                     "Выключает прозрачный фон. На Windows 10 размытие пересчитывается на каждый сдвиг "
                   + "окна, и оно едет за курсором с задержкой. Действует после перезапуска."));

            Section(x, ref y, w, L.S("PLUG-INS", "ПЛАГИНЫ"));
            Line(x, ref y, w, h, _source,
                 L.S("Where the list comes from", "Откуда берётся список"),
                 Folders
                 ? L.S("Walk the folders below. Shows files, not plug-ins: a shell like WaveShell is one "
                     + "file with hundreds of plug-ins inside, and stays one line here.",
                       "Обход папок ниже. Видно файлы, а не плагины: шелл вроде WaveShell — один файл "
                     + "с сотнями плагинов внутри, и здесь он останется одной строкой.")
                 : L.S("Live has already scanned every folder with its own settings and unpacked the "
                     + "shells. All installed versions of Live are read at once, the freshest wins.",
                       "Live уже обошла все папки со своими настройками и развернула шеллы. Читаются "
                     + "сразу все установленные версии Live, свежая важнее."));

            _install.Visible = !Folders;
            if (!Folders)
                Line(x, ref y, w, h, _install, L.S("Live install", "Установка Live"), "");

            foreach (Control c in new Control[] { _vst2On, _vst2Browse, _vst3SysOn, _vst3On, _vst3Browse })
                c.Visible = Folders;

            if (Folders)
            {
                Line(x, ref y, w, h, _vst2On,
                     L.S("Use VST2 Plug-In Custom Folder", "Своя папка VST2"), "");
                Line(x, ref y, w, h, _vst2Browse,
                     L.S("VST2 Plug-In Custom Folder", "Путь к папке VST2"),
                     _s.Vst2CustomPath.Length > 0 ? _s.Vst2CustomPath : L.S("not set", "не задана"));

                Line(x, ref y, w, h, _vst3SysOn,
                     L.S("Use VST3 Plug-In System Folders", "Системные папки VST3"), "");
                Line(x, ref y, w, h, _vst3On,
                     L.S("Use VST3 Plug-In Custom Folder", "Своя папка VST3"), "");
                Line(x, ref y, w, h, _vst3Browse,
                     L.S("VST3 Plug-In Custom Folder", "Путь к папке VST3"),
                     _s.Vst3CustomPath.Length > 0 ? _s.Vst3CustomPath : L.S("not set", "не задана"));
            }

            Line(x, ref y, w, h, _rescan, L.S("Installed right now", "Сейчас установлено"), "");
            _statusRect = new Rectangle(x, y, w - _rescan.Width - Sc(16), Sc(20));
            y += Sc(26);

            Section(x, ref y, w, L.S("TOOLS", "ИНСТРУМЕНТЫ"));
            Line(x, ref y, w, h, _options,
                 L.S("Live's Options.txt", "Options.txt для Live"),
                 L.S("Hidden switches of Live itself — with descriptions, by tickbox.",
                     "Скрытые переключатели самой Live — с описанием, галочкой."));
            Line(x, ref y, w, h, _shortcuts, L.S("Keyboard shortcuts", "Горячие клавиши"), "");

            FitHeight(y + Sc(18) + _close.Height + pad);
            _close.Location = new Point(right - _close.Width, Card.Bottom - pad - _close.Height);
            Invalidate();
        }

        bool _sizing;

        /// <summary>
        /// Подогнать высоту окна под содержимое. Строк тут не поровну: переключение на
        /// обход папок добавляет четыре ряда с путями, и окно постоянной высоты в одном
        /// режиме наполовину пустое, а в другом накрывает кнопку Close последней
        /// строкой (ровно это и было видно на первом снимке).
        /// </summary>
        void FitHeight(int need)
        {
            if (_sizing || _close == null) return;
            int max = Screen.FromControl(this).WorkingArea.Height - Sc(80);
            need = Math.Min(need, max);
            if (Math.Abs(ClientSize.Height - need) <= Sc(2)) return;

            _sizing = true;
            try { ClientSize = new Size(ClientSize.Width, need); }
            finally { _sizing = false; }
        }

        Rectangle _statusRect;

        /// <summary>
        /// Сколько места займёт пояснение. Меряем настоящим переносом, а не «двумя
        /// строками»: пояснения разной длины, и от фиксированной высоты у длинных
        /// срезало последнюю строку — ровно это и было видно на первом снимке окна.
        /// </summary>
        int DetailHeight(string detail, int width)
        {
            if (detail.Length == 0 || width <= 0) return 0;
            return TextRenderer.MeasureText(detail, Theme.FSmall, new Size(width, 0), Chrome.Wrap).Height + Sc(2);
        }

        void Section(int x, ref int y, int w, string title)
        {
            if (_rows.Count > 0) y += Sc(14);
            Row r = new Row();
            r.Section = true;
            r.Title = title;
            r.Rect = new Rectangle(x, y, w, Sc(22));
            _rows.Add(r);
            y += Sc(30);
        }

        /// <summary>Строка настройки: подпись слева, контрол прижат к правому краю.</summary>
        void Line(int x, ref int y, int w, int h, Control c, string title, string detail)
        {
            c.SetBounds(x + w - c.Width, y, c.Width, c is Segmented ? c.Height : h);

            Row r = new Row();
            r.Title = title;
            r.Detail = detail;
            r.Rect = new Rectangle(x, y, w - c.Width - Sc(16), Math.Max(h, c.Height));
            _rows.Add(r);

            y += r.Rect.Height + Sc(4);
            y += DetailHeight(detail, r.Rect.Width);
            y += Sc(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            foreach (Row r in _rows)
            {
                if (r.Section)
                {
                    Chrome.DrawText(g, r.Title, Theme.FLabel, r.Rect, Theme.TextDim,
                                    Chrome.Left | TextFormatFlags.NoClipping);
                    continue;
                }

                Chrome.DrawText(g, r.Title, Theme.FBody, r.Rect, Theme.Text, Chrome.Left);
                if (r.Detail.Length == 0) continue;

                Rectangle d = new Rectangle(r.Rect.X, r.Rect.Bottom + Sc(2),
                                            r.Rect.Width, DetailHeight(r.Detail, r.Rect.Width));
                Chrome.DrawText(g, r.Detail, Theme.FSmall, d, Theme.TextDim, Chrome.Wrap);
            }

            Chrome.DrawText(g, _inventory, Theme.FSmall, _statusRect, Theme.TextDim, Chrome.Left);
        }
    }
}
