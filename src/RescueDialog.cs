using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Помощник по восстановлению проекта, который не открывается.
    ///
    /// Работает в полуавтомате: программа готовит пробную копию сета с отключёнными
    /// плагинами и открывает её, а Live запускает и закрывает человек. Сама она в чужой
    /// редактор не лезет — уронить вместе с пробой чей-то несохранённый проект было бы
    /// ровно тем, от чего это окно и спасает.
    ///
    /// Чем кончилась проба, спрашивать не нужно: Live пишет это в свой Log.txt, окно его
    /// дочитывает и показывает исход само (см. LiveLog).
    /// </summary>
    public sealed class RescueDialog : GlassDialog
    {
        readonly RescueSession _s;

        readonly PluginCheckList _list = new PluginCheckList();
        readonly GlassButton _all = new GlassButton();
        readonly GlassButton _none = new GlassButton();
        readonly GlassButton _suggested = new GlassButton();

        readonly GlassButton _rescued = new GlassButton();
        readonly GlassButton _run = new GlassButton();

        readonly Timer _poll = new Timer();

        bool _waiting;              // проба лежит на диске, ждём вердикта от Live
        DateTime _waitingSince;
        string _status = "";
        string _hint = "";

        // Что показывать — своими флагами, а не через Control.Visible. Пока форма не
        // показана, Visible у её детей false, и раскладка, спрошенная в конструкторе,
        // молча пропустила бы все кнопки — они так и остались бы нулевой ширины у
        // правого края (ровно это и было видно на первом снимке окна).
        bool _showQuick = true;
        bool _showRescued;

        /// <summary>
        /// Запущена ли Live. Ответ кешируется: спрашивать его — это перебрать все процессы
        /// системы, а UpdateButtons зовётся и на каждый щелчок по галочке, и раз в секунду
        /// по таймеру.
        /// </summary>
        bool _liveRunning;

        /// <summary>Сет, который стоит показать в каталоге после закрытия окна, — спасённая копия.</summary>
        public string Produced = "";

        public RescueDialog(SetEntry set, PluginInventory inv)
        {
            _s = new RescueSession(set, inv);

            Caption = L.S("Rescue: ", "Восстановление: ") + set.Name;

            // Высота — по числу плагинов: у сета их бывает и один, и полсотни, а окно
            // постоянной высоты в первом случае наполовину пустое, во втором прокручивается
            // там, где могло бы показать всё сразу. Двенадцать строк — потолок, дальше
            // окно упёрлось бы в невысокие экраны.
            int rows = Math.Max(3, Math.Min(12, _s.Targets.Count));
            ClientSize = new Size(Sc(780), Sc(300) + HintHeight + rows * _list.RowHeight);

            _list.Items.AddRange(_s.Targets);
            _list.Changed += delegate { UpdateButtons(); };
            Controls.Add(_list);

            // Галочка у плагина означает «остаётся включён» (см. PluginCheckList),
            // поэтому «All Off» снимает все галочки, а «All On» их все ставит —
            // кнопки названы по тому, что получится, а не по тому, что они делают
            // с галочками.
            Quick(_all, L.S("All Off", "Выключить все"), delegate { _list.CheckAll(false); });
            Quick(_none, L.S("All On", "Включить все"), delegate { _list.CheckAll(true); });
            Quick(_suggested, L.S("Suggested", "Предложенные"), delegate { _list.SetDisabled(_s.Suggest()); });

            _rescued.Text = L.S("Save rescued copy", "Сохранить спасённую копию");
            _rescued.FitToText(16);
            _rescued.Click += delegate { SaveRescued(); };
            Controls.Add(_rescued);

            _run.Primary = true;
            _run.Click += delegate { OnRun(); };
            Controls.Add(_run);

            // Секунда — это шаг, на котором «Live грузит» ещё выглядит живым отсчётом, а
            // журнал перечитывается только хвостом, так что стоит это почти ничего.
            _poll.Interval = 1000;
            _poll.Tick += delegate { Tick(); };
            _poll.Start();

            // Открывается со всеми галочками — ничего ещё не отключено, пока человек сам
            // не снимет галочку или не нажмёт «Выключить все»/«Предложенные». Автоматически
            // подставлять сюда Suggest() значило бы сразу готовить пробу за человека —
            // а решение, что пробовать первым, должно быть видимым и обратимым действием,
            // а не стартовым состоянием окна.
            _liveRunning = RescueSession.LiveIsRunning();
            _list.CheckAll(true);
            Describe();
            UpdateButtons();
        }

        void Quick(GlassButton b, string text, EventHandler click)
        {
            b.Text = text;
            b.Quiet = true;
            b.Font = Theme.FLabel;
            b.FitToText(10);
            b.Click += click;
            Controls.Add(b);
        }

        // ------------------------------------------------------------------ состояние

        /// <summary>Верхний абзац: что вообще известно про этот сет прямо сейчас.</summary>
        void Describe()
        {
            if (_s.Error != null)
            {
                _status = L.S("This .als cannot be read at all: ", "Файл .als не читается вовсе: ") + _s.Error
                        + L.S(". That is damage to the file itself, not a plugin problem.",
                              ". Это повреждение самого файла, плагины тут ни при чём.");
                return;
            }

            if (_s.Targets.Count == 0)
            {
                _status = L.S("This set has no third-party plugins — there is nothing here to switch off. "
                            + "Whatever stops it from opening is somewhere else.",
                              "В сете нет сторонних плагинов — отключать нечего.");
                return;
            }

            if (_s.Finished)
            {
                string v = _s.VerdictText();
                _status = char.ToUpperInvariant(v[0]) + v.Substring(1);
                return;
            }

            if (_s.History == null)
            {
                _status = L.S("Live's log has no record of this set. Untick a plugin to test it, "
                            + "or click “All Off” to disable everything at once.",
                              "В журнале Live этого сета нет. Снимите галочку с плагина, чтобы проверить его, "
                            + "или нажмите «Выключить все», чтобы отключить сразу всё.");
                return;
            }

            PluginLoad hung = _s.History.Hung;
            string when = When(_s.History.Started);

            if (_s.History.Result == LoadResult.Loaded)
            {
                _status = string.Format(
                    L.S("Live's log says this set opened normally on {0}, with {1} restored. "
                      + "If it fails now, something changed since — a plugin update, most likely.",
                        "По журналу Live сет нормально открывался {0}."),
                    when, Plural(_s.History.RestoredCount, "plugin", "plugins"));
                return;
            }

            if (hung != null)
            {
                _status = string.Format(
                    L.S("Live's log stops inside {0} {1} on {2} — it restored {3} and never came back "
                      + "from that one. Untick it below and probe.",
                        "Журнал Live обрывается внутри {0} {1} — снимите с него галочку и начните пробу."),
                    hung.Format, hung.Name, when, Plural(_s.History.RestoredCount, "plugin", "plugins"));
                return;
            }

            _status = string.Format(
                L.S("Live's log has an unfinished attempt from {0}, with no plugin left pending — "
                  + "the set may be breaking before the plugins get their turn.",
                    "В журнале Live есть незавершённая попытка от {0}."),
                when);
        }

        /// <summary>
        /// Дата для интерфейса. Инвариантная культура, а не системная: интерфейс тут
        /// только английский (см. L.S), а CurrentCulture на этой машине русская — и в
        /// английскую фразу приезжало «24 авг».
        /// </summary>
        static string When(DateTime t)
        {
            return t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        }

        static string Plural(int n, string one, string many)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        void UpdateButtons()
        {
            bool usable = _s.Error == null && _s.HasTargets;
            bool live = _liveRunning;

            _list.ReadOnly = _waiting || !usable;

            _showQuick = usable && !_waiting;
            _showRescued = usable && _s.RescueSelection().Count > 0 && !_waiting;
            _all.Visible = _none.Visible = _suggested.Visible = _showQuick;
            _rescued.Visible = _showRescued;

            if (_waiting)
            {
                _run.Text = L.S("Waiting for Live…", "Ждём Live…");
                _run.Enabled = false;
            }
            else if (!usable)
            {
                _run.Text = L.S("Close", "Закрыть");
                _run.Enabled = true;
            }
            else if (_s.Finished)
            {
                _run.Text = L.S("Probe again", "Ещё проба");
                _run.Enabled = _list.Disabled.Count > 0 && !live;
            }
            else
            {
                _run.Text = _s.Round == 0
                          ? L.S("Open probe in Live", "Открыть пробу в Live")
                          : L.S("Next probe", "Следующая проба");
                _run.Enabled = _list.Disabled.Count > 0 && !live;
            }

            _run.FitToText(22);
            _rescued.FitToText(16);

            _hint = Hint(usable, live);
            LayoutControls();
            Invalidate(true);
        }

        string Hint(bool usable, bool live)
        {
            if (_waiting)
                return L.S("Live is opening the probe. Watch it, then close Live — the answer is read from Live's own log.",
                           "Live открывает пробу. Посмотрите и закройте Live.");
            if (!usable) return "";
            if (live)
                return L.S("Close Ableton Live first — if the probe crashes it would take your open project with it.",
                           "Сначала закройте Ableton Live.");
            if (_list.Disabled.Count == 0)
                return L.S("Untick the plugins you want to switch off in the probe.",
                           "Снимите галочки с плагинов, которые нужно отключить в пробе.");

            return string.Format(
                L.S("The probe is a copy — “{0}” next to the original. Your set is never modified. Do not save the probe from Live.",
                    "Проба — копия «{0}» рядом с оригиналом. Оригинал не меняется."),
                Path.GetFileName(RescueProbe.PathFor(_s.Set)));
        }

        // ------------------------------------------------------------------ проба

        void OnRun()
        {
            if (_s.Error != null || !_s.HasTargets) { Close(); return; }

            try
            {
                _s.Prepare(_list.Disabled);
                _s.Launch();
            }
            catch (Exception ex)
            {
                _status = L.S("Could not start the probe: ", "Не удалось начать пробу: ") + ex.Message;
                Diag.Fail("rescue: run", ex);
                UpdateButtons();
                return;
            }

            _waiting = true;
            _waitingSince = DateTime.Now;
            _status = string.Format(
                L.S("Probe {0}: {1} disabled. Waiting for Live to open it…",
                    "Проба {0}: отключено {1}. Ждём Live…"),
                _s.Round, RescueSession.Describe(_list.Disabled));
            UpdateButtons();
        }

        void Tick()
        {
            bool live = RescueSession.LiveIsRunning();
            bool changed = live != _liveRunning;
            _liveRunning = live;

            // Пока пробы нет, единственное, что здесь меняется, — запущена ли Live.
            // Перерисовывать окно каждую секунду просто так незачем.
            if (!_waiting) { if (changed) UpdateButtons(); return; }

            LoadAttempt a = _s.Poll();
            if (a == null)
            {
                // Пробу так и не открыли: человек передумал, или Live не запустилась.
                // Через пять минут перестаём ждать, чтобы окно не висело вечно.
                if (DateTime.Now - _waitingSince > TimeSpan.FromMinutes(5)
                    && !RescueSession.LiveIsRunning())
                {
                    _waiting = false;
                    _s.Cancel();
                    _status = L.S("Live never opened the probe. Try again — or open it by hand from the project folder.",
                                  "Live так и не открыла пробу.");
                    UpdateButtons();
                }
                return;
            }

            if (a.Result == LoadResult.Running)
            {
                _status = string.Format(
                    L.S("Probe {0}: Live is loading it — {1} plugins restored so far…",
                        "Проба {0}: Live грузит — восстановлено {1}…"),
                    _s.Round, a.RestoredCount);
                Invalidate();
                return;
            }

            _waiting = false;
            _s.Apply(_s.ProbeDisabled, a.Result == LoadResult.Loaded, a);
            AfterProbe(a);
        }

        void AfterProbe(LoadAttempt a)
        {
            _list.Notes.Clear();
            foreach (AlsPluginSlot s in _s.Suspects) _list.Notes[s.Uid] = L.S("suspect", "под подозрением");
            if (_s.Culprit != null) _list.Notes[_s.Culprit.Uid] = L.S("BREAKS THE SET", "ЛОМАЕТ СЕТ");

            if (_s.Finished) Describe();
            else if (a.Result == LoadResult.Loaded)
                _status = string.Format(
                    L.S("Probe {0} opened. The culprit is among the {1} it had switched off. "
                      + "Close Live and run the next probe.",
                        "Проба {0} открылась — виновник среди отключённых ({1})."),
                    _s.Round, Plural(_s.Suspects.Count, "plugin", "plugins"));
            else
                _status = string.Format(
                    L.S("Probe {0} did not open{1} — the culprit was still enabled. {2} left to check. "
                      + "Close Live and run the next probe.",
                        "Проба {0} не открылась. Осталось проверить: {2}."),
                    _s.Round,
                    a.Hung != null ? " (Live stopped inside " + a.Hung.Name + ")" : "",
                    Plural(_s.Suspects.Count, "plugin", "plugins"));

            _list.SetDisabled(_s.Suggest());
            UpdateButtons();
        }

        // ------------------------------------------------------------------ итог

        void SaveRescued()
        {
            List<AlsPluginSlot> pick = _s.RescueSelection();
            if (pick.Count == 0) return;

            try
            {
                string made = _s.SaveRescued(pick);
                Produced = made;
                _status = string.Format(
                    L.S("Saved “{0}”. It is your set with {1} disabled — everything else, automation included, is untouched. "
                      + "The original is unchanged.",
                        "Сохранено «{0}»: тот же сет с отключённым {1}."),
                    Path.GetFileName(made), RescueSession.Describe(pick));
            }
            catch (Exception ex)
            {
                _status = L.S("Could not save the copy: ", "Не удалось сохранить копию: ") + ex.Message;
                Diag.Fail("rescue: save", ex);
            }
            UpdateButtons();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Пробу за собой убираем всегда: это .als в папке проекта, и оставленный
            // там навсегда он однажды откроется вместо настоящего сета.
            _poll.Stop();
            _s.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _poll.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ раскладка

        Rectangle _statusRect, _listLabel, _hintRect;

        /// <summary>
        /// Три строки FSmall — столько занимает самая длинная подсказка: в ней
        /// разворачивается имя пробного файла, а оно длиной с имя сета.
        /// </summary>
        int HintHeight { get { return Theme.FSmall.Height * 3 + Sc(4); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
        }

        /// <summary>
        /// Ещё раз по показу: размеры кнопок в конструкторе считались по тексту, который
        /// с тех пор поменялся, а Card тогда ещё не был известен.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateButtons();
        }

        void LayoutControls()
        {
            if (_run == null || _list == null) return;

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int w = Card.Width - pad * 2;
            int y = Card.Top + Sc(66);

            _statusRect = new Rectangle(x, y, w, Sc(64));
            y += _statusRect.Height + Sc(14);

            _listLabel = new Rectangle(x, y, w, Sc(20));
            int qy = y - Sc(4);
            int qx = Card.Right - pad;
            foreach (GlassButton b in new GlassButton[] { _suggested, _none, _all })
            {
                if (!_showQuick) continue;
                qx -= b.Width;
                b.SetBounds(qx, qy, b.Width, Sc(26));
                qx -= Sc(6);
            }
            y += Sc(26);

            int by = Card.Bottom - pad - _run.Height;

            // Высоту подсказки считаем от самого шрифта, а не пикселями на глаз: Sc()
            // в Alive — множитель ×1 (см. ReelWindow.OnHandleCreated), а шрифт задан в
            // пунктах и на масштабированном экране растёт сам. От сорока «пикселей»
            // третья строка там уезжала под нижний край — ровно это и было видно.
            _hintRect = new Rectangle(x, by - Sc(8) - HintHeight, w, HintHeight);

            // Высота списка — по целым строкам: половина строки, срезанная нижним краем,
            // читается как обрыв отрисовки, а не как «дальше прокрути». Меньше строки не
            // опускаем, но и не поднимаем принудительно: высота окна уже посчитана под
            // нужное число строк, и любой «минимум» здесь налез бы на подсказку под списком.
            int room = Math.Max(_list.RowHeight, _hintRect.Top - Sc(12) - y);
            _list.SetBounds(x, y, w, room - room % _list.RowHeight);

            _run.Location = new Point(Card.Right - pad - _run.Width, by);
            if (_showRescued)
                _rescued.Location = new Point(_run.Left - Sc(10) - _rescued.Width, by);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Chrome.DrawText(g, _status, Theme.FBody, _statusRect, Theme.Text, Chrome.Wrap);

            if (_s.HasTargets)
            {
                string label = string.Format(
                    L.S("Third-party plugins ({0})", "Сторонние плагины ({0})"), _s.Targets.Count);
                if (_s.Unaddressable > 0)
                    label += string.Format(
                        L.S("  ·  {0} more cannot be identified", "  ·  ещё {0} не опознать"), _s.Unaddressable);
                Chrome.DrawText(g, label, Theme.FLabel, _listLabel, Theme.TextDim,
                                Chrome.Left | TextFormatFlags.NoClipping);
            }

            if (_hint.Length > 0)
                Chrome.DrawText(g, _hint, Theme.FSmall, _hintRect, Theme.TextDim, Chrome.Wrap);
        }
    }

    /// <summary>
    /// Список плагинов с галочками. Своя отрисовка, а не CheckedListBox: нативный список
    /// на этом окне был бы светлым прямоугольником с чужой полосой прокрутки — ровно тем
    /// же, из-за чего в заметках отключено стекло (см. NotesDialog).
    ///
    /// Галочка стоит там же, где и в самой Live у включённого девайса, — то есть означает
    /// «остаётся работать». Сняли галочку — плагин выключен в пробе, и строка гаснет тем
    /// же способом, каким Live сама гасит выключенное устройство. Наружу список отдаёт обе
    /// стороны: Checked — что отмечено в интерфейсе, Disabled — что фактически уйдёт
    /// отключённым в пробу (то есть ровно наоборот).
    /// </summary>
    public sealed class PluginCheckList : GlassControl
    {
        public readonly List<AlsPluginSlot> Items = new List<AlsPluginSlot>();

        /// <summary>Короткая пометка справа от плагина: «под подозрением», «ломает сет».</summary>
        public readonly Dictionary<string, string> Notes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        readonly HashSet<string> _on = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;
        public bool ReadOnly;

        int _scroll;
        int _hover = -1;

        public PluginCheckList()
        {
            Surface = Theme.Backdrop;
            Cursor = Cursors.Hand;
        }

        /// <summary>Шаг строки. Открыт наружу — по нему окно подгоняет высоту списка под целые строки.</summary>
        public int RowHeight { get { return Sc(30); } }

        int RowH { get { return RowHeight; } }
        int Inner { get { return Items.Count * RowH; } }

        public List<AlsPluginSlot> Checked
        {
            get
            {
                List<AlsPluginSlot> r = new List<AlsPluginSlot>();
                foreach (AlsPluginSlot s in Items) if (_on.Contains(s.Uid)) r.Add(s);
                return r;
            }
        }

        /// <summary>Плагины без галочки — то, что фактически отключится в пробе. Дополнение к Checked.</summary>
        public List<AlsPluginSlot> Disabled
        {
            get
            {
                List<AlsPluginSlot> r = new List<AlsPluginSlot>();
                foreach (AlsPluginSlot s in Items) if (!_on.Contains(s.Uid)) r.Add(s);
                return r;
            }
        }

        public void CheckAll(bool on)
        {
            _on.Clear();
            if (on) foreach (AlsPluginSlot s in Items) _on.Add(s.Uid);
            Fire();
        }

        public void Check(List<AlsPluginSlot> only)
        {
            _on.Clear();
            if (only != null) foreach (AlsPluginSlot s in only) _on.Add(s.Uid);
            Fire();
        }

        /// <summary>
        /// Снять галочки ровно у off, остальным — поставить. Дополнение к Check(): та
        /// сторона имеет дело с отмеченным, эта — с тем, что должно отключиться, а
        /// именно в этих терминах думает вызывающий код (Suggest() тоже возвращает то,
        /// что отключить).
        /// </summary>
        public void SetDisabled(List<AlsPluginSlot> off)
        {
            HashSet<string> drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (off != null) foreach (AlsPluginSlot s in off) drop.Add(s.Uid);

            _on.Clear();
            foreach (AlsPluginSlot s in Items) if (!drop.Contains(s.Uid)) _on.Add(s.Uid);
            Fire();
        }

        void Fire()
        {
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // Колесо приходит тому, кто в фокусе, — без этого длинный список не
            // прокручивался бы вовсе (тем же приёмом ловит колесо таблица сетов).
            Focus();
            if (ReadOnly) return;
            int i = RowAt(e.Y);
            if (i < 0) return;
            string uid = Items[i].Uid;
            if (!_on.Remove(uid)) _on.Add(uid);
            Fire();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Y);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Scroll(_scroll - e.Delta / 120 * RowH * 3);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Список стал выше — снизу могла открыться пустота под последней строкой.
            Scroll(_scroll);
        }

        void Scroll(int to)
        {
            int max = Math.Max(0, Inner - Height);
            int v = Math.Max(0, Math.Min(max, to));
            if (v == _scroll) return;
            _scroll = v;
            Invalidate();
        }

        int RowAt(int y)
        {
            int i = (y + _scroll) / RowH;
            return i >= 0 && i < Items.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            int pad = Sc(12);
            int box = Sc(15);

            for (int i = 0; i < Items.Count; i++)
            {
                int top = i * RowH - _scroll;
                if (top + RowH < 0 || top > Height) continue;

                AlsPluginSlot s = Items[i];
                Rectangle row = new Rectangle(0, top, Width, RowH);

                if (i == _hover && !ReadOnly)
                    Theme.FillRound(g, row, Sc(8), Theme.RowHover);

                // Галочка = «остаётся включён», как у девайса в самой Live. Отмеченное —
                // светлая заливка, как у главной кнопки; снятая галочка гасит и подпись
                // строки (см. ниже) — «этот плагин выключен в пробе» читается одним взглядом.
                Rectangle mark = new Rectangle(pad, top + (RowH - box) / 2, box, box);
                bool on = _on.Contains(s.Uid);
                if (on)
                {
                    Theme.FillRound(g, mark, Sc(4), ReadOnly ? Theme.TextDim : Theme.Light);
                    Icons.Draw(g, Glyph.Check,
                               new RectangleF(mark.X + Sc(2), mark.Y + Sc(2), box - Sc(4), box - Sc(4)),
                               Theme.OnLight, 1.8f);
                }
                else
                {
                    using (System.Drawing.Drawing2D.GraphicsPath p =
                           Theme.Round(new RectangleF(mark.X, mark.Y, mark.Width, mark.Height), Sc(4)))
                    using (Pen pen = new Pen(Theme.Hairline, 1.4f))
                        g.DrawPath(pen, p);
                }

                int x = mark.Right + Sc(12);
                Chrome.DrawText(g, s.Format, Theme.FBadge, new Rectangle(x, top, Sc(52), RowH),
                                Theme.TextDim, Chrome.Left);
                x += Sc(58);

                string note;
                bool flagged = Notes.TryGetValue(s.Uid, out note);
                int noteW = flagged ? Sc(130) : 0;
                int vendorW = s.Vendor.Length > 0 ? Sc(150) : 0;
                int nameW = Math.Max(Sc(80), Width - x - noteW - vendorW - pad);

                Chrome.DrawText(g, s.Label, Theme.FBody, new Rectangle(x, top, nameW, RowH),
                                on ? Theme.Text : Theme.TextDim, Chrome.Left);

                if (vendorW > 0)
                    Chrome.DrawText(g, s.Vendor, Theme.FSmall,
                                    new Rectangle(x + nameW, top, vendorW, RowH), Theme.TextDim, Chrome.Left);

                if (flagged)
                    Chrome.DrawText(g, note, Theme.FBadge,
                                    new Rectangle(Width - noteW - pad, top, noteW, RowH),
                                    note.Length > 0 && note == note.ToUpperInvariant() ? Theme.Red : Theme.TextDim,
                                    Chrome.Right);
            }

            if (Items.Count == 0)
                Chrome.DrawText(g, L.S("no third-party plugins in this set", "сторонних плагинов нет"),
                                Theme.FBody, new Rectangle(0, 0, Width, Height), Theme.TextDim, Chrome.Center);

            // Полоска прокрутки — та же волосяная, что и в таблице сетов. Без неё в
            // длинном списке не видно, что под нижним краем есть ещё плагины, а именно
            // они обычно и нужны.
            int over = Inner - Height;
            if (over > 0)
            {
                int h = Math.Max(Sc(30), (int)(Height * (float)Height / Inner));
                int y = (int)((Height - h) * (_scroll / (float)over));
                Theme.FillRound(g, new Rectangle(Width - Sc(5), y, Sc(3), h), Sc(2), Theme.Hairline);
            }
        }
    }
}
