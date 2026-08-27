using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Плеер рендеров. Отдельное немодальное окно: слушать материал и продолжать
    /// разбирать библиотеку — обычно одно и то же занятие, и блокировать главное окно
    /// на время прослушивания незачем.
    /// </summary>
    public sealed class PlayerDialog : GlassDialog
    {
        readonly WaveView _wave = new WaveView();
        readonly IconButton _setPrev = new IconButton();
        readonly IconButton _prev = new IconButton();
        readonly IconButton _playBtn = new IconButton();
        readonly IconButton _next = new IconButton();
        readonly IconButton _setNext = new IconButton();
        readonly VolumeSlider _vol = new VolumeSlider();
        readonly RowListView _list = new RowListView();
        readonly GlassButton _pin = new GlassButton();
        readonly GlassButton _reveal = new GlassButton();

        readonly AudioPlayer _audio = new AudioPlayer();
        readonly System.Windows.Forms.Timer _audioTick = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer _fadeTimer = new System.Windows.Forms.Timer();

        public void TriggerEntrance()
        {
            Opacity = 0.05f;
            _list.TriggerEntrance();
            if (!_fadeTimer.Enabled) _fadeTimer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                TriggerEntrance();
            }
        }

        SetEntry _set;
        string _projectDir = "";
        List<RenderFile> _files = new List<RenderFile>();
        int _current = -1;
        readonly List<SetEntry> _playlist = new List<SetEntry>();
        int _playlistIndex = -1;

        int _peakSeq;
        string _note = "";

        // Пиксель головки воспроизведения на прошлом тике: волну перерисовываем,
        // только когда головка реально перешла в другой пиксель, — иначе 25 раз в
        // секунду гоняем недешёвый OnPaint (по DrawLine на каждый пиксель ширины)
        // ради картинки, которая не изменилась.
        int _lastHeadPx = -1;

        Rectangle _rSub, _rTimeLeft, _rTimeRight, _rListHead, _rNote;

        // Место окна и громкость помним, пока работает программа, — статика, а не
        // Settings: если просто закрыть и снова открыть плеер, он должен встать туда же
        // и звучать так же, но при перезапуске всего приложения это состояние
        // обнуляется само собой.
        static Point? _lastLocation;
        static float _lastVolume = 0.5f;

        /// <summary>Сет, который сейчас открыт в плеере, — главному окну для подсветки строки.</summary>
        public SetEntry CurrentSet { get { return _set; } }

        /// <summary>Играет ли сейчас — главному окну для значка play/pause в своём футере.</summary>
        public bool IsPlaying { get { return _audio.IsPlaying; } }

        public event Action<SetEntry> SetChanged;

        /// <summary>Play↔pause, следующий/предыдущий трек — главному окну для мини-
        /// транспорта в футере, чтобы не поднимать окно плеера ради одной кнопки.</summary>
        public event Action PlayStateChanged;

        public AudioPlayer Audio { get { return _audio; } }
        public float Volume { get { return _vol.Value; } set { _vol.Value = value; } }
        public void Seek(int ms) { if (_audio != null && _audio.IsOpen) _audio.Seek(ms); }

        public void PlayPause() { TogglePlay(); }
        public void PrevSet() { ChangeSet(-1, true); }
        public void NextSet() { ChangeSet(+1, true); }

        public PlayerDialog()
        {
            Caption = L.S("Player", "Плеер");
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            if (Glass.AppIcon != null) Icon = Glass.AppIcon;
            ClientSize = new Size(Sc(720), Sc(560));

            _setPrev.Icon = Glyph.PrevSet;
            _setPrev.IconScale = 0.32f;
            _setPrev.Click += delegate { ChangeSet(-1, true); };
            _setNext.Icon = Glyph.NextSet;
            _setNext.IconScale = 0.32f;
            _setNext.Click += delegate { ChangeSet(+1, true); };

            _prev.Icon = Glyph.PrevTrack;
            _next.Icon = Glyph.NextTrack;
            _playBtn.Icon = Glyph.Play;
            _playBtn.IconScale = 0.42f;
            _prev.Click += delegate { Step(-1); };
            _next.Click += delegate { Step(+1); };
            _playBtn.Click += delegate { TogglePlay(); };
            Controls.Add(_setPrev); Controls.Add(_prev); Controls.Add(_playBtn); Controls.Add(_next); Controls.Add(_setNext);

            _vol.Value = _lastVolume;
            _vol.ValueChanged += delegate { _audio.Volume = _vol.Value; };
            Controls.Add(_vol);

            _wave.Seeked += OnSeek;
            Controls.Add(_wave);

            _list.ShowPlayButton = true;
            _list.ShowPinIndicator = true;
            _list.ShowHeaderPin = false;
            _list.FadeBottom = false;
            _list.RowPlayClicked += delegate (int i) { PlayIndex(i, true); };
            _list.RowPinClicked += delegate (int i) { if (i >= 0 && i < _files.Count) Pin(_files[i]); };
            _list.ItemActivated += delegate { PlayIndex(_list.Rows.IndexOf(_list.Selected), true); };
            _list.RowRightClicked += OnRowMenu;
            Controls.Add(_list);

            _pin.Text = L.S("Set as Preview", "Сделать главным");
            _pin.FitToText(18);
            _pin.Click += delegate { PinSelected(); };
            Controls.Add(_pin);

            _reveal.Text = L.S("Show in folder", "Показать в папке");
            _reveal.FitToText(18);
            _reveal.Click += delegate { Reveal(SelectedFile()); };
            Controls.Add(_reveal);

            _audioTick.Interval = 40;
            _audioTick.Tick += delegate { OnTick(); };
            _audioTick.Start();

            _fadeTimer.Interval = 16;
            _fadeTimer.Tick += delegate
            {
                float diff = 1.0f - (float)Opacity;
                if (diff > 0.02f)
                {
                    Opacity += diff * 0.35f;
                }
                else
                {
                    Opacity = 1.0f;
                    _fadeTimer.Stop();
                }
            };
        }

        // ------------------------------------------------------------- содержимое

        public void LoadSet(SetEntry s, List<SetEntry> playlist = null, int playlistIndex = -1)
        {
            if (s == null) return;
            bool sameSet = _set != null && ReferenceEquals(_set, s);
            _set = s;
            Caption = s.Name;
            _projectDir = RenderScan.ProjectRoot(s);
            _files = RenderScan.Find(s);
            _note = "";

            if (playlist != null && playlist.Count > 0)
            {
                List<SetEntry> nextPlaylist = new List<SetEntry>(playlist);
                _playlist.Clear();
                _playlist.AddRange(nextPlaylist);
                _playlistIndex = FindPlaylistIndex(s);
                if (_playlistIndex < 0) _playlistIndex = playlistIndex >= 0 && playlistIndex < _playlist.Count ? playlistIndex : 0;
            }
            else
            {
                _playlist.Clear();
                _playlist.Add(s);
                _playlistIndex = 0;
            }

            FillList();

            if (_files.Count == 0)
            {
                _audio.Close();
                _wave.Wave = null;
                _wave.Progress = 0f;
                _note = L.S("No renders next to this project (Samples are skipped)",
                            "Рядом с проектом нет рендеров (Samples не в счёт)");
            }
            else if (!sameSet || !_audio.IsOpen || _current < 0)
            {
                _current = -1;
                PlayIndex(0, true);
            }

            if (SetChanged != null) SetChanged(_set);
            Invalidate(true);
        }

        int FindPlaylistIndex(SetEntry s)
        {
            if (s == null || _playlist.Count == 0) return -1;
            for (int i = 0; i < _playlist.Count; i++)
                if (ReferenceEquals(_playlist[i], s)) return i;
            return -1;
        }

        void FillList()
        {
            _list.SetColumns(
                new Column(L.S("File", "Файл"), 0) { Id = "name", Font = Theme.FTitle, Color = Theme.Text },
                new Column(L.S("Folder", "Папка"), 90) { Id = "dir" },
                new Column(L.S("Modified", "Изменён"), 150) { Id = "mod" });

            List<RowData> rows = new List<RowData>();
            foreach (RenderFile f in _files)
            {
                RowData r = new RowData();
                r.Cells = new string[] {
                    // С расширением: рядом обычно лежат «имя.wav» и «имя.mp3» одного
                    // рендера, и без него две строки в списке неотличимы.
                    f.Name + "." + f.Ext.ToLowerInvariant(),
                    // Корень проекта — случай по умолчанию, и подписывать его нечем:
                    // пустая ячейка сама говорит «файл лежит рядом с сетом».
                    f.Folder,
                    f.Modified == default(DateTime) ? "" : f.Modified.ToLocalTime().ToString("yyyy-MM-dd")
                };
                r.Pinned = f.Pinned;
                r.Tag = f;
                rows.Add(r);
            }
            _list.SetRows(rows);
        }

        RenderFile SelectedFile()
        {
            RowData r = _list.Selected;
            return r != null ? r.Tag as RenderFile : null;
        }

        // -------------------------------------------------------------- транспорт

        void PlayIndex(int i, bool autoStart)
        {
            if (i < 0 || i >= _files.Count) return;
            _current = i;
            _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, _files[i]); });
            _list.PlayingTag = _files[i];

            RenderFile f = _files[i];
            _wave.Wave = null;
            _wave.Progress = 0f;
            _lastHeadPx = -1;
            _wave.Hint = L.S("reading…", "читаю…");
            _note = "";

            // Огибающую считаем в любом случае: даже если файл не заиграл, увидеть,
            // что в нём вообще есть, полезнее пустого прямоугольника.
            RequestPeaks(f.Path);

            // Громкость задаём ДО открытия: Open теперь не ждёт готовности, и после
            // возврата устройства ещё может не быть — тогда присваивание просто
            // пропало бы. Заданную заранее, её применит сам поток вывода.
            _audio.Volume = _vol.Value;
            _audio.Open(f.Path, autoStart);
            _openReported = false;
            _atEnd = false;

            UpdatePlayIcon();
            Invalidate(true);
        }

        // Открытие файла идёт в своём потоке и может не удаться. Про неудачу узнаём
        // из таймера, но сообщить о ней надо один раз, а не 25 раз в секунду.
        bool _openReported;

        // Трек доиграл, и переходить оказалось некуда. Тоже нужен признак «уже
        // отработали»: AudioPlayer.Finished остаётся true до следующего открытия.
        bool _atEnd;

        /// <summary>
        /// Огибающая читается в фоне: гигабайтный мастер разбирается заметно дольше
        /// кадра, а звук должен пойти сразу. Устаревшие ответы отбрасываем по номеру
        /// запроса — переключать треки можно быстрее, чем считается волна.
        /// </summary>
        void RequestPeaks(string path)
        {
            int seq = ++_peakSeq;
            int buckets = Math.Max(240, _wave.Width > 0 ? _wave.Width : 700);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Waveform w = WaveReader.Read(path, buckets);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (seq != _peakSeq || IsDisposed) return;
                        _wave.Wave = w.Ok ? w : null;
                        _wave.Hint = w.Ok ? "" : w.Note;
                        _wave.Invalidate();
                    });
                }
                catch { }
            });
        }

        void TogglePlay()
        {
            // Файл ещё открывается — второй Open просто перезапустил бы то же самое
            // с начала, поэтому просто отмечаем «играть, как будешь готов».
            if (_audio.IsOpening) { _audio.Play(); UpdatePlayIcon(); return; }
            if (!_audio.IsOpen) { PlayIndex(_current >= 0 ? _current : 0, true); return; }
            if (_audio.IsPlaying) _audio.Pause(); else _audio.Play();
            UpdatePlayIcon();
        }

        /// <summary>Возвращает false, если идти оказалось некуда.</summary>
        bool Step(int delta)
        {
            if (_files.Count == 0) return ChangeSet(delta, true);

            int i = _current + delta;
            if (i < 0 || i >= _files.Count) return ChangeSet(delta, true);

            PlayIndex(i, true);
            return true;
        }

        /// <summary>
        /// Переход на соседний сет плейлиста. Возвращает false, если переходить не на
        /// что. Сет, на котором стоим, кандидатом не считается: с главной плеер
        /// открывается плейлистом из одного сета, и раньше круг заворачивался на него
        /// же — трек играл заново без конца, каждый раз перечитывая рендеры с диска.
        /// Библиотека из списка сетов при этом по-прежнему обходится по кругу целиком:
        /// там соседи есть, и на себя круг замыкается только в самом конце.
        /// </summary>
        bool ChangeSet(int delta, bool autoStart)
        {
            if (_playlist.Count == 0) return false;

            int index = _playlistIndex;
            if (index < 0) index = FindPlaylistIndex(_set);
            if (index < 0) index = 0;

            for (int step = 1; step <= _playlist.Count; step++)
            {
                int candidateIndex = (index + delta * step + _playlist.Count) % _playlist.Count;
                if (candidateIndex == index) continue;
                SetEntry candidate = _playlist[candidateIndex];
                if (candidate == null || !candidate.HasRenders) continue;

                _playlistIndex = candidateIndex;
                LoadSet(candidate, _playlist, candidateIndex);
                return true;
            }
            return false;
        }

        void OnSeek(float t)
        {
            if (!_audio.IsOpen) return;
            int len = _audio.Length;
            if (len <= 0) return;
            _audio.Seek((int)(t * len));
            _wave.Progress = t;
            _lastHeadPx = -1;
            _wave.Invalidate();
        }

        void UpdatePlayIcon()
        {
            Glyph want = _audio.IsPlaying ? Glyph.Pause : Glyph.Play;
            if (_playBtn.Icon != want)
            {
                _playBtn.Icon = want;
                _playBtn.Invalidate();
                if (PlayStateChanged != null) PlayStateChanged();
            }
        }

        void OnTick()
        {
            // Открытие файла идёт в своём потоке, и про неудачу мы узнаём отсюда —
            // раньше ради этого ответа Open() держал поток интерфейса до восьми секунд.
            if (!_openReported && _audio.OpenFailed)
            {
                _openReported = true;
                _note = _audio.Error;
                UpdatePlayIcon();
                Invalidate(true);
            }

            if (!_audio.IsOpen) return;

            int len = _audio.Length, pos = _audio.Position;

            _wave.Progress = len > 0 ? Math.Min(1f, pos / (float)len) : 0f;

            int headPx = (int)(_wave.Width * _wave.Progress);
            if (headPx != _lastHeadPx)
            {
                _wave.Invalidate();
                _lastHeadPx = headPx;
            }

            Invalidate(_rTimeLeft);
            Invalidate(_rTimeRight);
            UpdatePlayIcon();

            // Конец файла плеер сообщает сам — когда декодер дошёл до конца и очередь
            // устройства опустела. Гадать по позиции не нужно.
            //
            // Признак _atEnd обязателен: Finished остаётся true до следующего открытия,
            // и без него сюда заходили бы 25 раз в секунду. А если идти некуда — просто
            // останавливаемся: на главной плейлист состоит из одного сета, и раньше
            // предпрослушка заводилась по кругу сама собой.
            if (_audio.Finished && !_atEnd)
            {
                _atEnd = true;

                // Куда идти дальше — зависит от того, открыто ли окно плеера.
                //
                // Окно закрыто: играет предпрослушка из каталога. Слышно «демку» —
                // рендер, помеченный главным (RenderScan.Find ставит его первым), — и
                // логичное продолжение это демка СЛЕДУЮЩЕГО сета, а не второй рендер
                // того же проекта. Ради этого главный рендер и помечают: один проект —
                // один трек в очереди.
                //
                // Окно открыто: перед глазами список рендеров проекта, и обход по нему
                // до конца — ровно то, чего от плеера ждут.
                bool advanced = Visible ? Step(+1) : ChangeSet(+1, true);
                if (!advanced) { _audio.Pause(); UpdatePlayIcon(); }
            }
        }

        static string Time(int ms)
        {
            if (ms < 0) ms = 0;
            int total = ms / 1000;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        // ------------------------------------------------------------ превью и меню

        void PinSelected() { Pin(SelectedFile()); }

        void Pin(RenderFile f)
        {
            if (f == null || _projectDir.Length == 0) return;
            bool was = f.Pinned;
            foreach (RenderFile o in _files) o.Pinned = false;

            if (was) PreviewPins.Clear(_projectDir);
            else { f.Pinned = true; PreviewPins.Set(_projectDir, f.Path); }

            // Порядок зависит от закрепления, поэтому пересобираем список целиком и
            // возвращаем на место указатель на текущий трек.
            RenderFile playing = _current >= 0 && _current < _files.Count ? _files[_current] : null;
            _files = RenderScan.Find(_set);
            FillList();
            _current = playing != null ? IndexOf(playing.Path) : -1;
            if (_current >= 0)
            {
                _list.PlayingTag = _files[_current];
                _list.SelectRow(delegate (RowData r) { return ReferenceEquals(r.Tag, _files[_current]); });
            }
            Invalidate(true);
        }

        int IndexOf(string path)
        {
            for (int i = 0; i < _files.Count; i++)
                if (string.Equals(_files[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void OnRowMenu(int idx, Point at)
        {
            if (idx < 0 || idx >= _files.Count) return;
            RenderFile f = _files[idx];

            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem play = new ToolStripMenuItem(L.S("Play", "Играть"));
            play.Click += delegate { PlayIndex(idx, true); };
            m.Items.Add(play);

            ToolStripMenuItem pin = new ToolStripMenuItem(
                f.Pinned ? L.S("Clear preview", "Снять главный") : L.S("Set as Preview", "Сделать главным"));
            pin.Checked = f.Pinned;
            pin.Click += delegate { Pin(f); };
            m.Items.Add(pin);

            ToolStripMenuItem show = new ToolStripMenuItem(L.S("Show in folder", "Показать в папке"));
            show.Click += delegate { Reveal(f); };
            m.Items.Add(show);

            m.Show(_list, at);
        }

        static void Reveal(RenderFile f)
        {
            if (f == null) return;
            try
            {
                if (File.Exists(f.Path)) Process.Start("explorer.exe", "/select,\"" + f.Path + "\"");
            }
            catch { }
        }

        // -------------------------------------------------------------- раскладка

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            int pad = Sc(Theme.Pad);
            int w = ClientSize.Width - pad * 2;
            int icon = Sc(Theme.IconSize);

            _rSub = new Rectangle(pad, Sc(54), w - Sc(60), Sc(22));

            int waveTop = Sc(74), waveH = Sc(128);
            _wave.SetBounds(pad, waveTop, w, waveH);

            int timeY = waveTop + waveH + Sc(4);
            _rTimeLeft = new Rectangle(pad, timeY, Sc(120), Sc(22));
            _rTimeRight = new Rectangle(pad + w - Sc(120), timeY, Sc(120), Sc(22));

            // Транспорт по центру окна, громкость — прижата вправо: так кнопки остаются
            // на месте при любой ширине, а регулятор не лезет в середину.
            int trY = timeY + Sc(18);
            int big = Sc(58), gap = Sc(10);
            int cx = ClientSize.Width / 2;
            int setW = Sc(34), setH = Sc(34);
            _playBtn.SetBounds(cx - big / 2, trY, big, big);
            _prev.SetBounds(_playBtn.Left - gap - icon, trY + (big - icon) / 2, icon, icon);
            _next.SetBounds(_playBtn.Right + gap, trY + (big - icon) / 2, icon, icon);
            _setPrev.SetBounds(_prev.Left - gap - setW, trY + (big - setH) / 2, setW, setH);
            _setNext.SetBounds(_next.Right + gap, trY + (big - setH) / 2, setW, setH);

            int volW = Sc(140);
            _vol.SetBounds(pad + w - volW, trY + (big - Sc(Theme.ControlH)) / 2, volW, Sc(Theme.ControlH));

            // Сообщение об ошибке живёт слева от транспорта.
            _rNote = new Rectangle(pad, trY + (big - Sc(22)) / 2,
                                   Math.Max(Sc(60), _setPrev.Left - Sc(10) - pad), Sc(22));

            int headY = trY + big + Sc(2);
            _rListHead = new Rectangle(pad, headY, w, Sc(22));

            int btnH = Sc(Theme.ControlH);
            int footY = ClientSize.Height - pad - btnH;
            _pin.SetBounds(pad + w - _pin.Width, footY, _pin.Width, btnH);
            _reveal.SetBounds(_pin.Left - Sc(10) - _reveal.Width, footY, _reveal.Width, btnH);

            int listTop = headY + Sc(2);
            int listHeight = Math.Max(Sc(60), footY - Sc(10) - listTop);
            _list.SetBounds(pad - Sc(Theme.CellPadX), listTop,
                            w + Sc(Theme.CellPadX) * 2,
                            listHeight);
            _list.Visible = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            string sub = "";
            Chrome.DrawText(g, sub, Theme.FSmall, _rSub, Theme.TextDim, Chrome.Left);

            int len = _audio.IsOpen ? _audio.Length : 0;
            int pos = _audio.IsOpen ? _audio.Position : 0;
            Chrome.DrawText(g, Time(pos), Theme.FLabel, _rTimeLeft, Theme.Text, Chrome.Left);
            Chrome.DrawText(g, len > 0 ? Time(len) : "", Theme.FLabel, _rTimeRight, Theme.TextDim, Chrome.Right);

        }

        /// <summary>
        /// Окно немодальное, поэтому CenterParent не работает — ставим сами: там же, где
        /// плеер закрыли в прошлый раз, либо по центру экрана владельца при первом
        /// открытии. Считаем это на Load, а не на Shown: Shown уже после того, как окно
        /// показалось на экране, — координаты применились бы после короткой вспышки не
        /// в том углу, а не сразу на месте.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle wa = (Owner != null ? Screen.FromControl(Owner) : Screen.FromPoint(Cursor.Position))
                           .WorkingArea;

            if (_lastLocation.HasValue)
            {
                Point p = _lastLocation.Value;
                // Экран мог измениться (монитор отключили) — не даём окну уехать за
                // пределы видимой области.
                p.X = Math.Max(wa.X, Math.Min(p.X, wa.Right - Width));
                p.Y = Math.Max(wa.Y, Math.Min(p.Y, wa.Bottom - Height));
                Location = p;
                return;
            }

            int x = wa.X + (wa.Width - Width) / 2;
            int y = wa.Y + Math.Max(0, (wa.Height - Height) / 2);
            Location = new Point(Math.Max(wa.X, x), Math.Max(wa.Y, y));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _lastLocation = Location;
                _lastVolume = _vol.Value;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>
        /// Медиаклавиши работают и когда фокус в окне плеера, а не в главном: WM_APPCOMMAND
        /// приходит тому окну, которое сейчас активно. Правило то же — «следующий трек»
        /// это следующий сет, между рендерами одного сета ходят Ctrl+←/→.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            switch (MediaKeys.Parse(m))
            {
                case MediaKeys.Cmd.Next: ChangeSet(+1, true); m.Result = (IntPtr)1; return;
                case MediaKeys.Cmd.Prev: ChangeSet(-1, true); m.Result = (IntPtr)1; return;
                case MediaKeys.Cmd.Play:
                case MediaKeys.Cmd.PlayPause: TogglePlay(); m.Result = (IntPtr)1; return;
                case MediaKeys.Cmd.Stop:
                case MediaKeys.Cmd.Pause:
                    if (_audio.IsPlaying) TogglePlay();
                    m.Result = (IntPtr)1;
                    return;
            }
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { TogglePlay(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Right && e.Control) { Step(+1); e.Handled = true; return; }
            if (e.KeyCode == Keys.Left && e.Control) { Step(-1); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape)
            {
                _lastLocation = Location;
                _lastVolume = _vol.Value;
                Hide();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lastLocation = Location;
            _lastVolume = _vol.Value;
            _fadeTimer.Stop();
            _fadeTimer.Dispose();
            _audioTick.Stop();
            _audioTick.Dispose();
            _audio.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// Огибающая с навигацией. Столбики рисуются из посчитанных пиков; сыгранная часть
    /// светлая, остаток приглушён — положение видно и без бегунка. Если пиков нет
    /// (не WAV), остаётся честная полоса прогресса, а не выдуманная волна.
    /// </summary>
    public sealed class WaveView : GlassControl
    {
        public Waveform Wave;
        public float Progress;
        public string Hint = "";

        public event Action<float> Seeked;

        bool _drag;

        public WaveView() { Cursor = Cursors.Hand; }

        float At(int x)
        {
            int pad = Sc(10);
            float t = (x - pad) / (float)Math.Max(1, Width - pad * 2);
            return Math.Max(0f, Math.Min(1f, t));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _drag = true;
            if (Seeked != null) Seeked(At(e.X));
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag && Seeked != null) Seeked(At(e.X));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            Rectangle box = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, box, Sc(Theme.CardR), Theme.Sunken);

            int pad = Sc(10);
            Rectangle inner = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
            if (inner.Width <= 4 || inner.Height <= 4) return;

            int playedX = inner.X + (int)(inner.Width * Math.Max(0f, Math.Min(1f, Progress)));

            if (Wave != null && Wave.Ok && Wave.Max.Length > 0)
            {
                int n = Wave.Max.Length;
                float cy = inner.Y + inner.Height / 2f;
                float half = inner.Height / 2f - 1f;

                // Несыгранная часть светлее TextDim: на утопленном фоне подписи-серый
                // сливается в грязное пятно, а волна должна читаться целиком.
                using (Pen dim = new Pen(Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93)))
                using (Pen lit = new Pen(Theme.Light))
                {
                    for (int px = 0; px < inner.Width; px++)
                    {
                        int i = (int)((long)px * n / inner.Width);
                        if (i >= n) i = n - 1;
                        float top = cy - Wave.Max[i] * half;
                        float bot = cy - Wave.Min[i] * half;
                        if (bot - top < 1f) { top = cy - 0.5f; bot = cy + 0.5f; }
                        int x = inner.X + px;
                        g.DrawLine(x < playedX ? lit : dim, x, top, x, bot);
                    }
                }
            }
            else
            {
                int barH = Sc(6);
                Rectangle track = new Rectangle(inner.X, inner.Y + (inner.Height - barH) / 2,
                                                inner.Width, barH);
                Theme.FillRound(g, track, barH / 2f, Theme.Surface);
                Rectangle done = new Rectangle(track.X, track.Y, Math.Max(0, playedX - track.X), barH);
                if (done.Width > 0) Theme.FillRound(g, done, barH / 2f, Theme.Light);

                if (Hint.Length > 0)
                    Chrome.DrawText(g, Hint, Theme.FLabel,
                        new Rectangle(inner.X, inner.Y, inner.Width, inner.Height / 2 - Sc(4)),
                        Theme.TextDim, Chrome.Center);
            }

            using (Pen head = new Pen(Theme.Text, 1.5f))
                g.DrawLine(head, playedX, inner.Y, playedX, inner.Bottom);
        }
    }

    /// <summary>Громкость: значок и дорожка с ручкой — тем же языком, что и остальные пилюли.</summary>
    public sealed class VolumeSlider : GlassControl
    {
        float _value = 0.5f;
        bool _drag;

        public event EventHandler ValueChanged;

        public VolumeSlider() { Cursor = Cursors.Hand; Height = Theme.ControlH; }

        public float Value
        {
            get { return _value; }
            set
            {
                float v = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(v - _value) < 0.001f) return;
                _value = v; Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        Rectangle Track
        {
            get
            {
                int x = Sc(28);
                return new Rectangle(x, Height / 2 - Sc(2), Math.Max(Sc(20), Width - x - Sc(8)), Sc(4));
            }
        }

        void Grab(int x)
        {
            Rectangle t = Track;
            Value = (x - t.X) / (float)Math.Max(1, t.Width);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _drag = true; Grab(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (_drag) Grab(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            float box = Sc(18);
            Icons.Draw(g, _value <= 0.001f ? Glyph.Mute : Glyph.Volume,
                       new RectangleF(Sc(2), (Height - box) / 2f, box, box), Theme.TextDim, 1.4f);

            Rectangle t = Track;
            Theme.FillRound(g, t, t.Height / 2f, Theme.Surface);
            int done = (int)(t.Width * _value);
            if (done > 0)
                Theme.FillRound(g, new Rectangle(t.X, t.Y, done, t.Height), t.Height / 2f, Theme.Light);

            float knob = Sc(11);
            Theme.FillRound(g, new RectangleF(t.X + done - knob / 2f, Height / 2f - knob / 2f, knob, knob),
                            knob / 2f, Hot || _drag ? Color.White : Theme.Light);
        }
    }
}
