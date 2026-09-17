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
        readonly Settings _settings;
        readonly List<Row> _rows = new List<Row>();
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly PillToggle _zip = new PillToggle();

        const string ZipLabel = "Add to ZIP";

        /// <summary>Самый широкий итог, какой бывает, — по нему меряется полка.</summary>
        const string WidestSummary = "Will copy 9999 files, 999.9 GB";

        // Ширина — нижняя граница, а не размер: полка внизу может попросить больше, см.
        // OnHandleCreated. Высота — под четыре строки, итоги и полку; строка «не нашлись»
        // есть не всегда, запас на неё заложен.
        const int DialogW = 720;
        const int DialogH = 350;
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

        public CollectDialog(SetEntry set, LiveEnvironment env, Settings settings)
        {
            _set = set;
            _env = env;
            _settings = settings;

            Caption = "Export: " + set.Name;
            ClientSize = new Size(Sc(DialogW), Sc(DialogH));

            // Тот же экземпляр, что грузит и сохраняет MainForm, — не Settings.Load()
            // заново: своя копия не видела бы изменений с других окон и, что хуже,
            // затиралась бы следующим чужим Save() из устаревшего снимка на диске.
            _opt.FromElsewhere = _settings.CollectElsewhere;
            _opt.FromOtherProjects = _settings.CollectOtherProjects;
            _opt.FromUserLibrary = _settings.CollectUserLibrary;
            _opt.FromFactoryPacks = _settings.CollectFactoryPacks;

            AddRow("Files from elsewhere", SampleOrigin.Elsewhere, _opt.FromElsewhere);
            AddRow("Files from other Projects", SampleOrigin.OtherProject, _opt.FromOtherProjects);
            AddRow("Files from User Library", SampleOrigin.UserLibrary, _opt.FromUserLibrary);
            AddRow("Files from Factory Packs", SampleOrigin.FactoryPack, _opt.FromFactoryPacks);

            _ok.Text = "Export";
            _ok.Primary = true;
            _ok.FitToText(20);
            _ok.Enabled = false;
            _ok.Click += delegate { Start(); };
            Controls.Add(_ok);

            _cancel.Text = "Cancel";
            _cancel.FitToText(20);
            _cancel.Click += delegate { OnCancel(); };
            Controls.Add(_cancel);

            // Тот же тумблер, что у строк выше, и в той же полке, что кнопки: архив —
            // это про то, чем кончится сборка, а не ещё один вид файлов для неё.
            _zip.IsSwitch = true;
            _zip.Size = new Size(Sc(42), Sc(24));
            _zip.Checked = _settings.CollectToZip;
            _zip.Enabled = false;
            _zip.CheckedChanged += delegate { Recount(); };
            Controls.Add(_zip);

            _tick.Interval = 100;
            _tick.Tick += delegate { Invalidate(); };
            _tick.Start();
        }

        /// <summary>
        /// Ширину окна задаёт не только Sc(). Sc() считает по DeviceDpi, а текст GDI рисует
        /// по DPI шрифта, и это разные числа: на системе со 125% окно выходило 96-точечным,
        /// а буквы в нём — 120-точечными. Нижняя полка — единственная строка, где всё стоит
        /// впритык, и она переставала помещаться в собственное окно: итог слева обрезался
        /// многоточием. Поэтому меряем полку настоящим текстом и, если ей тесно, раздаём
        /// окну ровно столько, сколько она просит.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _ok.FitToText(20);
            _cancel.FitToText(20);

            int need = Sc(24) * 2
                     + TextRenderer.MeasureText(WidestSummary, Theme.FLabel).Width
                     + Sc(16) + _zip.Width + Sc(14)
                     + TextRenderer.MeasureText(ZipLabel, Theme.FBody).Width
                     + Sc(24) + _cancel.Width + Sc(10) + _ok.Width;

            // Только когда тесно: присваивание ClientSize уже созданному окну проходит
            // через пересчёт рамки и прибавляет к высоте лишнее, а при обычном масштабе
            // менять нечего — размер из конструктора и так верен.
            if (need > ClientSize.Width) ClientSize = new Size(need, ClientSize.Height);
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
                _zip.Enabled = true;
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
            // По Origin, а не по индексу: индекс — это порядок AddRow в конструкторе,
            // и молчаливая привязка к нему рвётся первой же перестановкой строк.
            foreach (Row r in _rows)
            {
                switch (r.Origin)
                {
                    case SampleOrigin.Elsewhere: _opt.FromElsewhere = r.Toggle.Checked; break;
                    case SampleOrigin.OtherProject: _opt.FromOtherProjects = r.Toggle.Checked; break;
                    case SampleOrigin.UserLibrary: _opt.FromUserLibrary = r.Toggle.Checked; break;
                    case SampleOrigin.FactoryPack: _opt.FromFactoryPacks = r.Toggle.Checked; break;
                }
            }
            _opt.ToZip = _zip.Checked;

            _plan = CollectAll.Plan(_set, _deps, _opt);
            _ok.Enabled = !_running && Fits;
            Invalidate();
        }

        /// <summary>
        /// Хватит ли места. В режиме .zip нужно вдвое: архив пишется рядом с папкой, и
        /// пока он не готов, на диске лежат оба. Сжатие в расчёт не берём — сэмплы не жмутся.
        /// </summary>
        bool Fits
        {
            get
            {
                if (_plan == null) return false;
                long need = _plan.Zip ? _plan.TotalBytes * 2 : _plan.TotalBytes;
                return need < _plan.FreeBytes;
            }
        }

        // ------------------------------------------------------------------ сборка

        void Start()
        {
            if (_plan == null || _running) return;

            _settings.CollectElsewhere = _opt.FromElsewhere;
            _settings.CollectOtherProjects = _opt.FromOtherProjects;
            _settings.CollectUserLibrary = _opt.FromUserLibrary;
            _settings.CollectFactoryPacks = _opt.FromFactoryPacks;
            _settings.CollectToZip = _opt.ToZip;
            _settings.Save();

            _running = true;
            _ok.Enabled = false;
            foreach (Row r in _rows) r.Toggle.Enabled = false;
            _zip.Enabled = false;
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
                catch (Exception ex)
                {
                    // В окне эта строка стоит в нижней полке и длинное сообщение там
                    // обрежется — целиком оно остаётся в журнале.
                    error = ex.Message;
                    Diag.Line("export: " + ex);
                }

                Post(delegate
                {
                    _running = false;
                    if (done)
                    {
                        Produced = plan.Zip ? plan.ZipPath : plan.TargetDir;
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
                            // Enabled считает Recount() — не «true» в лоб: место на диске
                            // могло кончиться как раз за время неудачной попытки, и план
                            // на устаревших цифрах включил бы Collect там, где он снова
                            // не поместится.
                            foreach (Row r in _rows) r.Toggle.Enabled = true;
                            _zip.Enabled = true;
                            Recount();
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

        // Сразу под шапкой окна: та кончается на +59 (заголовок и крестик), дальше список.
        int RowTop { get { return Card.Top + Sc(72); } }
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
            int by = Card.Bottom - pad - h;

            _ok.SetBounds(Card.Right - pad - _ok.Width, by, _ok.Width, h);
            _cancel.SetBounds(_ok.Left - Sc(10) - _cancel.Width, by, _cancel.Width, h);

            // Подпись тумблера рисует OnPaint — ширину её меряем здесь, чтобы отодвинуть
            // сам тумблер ровно настолько, насколько она займёт справа от него.
            int labelW = TextRenderer.MeasureText(ZipLabel, Theme.FBody).Width;
            _zip.Location = new Point(_cancel.Left - Sc(24) - labelW - Sc(14) - _zip.Width,
                                      by + (h - _zip.Height) / 2);
            _zip.Visible = !_running && !_counting;

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

            // Нижняя полка есть в любом состоянии: Cancel стоит в ней и пока идёт подсчёт,
            // и пока копируем. Линейка отделяет её от списка.
            int barTop = _ok.Top;
            int rule = barTop - Sc(14);
            using (Pen p = new Pen(Theme.Hairline)) g.DrawLine(p, left, rule, right, rule);

            Rectangle head = new Rectangle(left, RowTop, right - left, Sc(24));

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
                // «Exporting», а не «Copying»: в режиме .zip за копированием идёт второй
                // проход, упаковка, и счётчик пробегает шкалу дважды. Что именно идёт
                // сейчас, говорит строка под полосой.
                int total = _total, done = _done;
                Chrome.DrawText(g, string.Format("Exporting {0} of {1}", done, total),
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

            foreach (Row r in _rows)
            {
                int textX = r.Toggle.Right + Sc(14);
                Rectangle label = new Rectangle(textX, r.Rect.Y, Sc(260), r.Rect.Height);
                Chrome.DrawText(g, r.Label, Theme.FBody, label, Theme.Text, Chrome.Left);

                Rectangle nums = new Rectangle(right - Sc(240), r.Rect.Y, Sc(240), r.Rect.Height);
                string s = r.Files == 0 ? "—"
                    : string.Format("{0}    {1}", Chrome.Plural(r.Files, "file"), Mb(r.Bytes));
                Chrome.DrawText(g, s, Theme.FLabel, nums,
                                r.Toggle.Checked ? Theme.Text : Theme.TextDim, Chrome.Right);
            }

            int y = RowTop + _rows.Count * RowStep + Sc(14);
            Extra(g, left, right, ref y, "In project (always copied)",
                  string.Format("{0}    {1}", Chrome.Plural(_inProjectFiles, "file"), Mb(_inProjectBytes)));
            if (_notFound > 0)
                Extra(g, left, right, ref y, "Not found — left as they are",
                      Chrome.Plural(_notFound, "file"));

            int barH = Sc(Theme.ControlH);
            Chrome.DrawText(g, ZipLabel, Theme.FBody,
                            new Rectangle(_zip.Right + Sc(14), barTop, _cancel.Left - Sc(24) - _zip.Right, barH),
                            Theme.Text, Chrome.Left);

            // Левый край полки — итог, он же место для «не влезет» и для сбоя сборки:
            // всё это ответ на один вопрос, можно ли жать Export.
            string sum = "";
            Color color = Theme.Text;
            if (_collectError.Length > 0) { sum = "Could not export: " + _collectError; color = Theme.Red; }
            else if (_plan != null)
            {
                // Не влезает — цифры уступают место причине: они разбиты по строкам выше,
                // а здесь важно одно, почему Export не нажимается.
                if (Fits)
                    sum = string.Format("Will copy {0}, {1}",
                                        Chrome.Plural(_plan.Copy.Count, "file"), Mb(_plan.TotalBytes));
                else { sum = "Not enough space"; color = Theme.Red; }
            }
            if (sum.Length > 0)
                Chrome.DrawText(g, sum, Theme.FLabel,
                                new Rectangle(left, barTop, _zip.Left - Sc(16) - left, barH), color, Chrome.Left);
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value)
        {
            Rectangle l = new Rectangle(left, y, right - left - Sc(240), Sc(22));
            Rectangle v = new Rectangle(right - Sc(240), y, Sc(240), Sc(22));
            Chrome.DrawText(g, label, Theme.FLabel, l, Theme.TextDim, Chrome.Left);
            Chrome.DrawText(g, value, Theme.FLabel, v, Theme.TextDim, Chrome.Right);
            y += Sc(22);
        }
    }
}
