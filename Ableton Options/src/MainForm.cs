using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonOptions
{
    public sealed class MainForm : Form
    {
        readonly DropField _version = new DropField();
        readonly DropField _category = new DropField();
        readonly FieldBox _search = new FieldBox();
        readonly PillToggle _onlyOn = new PillToggle();
        readonly PillToggle _internal = new PillToggle();
        readonly PillToggle _deprecated = new PillToggle();
        readonly Segmented _lang = new Segmented();
        readonly CloseButton _close = new CloseButton();

        readonly GlassButton _browse = new GlassButton();
        readonly GlassButton _reveal = new GlassButton();
        readonly GlassButton _clear = new GlassButton();
        readonly GlassButton _custom = new GlassButton();
        readonly GlassButton _preview = new GlassButton();
        readonly GlassButton _reload = new GlassButton();
        readonly GlassButton _save = new GlassButton();

        readonly OptionListView _list = new OptionListView();

        readonly List<LiveProfile> _profiles = new List<LiveProfile>();
        readonly List<OptionLine> _customLines = new List<OptionLine>();
        readonly List<string> _foreign = new List<string>();

        LiveProfile _profile;
        int _rejectedCount;
        bool _loading, _dirty;
        string _statusEn = "", _statusRu = "", _count = "";
        bool _statusIsError;

        Rectangle _card, _rTitle, _rSub, _rPath, _rCount, _rStatus;
        int _sepY;

        public MainForm()
        {
            Text = "Ableton Options";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 840);
            MinimumSize = new Size(940, 620);
            BackColor = Color.FromArgb(0x14, 0x14, 0x17);
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            BuildControls();
            ApplyTexts();
            L.Changed += delegate { ApplyTexts(); ApplyFilter(); LayoutAll(); Invalidate(true); };

            LoadProfiles();
        }

        // --------------------------------------------------------- построение

        void BuildControls()
        {
            _lang.SetItems("EN", "RU");
            _lang.SelectedIndex = L.Ru ? 1 : 0;
            _lang.SelectedChanged += delegate { L.SetRussian(_lang.SelectedIndex == 1); };
            Controls.Add(_lang);

            _close.Click += delegate { Close(); };
            Controls.Add(_close);

            _version.SelectedChanged += delegate { OnProfileChanged(); };
            Controls.Add(_version);

            _search.Glyph = "⌕";
            _search.Box.TextChanged += delegate { ApplyFilter(); };
            Controls.Add(_search);

            _category.SelectedChanged += delegate { ApplyFilter(); };
            Controls.Add(_category);

            foreach (PillToggle p in new PillToggle[] { _onlyOn, _internal, _deprecated })
            {
                p.CheckedChanged += delegate { ApplyFilter(); };
                Controls.Add(p);
            }

            _browse.Quiet = true; _browse.Click += delegate { BrowseForFolder(); }; Controls.Add(_browse);
            _reveal.Quiet = true; _reveal.Click += delegate { OpenInExplorer(); }; Controls.Add(_reveal);

            _clear.Click += delegate { ClearAll(); }; Controls.Add(_clear);
            _custom.Click += delegate { EditCustom(); }; Controls.Add(_custom);
            _preview.Click += delegate { ShowPreview(); }; Controls.Add(_preview);
            _reload.Click += delegate { ReloadFromDisk(true); }; Controls.Add(_reload);
            _save.Primary = true; _save.Click += delegate { Save(); }; Controls.Add(_save);

            _list.Changed += delegate { MarkDirty(); };
            Controls.Add(_list);
        }

        void ApplyTexts()
        {
            _browse.Text = L.S("Choose folder…", "Выбрать папку…");
            _reveal.Text = L.S("Reveal in Explorer", "Показать в проводнике");
            _clear.Text = L.S("Clear all", "Снять все");
            _custom.Text = L.S("Custom options…", "Свои опции…");
            _preview.Text = L.S("Preview file", "Показать файл");
            _reload.Text = L.S("Reload", "Перечитать");
            _save.Text = L.S("Save Options.txt", "Сохранить Options.txt");

            _onlyOn.Text = L.S("Enabled only", "Только включённые");
            _internal.Text = L.S("Internal", "Служебные");
            _deprecated.Text = L.S("Deprecated", "Устаревшие");

            // Подписи у поля нет: в самом значении уже написано «Live 12.4.3».

            foreach (GlassButton b in new GlassButton[] { _browse, _reveal, _clear, _custom, _preview, _reload, _save })
                b.FitToText(b.Quiet ? 10 : 16);
            foreach (PillToggle p in new PillToggle[] { _onlyOn, _internal, _deprecated })
                p.FitToText();

            List<string> cats = new List<string>();
            cats.Add(L.S("All categories", "Все категории"));
            foreach (string c in Catalog.Categories) cats.Add(Catalog.CatName(c));
            int keep = _category.SelectedIndex < 0 ? 0 : _category.SelectedIndex;
            _category.SetItems(cats, keep);

            SetSearchCue();
            _list.Relayout(true);
        }

        void SetSearchCue()
        {
            if (!_search.Box.IsHandleCreated) return;
            const int EM_SETCUEBANNER = 0x1501;
            SendMessage(_search.Box.Handle, EM_SETCUEBANNER, (IntPtr)1,
                        L.S("Search options…", "Поиск по опциям…"));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // ---------------------------------------------------------- раскладка

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        void LayoutAll()
        {
            // Подложка одна: окно и есть карточка, внешнего поля вокруг неё нет.
            _card = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            int pad = Sc(26);
            int left = _card.Left + pad, right = _card.Right - pad;
            int y = _card.Top + pad;

            // --- строка 1: заголовок, язык, закрыть
            _close.Location = new Point(right - _close.Width, y);
            _lang.Location = new Point(_close.Left - Sc(12) - _lang.Width, y + Sc(1));

            Size th = TextRenderer.MeasureText("Options.txt", Theme.FHead);
            _rTitle = new Rectangle(left, y - Sc(2), th.Width, th.Height + Sc(4));
            _rSub = new Rectangle(_rTitle.Right + Sc(14), _rTitle.Top,
                                  Math.Max(0, _lang.Left - Sc(20) - (_rTitle.Right + Sc(14))), _rTitle.Height);

            // --- строка 2: версия Live, путь, кнопки
            y += Sc(46);
            _version.SetBounds(left, y, Sc(230), Sc(34));
            _browse.Location = new Point(right - _browse.Width, y + (Sc(34) - _browse.Height) / 2);
            _reveal.Location = new Point(_browse.Left - Sc(6) - _reveal.Width, _browse.Top);
            _rPath = new Rectangle(_version.Right + Sc(14), y,
                                   Math.Max(0, _reveal.Left - Sc(14) - (_version.Right + Sc(14))), Sc(34));

            // --- строка 3: поиск, категория, фильтры, счётчик
            y += Sc(46);
            int pillsW = _onlyOn.Width + _internal.Width + _deprecated.Width + Sc(16);
            Size cs = TextRenderer.MeasureText("8888 / 8888  ·  888", Theme.FLabel);
            int searchW = Math.Max(Sc(200), right - left - Sc(240) - pillsW - cs.Width - Sc(48));

            _search.SetBounds(left, y, searchW, Sc(34));
            _category.SetBounds(_search.Right + Sc(10), y, Sc(230), Sc(34));

            int px = _category.Right + Sc(16);
            foreach (PillToggle p in new PillToggle[] { _onlyOn, _internal, _deprecated })
            {
                p.Location = new Point(px, y + (Sc(34) - p.Height) / 2);
                px = p.Right + Sc(8);
            }
            _rCount = new Rectangle(right - cs.Width, y, cs.Width, Sc(34));

            // --- разделитель и список
            y += Sc(46);
            _sepY = y;

            int footerH = Sc(52);
            int listTop = y + Sc(10);
            int listBottom = _card.Bottom - pad - footerH;
            _list.SetBounds(left - Sc(8), listTop, right - left + Sc(16), Math.Max(Sc(80), listBottom - listTop));

            // --- подвал
            int by = _card.Bottom - pad - _save.Height;
            int bx = right;
            foreach (GlassButton b in new GlassButton[] { _save, _reload, _preview, _custom, _clear })
            {
                bx -= b.Width;
                b.Location = new Point(bx, by);
                bx -= Sc(8);
            }
            _rStatus = new Rectangle(left, by, Math.Max(0, bx - Sc(10) - left), _save.Height);

            _list.Relayout(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
            Invalidate(true);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            Invalidate(true);   // подложка смещается вместе с окном
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            EnableRoundCorners();
            SetSearchCue();
            LayoutAll();
            Invalidate(true);
        }

        // ------------------------------------------------------------ отрисовка

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Backdrop.Paint(g, ClientRectangle, Bounds);
            using (SolidBrush b = new SolidBrush(Theme.CardFill))
                g.FillRectangle(b, ClientRectangle);
            Theme.Smooth(g);

            TextRenderer.DrawText(g, "Options.txt", Theme.FHead, _rTitle, Theme.Text, Chrome.Left);
            TextRenderer.DrawText(g,
                L.S("Hidden Ableton Live settings · changes apply after restarting Live",
                    "Скрытые настройки Ableton Live · применяются после перезапуска Live"),
                Theme.FLabel, _rSub, Theme.TextFaint, Chrome.Left);

            string path = _profile == null ? "" : _profile.OptionsPath;
            TextRenderer.DrawText(g, path, Theme.FSmall, _rPath, Theme.TextFaint, Chrome.Left);

            TextRenderer.DrawText(g, _count, Theme.FLabel, _rCount, Theme.TextFaint,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using (Pen p = new Pen(Theme.Separator))
                g.DrawLine(p, _card.Left + Sc(26), _sepY, _card.Right - Sc(26), _sepY);

            TextRenderer.DrawText(g, L.S(_statusEn, _statusRu), Theme.FSmall, _rStatus,
                                  _statusIsError ? Theme.Red : Theme.TextFaint, Chrome.Left);
        }

        // -------------------------------------------- безрамочное окно: рамка

        const int WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1, HTCAPTION = 2;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void EnableRoundCorners()
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            }
            catch { /* до Windows 11 просто останутся прямые углы */ }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCHITTEST || (int)m.Result != HTCLIENT) return;

            // LOWORD/HIWORD — знаковые: на мониторе слева или сверху от основного
            // экранные координаты отрицательные, и без приведения к short окно
            // «ловилось» только за рамку изменения размера.
            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));

            int b = Sc(6);
            bool l = p.X <= b, r = p.X >= ClientSize.Width - b;
            bool t = p.Y <= b, d = p.Y >= ClientSize.Height - b;

            if (t && l) m.Result = (IntPtr)HTTOPLEFT;
            else if (t && r) m.Result = (IntPtr)HTTOPRIGHT;
            else if (d && l) m.Result = (IntPtr)HTBOTTOMLEFT;
            else if (d && r) m.Result = (IntPtr)HTBOTTOMRIGHT;
            else if (l) m.Result = (IntPtr)HTLEFT;
            else if (r) m.Result = (IntPtr)HTRIGHT;
            else if (t) m.Result = (IntPtr)HTTOP;
            else if (d) m.Result = (IntPtr)HTBOTTOM;
            // Всё, что не список и не кнопка, тянет окно: у контролов свои окна,
            // сюда попадают только пустые места шапки и подвала.
            else if (p.Y < _list.Top || p.Y > _list.Bottom) m.Result = (IntPtr)HTCAPTION;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Box.Focus(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------- фильтр

        void ApplyFilter()
        {
            string q = _search.Box.Text.Trim();
            int catIdx = _category.SelectedIndex;
            string cat = catIdx <= 0 ? null : Catalog.Categories[catIdx - 1];

            int shown = 0, on = 0;
            foreach (Row r in _list.Rows)
            {
                if (r.On) on++;
                bool ok = true;
                if (r.O.Stat == Stat.Dev && !_internal.Checked && !r.On) ok = false;
                if (r.O.Stat == Stat.Legacy && !_deprecated.Checked && !r.On) ok = false;
                if (ok && cat != null && r.O.Cat != cat) ok = false;
                if (ok && _onlyOn.Checked && !r.On) ok = false;
                if (ok && q.Length > 0) ok = Matches(r.O, q);
                r.Shown = ok;
                if (ok) shown++;
            }

            _count = shown + " / " + _list.Rows.Count + "  ·  " + on + L.S(" on", " вкл.");
            _list.Relayout(true);
            Invalidate();
        }

        static bool Matches(Opt o, string q)
        {
            return Has(o.Name, q) || Has(o.TitleEn, q) || Has(o.TitleRu, q)
                || Has(o.DescEn, q) || Has(o.DescRu, q) || Has(o.Cat, q);
        }

        static bool Has(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        void UpdateCount()
        {
            int shown = 0, on = 0;
            foreach (Row r in _list.Rows) { if (r.Shown) shown++; if (r.On) on++; }
            _count = shown + " / " + _list.Rows.Count + "  ·  " + on + L.S(" on", " вкл.");
            Invalidate();
        }

        // ------------------------------------------------------------ профили

        void LoadProfiles()
        {
            _profiles.Clear();
            _profiles.AddRange(LiveProfile.Detect());
            if (_profiles.Count == 0)
            {
                Status("No Live preferences folder found. Point at one with “Choose folder…”.",
                       "Папка настроек Live не найдена. Укажи её кнопкой «Выбрать папку…».", true);
                return;
            }
            RefreshProfileList(0);
        }

        void RefreshProfileList(int index)
        {
            List<string> names = new List<string>();
            foreach (LiveProfile p in _profiles) names.Add(p.DisplayName);
            _version.SetItems(names, -1);
            _version.SelectedIndex = index;
        }

        void OnProfileChanged()
        {
            int i = _version.SelectedIndex;
            if (i < 0 || i >= _profiles.Count) return;
            if (_dirty && _profile != null && !ConfirmDiscard()) return;
            _profile = _profiles[i];
            ReloadFromDisk(false);
        }

        bool ConfirmDiscard()
        {
            return MessageBox.Show(this,
                L.S("You have unsaved changes. Discard them?",
                    "Есть несохранённые изменения. Отбросить их?"),
                "Ableton Options", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        void BrowseForFolder()
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = L.S("Pick the Preferences folder of the Live version you want",
                                  "Выбери папку Preferences нужной версии Live");
            if (_profile != null && Directory.Exists(_profile.PreferencesDir))
                dlg.SelectedPath = _profile.PreferencesDir;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string dir = dlg.SelectedPath;
            LiveProfile p = LiveProfile.FromPreferencesDir(
                dir, new DirectoryInfo(dir).Name + L.S(" (manual)", " (вручную)"));
            _profiles.Add(p);
            RefreshProfileList(_profiles.Count - 1);
        }

        void OpenInExplorer()
        {
            if (_profile == null) return;
            if (!Directory.Exists(_profile.PreferencesDir))
            {
                Status("Folder does not exist: " + _profile.PreferencesDir,
                       "Папка не существует: " + _profile.PreferencesDir, true);
                return;
            }
            if (File.Exists(_profile.OptionsPath))
                Process.Start("explorer.exe", "/select,\"" + _profile.OptionsPath + "\"");
            else
                Process.Start("explorer.exe", "\"" + _profile.PreferencesDir + "\"");
        }

        // ------------------------------------------------------ чтение и запись

        void ReloadFromDisk(bool askIfDirty)
        {
            if (_profile == null) return;
            if (askIfDirty && _dirty && !ConfirmDiscard()) return;

            OptionsDoc doc;
            try { doc = OptionsDoc.Load(_profile.OptionsPath); }
            catch (Exception ex)
            {
                Status("Could not read the file: " + ex.Message,
                       "Не удалось прочитать файл: " + ex.Message, true);
                return;
            }

            _loading = true;
            foreach (Row r in _list.Rows)
            {
                string v;
                if (doc.Enabled.TryGetValue(r.O.Name, out v))
                {
                    r.On = true;
                    if (v.Length > 0) r.Val = v;
                }
                else
                {
                    r.On = false;
                    if (!string.IsNullOrEmpty(r.O.Def)) r.Val = r.O.Def;
                }
            }
            _customLines.Clear(); _customLines.AddRange(doc.Custom);
            _foreign.Clear(); _foreign.AddRange(doc.Foreign);

            // Сверяемся с журналом самого Live: он записывает каждую опцию, которую
            // не понял. Это точнее любого списка из интернета и работает для любой версии.
            HashSet<string> rejected = LiveLog.RejectedBy(_profile.PreferencesDir);
            _rejectedCount = 0;
            foreach (Row r in _list.Rows)
            {
                r.RejectedByLive = rejected.Contains(r.O.Name);
                if (r.RejectedByLive) _rejectedCount++;
            }

            _loading = false;
            _dirty = false;

            string note = _rejectedCount > 0
                ? "  ·  Live's log: " + _rejectedCount + " option(s) it rejected"
                : "";
            string noteRu = _rejectedCount > 0
                ? "  ·  по журналу Live: не понял " + _rejectedCount
                : "";

            if (File.Exists(_profile.OptionsPath))
            {
                int n = _customLines.Count;
                Status("Loaded from disk" + (n > 0 ? "  ·  unknown options kept as-is: " + n : "") + note,
                       "Загружено с диска" + (n > 0 ? "  ·  неизвестных опций сохранено: " + n : "") + noteRu,
                       false);
            }
            else
                Status("No Options.txt yet — it will be created when you save." + note,
                       "Options.txt ещё нет — он будет создан при сохранении." + noteRu, false);

            ApplyFilter();
        }

        void MarkDirty()
        {
            if (_loading) return;
            _dirty = true;
            Status("Unsaved changes.", "Есть несохранённые изменения.", false);
            if (_onlyOn.Checked) ApplyFilter(); else UpdateCount();
        }

        OptionsDoc Collect(out string error)
        {
            error = _list.Validate();
            if (error != null) return null;

            OptionsDoc doc = new OptionsDoc();
            foreach (Row r in _list.Rows)
            {
                if (!r.On) continue;
                doc.Enabled[r.O.Name] = r.O.Kind == Kind.Flag ? "" : r.Val;
            }
            doc.Custom.AddRange(_customLines);
            doc.Foreign.AddRange(_foreign);
            return doc;
        }

        void Save()
        {
            if (_profile == null)
            {
                Status("Pick a Live version first.", "Сначала выбери версию Live.", true);
                return;
            }

            string error;
            OptionsDoc doc = Collect(out error);
            if (doc == null)
            {
                MessageBox.Show(this, error, L.S("Check the value", "Проверь значение"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Отдельно и строже: эти опции твой Live уже отвергал — он снова покажет
            // окно с ошибкой при запуске.
            List<string> rejected = new List<string>();
            foreach (Row r in _list.Rows) if (r.On && r.RejectedByLive) rejected.Add("-" + r.O.Name);
            if (rejected.Count > 0)
            {
                string list = string.Join("\n", rejected.ToArray());
                DialogResult res = MessageBox.Show(this,
                    L.S("Live's own log says it does not understand these options — it will show an " +
                        "error dialog for each one at startup:\n\n" + list + "\n\nSave anyway?",
                        "Судя по журналу самого Live, он не понимает эти опции и покажет окно с " +
                        "ошибкой для каждой при запуске:\n\n" + list + "\n\nВсё равно сохранить?"),
                    "Ableton Options", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (res != DialogResult.Yes) return;
            }

            bool legacyOn = false;
            foreach (Row r in _list.Rows) if (r.On && r.O.Stat == Stat.Legacy && !r.RejectedByLive) legacyOn = true;
            if (legacyOn)
            {
                DialogResult res = MessageBox.Show(this,
                    L.S("Some enabled options were removed in Live 11 — they do nothing in Live 12 " +
                        "and Live may complain at startup.\n\nSave anyway?",
                        "Среди включённых есть опции, удалённые в Live 11 — в Live 12 они не работают " +
                        "и Live может показать сообщение об ошибке при запуске.\n\nВсё равно сохранить?"),
                    "Ableton Options", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (res != DialogResult.Yes) return;
            }

            try
            {
                string backup = doc.Save(_profile.OptionsPath);
                _dirty = false;
                string name = backup == null ? null : Path.GetFileName(backup);
                Status("Saved · restart Live to apply" + (name == null ? "" : "  ·  backup: " + name),
                       "Сохранено · перезапусти Live" + (name == null ? "" : "  ·  резервная копия: " + name),
                       false);
            }
            catch (Exception ex)
            {
                Status("Could not save: " + ex.Message,
                       "Не удалось сохранить: " + ex.Message, true);
                MessageBox.Show(this, ex.Message, L.S("Write error", "Ошибка записи"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ClearAll()
        {
            if (MessageBox.Show(this,
                    L.S("Untick every option? The file stays untouched until you press Save.",
                        "Снять все галочки? Файл не изменится, пока не нажмёшь «Сохранить»."),
                    "Ableton Options", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            foreach (Row r in _list.Rows) r.On = false;
            MarkDirty();
            ApplyFilter();
        }

        void ShowPreview()
        {
            string error;
            OptionsDoc doc = Collect(out error);
            if (doc == null)
            {
                MessageBox.Show(this, error, L.S("Check the value", "Проверь значение"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string text = doc.Render();
            if (text.Length == 0)
                text = L.S("(the file will be empty — nothing is selected)",
                           "(файл будет пустым — опции не выбраны)");
            using (TextDialog d = new TextDialog(L.S("Options.txt contents", "Содержимое Options.txt"), text))
                d.ShowDialog(this);
        }

        void EditCustom()
        {
            using (CustomOptionsDialog d = new CustomOptionsDialog(_customLines))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _customLines.Clear();
                _customLines.AddRange(d.Result);
                MarkDirty();
            }
        }

        /// <summary>
        /// Статус хранится сразу на двух языках: иначе после переключения EN/RU внизу
        /// осталась бы фраза, набранная до переключения.
        /// </summary>
        void Status(string en, string ru, bool error)
        {
            _statusEn = en;
            _statusRu = ru;
            _statusIsError = error;
            Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty && !ConfirmDiscard()) e.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}

