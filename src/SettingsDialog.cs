using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        readonly PillToggle _smooth = new PillToggle();

        readonly Segmented _source = new Segmented();
        readonly DropField _install = new DropField();
        readonly PillToggle _vst2On = new PillToggle();
        readonly GlassButton _vst2Browse = new GlassButton();
        readonly PillToggle _vst3SysOn = new PillToggle();
        readonly PillToggle _vst3On = new PillToggle();
        readonly GlassButton _vst3Browse = new GlassButton();
        readonly GlassButton _rescan = new GlassButton();

        readonly GlassButton _shortcuts = new GlassButton();
        readonly GlassButton _openCache = new GlassButton();
        readonly GlassButton _restart = new GlassButton();

        /// <summary>Тумблер прозрачности трогали — предлагаем перезапуск. Раньше об
        /// этом спрашивал системный MessageBox: чужой стиль поверх стеклянного окна, да
        /// ещё и обязательный ответ на случайное нажатие.</summary>
        bool _restartPending;

        /// <summary>Разойдётся ли картинка с настройкой, если не перезапускаться. От
        /// этого зависит только пояснение под строкой: сама строка остаётся, пока
        /// тумблер в этом сеансе трогали, — иначе, вернув тумблер обратно, человек
        /// оставался без способа применить хоть что-нибудь.</summary>
        bool _restartChanges;

        /// <summary>Пересобрать каталог: настройки плагинов поменялись.</summary>
        public bool RescanWanted;

        /// <summary>Показать горячие клавиши после закрытия окна.</summary>
        public bool ShortcutsWanted;

        readonly List<string> _installs = new List<string>();

        public SettingsDialog(Settings s)
        {
            _s = s;
            Caption = "Settings";
            ClientSize = new Size(Sc(700), Sc(660));

            _smooth.Checked = _s.SmoothScroll;
            _smooth.CheckedChanged += delegate
            {
                _s.SmoothScroll = _smooth.Checked;
                Theme.SmoothScroll = _s.SmoothScroll;
            };
            State(_smooth);

            _compat.Checked = _s.DisableGlass;
            _compat.CheckedChanged += delegate { ToggleCompat(); };
            State(_compat);

            _source.SetItems("Live's database", "Scan folders");
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
            _installs.Add("All installs");
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
                string p = Pick("VST2 plug-in folder", _s.Vst2CustomPath);
                if (p != null) { _s.Vst2CustomPath = p; _s.Vst2CustomOn = true; _vst2On.Checked = true; }
            });
            Browse(_vst3Browse, delegate
            {
                string p = Pick("VST3 plug-in folder", _s.Vst3CustomPath);
                if (p != null) { _s.Vst3CustomPath = p; _s.Vst3CustomOn = true; _vst3On.Checked = true; }
            });

            _rescan.Text = "Rescan";
            _rescan.FitToText(16);
            _rescan.Click += delegate { RescanWanted = true; Close(); };
            Controls.Add(_rescan);

            _shortcuts.Text = "Shortcuts…";
            _shortcuts.FitToText(16);
            _shortcuts.Click += delegate { ShortcutsWanted = true; Close(); };
            Controls.Add(_shortcuts);

            _restart.Text = "Restart now";
            _restart.Primary = true;
            _restart.Visible = false;
            _restart.Click += delegate { _s.Save(); Application.Restart(); };
            Controls.Add(_restart);

            _openCache.Text = "Open folder";
            _openCache.FitToText(16);
            _openCache.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                    Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
                }
                catch { }
            };
            Controls.Add(_openCache);

            // Одна ширина на все кнопки правого столбца: три разные ширины давали
            // три разных левых края в одной колонке, и правый столбец рассыпался.
            GlassButton[] rightButtons = new GlassButton[] { _shortcuts, _openCache, _rescan, _restart };
            int buttonW = Sc(128);
            foreach (GlassButton b in rightButtons) buttonW = Math.Max(buttonW, b.Width);
            foreach (GlassButton b in rightButtons) b.Width = buttonW;

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
        /// Переключатель в стиле Apple: чисто геометрический (акцентное ложе + гладкая белая ручка).
        /// </summary>
        void State(PillToggle t)
        {
            t.IsSwitch = true;
            t.Size = new Size(Sc(42), Sc(24));
            Controls.Add(t);
        }

        void Browse(GlassButton b, MethodInvoker click)
        {
            b.Text = "Browse";
            b.FitToText(16);
            b.Click += delegate { click(); RescanWanted = true; Relayout(); DescribeInventoryAsync(); };
            Controls.Add(b);
        }

        string Pick(string title, string current)
        {
            return ModernFolderPicker.PickFolder(Handle, title, current);
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

            // Предложение перезапуститься живёт строкой в этом же окне: случайно
            // щёлкнутый тумблер не должен требовать ответа в чужом диалоге.
            //
            // Строка появляется от любого щелчка и больше не прячется: раньше она
            // считалась по «настройка разошлась с текущим окном» и в обратную сторону
            // исчезала — тумблер вернули на место, и перезапуститься стало нечем,
            // хотя окно так и осталось нарисованным по-старому.
            _restartPending = Glass.Supported();
            _restartChanges = _s.DisableGlass == Glass.Enabled;
            Relayout();
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
            _inventory = "Reading…";
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
                      ? inv.Sources.Count + " installs of Live"
                      : "";
                string text = inv.All.Count == 0
                    ? (inv.Error ?? "nothing found")
                    : inv.All.Count + " plug-ins"
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
            _installs.Add("All installs");
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
            public bool Separator;
        }

        void Relayout() { OnResize(EventArgs.Empty); Invalidate(true); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            _rows.Clear();

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int right = Card.Right - pad;
            int w = right - x;
            int h = Sc(Theme.ControlH);
            int y = Card.Top + Sc(92);

            Section(x, ref y, w, "General");
            Line(x, ref y, w, h, _shortcuts, "Keyboard shortcuts", "");
            Line(x, ref y, w, h, _openCache,
                 "Temporary files",
                 Settings.Dir);

            Separator(x, ref y, w);
            Section(x, ref y, w, "Appearance");
            Line(x, ref y, w, h, _smooth,
                 "Smooth scrolling",
                 "");
            Line(x, ref y, w, h, _compat,
                 "Disable transparency (Win10 Compatible)",
                 "Turns off glass effect if window dragging lags. Applies after restart.");

            _restart.Visible = _restartPending;
            if (_restartPending)
                Line(x, ref y, w, h, _restart,
                     "Restart to apply",
                     _restartChanges
                     ? "Transparency changes only take effect on a fresh start."
                     : "The switch is back where it started — restart only if you want to be sure.");

            Separator(x, ref y, w);
            Section(x, ref y, w, "Plug-ins");
            Line(x, ref y, w, h, _source,
                 "Plug-in source",
                 Folders
                 ? "Direct folder scan (VST2 / VST3 files)."
                 : "Uses internal database from installed Live versions.");

            _install.Visible = !Folders;
            if (!Folders)
                Line(x, ref y, w, h, _install, "Live install", "");

            foreach (Control c in new Control[] { _vst2On, _vst2Browse, _vst3SysOn, _vst3On, _vst3Browse })
                c.Visible = Folders;

            if (Folders)
            {
                Line(x, ref y, w, h, _vst2On,
                     "Custom VST2 folder", "");
                Line(x, ref y, w, h, _vst2Browse,
                     "VST2 folder path",
                     _s.Vst2CustomPath.Length > 0 ? _s.Vst2CustomPath : "not set");

                Line(x, ref y, w, h, _vst3SysOn,
                     "System VST3 folders", "");
                Line(x, ref y, w, h, _vst3On,
                     "Custom VST3 folder", "");
                Line(x, ref y, w, h, _vst3Browse,
                     "VST3 folder path",
                     _s.Vst3CustomPath.Length > 0 ? _s.Vst3CustomPath : "not set");
            }

            Line(x, ref y, w, h, _rescan, "Installed right now", "");
            _statusRect = new Rectangle(x, y - Sc(6), w - _rescan.Width - Sc(16), Sc(28));
            y += Sc(32);

            FitHeight(y + Sc(16) + pad);
            Invalidate();
        }

        bool _sizing;

        /// <summary>
        /// Подогнать высоту окна под содержимое.
        /// </summary>
        void FitHeight(int need)
        {
            if (_sizing) return;
            int max = Screen.FromControl(this).WorkingArea.Height - Sc(80);
            need = Math.Min(need, max);
            if (Math.Abs(ClientSize.Height - need) <= Sc(2)) return;

            _sizing = true;
            try { ClientSize = new Size(ClientSize.Width, need); }
            finally { _sizing = false; }
        }

        Rectangle _statusRect;

        int DetailHeight(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            const TextFormatFlags wrapFlags = TextFormatFlags.Left | TextFormatFlags.Top |
                                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding;
            return TextRenderer.MeasureText(text, Theme.FSmall, new Size(width, 1000), wrapFlags).Height;
        }

        void Section(int x, ref int y, int w, string title)
        {
            Row r = new Row();
            r.Title = title;
            r.Section = true;
            r.Rect = new Rectangle(x, y, w, Sc(20));
            _rows.Add(r);
            y += Sc(28);
        }

        void Separator(int x, ref int y, int w)
        {
            y += Sc(16);
            Row r = new Row();
            r.Separator = true;
            r.Rect = new Rectangle(x, y, w, Sc(1));
            _rows.Add(r);
            y += Sc(16);
        }

        /// <summary>Строка настройки: подпись слева, контрол прижат к правому краю.</summary>
        void Line(int x, ref int y, int w, int h, Control c, string title, string detail)
        {
            int ch = (c is Segmented || (c is PillToggle && ((PillToggle)c).IsSwitch)) ? c.Height : h;
            int cy = y + (h - ch) / 2;
            c.SetBounds(x + w - c.Width, cy, c.Width, ch);

            Row r = new Row();
            r.Title = title;
            r.Detail = detail;
            r.Rect = new Rectangle(x, y, w - c.Width - Sc(16), h);
            _rows.Add(r);

            y += r.Rect.Height;
            if (detail.Length > 0)
            {
                y += Sc(4);
                y += DetailHeight(detail, r.Rect.Width);
            }
            y += Sc(20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            const TextFormatFlags leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                              TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoClipping;
            const TextFormatFlags wrapFlags = TextFormatFlags.Left | TextFormatFlags.Top |
                                              TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoClipping;

            foreach (Row r in _rows)
            {
                if (r.Separator)
                {
                    using (Pen pen = new Pen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1f))
                        g.DrawLine(pen, r.Rect.X, r.Rect.Y, r.Rect.Right, r.Rect.Y);
                    continue;
                }

                if (r.Section)
                {
                    Chrome.DrawText(g, r.Title, Theme.FLabel, r.Rect, Theme.TextDim, leftFlags);
                    continue;
                }

                Chrome.DrawText(g, r.Title, Theme.FBody, r.Rect, Theme.Text, leftFlags);
                if (r.Detail.Length == 0) continue;

                Rectangle d = new Rectangle(r.Rect.X, r.Rect.Bottom + Sc(4),
                                            r.Rect.Width, DetailHeight(r.Detail, r.Rect.Width) + Sc(8));
                Chrome.DrawText(g, r.Detail, Theme.FSmall, d, Theme.TextDim, wrapFlags);
            }

            Chrome.DrawText(g, _inventory, Theme.FSmall, _statusRect, Theme.TextDim, leftFlags);
        }
    }
}
