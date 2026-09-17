using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The helper for rescuing a project that will not open.
    ///
    /// It works semi-automatically: the program prepares a probe copy of the set with plugins
    /// disabled and opens it, while Live is started and closed by a person. It does not climb
    /// into somebody else's editor itself — bringing down an unsaved project of theirs along
    /// with the probe would be exactly what this window saves people from.
    ///
    /// There is no need to ask how a probe ended: Live writes it into its own Log.txt, and the
    /// window reads that and shows the outcome by itself (see LiveLog).
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

        bool _waiting;              // the probe is on disk, we are waiting for Live to deliver its verdict
        DateTime _waitingSince;
        string _status = "";
        string _hint = "";

        // What to show is decided by flags of our own rather than through Control.Visible.
        // Until a form is shown, Visible on its children is false, and a layout asked for in
        // the constructor would silently skip every button — they would stay at zero width
        // against the right edge (which is exactly what the first shot of the window showed).
        bool _showQuick = true;
        bool _showRescued;

        /// <summary>
        /// Whether Live is running. The answer is cached: asking it means walking every process
        /// in the system, while UpdateButtons is called both on each click of a checkbox and
        /// once a second by the timer.
        /// </summary>
        bool _liveRunning;

        /// <summary>The set worth showing in the catalog after the window closes — the rescued
        /// copy.</summary>
        public string Produced = "";

        public RescueDialog(SetEntry set, PluginInventory inv)
        {
            _s = new RescueSession(set, inv);

            Caption = "Rescue: " + set.Name;

            // The height follows the number of plugins: a set can have one or fifty, and a
            // window of fixed height is half empty in the first case and scrolls in the second
            // where it could have shown everything at once. Twelve rows is the cap; beyond that
            // the window would run into short screens.
            int rows = Math.Max(3, Math.Min(12, _s.Targets.Count));
            ClientSize = new Size(Sc(780), Sc(280) + HintHeight + rows * _list.RowHeight);

            _list.Items.AddRange(_s.Targets);
            _list.Changed += delegate { UpdateButtons(); };
            Controls.Add(_list);

            // A tick next to a plugin means "stays enabled" (see PluginCheckList), so "All Off"
            // clears every tick and "All On" sets them all — the buttons are named after the
            // result, not after what they do to the ticks.
            Quick(_all, "All Off", delegate { _list.CheckAll(false); });
            Quick(_none, "All On", delegate { _list.CheckAll(true); });
            Quick(_suggested, "Suggested", delegate { _list.SetDisabled(_s.Suggest()); });

            _rescued.Text = "Save rescued copy";
            _rescued.FitToText(16);
            _rescued.Click += delegate { SaveRescued(); };
            Controls.Add(_rescued);

            _run.Primary = true;
            _run.Click += delegate { OnRun(); };
            Controls.Add(_run);

            // A second is the step at which "Live is loading" still looks like a live
            // countdown, and only the tail of the log is re-read, so it costs almost nothing.
            _poll.Interval = 1000;
            _poll.Tick += delegate { Tick(); };
            _poll.Start();

            // It opens with every box ticked — nothing is disabled yet until a person unticks
            // one themselves or presses "All Off"/"Suggested". Putting Suggest() in here
            // automatically would mean preparing the probe for them straight away — and the
            // decision of what to try first has to be a visible, reversible action rather than
            // the window's starting state.
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

        // ------------------------------------------------------------------ state

        /// <summary>The top paragraph: what is known about this set at all right now.</summary>
        void Describe()
        {
            if (_s.Error != null)
            {
                _status = "This .als cannot be read at all: " + _s.Error
                        + ". That is damage to the file itself, not a plugin problem.";
                return;
            }

            if (_s.Targets.Count == 0)
            {
                _status = "This set has no third-party plugins — there is nothing here to switch off. "
                            + "Whatever stops it from opening is somewhere else.";
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
                _status = "Live's log has no record of this set. Untick a plugin to test it, "
                            + "or click “All Off” to disable everything at once.";
                return;
            }

            PluginLoad hung = _s.History.Hung;
            string when = When(_s.History.Started);

            if (_s.History.Result == LoadResult.Loaded)
            {
                _status = string.Format(
                    "Live's log says this set opened normally on {0}, with {1} restored. "
                      + "If it fails now, something changed since — a plugin update, most likely.",
                    when, Chrome.Plural(_s.History.RestoredCount, "plugin"));
                return;
            }

            if (hung != null)
            {
                _status = string.Format(
                    "Live's log stops inside {0} {1} on {2} — it restored {3} and never came back "
                      + "from that one. Untick it below and probe.",
                    hung.Format, hung.Name, when, Chrome.Plural(_s.History.RestoredCount, "plugin"));
                return;
            }

            _status = string.Format(
                "Live's log has an unfinished attempt from {0}, with no plugin left pending — "
                  + "the set may be breaking before the plugins get their turn.",
                when);
        }

        /// <summary>
        /// The date for the interface. The invariant culture rather than the system one: the
        /// interface here is English only, while CurrentCulture on this machine is Russian —
        /// and a localised month name turned up in the middle of an English sentence.
        /// </summary>
        static string When(DateTime t)
        {
            return t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
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
                _run.Text = "Waiting for Live…";
                _run.Enabled = false;
            }
            else if (!usable)
            {
                _run.Text = "Close";
                _run.Enabled = true;
            }
            else if (_s.Finished)
            {
                _run.Text = "Probe again";
                _run.Enabled = _list.Disabled.Count > 0 && !live;
            }
            else
            {
                _run.Text = _s.Round == 0
                          ? "Open probe in Live"
                          : "Next probe";
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
                return "Live is opening the probe. Watch it, then close Live — the answer is read from Live's own log.";
            if (!usable) return "";
            if (live)
                return "Close Ableton Live first — if the probe crashes it would take your open project with it.";
            if (_list.Disabled.Count == 0)
                return "Untick the plugins you want to switch off in the probe.";

            return string.Format(
                "The probe is a copy — “{0}” next to the original. Your set is never modified. Do not save the probe from Live.",
                Path.GetFileName(RescueProbe.PathFor(_s.Set)));
        }

        // ------------------------------------------------------------------ the probe

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
                _status = "Could not start the probe: " + ex.Message;
                Diag.Fail("rescue: run", ex);
                UpdateButtons();
                return;
            }

            _waiting = true;
            _waitingSince = DateTime.Now;
            _status = string.Format(
                "Probe {0}: {1} disabled. Waiting for Live to open it…",
                _s.Round, RescueSession.Describe(_list.Disabled));
            UpdateButtons();
        }

        void Tick()
        {
            bool live = RescueSession.LiveIsRunning();
            bool changed = live != _liveRunning;
            _liveRunning = live;

            // While there is no probe, the only thing that changes here is whether Live is
            // running. Redrawing the window every second for nothing is pointless.
            if (!_waiting) { if (changed) UpdateButtons(); return; }

            LoadAttempt a = _s.Poll();
            if (a == null)
            {
                // The probe was never opened: the person changed their mind, or Live did not
                // start. After five minutes we stop waiting so the window does not hang
                // forever.
                if (DateTime.Now - _waitingSince > TimeSpan.FromMinutes(5)
                    && !RescueSession.LiveIsRunning())
                {
                    _waiting = false;
                    _s.Cancel();
                    _status = "Live never opened the probe. Try again — or open it by hand from the project folder.";
                    UpdateButtons();
                }
                return;
            }

            if (a.Result == LoadResult.Running)
            {
                _status = string.Format(
                    "Probe {0}: Live is loading it — {1} plugins restored so far…",
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
            foreach (AlsPluginSlot s in _s.Suspects) _list.Notes[s.Uid] = "suspect";
            if (_s.Culprit != null) _list.Notes[_s.Culprit.Uid] = "BREAKS THE SET";

            if (_s.Finished) Describe();
            else if (a.Result == LoadResult.Loaded)
                _status = string.Format(
                    "Probe {0} opened. The culprit is among the {1} it had switched off. "
                      + "Close Live and run the next probe.",
                    _s.Round, Chrome.Plural(_s.Suspects.Count, "plugin"));
            else
                _status = string.Format(
                    "Probe {0} did not open{1} — the culprit was still enabled. {2} left to check. "
                      + "Close Live and run the next probe.",
                    _s.Round,
                    a.Hung != null ? " (Live stopped inside " + a.Hung.Name + ")" : "",
                    Chrome.Plural(_s.Suspects.Count, "plugin"));

            _list.SetDisabled(_s.Suggest());
            UpdateButtons();
        }

        // ------------------------------------------------------------------ the outcome

        void SaveRescued()
        {
            List<AlsPluginSlot> pick = _s.RescueSelection();
            if (pick.Count == 0) return;

            try
            {
                string made = _s.SaveRescued(pick);
                Produced = made;
                _status = string.Format(
                    "Saved “{0}”. It is your set with {1} disabled — everything else, automation included, is untouched. "
                      + "The original is unchanged.",
                    Path.GetFileName(made), RescueSession.Describe(pick));
            }
            catch (Exception ex)
            {
                _status = "Could not save the copy: " + ex.Message;
                Diag.Fail("rescue: save", ex);
            }
            UpdateButtons();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // We always clear the probe up after ourselves: it is an .als inside a project
            // folder, and left there for good it will one day be opened instead of the real
            // set.
            _poll.Stop();
            _s.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _poll.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ layout

        Rectangle _statusRect, _listLabel, _hintRect;

        /// <summary>
        /// Two lines of FSmall — enough for an ordinary hint. The longest one (the probe file
        /// name unfolds in it) occasionally takes three; NoClipping in OnPaint lets the third
        /// line spill into the gap above the button rather than be cut off. Always reserving
        /// three lines would mean leaving an empty band above the button under a one-line hint.
        /// ponytail: the window does not grow for a 3rd line. A short file name does not run
        /// into it; if it starts to, compute the height from the actual text wrapping.
        /// </summary>
        int HintHeight { get { return Theme.FSmall.Height * 2 + Sc(4); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
        }

        /// <summary>
        /// Once more on show: the button sizes were computed in the constructor from text that
        /// has changed since, and Card was not known back then.
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
            // Air between the row of buttons (All Off / All On / Suggested) and the list:
            // without it the buttons sit flush against the table and read as a column header
            // that is not there.
            y += Sc(26) + Sc(16);

            int by = Card.Bottom - pad - _run.Height;

            // The hint height is computed from the font itself rather than by eye in pixels:
            // Sc() in Alive is a ×1 multiplier, while the font is set in points and grows by
            // itself on a scaled screen. At forty "pixels" the third line slid under the bottom
            // edge there — which is precisely what could be seen.
            _hintRect = new Rectangle(x, by - Sc(8) - HintHeight, w, HintHeight);

            // The list height goes by whole rows: half a row shaved off by the bottom edge
            // reads as drawing cut short rather than as "scroll for more". We do not go below
            // one row, but neither do we force it up: the window height has already been
            // computed for the number of rows needed, and any "minimum" here would run into the
            // hint under the list.
            int room = Math.Max(_list.RowHeight, _hintRect.Top - Sc(12) - y);
            // No taller than the rows themselves need: the extra height showed as an empty band
            // of background under the last plugin — which read as drawing cut short.
            room = Math.Min(room, _list.Items.Count * _list.RowHeight);
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
                    "Third-party plugins ({0})", _s.Targets.Count);
                if (_s.Unaddressable > 0)
                    label += string.Format(
                        "  ·  {0} more cannot be identified", _s.Unaddressable);
                Chrome.DrawText(g, label, Theme.FLabel, _listLabel, Theme.TextDim,
                                Chrome.Left | TextFormatFlags.NoClipping);
            }

            if (_hint.Length > 0)
                Chrome.DrawText(g, _hint, Theme.FSmall, _hintRect, Theme.TextDim,
                                Chrome.Wrap | TextFormatFlags.NoClipping);
        }
    }

    /// <summary>
    /// A list of plugins with tick boxes. Drawn by hand rather than a CheckedListBox: a native
    /// list on this window would be a pale rectangle with a foreign scrollbar — exactly what
    /// glass is switched off in the notes for (see NotesDialog).
    ///
    /// The tick stands where it does in Live itself on an enabled device — meaning it says
    /// "carries on working". Untick it and the plugin is off in the probe, and the row dims the
    /// same way Live itself dims a disabled device. Outward the list gives both sides: Checked
    /// is what is ticked in the interface, Disabled is what will actually go into the probe
    /// switched off (that is, exactly the opposite).
    /// </summary>
    public sealed class PluginCheckList : GlassControl
    {
        public readonly List<AlsPluginSlot> Items = new List<AlsPluginSlot>();

        /// <summary>A short mark to the right of a plugin: "suspected", "breaks the
        /// set".</summary>
        public readonly Dictionary<string, string> Notes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        readonly HashSet<string> _on = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;
        public bool ReadOnly;

        int _scroll;
        int _hover = -1;

        public PluginCheckList()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Surface = Theme.Backdrop;
            Cursor = Cursors.Hand;
        }

        /// <summary>The row pitch. Exposed outward — the window fits the list height to whole
        /// rows by it.</summary>
        public int RowHeight { get { return Sc(36); } }

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

        /// <summary>The plugins with no tick — what will actually be disabled in the probe. The
        /// complement of Checked.</summary>
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
        /// Untick exactly off and tick the rest. The complement of Check(): that side deals
        /// with what is ticked, this one with what has to be disabled, and those are precisely
        /// the terms the calling code thinks in (Suggest() also returns what to disable).
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
            // The wheel goes to whoever has focus — without this a long list would not scroll
            // at all (the sets table catches the wheel the same way).
            Focus();
            if (ReadOnly) return;
            int i = RowAt(e.Y);
            if (i < 0) return;
            string uid = Items[i].Uid;
            if (!_on.Remove(uid)) _on.Add(uid);
            Fire();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            MouseEventArgs me = e as MouseEventArgs;
            if (me != null) OnMouseDown(me);
            base.OnDoubleClick(e);
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
            // The list grew taller — emptiness under the last row may have opened up below.
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

            // We measure the format column's width by the font rather than taking Sc(): in
            // Alive Sc is ×1 while the text is drawn larger (see RescueDialog.HintHeight), and
            // "VST3" did not fit into a fixed 52 pixels — in the shot all that was left of it
            // was "VS…".
            int fmtW = TextRenderer.MeasureText("VST3", Theme.FBadge).Width + Sc(4);

            for (int i = 0; i < Items.Count; i++)
            {
                int top = i * RowH - _scroll;
                if (top + RowH < 0 || top > Height) continue;

                AlsPluginSlot s = Items[i];
                Rectangle row = new Rectangle(0, top, Width, RowH);

                if (i == _hover && !ReadOnly)
                    Theme.FillRound(g, row, Sc(8), Theme.RowHover);

                // A tick = "stays enabled", as on a device in Live itself. What is ticked gets
                // a light fill, like the primary button; an unticked box also dims the row's
                // caption (see below) — "this plugin is off in the probe" reads at a glance.
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
                Chrome.DrawText(g, s.Format, Theme.FBadge, new Rectangle(x, top, fmtW, RowH),
                                Theme.TextDim, Chrome.Left);
                x += fmtW + Sc(10);

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
                Chrome.DrawText(g, "no third-party plugins in this set",
                                Theme.FBody, new Rectangle(0, 0, Width, Height), Theme.TextDim, Chrome.Center);

            // The scrollbar is the same hairline as in the sets table. Without it a long list
            // gives no sign that there are more plugins below the bottom edge — and those are
            // usually the ones wanted.
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
