using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace Reel
{
    /// <summary>Текущий .als на диске — верхняя строка списка, «то, что ещё не снято».</summary>
    internal sealed class WorkingItem
    {
        public string Path = "";
        public bool Dirty;
        public bool Known;      // удалось ли вообще посчитать хеш
    }

    /// <summary>
    /// Версии одного проекта: слева список снимков, справа разница с предыдущим и
    /// зависимости выбранного.
    ///
    /// Снимки делаются только руками — кнопкой Snapshot или Ctrl+S. Автосъёмки нет
    /// намеренно: сет весит полмегабайта, а сохраняют его в работе десятки раз за
    /// вечер, и склад растёт быстрее, чем от него есть польза. Снимок, сделанный
    /// руками, стоит всех промежуточных вместе взятых, потому что человек знал, зачем
    /// его делает. Поэтому в списке нет ни деления на «свои/авто», ни колонки с типом:
    /// делить нечего.
    ///
    /// Окно построено на той же оснастке, что и весь Alive — GlassDialog, Theme,
    /// RowListView, — а не на своей.
    ///
    /// Всё чтение .als уходит в фоновый поток: сет распаковывается в несколько мегабайт
    /// XML, и делать это в потоке интерфейса — значит подвешивать окно на каждом клике
    /// по списку.
    /// </summary>
    internal sealed class ReelWindow : GlassDialog
    {
        // ------------------------------------------------------------------ состояние

        string _alsPath = "", _projectDir = "";
        SnapshotStore _store;
        DependencyReport _report;

        LiveEnvironment _env;
        PluginInventory _inv;

        int _job;                 // поколение фонового расчёта: ответы от старых игнорируем
        string _status = "";
        string _compared = "";

        // ------------------------------------------------------------------ контролы

        readonly FieldBox _search = new FieldBox();
        readonly GlassButton _reveal = new GlassButton();
        readonly GlassButton _restore = new GlassButton();
        readonly GlassButton _rescue = new GlassButton();
        readonly GlassButton _snap = new GlassButton();

        readonly RowListView _list = new RowListView();
        readonly ReelLines _diff = new ReelLines();
        readonly ReelLines _deps = new ReelLines();

        Rectangle _rDiffLabel, _rDepsLabel, _rStatus, _rStore, _rEmpty;

        public ReelWindow(string startPath)
        {
            Caption = "Forks";
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;

            Build();
            LoadEnvironmentAsync();

            if (!string.IsNullOrEmpty(startPath) && File.Exists(startPath)) Open(startPath);
        }

        // ------------------------------------------------------------------ сборка

        void Build()
        {
            // Ищем по тому, что человек сам написал, и по идентификатору снимка: имя
            // сета во всех строках одно и то же, дата видна и так, а «где я это
            // сохранил» вспоминается либо словами, либо восемью знаками хеша, которые
            // стоят и в имени файла в проводнике.
            _search.ShowClear = true;
            _search.Cue = "Search comments and ids";
            _search.Box.TextChanged += delegate { FillHistory(0); };
            Controls.Add(_search);

            _reveal.Text = "Show file";
            _reveal.FitToText(16);
            _reveal.Click += delegate { RevealSelected(); };
            Controls.Add(_reveal);

            _restore.Text = "Restore";
            _restore.FitToText(18);
            _restore.Click += delegate { RestoreSelected(); };
            Controls.Add(_restore);

            // Restore чинит откатом на старую версию, Rescue — диагностикой текущей: два
            // разных ответа на один и тот же повод открыть Reel («сет не открывается»),
            // поэтому кнопки стоят рядом.
            _rescue.Text = "Rescue";
            _rescue.FitToText(18);
            _rescue.Click += delegate { RescueCurrent(); };
            Controls.Add(_rescue);

            _snap.Text = "Snapshot";
            _snap.Primary = true;
            _snap.FitToText(20);
            _snap.Click += delegate { TakeSnapshot(); };
            Controls.Add(_snap);

            _list.SelectionChanged += delegate { Recompute(); };

            // Двойной клик показывает файл, а НЕ откатывает на него: откат переписывает
            // рабочий сет, и вешать такое на движение, которым в таблицах обычно
            // «открывают», — способ однажды потерять вечер работы.
            _list.ItemActivated += delegate { RevealSelected(); };
            _list.RowRightClicked += RowMenu;
            Controls.Add(_list);

            _diff.EmptyText = "Nothing selected";
            Controls.Add(_diff);

            _deps.EmptyText = "Nothing selected";
            Controls.Add(_deps);
        }

        // ------------------------------------------------------------------ раскладка

        bool _sized;

        /// <summary>
        /// Размер окна — здесь, а не в конструкторе: только теперь известно, на каком
        /// мониторе оно откроется, и есть куда его ужать, если экран мал.
        ///
        /// Числа сырые, без Sc(), — ровно как ClientSize у MainForm. Так и надо:
        /// Sc() во всём Alive считается от Control.DeviceDpi, а он в этой сборке
        /// .NET Framework отдаёт 96 даже после создания хендла (замерено на машине со
        /// 125%: Graphics.DpiX = 120, DeviceDpi = 96). То есть Sc() — множитель ×1, и
        /// вся раскладка Alive живёт в физических пикселях, тогда как шрифты, заданные
        /// в пунктах, растут вместе с масштабом экрана сами. Отмасштабируй тут окно
        /// «правильно» — и оно разъедется с главным окном, стоящим рядом.
        /// Отсюда же и ширины колонок ниже: они с запасом под кегль крупнее номинала.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_sized) return;
            _sized = true;

            Rectangle work = Screen.FromHandle(Handle).WorkingArea;
            ClientSize = new Size(Math.Min(1500, work.Width - 60),
                                  Math.Min(920, work.Height - 60));
            MinimumSize = new Size(Math.Min(1000, work.Width),
                                   Math.Min(620, work.Height));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_list == null) return;

            int pad = Sc(Theme.Pad);
            int h = Sc(Theme.ControlH);
            int left = Card.Left + pad;
            int right = Card.Right - pad;
            int y = Card.Top + Sc(62);

            // Действия — справа, в порядке важности справа налево.
            _snap.SetBounds(right - _snap.Width, y, _snap.Width, h);
            _restore.SetBounds(_snap.Left - Sc(10) - _restore.Width, y, _restore.Width, h);
            _rescue.SetBounds(_restore.Left - Sc(10) - _rescue.Width, y, _rescue.Width, h);
            _reveal.SetBounds(_rescue.Left - Sc(10) - _reveal.Width, y, _reveal.Width, h);

            // Поиск слева, во всю оставшуюся ширину до кнопок, но не шире разумного:
            // растянутое на пол-окна поле ввода выглядит как ошибка вёрстки.
            _search.SetBounds(left, y, Math.Max(Sc(160), Math.Min(Sc(340), _reveal.Left - Sc(20) - left)), h);

            // Содержимое
            int top = y + h + Sc(20);
            int footerH = Sc(28);
            int bottom = Card.Bottom - pad - footerH - Sc(10);

            int rightW = Math.Max(Sc(380), (int)((right - left) * 0.36f));
            int listW = right - left - rightW - Sc(24);

            _list.SetBounds(left, top, listW, Math.Max(Sc(120), bottom - top));

            int rx = left + listW + Sc(24);
            int labelH = Sc(22);

            // Разница сверху, зависимости снизу: разница отвечает «что я сделал», а
            // зависимости — «откроется ли это вообще». Первый вопрос задают чаще.
            int paneH = (bottom - top - Sc(20)) / 2;
            int diffTop = top;
            _rDiffLabel = new Rectangle(rx, diffTop, rightW, labelH);
            _diff.SetBounds(rx, diffTop + labelH + Sc(6), rightW,
                            Math.Max(Sc(60), paneH - labelH - Sc(6)));

            int depsTop = _diff.Bottom + Sc(20);
            _rDepsLabel = new Rectangle(rx, depsTop, rightW, labelH);
            _deps.SetBounds(rx, depsTop + labelH + Sc(6), rightW,
                            Math.Max(Sc(60), bottom - (depsTop + labelH + Sc(6))));

            int fy = Card.Bottom - pad - footerH;
            _rStore = new Rectangle(left, fy, listW, footerH);
            _rStatus = new Rectangle(rx, fy, rightW, footerH);
            _rEmpty = new Rectangle(left, top, right - left, Sc(60));

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            // Заголовок окна дополняем именем сета — так видно, чью историю смотришь,
            // не переводя взгляд на список.
            if (_alsPath.Length > 0)
            {
                int pad = Sc(Theme.Pad);
                Size cap = TextRenderer.MeasureText(Caption, Theme.FTitle);
                Chrome.DrawText(g, Path.GetFileName(_alsPath) + "   ·   " + _projectDir, Theme.FSmall,
                                new Rectangle(Card.Left + pad + cap.Width + Sc(14), Card.Top + Sc(24),
                                              Card.Width - cap.Width - Sc(120), Sc(28)),
                                Theme.TextDim,
                                Chrome.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
            }

            SectionLabel(g, _rDiffLabel, "WHAT CHANGED", _compared, 0);
            SectionLabel(g, _rDepsLabel, "NEEDED TO OPEN", "", 0);

            if (_store != null)
            {
                // Пустая история — не повод писать «0 snapshots»: это состояние, из
                // которого человеку нужно знать выход, а не его название. Место то же
                // самое, потому что рисует его само окно, а не список: всё, что
                // нарисовано в границах RowListView, он закрывает своей отрисовкой.
                string left = _store.Entries.Count == 0
                    ? "No snapshots yet — press Snapshot, or turn on Auto-save"
                    : _store.Describe();

                Chrome.DrawText(g, left, Theme.FSmall, _rStore, Theme.TextDim,
                                Chrome.Left | TextFormatFlags.VerticalCenter);
            }

            Chrome.DrawText(g, _status, Theme.FSmall, _rStatus, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (_alsPath.Length == 0)
            {
                Chrome.DrawText(g, "Pick a project in the catalogue and press Forks",
                                Theme.FBody, _rEmpty, Theme.TextDim,
                                Chrome.Left | TextFormatFlags.VerticalCenter);
            }
        }

        /// <summary>
        /// Подпись раздела и приглушённое уточнение за ней — как в панели Alive.
        /// reserve — сколько места справа занято кнопкой этого раздела, если она там есть.
        /// </summary>
        void SectionLabel(Graphics g, Rectangle r, string title, string detail, int reserve)
        {
            if (r.Width <= 0) return;
            Chrome.DrawText(g, title, Theme.FLabel, r, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoClipping);
            if (detail.Length == 0) return;

            int used = TextRenderer.MeasureText(g, title, Theme.FLabel).Width;
            Rectangle dr = new Rectangle(r.X + used + Sc(14), r.Y,
                                         Math.Max(0, r.Width - used - Sc(14) - reserve), r.Height);
            Chrome.DrawText(g, detail, Theme.FSmall, dr, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ------------------------------------------------------------------ окно

        const int WM_NCHITTEST = 0x0084;

        /// <summary>
        /// Окно тянется за края. GlassDialog сам умеет только таскать за шапку — ему
        /// хватает, диалоги там маленькие и фиксированные, а тут таблица и два списка,
        /// и размер приходится подгонять под экран.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCHITTEST) return;

            int result = (int)m.Result;
            if (result != 1 && result != 2) return;      // 1 — клиентская область, 2 — шапка

            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));

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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool typing = ActiveControl is TextBox;

            if (e.Control && e.KeyCode == Keys.S && !typing) { TakeSnapshot(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.F) { _search.Box.Focus(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.R && !typing) { RescueCurrent(); e.Handled = true; return; }
            if (e.KeyCode == Keys.F5 && !typing) { ReloadStore(); e.Handled = true; return; }

            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------------ окружение

        void LoadEnvironmentAsync()
        {
            Status("Reading Live configuration…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                LiveEnvironment env = LiveEnvironment.Detect();
                PluginInventory inv = PluginInventory.Load();
                Post(delegate
                {
                    _env = env;
                    _inv = inv;
                    Status(inv.IsEmpty
                        ? "Live has no plugin database — plugin checks will be unknown"
                        : inv.All.Count + " plugins installed");
                    if (_store != null) Recompute();
                });
            });
        }

        // ------------------------------------------------------------------ проект

        public void Open(string alsPath)
        {
            _alsPath = alsPath;
            _projectDir = Path.GetDirectoryName(alsPath);

            _store = SnapshotStore.Open(_projectDir);
            FillHistory(0);

            if (_store.Entries.Count == 0)
                Status("No versions yet — press Snapshot to keep this one");

            Invalidate();
        }

        void ReloadStore()
        {
            if (_projectDir.Length == 0) return;
            _store = SnapshotStore.Open(_projectDir);
            FillHistory(_list.SelectedIndex);
        }

        // ------------------------------------------------------------------ список

        /// <summary>
        /// Проходит ли снимок через поиск. Ищем по сообщению и по короткому
        /// идентификатору — по тому же, что видно в списке и в имени файла на диске.
        /// Пустой запрос пропускает всё.
        /// </summary>
        bool Passes(Snapshot s)
        {
            string q = _search.Box.Text.Trim();
            if (q.Length == 0) return true;
            return s.Message.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || s.Hash.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Колонка с именем сета нужна не всегда: в обычном проекте один .als, и она
        /// повторяет одно и то же во всех строках, отнимая место у сообщения — а
        /// сообщение тут главное. Показываем её, только когда сетов в истории больше
        /// одного (типичный случай — «rrobin.als» и «rrobin12.4.3.als» после переезда
        /// проекта на новую версию Live).
        /// </summary>
        bool _showSet;

        void BuildColumns(bool showSet)
        {
            // Ширины с запасом: кегль на масштабированном экране крупнее номинала, а
            // сами колонки Alive считает в физических пикселях (см. OnHandleCreated).
            // «531.5 KB» и восьмизначный хеш должны помещаться целиком — обрезанный
            // размер или хеш бесполезны, их читают ради последних знаков.
            List<Column> cols = new List<Column>();
            cols.Add(new Column("When", 165));
            cols.Add(new Column("Message", 0));
            if (showSet) cols.Add(new Column("Set", 180));
            cols.Add(new Column("Size", 135) { Right = true });
            cols.Add(new Column("Id", 135) { Font = Theme.FSmall, Color = Theme.TextDim });

            _list.SetColumns(cols.ToArray());
            _showSet = showSet;
        }

        string[] Cells(string when, string message, string set, string size, string id)
        {
            return _showSet
                ? new string[] { when, message, set, size, id }
                : new string[] { when, message, size, id };
        }

        /// <summary>keepIndex = -1 — сохранить нынешний выбор.</summary>
        void FillHistory(int keepIndex)
        {
            if (_store == null) return;
            if (keepIndex < 0) keepIndex = Math.Max(0, _list.SelectedIndex);

            bool multiSet = false;
            string only = null;
            foreach (Snapshot s in _store.Entries)
            {
                if (only == null) { only = s.Source; continue; }
                if (!string.Equals(only, s.Source, StringComparison.OrdinalIgnoreCase)) { multiSet = true; break; }
            }
            BuildColumns(multiSet);

            List<RowData> rows = new List<RowData>();

            // Текущий файл стоит первым и поиску не подчиняется: он не снимок, а то,
            // с чем снимки сравнивают, — прятать его за пустым запросом бессмысленно.
            if (_alsPath.Length > 0 && File.Exists(_alsPath))
            {
                WorkingItem w = new WorkingItem();
                w.Path = _alsPath;
                string hash = SnapshotStore.HashFile(_alsPath);
                Snapshot last = _store.LastOf(Path.GetFileName(_alsPath));
                w.Known = hash != null;
                w.Dirty = hash != null && (last == null || last.Hash != hash);

                RowData r = new RowData();
                r.Cells = Cells(
                    "Working file",
                    w.Dirty ? "not snapshotted yet"
                            : (w.Known ? "same as latest snapshot" : "cannot read the file"),
                    Path.GetFileName(_alsPath), "", "");
                r.Tag = w;
                r.CanPlay = false;
                rows.Add(r);
            }

            foreach (Snapshot s in _store.Entries)
            {
                if (!Passes(s)) continue;

                RowData r = new RowData();
                r.Cells = Cells(
                    s.Time.ToString("d MMM  HH:mm", CultureInfo.CurrentCulture),
                    s.Message,
                    s.Source,
                    SnapshotStore.Size(s.Size),
                    s.Short);
                r.Tag = s;
                r.CanPlay = false;
                rows.Add(r);
            }

            _list.SetRows(rows, false);
            if (rows.Count > 0) _list.SelectIndex(Math.Min(keepIndex, rows.Count - 1));

            UpdateButtons();
            Invalidate();
        }

        void UpdateButtons()
        {
            bool open = _store != null && _alsPath.Length > 0;
            _snap.Enabled = open;
            _restore.Enabled = open && _list.Selected != null && _list.Selected.Tag is Snapshot;
            _rescue.Enabled = open;
            _reveal.Enabled = SelectedFile() != null;
        }

        // ------------------------------------------------------------------ действия

        void TakeSnapshot()
        {
            if (_store == null || _alsPath.Length == 0) return;
            using (SnapshotDialog d = new SnapshotDialog(Path.GetFileName(_alsPath), ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;

                Snapshot s = _store.CaptureManual(_alsPath, d.Value);
                if (s == null)
                {
                    Status(string.IsNullOrEmpty(_store.Error)
                        ? "Nothing changed since the last snapshot"
                        : "Could not save: " + _store.Error);
                    FillHistory(-1);
                    return;
                }

                Status("Snapshot " + s.Short + (s.Message.Length > 0 ? " — " + s.Message : ""));

                // Показываем именно его: под открытым поиском новая запись могла бы в
                // список и не попасть, а после сохранения человек ждёт увидеть её.
                _search.Box.Text = "";
                FillHistory(1);
            }
        }

        void RestoreSelected()
        {
            RowData row = _list.Selected;
            Snapshot s = row != null ? row.Tag as Snapshot : null;
            if (s == null || _store == null) return;

            string target = Path.Combine(_projectDir, s.Source);
            string question = "Replace" + "\n\n    " + s.Source + "\n\n"
                            + "with the version from " + s.Time.ToString("d MMM yyyy, HH:mm") + "?\n\n"
                            + "The current file goes into history first." + "\n\n"
                            + "Close the set in Live before restoring.";

            if (MessageBox.Show(this, question, "Restore version",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            string note;
            bool ok = _store.Restore(s, target, out note);
            Status(ok ? "Restored " + s.Short + " — " + note
                      : "Restore failed: " + note);
            FillHistory(0);
        }

        /// <summary>
        /// Помощник по восстановлению для текущего файла — не для выбранного снимка:
        /// снимок в складе Reel это блоб на диске, а не рабочий .als, который открывает
        /// Live. Работает как Restore — оригинал не трогает, — только чинит не откатом
        /// на старую версию, а поиском плагина, который ломает нынешнюю.
        /// </summary>
        void RescueCurrent()
        {
            if (_alsPath.Length == 0) return;
            if (!File.Exists(_alsPath)) { Status("The file is gone: " + _alsPath); return; }

            SetEntry set = new SetEntry();
            set.Path = _alsPath;
            set.Name = Path.GetFileNameWithoutExtension(_alsPath);

            // Окружение читается в фоне при открытии окна (LoadEnvironmentAsync) и обычно
            // уже готово; если человек успел кликнуть раньше — читаем на месте, чем ждать.
            PluginInventory inv = _inv ?? PluginInventory.Load();

            using (RescueDialog d = new RescueDialog(set, inv))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                {
                    Status("Saved " + Path.GetFileName(d.Produced));
                    // Спасённая копия — новый .als в папке проекта; список слева её не
                    // подхватит сам, пока не перечитать склад.
                    ReloadStore();
                }
            }
        }

        /// <summary>
        /// Показать в проводнике: у снимка — его файл в складе, у строки текущего
        /// файла — сам сет. Ровно тем же способом, что и каталог Alive (RevealSet).
        /// </summary>
        void RevealSelected()
        {
            string path = SelectedFile();
            if (path == null) return;

            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (_projectDir.Length > 0 && Directory.Exists(_projectDir))
                    Process.Start("explorer.exe", "\"" + _projectDir + "\"");
                else { Status("The file is gone: " + path); return; }

                Status(path);
            }
            catch (Exception ex) { Status(ex.Message); }
        }

        string SelectedFile()
        {
            RowData row = _list.Selected;
            if (row == null) return null;

            Snapshot s = row.Tag as Snapshot;
            if (s != null) return s.ObjectPath;

            WorkingItem w = row.Tag as WorkingItem;
            return w != null ? w.Path : null;
        }

        void RowMenu(int index, Point where)
        {
            if (index < 0 || index >= _list.Rows.Count) return;
            _list.SelectIndex(index);

            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem show = new ToolStripMenuItem("Show file in Explorer");
            show.Click += delegate { RevealSelected(); };
            m.Items.Add(show);

            bool isSnapshot = _list.Rows[index].Tag is Snapshot;
            ToolStripMenuItem restore = new ToolStripMenuItem("Restore this version…");
            restore.Enabled = isSnapshot;
            restore.Click += delegate { RestoreSelected(); };
            m.Items.Add(restore);

            m.Show(_list, where);
        }

        // ------------------------------------------------------------------ расчёт

        void Recompute()
        {
            UpdateButtons();
            if (_store == null) return;

            RowData row = _list.Selected;
            object sel = row != null ? row.Tag : null;
            if (sel == null) { _diff.Clear(); _deps.Clear(); return; }

            string newPath, title;
            Snapshot older;

            WorkingItem w = sel as WorkingItem;
            if (w != null)
            {
                newPath = w.Path;
                older = _store.LastOf(Path.GetFileName(w.Path));
                title = "working file";
            }
            else
            {
                Snapshot s = (Snapshot)sel;
                newPath = s.ObjectPath;
                older = _store.Previous(s);
                title = s.Time.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
            }

            string oldPath = older != null ? older.ObjectPath : null;
            _compared = (older != null
                            ? older.Time.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)
                            : "nothing earlier")
                      + "  →  " + title;

            string displayName = Path.GetFileName(w != null ? w.Path : ((Snapshot)sel).Source);

            _diff.SetItems(new LineItem[] { new LineItem("reading…", Theme.TextDim) });
            _deps.SetItems(new LineItem[] { new LineItem("reading…", Theme.TextDim) });
            Invalidate();

            int job = ++_job;
            string projectDir = _projectDir;
            LiveEnvironment env = _env;
            PluginInventory inv = _inv;

            ThreadPool.QueueUserWorkItem(delegate
            {
                SetModel a = oldPath != null ? SetModel.Read(oldPath) : null;
                SetModel b = SetModel.Read(newPath);
                List<DiffLine> lines = SetDiff.Compare(a, b);

                DependencyReport rep = env != null
                    ? DependencyReport.Build(newPath, projectDir, env, inv, displayName)
                    : null;

                Post(delegate
                {
                    if (job != _job) return;          // пока считали, выбрали другое

                    _diff.SetItems(DiffLines(lines));
                    _report = rep;
                    _deps.SetItems(DepLines(rep));

                    if (rep != null && b.Error == null) Status(Summary(b, rep));
                    UpdateButtons();
                    Invalidate();
                });
            });
        }

        static List<LineItem> DiffLines(List<DiffLine> lines)
        {
            List<LineItem> res = new List<LineItem>();
            foreach (DiffLine l in lines)
            {
                Color c = l.Kind == DiffKind.Added ? Theme.Green
                        : l.Kind == DiffKind.Removed ? Theme.Red
                        : l.Kind == DiffKind.Changed ? ReelTheme.Amber
                        : l.Kind == DiffKind.Header ? Theme.Text
                        : Theme.TextDim;

                LineItem it = new LineItem(l.Text, c);
                it.Indent = l.Indent;
                if (l.Kind == DiffKind.Header) { it.Font = Theme.FTitle; it.Rule = true; }
                res.Add(it);
            }
            return res;
        }

        static List<LineItem> DepLines(DependencyReport rep)
        {
            List<LineItem> res = new List<LineItem>();
            if (rep == null) return res;

            if (rep.Error != null)
            {
                res.Add(new LineItem("Cannot read set: " + rep.Error, Theme.Red));
                return res;
            }

            foreach (DepItem i in rep.Items)
            {
                Color c = i.State == DepState.Missing ? Theme.Red
                        : i.State == DepState.Unknown ? ReelTheme.Amber
                        : Theme.Green;

                if (i.IsHeader)
                {
                    LineItem head = new LineItem(i.Name, Theme.Text);
                    head.Font = Theme.FTitle;
                    head.Rule = true;
                    res.Add(head);
                    continue;
                }

                LineItem it = new LineItem(i.Name + (i.Count > 1 ? "  ×" + i.Count : ""),
                                           i.State == DepState.Ok ? Theme.Text : c);
                it.DotColor = c;
                it.Trailing = i.Detail;
                res.Add(it);
            }
            return res;
        }

        static string Summary(SetModel m, DependencyReport rep)
        {
            string s = m.Tracks.Count + " tracks, " + m.TotalClips + " clips, "
                     + m.DeviceCount + " devices";
            if (m.Tempo > 0) s += ", " + m.Tempo.ToString("0.##", CultureInfo.InvariantCulture) + " BPM";
            if (m.Key.Length > 0) s += ", " + m.Key;

            if (rep.MissingSamples > 0 || rep.MissingPlugins > 0 || rep.MissingPacks > 0)
            {
                List<string> parts = new List<string>();
                if (rep.MissingSamples > 0) parts.Add(rep.MissingSamples + " samples");
                if (rep.MissingPlugins > 0) parts.Add(rep.MissingPlugins + " plugins");
                if (rep.MissingPacks > 0) parts.Add(rep.MissingPacks + " packs");
                s += "   ·   missing: " + string.Join(", ", parts.ToArray());
            }
            else s += "   ·   everything on this machine";

            return s;
        }

        // ------------------------------------------------------------------ мелочи

        void Status(string text)
        {
            _status = text;
            if (_rStatus.Width > 0) Invalidate(_rStatus);
        }

        /// <summary>Выполнить в потоке интерфейса, если окно ещё живо.</summary>
        void Post(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch { }
        }
    }
}
