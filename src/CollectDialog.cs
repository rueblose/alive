using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Сборка проекта в переносимую папку. Одно окно, два состояния: выбор и копирование, —
    /// тем же приёмом, что RescueDialog, и по той же причине: это один поступок, а не два
    /// разных дела, и второе окно поверх первого только мешало бы.
    ///
    /// Пункты — ровно те четыре, что задаёт «Collect All and Save» самой Live. Сверх неё
    /// показаны число файлов и вес: 5.8 ГБ у паков надо видеть ДО нажатия OK, а не после.
    /// </summary>
    public sealed class CollectDialog : GlassDialog
    {
        sealed class Row
        {
            public string Label = "";
            public SampleOrigin Origin;
            public PillToggle Toggle;
            public int Files;
            public long Bytes;
            public Rectangle Rect;
        }

        readonly SetEntry _set;
        readonly LiveEnvironment _env;
        readonly List<Row> _rows = new List<Row>();
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();

        AlsInfo _info;
        List<SampleDep> _deps;
        CollectPlan _plan;
        CollectOptions _opt = new CollectOptions();

        int _inProjectFiles; long _inProjectBytes;
        int _notFound;

        bool _counting = true;
        bool _running;
        string _error = "";

        // Своё поле, а не переиспользование _error: тот про чтение сета (не удалось
        // разобрать .als), этот — про сбой самого копирования. Разные причины, разный текст.
        string _collectError = "";

        // Прогресс пишется рабочим потоком, читается таймером окна. Простые поля:
        // int и long читаются и пишутся атомарно, а точность до одного файла тут
        // никому не нужна — это полоса, а не отчёт.
        volatile int _done, _total;
        volatile string _current = "";
        CancellationTokenSource _cts;

        /// <summary>Папка собранного проекта. Пусто, если отменили или не дошло до сборки.</summary>
        public string Produced = "";

        /// <summary>Сколько файлов скопировать не вышло — занято, слишком длинный путь.</summary>
        public int Failed;

        public CollectDialog(SetEntry set, LiveEnvironment env)
        {
            _set = set;
            _env = env;

            Caption = "Collect All: " + set.Name;
            // 640 обрезало подпись «Specify which used media files are to be copied into
            // the project.» многоточием на «…the p…» — при 13pt Segoe UI Variable Text
            // строка чуть шире, чем помещалось в исходную ширину. 720 — с запасом,
            // проверено снимком (см. отчёт задачи).
            ClientSize = new Size(Sc(720), Sc(400));

            Settings st = Settings.Load();
            _opt.FromElsewhere = st.CollectElsewhere;
            _opt.FromOtherProjects = st.CollectOtherProjects;
            _opt.FromUserLibrary = st.CollectUserLibrary;
            _opt.FromFactoryPacks = st.CollectFactoryPacks;

            AddRow("Files from elsewhere", SampleOrigin.Elsewhere, _opt.FromElsewhere);
            AddRow("Files from other Projects", SampleOrigin.OtherProject, _opt.FromOtherProjects);
            AddRow("Files from User Library", SampleOrigin.UserLibrary, _opt.FromUserLibrary);
            AddRow("Files from Factory Packs", SampleOrigin.FactoryPack, _opt.FromFactoryPacks);

            _ok.Text = "Collect";
            _ok.Primary = true;
            _ok.FitToText(20);
            _ok.Enabled = false;
            _ok.Click += delegate { Start(); };
            Controls.Add(_ok);

            _cancel.Text = "Cancel";
            _cancel.FitToText(20);
            _cancel.Click += delegate { OnCancel(); };
            Controls.Add(_cancel);

            _tick.Interval = 100;
            _tick.Tick += delegate { Invalidate(); };
            _tick.Start();
        }

        bool _started;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Считать начинаем, только когда окно показано. BeginInvoke до создания
            // хендла бросает InvalidOperationException, а подсчёт вполне успевает
            // кончиться раньше, чем ShowDialog доберётся до показа окна.
            if (_started) return;
            _started = true;
            ThreadPool.QueueUserWorkItem(delegate { CountInBackground(); });
        }

        void AddRow(string label, SampleOrigin origin, bool on)
        {
            Row r = new Row();
            r.Label = label;
            r.Origin = origin;
            r.Toggle = new PillToggle();
            r.Toggle.IsSwitch = true;
            r.Toggle.Size = new Size(Sc(42), Sc(24));
            r.Toggle.Checked = on;
            r.Toggle.Enabled = false;           // до конца подсчёта трогать нечего
            r.Toggle.CheckedChanged += delegate { Recount(); };
            Controls.Add(r.Toggle);
            _rows.Add(r);
        }

        // ------------------------------------------------------------------ подсчёт

        /// <summary>
        /// Вернуться в поток окна из фонового. Окно могут закрыть, пока фоновая работа
        /// идёт: тогда хендла уже нет и BeginInvoke бросает — ловим здесь, в одном месте,
        /// а не проверкой IsDisposed в каждом обработчике (она всё равно гонка).
        /// </summary>
        void Post(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception) { }
        }

        void CountInBackground()
        {
            AlsInfo info = AlsFile.Read(_set.Path);
            List<SampleDep> deps = info.Error == null
                ? SampleScan.Of(info, Path.GetDirectoryName(_set.Path), _env)
                : new List<SampleDep>();

            Post(delegate
            {
                _info = info;
                _deps = deps;
                _counting = false;
                if (info.Error != null) { _error = info.Error; Invalidate(); return; }

                foreach (Row r in _rows)
                {
                    r.Files = 0; r.Bytes = 0;
                    r.Toggle.Enabled = true;
                }
                _inProjectFiles = 0; _inProjectBytes = 0; _notFound = 0;

                foreach (SampleDep d in deps)
                {
                    if (d.Origin == SampleOrigin.Missing) { _notFound++; continue; }
                    if (d.Origin == SampleOrigin.InProject)
                    { _inProjectFiles++; _inProjectBytes += d.Size; continue; }
                    foreach (Row r in _rows)
                        if (r.Origin == d.Origin) { r.Files++; r.Bytes += d.Size; break; }
                }

                Recount();
                LayoutRows();
                Invalidate();
            });
        }

        void Recount()
        {
            if (_deps == null || _info == null) return;
            _opt.FromElsewhere = _rows[0].Toggle.Checked;
            _opt.FromOtherProjects = _rows[1].Toggle.Checked;
            _opt.FromUserLibrary = _rows[2].Toggle.Checked;
            _opt.FromFactoryPacks = _rows[3].Toggle.Checked;

            _plan = CollectAll.Plan(_set, _info, _deps, _opt);
            _ok.Enabled = !_running && _plan.TotalBytes < _plan.FreeBytes;
            Invalidate();
        }

        // ------------------------------------------------------------------ сборка

        void Start()
        {
            if (_plan == null || _running) return;

            Settings st = Settings.Load();
            st.CollectElsewhere = _opt.FromElsewhere;
            st.CollectOtherProjects = _opt.FromOtherProjects;
            st.CollectUserLibrary = _opt.FromUserLibrary;
            st.CollectFactoryPacks = _opt.FromFactoryPacks;
            st.Save();

            _running = true;
            _ok.Enabled = false;
            foreach (Row r in _rows) r.Toggle.Enabled = false;
            LayoutRows();            // пересинхронизировать Visible — иначе строки не прячутся
            _total = _plan.Copy.Count;
            _done = 0;
            _cts = new CancellationTokenSource();

            CollectPlan plan = _plan;
            AlsInfo info = _info;
            CancellationToken token = _cts.Token;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "";
                bool done = false;
                try
                {
                    CollectAll.Run(plan, _set, info,
                        delegate (int n, int of, string what) { _done = n; _total = of; _current = what; },
                        token);
                    done = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex.Message; }

                Post(delegate
                {
                    _running = false;
                    if (done)
                    {
                        Produced = plan.TargetDir;
                        Failed = plan.Failed.Count;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        _collectError = error;
                        if (error.Length == 0) Close();     // отменили — просто закрываемся
                        else
                        {
                            // Настоящий сбой копирования (не отмена) — окно остаётся
                            // открытым, экран выбора должен вернуться полностью: и
                            // Enabled, и Visible строк, иначе Collect бьёт в стену.
                            _ok.Enabled = true;
                            foreach (Row r in _rows) r.Toggle.Enabled = true;
                            LayoutRows();
                            Invalidate();
                        }
                    }
                });
            });

            Invalidate();
        }

        void OnCancel()
        {
            if (_running && _cts != null) { _cts.Cancel(); return; }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------ раскладка

        int RowTop { get { return Card.Top + Sc(80); } }
        int RowStep { get { return Sc(34); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutRows();
        }

        void LayoutRows()
        {
            int h = Sc(Theme.ControlH);
            int pad = Sc(24);
            _cancel.SetBounds(Card.Right - pad - _cancel.Width, Card.Bottom - pad - h, _cancel.Width, h);
            _ok.SetBounds(_cancel.Left - Sc(10) - _ok.Width, Card.Bottom - pad - h, _ok.Width, h);

            int y = RowTop;
            foreach (Row r in _rows)
            {
                r.Rect = new Rectangle(Card.Left + pad, y, Card.Width - pad * 2, RowStep);
                r.Toggle.Location = new Point(r.Rect.X, y + (RowStep - r.Toggle.Height) / 2);
                r.Toggle.Visible = !_running && !_counting;
                y += RowStep;
            }
        }

        // ------------------------------------------------------------------ рисование

        static string Mb(long bytes)
        {
            if (bytes >= 1073741824L)
                return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            int pad = Sc(24);
            int left = Card.Left + pad, right = Card.Right - pad;
            Rectangle head = new Rectangle(left, Card.Top + Sc(46), right - left, Sc(24));

            if (_error.Length > 0)
            {
                Chrome.DrawText(g, "Cannot read this set: " + _error, Theme.FBody, head, Theme.Red, Chrome.Left);
                return;
            }

            if (_counting)
            {
                Chrome.DrawText(g, "Counting…", Theme.FBody, head, Theme.TextDim, Chrome.Left);
                return;
            }

            if (_running)
            {
                int total = _total, done = _done;
                Chrome.DrawText(g, string.Format("Copying {0} of {1}", done, total),
                                Theme.FBody, head, Theme.Text, Chrome.Left);

                RectangleF bar = new RectangleF(left, head.Bottom + Sc(16), right - left, Sc(6));
                Theme.FillRound(g, bar, bar.Height / 2f, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                if (total > 0 && done > 0)
                {
                    RectangleF fill = new RectangleF(bar.X, bar.Y, bar.Width * done / (float)total, bar.Height);
                    Theme.FillRound(g, fill, bar.Height / 2f, Theme.Text);
                }

                Rectangle now = new Rectangle(left, (int)bar.Bottom + Sc(10), right - left, Sc(20));
                Chrome.DrawText(g, _current ?? "", Theme.FLabel, now, Theme.TextDim, Chrome.Left);
                return;
            }

            // Тот же заголовок показывает и сбой копирования — окно уже прочитало сет,
            // строки внизу остаются на месте, меняется только эта строка и её цвет.
            string intro = _collectError.Length > 0
                ? "Could not collect: " + _collectError
                : "Specify which used media files are to be copied into the project.";
            Chrome.DrawText(g, intro, Theme.FBody, head,
                            _collectError.Length > 0 ? Theme.Red : Theme.Text, Chrome.Left);

            foreach (Row r in _rows)
            {
                int textX = r.Toggle.Right + Sc(14);
                Rectangle label = new Rectangle(textX, r.Rect.Y, Sc(260), r.Rect.Height);
                Chrome.DrawText(g, r.Label, Theme.FBody, label, Theme.Text, Chrome.Left);

                Rectangle nums = new Rectangle(right - Sc(240), r.Rect.Y, Sc(240), r.Rect.Height);
                string s = r.Files == 0 ? "—"
                    : string.Format("{0} files    {1}", r.Files, Mb(r.Bytes));
                Chrome.DrawText(g, s, Theme.FLabel, nums,
                                r.Toggle.Checked ? Theme.Text : Theme.TextDim, Chrome.Right);
            }

            int y = RowTop + _rows.Count * RowStep + Sc(14);
            Extra(g, left, right, ref y, "In project (always copied)",
                  string.Format("{0} files    {1}", _inProjectFiles, Mb(_inProjectBytes)));
            if (_notFound > 0)
                Extra(g, left, right, ref y, "Not found — left as they are",
                      string.Format("{0} files", _notFound));

            y += Sc(8);
            using (Pen p = new Pen(Theme.Hairline)) g.DrawLine(p, left, y, right, y);
            y += Sc(10);

            if (_plan != null)
            {
                bool fits = _plan.TotalBytes < _plan.FreeBytes;
                Extra(g, left, right, ref y,
                      string.Format("Will copy {0} files, {1}", _plan.Copy.Count, Mb(_plan.TotalBytes)),
                      fits ? "Free: " + Mb(_plan.FreeBytes) : "Not enough space",
                      fits ? Theme.Text : Theme.Red);
            }
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value)
        {
            Extra(g, left, right, ref y, label, value, Theme.TextDim);
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value, Color color)
        {
            Rectangle l = new Rectangle(left, y, right - left - Sc(240), Sc(22));
            Rectangle v = new Rectangle(right - Sc(240), y, Sc(240), Sc(22));
            Chrome.DrawText(g, label, Theme.FLabel, l, color, Chrome.Left);
            Chrome.DrawText(g, value, Theme.FLabel, v, color, Chrome.Right);
            y += Sc(22);
        }
    }
}
