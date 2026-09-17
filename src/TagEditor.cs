using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The project tag field: the tags already set lie as pills, the caret stands right after
    /// the last one, and a new tag is typed here and closed with a comma. It differs from
    /// TagField in that the values are not picked from a ready list but invented — which is why
    /// a real TextBox lives inside: the caret, the selection and the keyboard layouts come for
    /// free, while drawing all of that by hand would mean writing it again.
    ///
    /// The field is native, so the dialog showing it switches glass off — see the comment on
    /// NotesDialog.
    /// </summary>
    public sealed class TagEditor : GlassControl
    {
        public readonly List<string> Tags = new List<string>();
        public readonly TextBox Box = new TextBox();

        /// <summary>The placeholder in an empty field.</summary>
        public string Cue = "";

        /// <summary>The tag list changed — from outside that is a reason to rebuild the
        /// layout.</summary>
        public event EventHandler Changed;

        readonly List<Rectangle> _chips = new List<Rectangle>();
        Rectangle _boxRect;
        int _hotChip = -1;

        /// <summary>
        /// How many rows of pills we show before the field stops growing and starts scrolling.
        /// The window height is fixed while a project can have any number of tags — without a
        /// cap the field squeezed both the note and the buttons out of the dialog.
        /// </summary>
        public int MaxRows = 3;

        int _scroll;      // how many pixels the content has moved up by
        int _contentH;    // the full height of all the rows, regardless of the cap

        int Pad { get { return Sc(8); } }
        int Gap { get { return Sc(6); } }
        int RowGap { get { return Sc(6); } }
        int ChipH { get { return Chrome.PillHeight(Font); } }
        int MinBoxW { get { return Sc(90); } }

        // A text inset from the pill's edge and a separate zone for the cross on the right — as
        // in TagField: without it a long tag runs into the cross.
        int ChipPadX { get { return Sc(10); } }
        int ChipCrossZone { get { return Sc(16); } }

        public TagEditor()
        {
            Font = Theme.FBadge;

            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Sunken;
            Box.ForeColor = Theme.Text;
            Box.Font = Font;
            // Scrolled up with the wheel and started typing — we bring the caret back on
            // screen: it is at the very bottom of the content, so we simply scroll all the way
            // down.
            Box.TextChanged += delegate { TakeCommas(); SetScroll(int.MaxValue); Invalidate(); };
            Box.GotFocus += delegate { SetScroll(int.MaxValue); };
            Box.KeyDown += OnBoxKeyDown;
            // Windows gives the wheel to whoever has focus, and here focus is always on the
            // TextBox: without forwarding, the field could not be scrolled with the mouse at
            // all.
            Box.MouseWheel += delegate (object s, MouseEventArgs e) { DoWheel(e.Delta); };
            Controls.Add(Box);
        }

        public void SetTags(IEnumerable<string> tags)
        {
            Tags.Clear();
            if (tags != null) foreach (string t in tags) Add(t);
            Relayout();
            Invalidate();
        }

        /// <summary>
        /// Commit a tag that has been typed but not yet closed with a comma. Called before
        /// saving: a word typed and Save pressed — the tag has to be kept, not lost.
        /// </summary>
        public void CommitPending()
        {
            if (Box.Text.Trim().Length == 0) return;
            Add(Box.Text);
            Box.Text = "";
            Fire();
        }

        public void AddTag(string tag)
        {
            if (!Add(tag)) return;
            Fire();
        }

        bool Add(string tag)
        {
            string t = (tag ?? "").Trim();
            if (t.Length == 0) return false;
            foreach (string have in Tags)
                if (string.Equals(have, t, StringComparison.CurrentCultureIgnoreCase)) return false;
            Tags.Add(t);
            return true;
        }

        /// <summary>A comma closes a tag: everything before it goes into a pill, the tail stays
        /// typed.</summary>
        void TakeCommas()
        {
            string text = Box.Text;
            if (text.IndexOf(',') < 0) return;

            string[] parts = text.Split(',');
            bool any = false;
            for (int i = 0; i < parts.Length - 1; i++) any |= Add(parts[i]);

            // The assignment raises TextChanged again, but there are no commas left in it by
            // then — this method is entered exactly twice, with no recursion.
            Box.Text = parts[parts.Length - 1].TrimStart();
            Box.SelectionStart = Box.Text.Length;
            if (any) Fire(); else { Relayout(); Invalidate(); }
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            // Backspace in an empty field eats the last pill — every tag field behaves that
            // way, and it is the only way to remove a tag from the keyboard.
            if (e.KeyCode == Keys.Back && Box.Text.Length == 0 && Tags.Count > 0)
            {
                Tags.RemoveAt(Tags.Count - 1);
                Fire();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            // Enter closes a tag on equal terms with a comma. Ctrl+Enter is left alone: the
            // dialog has already taken it for saving (KeyPreview hands it to the form before
            // us).
            if (e.KeyCode == Keys.Enter && !e.Control)
            {
                CommitPending();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        void DoWheel(int delta)
        {
            if (_contentH <= Height) return;
            SetScroll(_scroll - Math.Sign(delta) * (ChipH + RowGap));
        }

        void SetScroll(int value)
        {
            int max = Math.Max(0, _contentH - Height);
            int v = Math.Max(0, Math.Min(max, value));
            if (v == _scroll) return;
            _scroll = v;
            PlaceBox();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e) { DoWheel(e.Delta); }

        void Fire()
        {
            Relayout();
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- layout

        int ChipWidth(string tag)
        {
            int textW = TextRenderer.MeasureText(tag, Font, new Size(short.MaxValue, ChipH),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            return Math.Min(textW + ChipPadX * 2 + ChipCrossZone, Math.Max(Sc(60), Width - Pad * 2));
        }

        bool _laying;

        /// <summary>
        /// Lays out the pills and the input field, and returns the height needed. It changes
        /// the height right here, which is an OnResize and another entry into this same method
        /// — hence the flag.
        /// </summary>
        public int Relayout()
        {
            if (_laying) return Height;
            _laying = true;
            try { return DoLayout(); }
            finally { _laying = false; }
        }

        int DoLayout()
        {
            _chips.Clear();
            if (Width <= 0) return Height;

            int right = Width - Pad;
            int x = Pad, y = Pad;

            foreach (string tag in Tags)
            {
                int w = ChipWidth(tag);
                if (x > Pad && x + w > right) { x = Pad; y += ChipH + RowGap; }
                _chips.Add(new Rectangle(x, y, w, ChipH));
                x += w + Gap;
            }

            // The caret follows the last pill, and if only a sliver is left to the right edge —
            // onto a new line: typing a tag in twenty pixels is impossible.
            int boxW = right - x;
            if (boxW < MinBoxW && _chips.Count > 0)
            {
                x = Pad;
                y += ChipH + RowGap;
                boxW = right - x;
            }
            _boxRect = new Rectangle(x, y, Math.Max(MinBoxW, boxW), ChipH);

            _contentH = y + ChipH + Pad;
            int maxH = Pad * 2 + MaxRows * ChipH + (MaxRows - 1) * RowGap;
            int viewH = Math.Min(_contentH, maxH);

            // The caret is always in sight: it is at the very bottom of the content, and that
            // is where we scroll to. Whatever is above can be reached with the wheel.
            _scroll = Math.Max(0, _contentH - viewH);
            Height = viewH;      // comes back here through OnResize, hence _laying
            PlaceBox();
            return viewH;
        }

        /// <summary>
        /// The height of a single-line TextBox is set by the font and changing it is pointless
        /// — we simply centre it in its own row, taking the scroll into account. A field that
        /// has moved past the edge need not be hidden: a WinForms child control is clipped to
        /// the parent's client area anyway, and Visible=false would take its focus away.
        /// </summary>
        void PlaceBox()
        {
            int top = _boxRect.Y - _scroll;
            Box.SetBounds(_boxRect.X + Sc(4), top + (ChipH - Box.Height) / 2,
                          Math.Max(Sc(20), _boxRect.Width - Sc(8)), Box.Height);
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        // -------------------------------------------------------------------- mouse

        int ChipAt(Point p)
        {
            Point q = new Point(p.X, p.Y + _scroll);
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Contains(q)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ChipAt(e.Location);
            if (i != _hotChip)
            {
                _hotChip = i;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.IBeam;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hotChip >= 0) { _hotChip = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            int chip = ChipAt(e.Location);
            if (chip >= 0 && chip < Tags.Count)
            {
                Tags.RemoveAt(chip);
                _hotChip = -1;
                Fire();
                return;
            }
            Box.Focus();
        }

        // ---------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            if (_chips.Count != Tags.Count) Relayout();

            Theme.FillRound(g, new Rectangle(0, 0, Width, Height), Sc(12), Theme.Sunken);

            for (int i = 0; i < _chips.Count && i < Tags.Count; i++)
            {
                Rectangle c = _chips[i];
                c.Y -= _scroll;
                if (c.Bottom < 0 || c.Y > Height) continue;
                bool hot = i == _hotChip;
                Theme.FillRound(g, c, c.Height / 2f, hot ? Theme.Red : Theme.Light);

                Rectangle tr = new Rectangle(c.X + ChipPadX, c.Y + Chrome.PillTop(g, Font, c.Height),
                                             c.Width - ChipPadX - ChipCrossZone, c.Height);
                Chrome.DrawText(g, Tags[i], Font, tr, hot ? Color.White : Theme.OnLight,
                                Chrome.PillTextClipped);

                // The cross at the right edge: a click on a pill removes that same pill.
                float cx = c.Right - Sc(10), cy = c.Y + c.Height / 2f, s = Sc(3);
                using (Pen p = new Pen(hot ? Color.White : Theme.OnLight, 1.4f))
                {
                    g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
                }
            }

            if (Tags.Count == 0 && Box.Text.Length == 0 && Cue.Length > 0)
                Chrome.DrawText(g, Cue, Font,
                                new Rectangle(_boxRect.X + Sc(4), _boxRect.Y - _scroll,
                                              _boxRect.Width, _boxRect.Height),
                                Theme.TextDim, Chrome.CellLeft);

            // The scrollbar sits in the right margin, so it does not affect the pill layout; it
            // is visible only when there are more rows than fit.
            if (_contentH > Height)
            {
                int barH = Math.Max(Sc(20), Height * Height / _contentH);
                int barY = (Height - barH) * _scroll / Math.Max(1, _contentH - Height);
                Rectangle bar = new Rectangle(Width - Sc(5), barY, Sc(3), barH);
                Theme.FillRound(g, bar, bar.Width / 2f, Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
            }
        }
    }

    /// <summary>
    /// A row of tags that already exist somewhere: a click on a pill adds it to the field. It
    /// replaces the old "Existing…" button with its dropdown menu — the tags are visible
    /// straight away without opening anything, and that is the entire point of a list of tags
    /// already invented.
    /// </summary>
    public sealed class TagSuggest : GlassControl
    {
        readonly List<string> _items = new List<string>();
        readonly List<Rectangle> _chips = new List<Rectangle>();
        int _hot = -1;

        public event Action<string> Picked;

        // ponytail: extra rows are simply not shown — scrolling will only be needed here if the
        // tags grow well past a dozen.
        public int MaxRows = 2;

        int Gap { get { return Sc(6); } }
        int ChipH { get { return Chrome.PillHeight(Font); } }
        int ChipPadX { get { return Sc(10); } }

        public TagSuggest()
        {
            Font = Theme.FBadge;
            Cursor = Cursors.Hand;
        }

        public void SetItems(IEnumerable<string> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            Relayout();
            Invalidate();
        }

        public bool IsEmpty { get { return _items.Count == 0; } }

        public int Relayout()
        {
            _chips.Clear();
            if (Width <= 0) return 0;

            int x = 0, y = 0, row = 0;
            foreach (string item in _items)
            {
                int w = TextRenderer.MeasureText(item, Font, new Size(short.MaxValue, ChipH),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + ChipPadX * 2;
                if (x > 0 && x + w > Width)
                {
                    if (row + 1 >= MaxRows) break;
                    x = 0; y += ChipH + Gap; row++;
                }
                _chips.Add(new Rectangle(x, y, w, ChipH));
                x += w + Gap;
            }
            return _chips.Count == 0 ? 0 : y + ChipH;
        }

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        int ChipAt(Point p)
        {
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = ChipAt(e.Location);
            if (i != _hot) { _hot = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot >= 0) { _hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = ChipAt(e.Location);
            if (e.Button != MouseButtons.Left || i < 0 || i >= _items.Count) return;
            if (Picked != null) Picked(_items[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            if (_chips.Count == 0 && _items.Count > 0) Relayout();

            for (int i = 0; i < _chips.Count && i < _items.Count; i++)
            {
                Rectangle c = _chips[i];
                bool hot = i == _hot;
                // Muted, unlike the bright pills of the field itself: these are not the
                // project's tags yet, merely what can be added.
                Theme.FillRound(g, c, c.Height / 2f,
                                Color.FromArgb(hot ? 0x3D : 0x22, 0xFF, 0xFF, 0xFF));
                Chrome.DrawText(g, _items[i], Font,
                                new Rectangle(c.X, c.Y + Chrome.PillTop(g, Font, c.Height), c.Width, c.Height),
                                hot ? Theme.Text : Theme.TextDim, Chrome.PillText);
            }
        }
    }
}
