using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The render player. A separate modeless window: listening to material and carrying on
    /// sorting the library are usually one and the same occupation, and there is no reason to
    /// block the main window for the duration.
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

        // The playhead's pixel on the previous tick: we redraw the waveform only when the
        // playhead has really moved into a different pixel — otherwise we drive a far from
        // cheap OnPaint (a DrawLine per pixel of width) 25 times a second for a picture that
        // has not changed.
        int _lastHeadPx = -1;

        Rectangle _rSub, _rTimeLeft, _rTimeRight, _rListHead, _rNote;

        // The window's place and the volume are remembered for as long as the program runs — as
        // statics rather than Settings: simply closing and reopening the player should put it
        // back in the same place sounding the same, while a restart of the whole application
        // zeroes that state by itself.
        static Point? _lastLocation;
        static float _lastVolume = 0.5f;

        /// <summary>The set currently open in the player — for the main window to highlight its
        /// row.</summary>
        public SetEntry CurrentSet { get { return _set; } }

        /// <summary>Whether it is playing right now — for the main window's play/pause glyph in
        /// its footer.</summary>
        public bool IsPlaying { get { return _audio.IsPlaying; } }

        public event Action<SetEntry> SetChanged;

        /// <summary>Play↔pause, next/previous track — for the main window's mini transport in
        /// the footer, so the player window need not be raised for a single button.</summary>
        public event Action PlayStateChanged;

        public AudioPlayer Audio { get { return _audio; } }
        public RenderFile CurrentFile
        {
            get { return _current >= 0 && _current < _files.Count ? _files[_current] : null; }
        }
        public string CurrentFileName
        {
            get
            {
                RenderFile f = CurrentFile;
                if (f == null) return "";
                return Path.GetFileName(f.Path);
            }
        }
        public float Volume { get { return _vol.Value; } set { _vol.Value = value; } }
        public void Seek(int ms) { if (_audio != null && _audio.IsOpen) _audio.Seek(ms); }

        public void PlayPause() { TogglePlay(); }
        public void PrevSet() { ChangeSet(-1, true); }
        public void NextSet() { ChangeSet(+1, true); }

        public PlayerDialog()
        {
            Caption = "Player";
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
            _list.DragFilePath = delegate (RowData r) { RenderFile f = r.Tag as RenderFile; return f != null ? f.Path : null; };
            _list.ShowHeaderPin = false;
            // As in MainForm: without a gap the scrollbar sits over the rounded edge of the
            // selection pill across the full width of the row.
            _list.PillRightGap = Sc(20);
            _list.RowPlayClicked += delegate (int i) { PlayIndex(i, true); };
            _list.RowPinClicked += delegate (int i) { if (i >= 0 && i < _files.Count) Pin(_files[i]); };
            _list.ItemActivated += delegate { PlayIndex(_list.Rows.IndexOf(_list.Selected), true); };
            _list.RowRightClicked += OnRowMenu;
            Controls.Add(_list);

            _pin.Text = "Set as Preview";
            _pin.FitToText(18);
            _pin.Click += delegate { PinSelected(); };
            Controls.Add(_pin);

            _reveal.Text = "Show in folder";
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

        // ------------------------------------------------------------------ content

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
                _note = "No renders next to this project (Samples are skipped)";
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
            // We show the folder column only if it is filled in for at least somebody: renders
            // next to a set have no folder, and an empty column with a live heading took up
            // room and said nothing.
            bool anyFolder = false;
            foreach (RenderFile f in _files)
                if (!string.IsNullOrEmpty(f.Folder)) { anyFolder = true; break; }

            List<Column> cols = new List<Column>();
            cols.Add(new Column("File", 0) { Id = "name", Font = Theme.FTitle, Color = Theme.Text });
            if (anyFolder) cols.Add(new Column("Folder", 90) { Id = "dir" });
            cols.Add(new Column("Modified", 150) { Id = "mod" });
            _list.SetColumns(cols.ToArray());

            List<RowData> rows = new List<RowData>();
            foreach (RenderFile f in _files)
            {
                RowData r = new RowData();
                List<string> cells = new List<string>();
                // With the extension: "name.wav" and "name.mp3" of one render usually lie side
                // by side, and without it two rows in the list are indistinguishable.
                cells.Add(f.Name + "." + f.Ext.ToLowerInvariant());
                if (anyFolder) cells.Add(f.Folder);
                cells.Add(f.Modified == default(DateTime) ? "" : f.Modified.ToLocalTime().ToString("yyyy-MM-dd"));
                r.Cells = cells.ToArray();
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

        // ------------------------------------------------------------------ transport

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
            _wave.Hint = "reading…";
            _note = "";

            // We compute the envelope in any case: even if the file did not play, seeing what
            // is in it at all is more use than an empty rectangle.
            RequestPeaks(f.Path);

            // The volume is set BEFORE opening: Open no longer waits for readiness, and the
            // device may still be absent when it returns — the assignment would then simply
            // vanish. Set in advance, it is applied by the output thread itself.
            _audio.Volume = _vol.Value;
            _audio.Open(f.Path, autoStart);
            _openReported = false;
            _atEnd = false;

            UpdatePlayIcon();
            if (PlayStateChanged != null) PlayStateChanged();
            Invalidate(true);
        }

        // Opening a file runs on its own thread and can fail. We learn of a failure from the
        // timer, but it has to be reported once rather than 25 times a second.
        bool _openReported;

        // The track finished and there turned out to be nowhere to go. This needs a "already
        // handled" flag too: AudioPlayer.Finished stays true until the next open.
        bool _atEnd;

        /// <summary>
        /// The envelope is read in the background: a gigabyte master takes noticeably longer
        /// than a frame to parse, while the sound has to start at once. Stale answers are
        /// discarded by request number — tracks can be switched faster than a waveform is
        /// computed.
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
            // The file is still opening — a second Open would simply restart the same thing
            // from the beginning, so we merely note "play when you are ready".
            if (_audio.IsOpening) { _audio.Play(); UpdatePlayIcon(); return; }
            if (!_audio.IsOpen) { PlayIndex(_current >= 0 ? _current : 0, true); return; }
            if (_audio.IsPlaying) _audio.Pause(); else _audio.Play();
            UpdatePlayIcon();
        }

        /// <summary>Returns false if there turned out to be nowhere to go.</summary>
        bool Step(int delta)
        {
            if (_files.Count == 0) return ChangeSet(delta, true);

            int i = _current + delta;
            if (i < 0 || i >= _files.Count) return ChangeSet(delta, true);

            PlayIndex(i, true);
            return true;
        }

        /// <summary>
        /// Moving to a neighbouring set of the playlist. Returns false if there is nothing to
        /// move to. The set we are standing on does not count as a candidate: from the home
        /// page the player opens with a playlist of one set, and the circle used to wrap round
        /// onto it — the track played again endlessly, re-reading the renders from disk each
        /// time. The library from the sets list is still walked in a full circle: there the
        /// neighbours exist, and the circle closes onto itself only at the very end.
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
            // Opening a file runs on its own thread, and this is where we learn of a failure —
            // Open() used to hold the UI thread for up to eight seconds for that answer.
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

            // The player reports the end of a file itself — when the decoder has reached the
            // end and the device's queue has run dry. There is no need to guess from the
            // position.
            //
            // The _atEnd flag is mandatory: Finished stays true until the next open, and
            // without it we would come in here 25 times a second. And if there is nowhere to go
            // we simply stop: on the home page the playlist consists of one set, and the
            // preview used to loop round by itself.
            if (_audio.Finished && !_atEnd)
            {
                _atEnd = true;

                // Where to go next depends on whether the player window is open.
                //
                // The window is closed: the catalog's preview is playing. What is heard is the
                // "demo" — the render marked as the main one (RenderScan.Find puts it first) —
                // and the logical continuation is the demo of the NEXT set, not a second render
                // of the same project. That is what the main render is marked for: one project,
                // one track in the queue.
                //
                // The window is open: the list of the project's renders is in front of you, and
                // walking through it to the end is exactly what is expected of a player.
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

        // ------------------------------------------------------- preview and menu

        void PinSelected() { Pin(SelectedFile()); }

        void Pin(RenderFile f)
        {
            if (f == null || _projectDir.Length == 0) return;
            bool was = f.Pinned;
            foreach (RenderFile o in _files) o.Pinned = false;

            if (was) PreviewPins.Clear(_projectDir);
            else { f.Pinned = true; PreviewPins.Set(_projectDir, f.Path); }

            // The order depends on pinning, so we rebuild the list whole and put the pointer to
            // the current track back in place.
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

            ToolStripMenuItem play = new ToolStripMenuItem("Play");
            play.Click += delegate { PlayIndex(idx, true); };
            m.Items.Add(play);

            ToolStripMenuItem pin = new ToolStripMenuItem(
                f.Pinned ? "Clear preview" : "Set as Preview");
            pin.Checked = f.Pinned;
            pin.Click += delegate { Pin(f); };
            m.Items.Add(pin);

            ToolStripMenuItem show = new ToolStripMenuItem("Show in folder");
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

        // ------------------------------------------------------------------ layout

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

            // The transport is centred in the window and the volume pushed right: the buttons
            // then stay in place at any width, and the slider does not climb into the middle.
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

            // The error message lives to the left of the transport.
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
            // Ordinarily the list is deliberately wider than pad by CellPadX — so that a row's
            // text, inset from the edge of the pill by PadX, lands exactly on pad. Here,
            // though, the selection pill — a visible frame — has to stand level with the
            // scrubber and the buttons, so the list is bounded by that same pad rather than
            // pushed outwards.
            _list.SetBounds(pad, listTop, w, listHeight);
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
        /// The window is modeless, so CenterParent does not work — we place it ourselves: where
        /// the player was closed last time, or in the centre of the owner's screen on first
        /// opening. We compute that on Load rather than on Shown: Shown comes after the window
        /// has appeared on screen, and the coordinates would be applied after a brief flash in
        /// the wrong corner rather than straight into place.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle wa = (Owner != null ? Screen.FromControl(Owner) : Screen.FromPoint(Cursor.Position))
                           .WorkingArea;

            if (_lastLocation.HasValue)
            {
                Point p = _lastLocation.Value;
                // The screen may have changed (a monitor was unplugged) — we do not let the
                // window drive off past the visible area.
                p.X = Math.Max(wa.X, Math.Min(p.X, wa.Right - Width));
                p.Y = Math.Max(wa.Y, Math.Min(p.Y, wa.Bottom - Height));
                Location = p;
                return;
            }

            int x = wa.X + (wa.Width - Width) / 2;
            int y = wa.Y + Math.Max(0, (wa.Height - Height) / 2);
            Location = new Point(Math.Max(wa.X, x), Math.Max(wa.Y, y));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Closing the player window is a Hide rather than destroying the form. Simply hiding
        /// an active window with ShowInTaskbar makes Windows move the focus to the previously
        /// active application (a browser, Explorer and so on) rather than to the manager's main
        /// window. We explicitly activate the owner before hiding and confirm it afterwards.
        /// </summary>
        void Dismiss()
        {
            _lastLocation = Location;
            _lastVolume = _vol.Value;

            ActivateFallback();
            Hide();
            ActivateFallback();
        }

        void ActivateFallback()
        {
            Form target = null;

            // 1. If a modal dialog is open on top (the filters or the settings, say) — the
            // focus has to return to it rather than to a disabled main window.
            for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
            {
                Form f = Application.OpenForms[i];
                if (f != this && f.Visible && !f.IsDisposed && f.Enabled && f.WindowState != FormWindowState.Minimized)
                {
                    if (f.Modal) { target = f; break; }
                }
            }

            // 2. Otherwise — the window owner (the manager's main window).
            if (target == null && Owner != null && !Owner.IsDisposed && Owner.Visible && Owner.Enabled &&
                Owner.WindowState != FormWindowState.Minimized)
            {
                target = Owner;
            }

            // 3. Any other active and visible form of the application.
            if (target == null)
            {
                for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                {
                    Form f = Application.OpenForms[i];
                    if (f != this && f.Visible && !f.IsDisposed && f.Enabled && f.WindowState != FormWindowState.Minimized)
                    {
                        target = f;
                        break;
                    }
                }
            }

            if (target != null && target.IsHandleCreated)
            {
                try
                {
                    SetForegroundWindow(target.Handle);
                    target.Activate();
                }
                catch { }
            }
        }

        bool _shuttingDown;

        /// <summary>
        /// Close for good — together with the sound and the mini transport. The player window's
        /// own cross only hides it (listening and carrying on sorting the library are one and
        /// the same occupation), while the cross in the mini strip has to mean "enough".
        /// </summary>
        public void ShutDown()
        {
            _shuttingDown = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_shuttingDown)
            {
                e.Cancel = true;
                Dismiss();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>
        /// The media keys work when focus is in the player window rather than the main one too:
        /// WM_APPCOMMAND goes to whichever window is active right now. The rule is the same —
        /// "next track" means the next set, while Ctrl+←/→ walk between the renders of one set.
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
                Dismiss();
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
    /// The envelope with navigation. The bars are drawn from the computed peaks; the part
    /// already played is light and the remainder muted — the position is visible without a
    /// slider. If there are no peaks (not a WAV), an honest progress bar remains rather than an
    /// invented waveform.
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

                // The unplayed part is lighter than TextDim: on a recessed background the
                // caption grey merges into a dirty smudge, while the waveform has to read in
                // full.
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

            // The playhead has rounded ends — otherwise a 1.5px line breaks off square and
            // reads on the waveform as a stray bar.
            using (Pen head = new Pen(Color.White, 1.5f))
            {
                head.StartCap = LineCap.Round;
                head.EndCap = LineCap.Round;
                g.DrawLine(head, playedX, inner.Y + 1, playedX, inner.Bottom - 1);
            }
        }
    }

    /// <summary>Volume: a glyph and a track with a knob — in the same language as the other
    /// pills.</summary>
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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta != 0)
            {
                int steps = e.Delta / 120;
                if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                float newVal = (float)Math.Round((_value + steps * 0.05f) / 0.05f) * 0.05f;
                Value = Math.Max(0f, Math.Min(1f, newVal));
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            float iconW = Sc(17);
            float iconH = Sc(13);
            RectangleF iconRect = new RectangleF(Sc(3), (Height - iconH) / 2f, iconW, iconH);
            Color iconColor = Hot || _drag ? Color.White : Theme.Light;
            Glyph volGlyph = _value <= 0.001f ? Glyph.Volume0
                           : _value <= 0.5f ? Glyph.VolumeLow
                           : Glyph.VolumeHigh;
            Icons.Draw(g, volGlyph, iconRect, iconColor, 1.5f);

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
