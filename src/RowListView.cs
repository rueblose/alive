using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    public sealed class Column
    {
        public string Id = "";            // a stable column key — for the settings and the menu
        public string Title = "";
        public int Width;                 // 0 — the column stretches over the remainder; otherwise logical px
        public bool Right;                // the text is right-aligned
        public bool Sortable = true;
        public Font Font;
        public Color? Color;

        /// <summary>
        /// The cell is not a string but a list of tags joined by ", " (see
        /// ProjectMeta.JoinTags), and it has to be drawn as pills, as in the details panel,
        /// rather than as solid text. There is no separate field for the list itself: the
        /// string is already split into tags by one Split — keeping another array beside it for
        /// the same thing would be pointless.
        /// </summary>
        public bool Chips;

        public Column(string title, int width) { Title = title; Width = width; }
    }

    /// <summary>
    /// A mark in a column: coloured text and a strip of the same colour at the right edge — so
    /// "how much is lost" reads at a glance by colour, without poring over the number.
    /// </summary>
    public struct CellMark
    {
        public int Column;
        public Color Color;
        public string Text;
        public CellMark(int column, Color color, string text)
        { Column = column; Color = color; Text = text; }
    }

    public sealed class RowData
    {
        public string[] Cells;
        public object Tag;
        public readonly List<CellMark> Marks = new List<CellMark>();

        /// <summary>Only when the list has RowListView.ShowCheckboxes on.</summary>
        public bool Checked = true;

        /// <summary>Whether there is anything to play: a row without it gets no play
        /// button.</summary>
        public bool CanPlay = true;

        /// <summary>Whether the project is pinned — only when the list has ShowPinIndicator
        /// on.</summary>
        public bool Pinned;

        /// <summary>One of the versions under an expanded row rather than the project itself —
        /// the name in the first column is indented and gets a short rail in front of it,
        /// showing the nesting.</summary>
        public bool ChildRow;
    }

    /// <summary>
    /// A table drawn by hand: there are thousands of rows and only the visible ones are drawn.
    /// A row is single-storey and the selection is a pill across the full width, as in the
    /// mockup.
    /// </summary>
    public sealed class RowListView : GlassControl
    {
        readonly List<RowData> _rows = new List<RowData>();
        readonly List<Column> _columns = new List<Column>();

        int _scroll, _scrollX, _hot = -1, _selected = -1;
        bool _draggingBar, _draggingHBar;
        int _dragOffset, _dragHOffset;
        float _scrollTarget, _scrollCurrent;
        float _scrollXTarget, _scrollXCurrent;
        readonly SmoothScroller _scroller;
        float[] _rowHoverFactors;
        float[] _rowEntrance;
        int _entranceStartTick;

        public void TriggerEntrance()
        {
            _rowEntrance = new float[_rows.Count];
            _entranceStartTick = Environment.TickCount;
            AnimEngine.Register(this);
            Invalidate();
        }

        public override bool OnAnimTick()
        {
            bool parentStill = base.OnAnimTick();
            bool anim = false;

            if (_rowHoverFactors == null || _rowHoverFactors.Length != _rows.Count)
                _rowHoverFactors = new float[_rows.Count];

            for (int i = 0; i < _rows.Count; i++)
            {
                float target = (i == _hot) ? 1.0f : 0.0f;
                float diff = target - _rowHoverFactors[i];
                if (Math.Abs(diff) > 0.01f)
                {
                    _rowHoverFactors[i] += diff * 0.35f;
                    anim = true;
                }
                else
                {
                    _rowHoverFactors[i] = target;
                }
            }

            int now = Environment.TickCount;
            if (_rowEntrance == null || _rowEntrance.Length != _rows.Count)
                _rowEntrance = new float[_rows.Count];

            for (int i = 0; i < _rows.Count; i++)
            {
                int delay = Math.Min(i, 30) * 15;
                int elapsed = now - _entranceStartTick - delay;
                if (elapsed > 0)
                {
                    float diff = 1.0f - _rowEntrance[i];
                    if (diff > 0.005f)
                    {
                        _rowEntrance[i] += diff * 0.25f;
                        anim = true;
                    }
                    else
                    {
                        _rowEntrance[i] = 1.0f;
                    }
                }
                else
                {
                    anim = true;
                }
            }

            if (anim) Invalidate();
            return parentStill || anim;
        }

        public event EventHandler SelectionChanged;
        public event EventHandler ItemActivated;      // a double click
        public event Action<int> HeaderClicked;
        public event Action<Point> HeaderRightClicked; // a right click on the header — the column menu
        public event Action ColumnsResized;            // a column edge was released — save the widths
        public event Action<int, int> ColumnsReordered; // a column was dragged: from where, to where
        public event Action<int> RowCheckedChanged;    // a click on a row's checkbox
        public event Action<int> RowPlayClicked;       // a click on the listen button
        public event Action<int> RowPinClicked;        // a click on the pin star
        public event Action<int, Point> RowRightClicked;
        public event Action<int> RowCountClicked;      // a click on the "+3" / "−3" tail
        public event Action<int> RowTagsClicked;       // a click on a row's tags (or on the "+" in an empty cell)

        public int SortColumn = -1;
        public bool SortDescending;

        /// <summary>Whether column widths can be changed and the column menu called with the
        /// right button.</summary>
        public bool ColumnsConfigurable;

        /// <summary>
        /// A checkbox before the first column — "is this row on" (a folder temporarily excluded
        /// from scanning while staying in the list, for instance). A click on it does not touch
        /// the selection — this is an independent toggle rather than a choice of row.
        /// </summary>
        public bool ShowCheckboxes;

        /// <summary>
        /// The "listen" triangle to the left of the first column. It lives in a gutter of its
        /// own rather than over the name: otherwise it either runs into the text or appears
        /// only under the cursor, and then nobody learns about it.
        /// </summary>
        public bool ShowPlayButton;

        /// <summary>
        /// The gap to the right of a row's pill (px). When set, the pills end at Width -
        /// PillRightGap, and the vertical scrollbar is centred exactly in that space between
        /// the selection pill and the right edge of the control.
        /// </summary>
        public int PillRightGap;

        /// <summary>
        /// The file path for a row — when set (not null and not empty), the row can be dragged
        /// out as a file (into Explorer, into another application). null for a row with no file
        /// — a project version with no render reference, for instance.
        /// </summary>
        public Func<RowData, string> DragFilePath;

        /// <summary>The row whose play button the cursor is currently on, otherwise
        /// -1.</summary>
        int _playHot = -1;

        /// <summary>
        /// The "+3" tail at the end of a cell — how many versions of the project are hidden
        /// under the row, and at the same time the button to expand them. It is separated from
        /// the name by three spaces: it is drawn in its own colour and its own rectangle, so it
        /// has to be findable in the finished cell text. On an expanded row the sign becomes a
        /// minus.
        /// </summary>
        public const string CountSep = "   ";
        public const string CountOpen = "+";
        public const string CountClose = "−";

        static int CountAt(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return -1;
            int i = cell.LastIndexOf(CountSep + CountOpen, StringComparison.Ordinal);
            if (i < 0) i = cell.LastIndexOf(CountSep + CountClose, StringComparison.Ordinal);
            return i;
        }

        // Where that tail landed on the last repaint — one per record. Working it out again in
        // the mouse handler would mean repeating the whole cell parse together with the text
        // measurements; it is simpler to remember what has already been drawn.
        Rectangle[] _countHit;

        /// <summary>The row whose "+N" is currently under the cursor, otherwise -1.</summary>
        int _countHot = -1;

        int CountAtPoint(Point p)
        {
            if (_countHit == null) return -1;
            for (int i = 0; i < _countHit.Length; i++)
                if (!_countHit[i].IsEmpty && _countHit[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>Remember the drawn tail, widening it slightly horizontally: "+3" is three
        /// or four characters, and hitting them flush is awkward.</summary>
        void RememberCount(int row, int x, int y, int w, int h)
        {
            if (_countHit == null || row < 0 || row >= _countHit.Length) return;
            int pad = Sc(6);
            _countHit[row] = new Rectangle(x - pad, y, Math.Max(1, w) + pad * 2, h);
        }

        // The tags cell — by the same device as the "+N" tail: we click by what was drawn. What
        // is clickable is the pills specifically (or the "+" in an empty cell) rather than the
        // full width of the column: empty space to the right of the tags should simply select
        // the row.
        Rectangle[] _tagsHit;

        /// <summary>The row whose tags are currently under the cursor, otherwise -1.</summary>
        int _tagsHot = -1;

        int TagsAtPoint(Point p)
        {
            if (_tagsHit == null) return -1;
            for (int i = 0; i < _tagsHit.Length; i++)
                if (!_tagsHit[i].IsEmpty && _tagsHit[i].Contains(p)) return i;
            return -1;
        }

        void RememberTags(int row, Rectangle r, int clipLeft)
        {
            if (_tagsHit == null || row < 0 || row >= _tagsHit.Length) return;
            if (r.Left < clipLeft) r = Rectangle.FromLTRB(clipLeft, r.Top, r.Right, r.Bottom);
            if (r.Width > 0) _tagsHit[row] = r;
        }

        /// <summary>The row currently loaded into the player (not necessarily playing — see
        /// Playing) — its triangle glows light.</summary>
        public object PlayingTag
        {
            get { return _playingTag; }
            set { if (!ReferenceEquals(_playingTag, value)) { _playingTag = value; SyncPulse(); } }
        }
        object _playingTag;

        /// <summary>The player is really sounding right now rather than paused. The PlayingTag
        /// glyph becomes a pause only on both conditions at once — otherwise after pausing from
        /// the footer the row would go on showing a pause although there is nothing left
        /// playing.</summary>
        public bool Playing
        {
            get { return _playing; }
            set { if (_playing != value) { _playing = value; SyncPulse(); } }
        }
        bool _playing;

        // Overlay scrollbars: thin at rest, thicker under the cursor, fading a second after the
        // last scroll.
        ScrollFade _barFade, _hbarFade;

        /// <summary>A hairline under the header. Switched off where the header stands on its
        /// own surface anyway (dialogs with a single column).</summary>
        public bool ShowHeaderRule = true;

        /// <summary>While it sounds we repaint not the whole list but only the play circle of
        /// the playing row; it travels with the scroll, so we compute it every tick.</summary>
        void SyncPulse()
        {
            if (_playing && _playingTag != null) PlayPulse.Attach(this, PulseRect);
            else PlayPulse.Detach(this);
            Invalidate();
        }

        Rectangle PulseRect()
        {
            if (_playingTag == null || !ShowPlayButton) return Rectangle.Empty;
            int rowH = RowHeight;
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!SetEntry.SameSet(_rows[i].Tag, _playingTag)) continue;
                int top = HeaderHeight + i * rowH - _scroll - over;
                if (top + rowH < HeaderHeight || top > Height) return Rectangle.Empty;
                return PlayRect(top, rowH);
            }
            return Rectangle.Empty;
        }

        /// <summary>
        /// The pin star in the gutter, by the same device as play: on a pinned row it is always
        /// visible, on the rest while the cursor is on the row itself — unlike play (which is
        /// always visible but muted), or a long list would shimmer.
        /// </summary>
        public bool ShowPinIndicator;
        public bool ShowHeaderPin = true;
        int _pinHot = -1;

        /// <summary>
        /// A star toggle in the header, exactly above the gutter with the row stars: "keep the
        /// pinned ones on top". It lives here rather than in the filters dialog because this is
        /// not a filter (it hides nothing) but an order — and its logical place is where the
        /// stars that do the pinning stand.
        /// </summary>
        public bool PinnedFirst;
        public event Action PinnedFirstToggled;
        bool _headPinHot;

        /// <summary>
        /// Whether to dissolve the bottom rows into the background. It has no effect on glass:
        /// the gradient blends its own translucent background over already blurred wallpaper,
        /// the alpha accumulates in layers, and instead of a soft disappearance one gets a
        /// crisp dark rectangle at the very bottom of the list. On an opaque window this is not
        /// a problem.
        /// </summary>
        public bool FadeBottom = true;

        // Dragging a column's right edge: the column index, or -1.
        int _resizeCol = -1;
        // Where the grab happened and what the width was at that moment. The width is counted
        // from that pair rather than from the current layout: the layout itself depends on the
        // width, and counting from it closed the loop on itself — see DoResize.
        int _resizeStartX, _resizeStartW;

        // Dragging the header itself — as in Explorer.
        //
        // The press and the drag are deliberately kept apart: a header is used both for sorting
        // and for dragging, and the only way to tell one from the other is whether the mouse
        // moved. So on the press we merely remember the column (_pressCol), start dragging
        // after a threshold of a few pixels (_dragCol), and sort on release — and only if no
        // drag happened after all.
        int _pressCol = -1;
        int _pressX;
        int _dragCol = -1;
        int _dragX;
        int _dropAt = -1;      // where the column will land if released now

        // A row is also pressed before it becomes clear whether this is a click or a drag
        // outwards (into Explorer, into another application), by the same threshold as the
        // columns.
        int _rowDragIdx = -1;
        Point _rowDragStart;

        int DragThreshold { get { return Sc(5); } }

        // Grip strips between every header: they all appear at once as soon as the cursor
        // enters the header — so that it is immediately clear where anything can be dragged at
        // all, rather than groping for a boundary blindly. The opacity is shared by all of
        // them, 0..1, with a short animation — the whole path in two ticks of a 10 ms timer,
        // that is, around 20 ms.
        bool _headerHot;
        float _gripAlpha;
        readonly Timer _gripTimer;

        public RowListView()
        {
            Cursor = Cursors.Hand;
            _gripTimer = new Timer();
            _gripTimer.Interval = 10;
            _gripTimer.Tick += delegate { StepGripFade(); };
            _barFade = new ScrollFade(this, BarRect);
            _hbarFade = new ScrollFade(this, HBarRect);
            _scroller = new SmoothScroller(this,
                delegate (int s) { _scroll = s; _scrollCurrent = s; ClampScroll(); },
                delegate { return Math.Max(0, _rows.Count * RowHeight - (ViewH - HeaderHeight)); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PlayPulse.Detach(this);
                _gripTimer.Dispose(); _scroller.Dispose();
                _barFade.Dispose(); _hbarFade.Dispose();
            }
            base.Dispose(disposing);
        }

        public List<Column> ColumnList { get { return _columns; } }

        int RowHeight { get { return Sc(Theme.RowH); } }
        int HeaderHeight { get { return Sc(45); } }
        int PadX { get { return Sc(Theme.CellPadX); } }
        int PadRight { get { return Math.Max(PadX, (int)Math.Ceiling(Sc(Theme.RowPillH) / 2f) + Sc(16)) + PillRightGap; } }

        // The checkbox lives in a separate column to the left of the first ordinary one — the
        // content's left inset grows by it. The right inset (PadRight) guarantees that column
        // text does not creep under the rounding of the row's selection pill.
        int CheckW { get { return ShowCheckboxes ? Sc(30) : 0; } }
        int PinW { get { return ShowPinIndicator ? Sc(32) : 0; } }
        int PlayW { get { return ShowPlayButton ? Sc(32) : 0; } }
        int LeftX { get { return PadX + CheckW + PinW + PlayW; } }

        /// <summary>The pin star — a gutter between the checkbox and the play button, the same
        /// size as play itself.</summary>
        Rectangle PinRect(int top, int rowH)
        {
            int s = Sc(26);
            return new Rectangle(PadX + CheckW + (PinW - s) / 2, top + (rowH - s) / 2, s, s);
        }

        /// <summary>Is the cursor on the header star? It occupies the same gutter as the row
        /// stars.</summary>
        bool OnHeaderPin(Point p)
        {
            return ShowPinIndicator && ShowHeaderPin && p.Y < HeaderHeight && PinRect(0, HeaderHeight).Contains(p);
        }

        /// <summary>A row's play button — the gutter between the star and the first
        /// column.</summary>
        Rectangle PlayRect(int top, int rowH)
        {
            int s = Sc(26);
            return new Rectangle(PadX + CheckW + PinW + (PlayW - s) / 2, top + (rowH - s) / 2, s, s);
        }

        public List<RowData> Rows { get { return _rows; } }

        public void SetColumns(params Column[] cols)
        {
            _columns.Clear();
            _columns.AddRange(cols);
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;
            Invalidate();
        }

        public void SetRows(List<RowData> rows) { SetRows(rows, true); }

        /// <summary>
        /// animate=false — show the rows at once, with no rise from below. Typing in the search
        /// needs it: the list is rebuilt on every letter, and the entrance was restarted by
        /// each press instead of the rows simply being filtered.
        /// </summary>
        public void SetRows(List<RowData> rows, bool animate)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _scroll = 0;
            _scrollTarget = _scrollCurrent = 0;
            if (_scroller != null) _scroller.SyncPosition(0);
            _selected = -1;
            _hot = -1;
            ClampScroll();
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;

            if (animate) { TriggerEntrance(); return; }

            // An array of zeroes would mean "the rows have not appeared yet", and with no
            // animation running the list would stay empty.
            _rowEntrance = new float[_rows.Count];
            for (int i = 0; i < _rowEntrance.Length; i++) _rowEntrance[i] = 1f;
            Invalidate();
        }

        public RowData Selected
        {
            get { return _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null; }
        }

        /// <summary>
        /// Selecting a row programmatically — a jump to a specific set from a plugin's details
        /// panel, say, rather than a click on the list itself. It quietly does nothing if there
        /// is no suitable row right now (it may have been filtered out).
        /// </summary>
        public void SelectRow(Predicate<RowData> match)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!match(_rows[i])) continue;
                _selected = i;
                EnsureVisible(i);
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                return;
            }
        }

        /// <summary>
        /// The scroll position in pixels. The catalog's auto-refresh needs it: SetRows always
        /// puts the list back to the top, while a refresh arrives by itself, without a keypress
        /// — and it has no right to yank a person upwards in the middle of reading. The
        /// assignment places the list at once, with no settling: this is restoring the previous
        /// view rather than scrolling.
        /// </summary>
        public int ScrollOffset
        {
            get { return _scroll; }
            set
            {
                _scroll = value;
                ClampScroll();
                _scrollTarget = _scrollCurrent = _scroll;
                if (_scroller != null) _scroller.SyncPosition(_scroll);
                Invalidate();
            }
        }

        public int ScrollOffsetX
        {
            get { return _scrollX; }
            set
            {
                _scrollX = value;
                ClampScrollX();
                _scrollXTarget = _scrollXCurrent = _scrollX;
                Invalidate();
            }
        }

        public int MaxHScroll
        {
            get
            {
                int[] widths = ComputeWidths();
                if (widths.Length <= 1) return 0;
                int scrollableW = 0;
                for (int c = 1; c < widths.Length; c++) scrollableW += widths[c];
                int visibleScrollableW = Width - (LeftX + widths[0]) - PadRight;
                return Math.Max(0, scrollableW - visibleScrollableW);
            }
        }

        /// <summary>The horizontal scrollbar lives in the bottom Sc(10). Its height is
        /// subtracted from the rows area whole rather than added to the content: otherwise the
        /// empty strip appeared only at the very bottom of the list, while at any other scroll
        /// position the bar still lay across a row.</summary>
        int HBarSpace { get { return MaxHScroll > 0 ? Sc(14) : 0; } }

        /// <summary>The height of the rows area — the window minus the horizontal
        /// scrollbar.</summary>
        int ViewH { get { return Height - HBarSpace; } }

        int ContentHeight { get { return _rows.Count * RowHeight + Sc(8) + HeaderHeight; } }

        void ClampScroll()
        {
            int max = Math.Max(0, ContentHeight - ViewH);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        void ClampScrollX()
        {
            int max = MaxHScroll;
            if (_scrollX > max) _scrollX = max;
            if (_scrollX < 0) _scrollX = 0;
        }

        protected override void OnResize(EventArgs e)
        {
            ClampScroll();
            ClampScrollX();
            base.OnResize(e);
        }

        int GetSnapTarget(int direction)
        {
            int[] widths = ComputeWidths();
            if (widths.Length <= 1) return 0;

            int maxScroll = MaxHScroll;
            if (maxScroll <= 0) return 0;

            List<int> snapOffsets = new List<int>();
            int acc = 0;
            snapOffsets.Add(0);
            for (int c = 1; c < widths.Length - 1; c++)
            {
                acc += widths[c];
                if (acc >= maxScroll)
                {
                    snapOffsets.Add(maxScroll);
                    break;
                }
                snapOffsets.Add(acc);
            }
            if (!snapOffsets.Contains(maxScroll))
                snapOffsets.Add(maxScroll);

            int current = (int)Math.Round(_scrollXTarget);

            if (direction > 0) // Right, to the next column
            {
                for (int i = 0; i < snapOffsets.Count; i++)
                {
                    if (snapOffsets[i] > current + 2)
                        return snapOffsets[i];
                }
                return maxScroll;
            }
            else // Left, to the previous column
            {
                for (int i = snapOffsets.Count - 1; i >= 0; i--)
                {
                    if (snapOffsets[i] < current - 2)
                        return snapOffsets[i];
                }
                return 0;
            }
        }

        int GetNearestSnapTarget(int target)
        {
            int[] widths = ComputeWidths();
            if (widths.Length <= 1) return 0;
            int maxScroll = MaxHScroll;
            if (maxScroll <= 0) return 0;

            List<int> snapOffsets = new List<int>();
            int acc = 0;
            snapOffsets.Add(0);
            for (int c = 1; c < widths.Length - 1; c++)
            {
                acc += widths[c];
                if (acc >= maxScroll)
                {
                    snapOffsets.Add(maxScroll);
                    break;
                }
                snapOffsets.Add(acc);
            }
            if (!snapOffsets.Contains(maxScroll))
                snapOffsets.Add(maxScroll);

            int closest = snapOffsets[0];
            int minDist = Math.Abs(target - closest);
            for (int i = 1; i < snapOffsets.Count; i++)
            {
                int dist = Math.Abs(target - snapOffsets[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = snapOffsets[i];
                }
            }
            return closest;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            bool isShift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            if (isShift)
            {
                int steps = e.Delta / 120;
                if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                int direction = steps < 0 ? 1 : -1;
                int count = Math.Abs(steps);
                for (int s = 0; s < count; s++)
                {
                    _scrollXTarget = GetSnapTarget(direction);
                }
                ClampScrollXTarget();
                _scrollX = (int)_scrollXTarget;
                _scrollXCurrent = _scrollXTarget;
                ClampScrollX();
                _hbarFade.Ping();
                Invalidate();
            }
            else
            {
                _scroller.OnMouseWheel(e.Delta, RowHeight * 3);
                _barFade.Ping();
            }
            base.OnMouseWheel(e);
        }

        const int WM_MOUSEHWHEEL = 0x020E;

        // The list lives with the right button: a row menu and a header menu. The handling is
        // in OnMouseDown.
        protected override bool WantsRightClick { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (delta != 0)
                {
                    int steps = delta / 120;
                    if (steps == 0) steps = delta > 0 ? 1 : -1;
                    int direction = steps > 0 ? 1 : -1;
                    int count = Math.Abs(steps);
                    for (int s = 0; s < count; s++)
                    {
                        _scrollXTarget = GetSnapTarget(direction);
                    }
                    ClampScrollXTarget();
                    _scrollX = (int)_scrollXTarget;
                    _scrollXCurrent = _scrollXTarget;
                    ClampScrollX();
                    Invalidate();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void ClampScrollTarget()
        {
            int max = Math.Max(0, _rows.Count * RowHeight - (Height - HeaderHeight));
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scrollTarget < 0) _scrollTarget = 0;
        }

        void ClampScrollXTarget()
        {
            int max = MaxHScroll;
            if (_scrollXTarget > max) _scrollXTarget = max;
            if (_scrollXTarget < 0) _scrollXTarget = 0;
        }


        /// <summary>
        /// The vertical offset of the content: the scroll plus the rubber-band overshoot. Mouse
        /// hits have to be counted by the same one as the drawing — otherwise during the bounce
        /// the star and play fire where they are no longer visible.
        /// </summary>
        int ScrollY
        {
            get { return _scroll + (_scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0); }
        }

        int RowAt(int y)
        {
            int top = HeaderHeight - ScrollY;
            if (y < HeaderHeight) return -1;
            int idx = (y - top) / RowHeight;
            return idx >= 0 && idx < _rows.Count ? idx : -1;
        }

        Rectangle BarRect()
        {
            int content = ContentHeight;
            if (content <= ViewH) return Rectangle.Empty;
            int track = ViewH - HeaderHeight - Sc(10);
            int h = Math.Max(Sc(40), (int)(track * (float)ViewH / content));
            int max = Math.Max(1, content - ViewH);
            int y = HeaderHeight + Sc(5) + (int)((track - h) * (_scroll / (float)max));
            int barW = Sc(4);
            int barX = PillRightGap > 0
                ? (Width - PillRightGap) + (PillRightGap - barW) / 2
                : Width - Sc(8);
            return new Rectangle(barX, y, barW, h);
        }

        Rectangle HBarRect()
        {
            int max = MaxHScroll;
            if (max <= 0) return Rectangle.Empty;
            int[] widths = ComputeWidths();
            int startX = LeftX + widths[0] + Sc(5);
            int track = Width - startX - PadRight;
            if (track <= Sc(20)) return Rectangle.Empty;

            int totalScrollableW = 0;
            for (int c = 1; c < widths.Length; c++) totalScrollableW += widths[c];
            int visibleW = Width - (LeftX + widths[0]) - PadRight;

            int w = Math.Max(Sc(30), (int)(track * (float)visibleW / Math.Max(1, totalScrollableW)));
            int x = startX + (int)((track - w) * (_scrollX / (float)max));
            return new Rectangle(x, Height - Sc(6), w, Sc(4));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_rowDragIdx >= 0 && e.Button == MouseButtons.Left
                && (Math.Abs(e.X - _rowDragStart.X) > DragThreshold || Math.Abs(e.Y - _rowDragStart.Y) > DragThreshold))
            {
                int dragIdx = _rowDragIdx;
                _rowDragIdx = -1;
                string path = dragIdx < _rows.Count ? DragFilePath(_rows[dragIdx]) : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    DoDragDrop(new DataObject(DataFormats.FileDrop, new string[] { path }), DragDropEffects.Copy);
                return;
            }

            if (_resizeCol >= 0) { DoResize(e.X); return; }

            // A header is already being dragged — we carry it and work out where it will land.
            if (_dragCol >= 0)
            {
                _dragX = e.X;
                int at = DropIndexAt(e.X);
                if (at != _dropAt) _dropAt = at;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                return;
            }

            // The header was grabbed and led sideways — this is a drag rather than a click on
            // the sort. The threshold is there so that a hand shaking on an ordinary click is
            // not counted as moving a column. _pressCol > 0 — the same as in DropIndexAt: the
            // first column is not dragged.
            if (_pressCol > 0 && e.Button == MouseButtons.Left
                && Math.Abs(e.X - _pressX) > DragThreshold
                && ColumnsConfigurable && _columns.Count > 1)
            {
                _dragCol = _pressCol;
                _dragX = e.X;
                _dropAt = DropIndexAt(e.X);
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                return;
            }

            if (_draggingBar)
            {
                Rectangle bar = BarRect();
                int track = ViewH - HeaderHeight - Sc(10);
                int max = Math.Max(1, ContentHeight - ViewH);
                float t = (e.Y - _dragOffset - HeaderHeight - Sc(5)) / (float)Math.Max(1, track - bar.Height);
                _scroll = (int)(t * max);
                _scrollTarget = _scrollCurrent = _scroll;
                if (_scroller != null) _scroller.SyncPosition(_scroll);
                ClampScroll();
                Invalidate();
                return;
            }

            if (_draggingHBar)
            {
                int[] widths = ComputeWidths();
                int startX = LeftX + widths[0] + Sc(5);
                int track = Width - startX - PadRight;
                Rectangle hbar = HBarRect();
                int max = MaxHScroll;
                float t = (e.X - _dragHOffset - startX) / (float)Math.Max(1, track - hbar.Width);
                _scrollX = (int)Math.Max(0, Math.Min(max, t * max));
                _scrollXTarget = _scrollXCurrent = _scrollX;
                ClampScrollX();
                Invalidate();
                return;
            }

            // The scrollbars wake only when the cursor is near the bars themselves rather than
            // anywhere over the list.
            _barFade.SetHot(e.X >= Width - Sc(22) && e.Y > HeaderHeight);
            _hbarFade.SetHot(MaxHScroll > 0 && e.Y >= Height - Sc(18));

            // In the header, at the edges of resizable columns the cursor is "spread apart",
            // elsewhere a hand. We show all the grip strips at once the moment the cursor
            // enters the header.
            if (e.Y < HeaderHeight)
            {
                int grip = GripAt(e.X, ComputeWidths());
                SetHeaderHot(true);
                Cursor = grip >= 0 ? Cursors.VSplit : Cursors.Hand;
                bool headPin = OnHeaderPin(e.Location);
                if (headPin != _headPinHot) { _headPinHot = headPin; Invalidate(); }
                if (_hot != -1 || _playHot != -1 || _pinHot != -1 || _tagsHot != -1)
                { _hot = -1; _playHot = -1; _pinHot = -1; _tagsHot = -1; Invalidate(); }
                return;
            }
            if (_headPinHot) { _headPinHot = false; Invalidate(); }
            SetHeaderHot(false);
            if (Cursor != Cursors.Hand) Cursor = Cursors.Hand;

            int pillW = Math.Max(0, Width - PillRightGap);
            int idx = (PillRightGap > 0 && e.X >= pillW) ? -1 : RowAt(e.Y);
            int playHot = -1, pinHot = -1;
            if (idx >= 0)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
                if (ShowPlayButton && _rows[idx].CanPlay && PlayRect(top, RowHeight).Contains(e.Location))
                    playHot = idx;
                if (ShowPinIndicator && PinRect(top, RowHeight).Contains(e.Location))
                    pinHot = idx;
            }
            int countHot = CountAtPoint(e.Location);
            int tagsHot = TagsAtPoint(e.Location);
            if (tagsHot != idx) tagsHot = -1;
            if (idx != _hot || playHot != _playHot || pinHot != _pinHot
                || countHot != _countHot || tagsHot != _tagsHot)
            {
                _hot = idx; _playHot = playHot; _pinHot = pinHot; _countHot = countHot; _tagsHot = tagsHot;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        /// <summary>
        /// Between which columns a dragged header will land if released at point px. Counted by
        /// the MIDPOINTS of the columns rather than by their edges: until the cursor has passed
        /// a neighbour's midpoint it is too early to swap them — otherwise columns jump back
        /// and forth at the slightest movement on a boundary.
        ///
        /// Returns the insertion position in the column list: 0 — before the first, Count —
        /// after the last.
        /// </summary>
        int DropIndexAt(int px)
        {
            int[] widths = ComputeWidths();
            if (_columns.Count == 0) return 0;
            // The first column is nailed down: it is the set (or plugin) name, the one thing a
            // row can be recognised by at all, and it stretches across all the free width.
            // There is nowhere to insert before it — the leftmost place for the rest is 1.
            if (px < LeftX + widths[0]) return 1;
            for (int c = 1; c < _columns.Count; c++)
            {
                int x = ColX(widths, c);
                if (px < x + widths[c] / 2) return c;
            }
            return _columns.Count;
        }

        /// <summary>
        /// The index of the column whose left edge can be dragged if the cursor is near it. The
        /// stretching name column is on the left and the sum of the widths is pushed to the
        /// right edge of the list, so on fixed columns it is the left boundary that moves
        /// rather than the right.
        /// </summary>
        int GripAt(int px, int[] widths)
        {
            if (!ColumnsConfigurable) return -1;
            if (_columns.Count > 0)
            {
                int edge0 = LeftX + widths[0];
                if (Math.Abs(px - edge0) <= Sc(4)) return 0;
            }
            for (int c = 1; c < _columns.Count; c++)
            {
                if (_columns[c].Width > 0)
                {
                    int x = ColX(widths, c);
                    if (x >= LeftX + widths[0] - Sc(2) && x <= Width - PadRight + Sc(2) && Math.Abs(px - x) <= Sc(4)) return c;
                }
            }
            return -1;
        }

        void DoResize(int mouseX)
        {
            float scale = DeviceDpi / 96f;
            int[] before = ComputeWidths();
            int rightBefore = ColX(before, _resizeCol) + before[_resizeCol];

            if (_resizeCol == 0 && _columns.Count > 0)
            {
                int newScaled = _resizeStartW + (mouseX - _resizeStartX);
                int minW = Sc(100);
                int maxW = Width - LeftX - PadRight - Sc(100);
                if (maxW < minW) maxW = minW;
                if (newScaled < minW) newScaled = minW;
                if (newScaled > maxW) newScaled = maxW;
                _columns[0].Width = Math.Max(1, (int)Math.Round(newScaled / scale));
            }
            else if (_resizeCol > 0 && _resizeCol < _columns.Count)
            {
                // The width comes from what it was at the moment of the grab plus the distance
                // travelled. It used to be counted from the right edge of the current layout,
                // and that stays put only while the name column is stretching and absorbing the
                // difference. As soon as the columns stopped fitting, the name hit its minimum,
                // the edge began to travel with the width — and the width accelerated itself on
                // every MouseMove. From the grab point there is no feedback, and the column
                // dragged is always the same one — the one whose left edge was taken hold of.
                int newScaled = _resizeStartW + (_resizeStartX - mouseX);

                int minW = Sc(48);
                int maxW = Width - LeftX - PadRight - Sc(80);
                if (maxW < minW) maxW = minW;
                if (newScaled < minW) newScaled = minW;
                if (newScaled > maxW) newScaled = maxW;

                _columns[_resizeCol].Width = Math.Max(1, (int)Math.Round(newScaled / scale));
            }
            // Nothing to the right of the boundary should stir. While the columns fit, that
            // comes out by itself: the stretching name column absorbs the difference and
            // everything else stands pushed against the right edge. When they do not fit there
            // is nothing to absorb it — we then turn the scroll by exactly the same difference,
            // and the picture comes out the same as full screen. In the first case there is
            // nothing to scroll and ClampScrollX returns zero — the branch is not needed.
            int[] after = ComputeWidths();
            _scrollX += ColX(after, _resizeCol) + after[_resizeCol] - rightBefore;
            ClampScrollX();
            _scrollXTarget = _scrollXCurrent = _scrollX;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1 || _playHot != -1 || _pinHot != -1 || _countHot != -1
                || _tagsHot != -1 || _headPinHot)
            {
                _hot = -1; _playHot = -1; _pinHot = -1; _countHot = -1; _tagsHot = -1; _headPinHot = false;
                AnimEngine.Register(this);
                Invalidate();
            }
            SetHeaderHot(false);
            base.OnMouseLeave(e);
        }

        void SetHeaderHot(bool hot)
        {
            if (_headerHot == hot) return;
            _headerHot = hot;
            if (!_gripTimer.Enabled) _gripTimer.Start();
        }

        void StepGripFade()
        {
            float target = _headerHot ? 1f : 0f;
            const float step = 0.5f;    // two ticks of 10 ms = the whole path 0..1 in ~20 ms
            if (_gripAlpha < target) _gripAlpha = Math.Min(target, _gripAlpha + step);
            else if (_gripAlpha > target) _gripAlpha = Math.Max(target, _gripAlpha - step);

            Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
            if (_gripAlpha == target) _gripTimer.Stop();
        }

        int ColumnAt(int px)
        {
            if (_columns.Count == 0) return -1;
            int[] widths = ComputeWidths();
            if (px < LeftX || px >= Width - PadRight) return -1;
            if (px < LeftX + widths[0]) return 0;

            for (int c = 1; c < _columns.Count; c++)
            {
                int x = ColX(widths, c);
                if (px >= x && px < x + widths[c]) return c;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();

            if (e.Y < HeaderHeight)
            {
                if (e.Button == MouseButtons.Right)
                {
                    if (ColumnsConfigurable && HeaderRightClicked != null) HeaderRightClicked(e.Location);
                    return;
                }

                // The header star intercepts the click before the sort: it stands in the
                // gutter, left of the first column, so ColumnAt does not see it anyway — but
                // the check has to come before GripAt, as the first column's grip is right
                // beside it.
                if (OnHeaderPin(e.Location))
                {
                    PinnedFirst = !PinnedFirst;
                    Invalidate();
                    if (PinnedFirstToggled != null) PinnedFirstToggled();
                    return;
                }

                int[] widths = ComputeWidths();
                int grip = GripAt(e.X, widths);
                if (grip >= 0)
                {
                    _resizeCol = grip;
                    _resizeStartX = e.X;
                    _resizeStartW = widths[grip];
                    return;
                }

                // Neither a sort nor a drag right now — we merely remember what was grabbed:
                // which it was will become clear from the mouse movement. See _pressCol.
                _pressCol = ColumnAt(e.X);
                _pressX = e.X;
                return;
            }

            Rectangle bar = BarRect();
            if (!bar.IsEmpty &&
                new Rectangle(bar.X - Sc(6), bar.Y, bar.Width + Sc(10), bar.Height).Contains(e.Location))
            {
                _draggingBar = true;
                _dragOffset = e.Y - bar.Y;
                return;
            }

            Rectangle hbar = HBarRect();
            if (!hbar.IsEmpty &&
                new Rectangle(hbar.X, hbar.Y - Sc(4), hbar.Width, hbar.Height + Sc(6)).Contains(e.Location))
            {
                _draggingHBar = true;
                _dragHOffset = e.X - hbar.X;
                return;
            }

            int pillW = Math.Max(0, Width - PillRightGap);
            int idx = (PillRightGap > 0 && e.X >= pillW) ? -1 : RowAt(e.Y);

            if (e.Button == MouseButtons.Right)
            {
                // A right click on a row selects it and passes the menu outward — as in
                // Explorer.
                if (idx >= 0 && idx != _selected)
                {
                    _selected = idx;
                    Invalidate();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                }
                if (RowRightClicked != null) RowRightClicked(idx, e.Location);
                return;
            }

            // The "+3" tail is an independent "show the other versions" button, like play and
            // the star: it does not touch the row selection, or the details panel would jump on
            // every expansion.
            if (idx >= 0 && CountAtPoint(e.Location) == idx)
            {
                if (RowCountClicked != null) { RowCountClicked(idx); return; }
            }

            // The tags are a button of their own too: a click on the pills (or on the "+" in an
            // empty cell) opens the tag editor rather than simply selecting the row.
            if (idx >= 0 && TagsAtPoint(e.Location) == idx)
            {
                if (RowTagsClicked != null) { RowTagsClicked(idx); return; }
            }

            if (idx >= 0 && ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - ScrollY;
                if (PinRect(pinTop, RowHeight).Contains(e.Location))
                {
                    // Pinning is an independent action too, like play below.
                    if (RowPinClicked != null) RowPinClicked(idx);
                    return;
                }
            }

            if (idx >= 0 && ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
                if (PlayRect(top, RowHeight).Contains(e.Location))
                {
                    // Listening is an independent action: we do not touch the row selection, or
                    // the details panel would jump on every press of play.
                    if (RowPlayClicked != null) RowPlayClicked(idx);
                    return;
                }
            }

            if (idx >= 0 && ShowCheckboxes && e.X < PadX + CheckW)
            {
                // The checkbox is a toggle in its own right; we do not touch the row selection.
                RowData row = _rows[idx];
                row.Checked = !row.Checked;
                Invalidate();
                if (RowCheckedChanged != null) RowCheckedChanged(idx);
                return;
            }
            if (idx >= 0 && idx != _selected)
            {
                _selected = idx;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }

            // Dragging outwards is only possible when the row has a file at all — the cursor
            // confirms that before the mouse movement decides whether this is a click or a
            // drag.
            if (idx >= 0 && e.Button == MouseButtons.Left && DragFilePath != null)
            {
                _rowDragIdx = idx;
                _rowDragStart = e.Location;
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _rowDragIdx = -1;

            if (_resizeCol >= 0)
            {
                _resizeCol = -1;
                if (ColumnsResized != null) ColumnsResized();
            }

            if (_dragCol >= 0)
            {
                int from = _dragCol, to = _dropAt;
                _dragCol = -1; _dropAt = -1; _pressCol = -1;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
                // to == from and to == from+1 are both "put it back where it was taken from".
                if (to >= 0 && to != from && to != from + 1 && ColumnsReordered != null)
                    ColumnsReordered(from, to);
            }
            else if (_pressCol >= 0)
            {
                // The header was pressed and released without being led anywhere — that is a
                // sort.
                int col = _pressCol;
                _pressCol = -1;
                if (e.Button == MouseButtons.Left && col < _columns.Count
                    && _columns[col].Sortable && HeaderClicked != null)
                    HeaderClicked(col);
            }

            _draggingBar = false;
            if (_draggingHBar)
            {
                _draggingHBar = false;
                _scrollXTarget = GetNearestSnapTarget(_scrollX);
                ClampScrollXTarget();
                _scrollX = (int)_scrollXTarget;
                _scrollXCurrent = _scrollXTarget;
                ClampScrollX();
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        /// <summary>
        /// A double click activates a row — but only if it landed on the row itself rather than
        /// on one of its independent buttons (the "+N" tail, the star, play, the checkbox). On
        /// Windows the second click of a quick double tap does not go through OnMouseDown a
        /// second time — it arrives here, bypassing those same checks. The row's buttons are
        /// all toggles (pin, play/pause, expanding the versions, the checkbox), so the second
        /// click is simply swallowed here — otherwise it would repeat the same action and
        /// cancel the first: the pin would pin and unpin at once, play would start and pause at
        /// once.
        /// </summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int idx = RowAt(e.Y);
            if (idx >= 0 && OnRowAccessory(idx, e.Location))
            {
                base.OnMouseDoubleClick(e);
                return;
            }
            if (idx < 0)
            {
                base.OnMouseDoubleClick(e);
                return;
            }
            if (ItemActivated != null) ItemActivated(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        /// <summary>Is the cursor currently over one of row idx's buttons — the "+N" tail, the
        /// tags, the star, play or the checkbox — rather than over the row itself.</summary>
        bool OnRowAccessory(int idx, Point p)
        {
            if (CountAtPoint(p) == idx) return true;
            if (TagsAtPoint(p) == idx) return true;

            if (ShowPinIndicator)
            {
                int pinTop = HeaderHeight + idx * RowHeight - ScrollY;
                if (PinRect(pinTop, RowHeight).Contains(p)) return true;
            }
            if (ShowPlayButton && _rows[idx].CanPlay)
            {
                int top = HeaderHeight + idx * RowHeight - ScrollY;
                if (PlayRect(top, RowHeight).Contains(p)) return true;
            }
            if (ShowCheckboxes && p.X < PadX + CheckW) return true;

            return false;
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = 0;
            if (e.KeyCode == Keys.Down) step = 1;
            else if (e.KeyCode == Keys.Up) step = -1;
            else if (e.KeyCode == Keys.PageDown) step = PageStep;
            else if (e.KeyCode == Keys.PageUp) step = -PageStep;

            if (step != 0 && MoveSelection(step)) e.Handled = true;
            base.OnKeyDown(e);
        }

        public int SelectedIndex { get { return _selected; } }

        /// <summary>
        /// Put the selection back on the row with the same number. Reordering the columns needs
        /// it: the rows there are the very same ones and only their layout changes, while
        /// SetRows resets the selection all the same — and without this a column would move at
        /// the price of losing whatever was selected.
        /// </summary>
        public void SelectIndex(int idx)
        {
            if (idx < 0 || idx >= _rows.Count || idx == _selected) return;
            _selected = idx;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        /// <summary>How far PageUp/PageDown jump — by a screen of rows.</summary>
        public int PageStep { get { return Math.Max(1, (Height - HeaderHeight) / RowHeight); } }

        /// <summary>
        /// Move the selection by step rows. Kept apart from OnKeyDown because the arrows arrive
        /// from more than here: the main window drives the catalog with them regardless of
        /// where the focus currently is.
        ///
        /// true at the edge of the list too — we swallowed the key all the same, and it must
        /// not be passed on into focus navigation: that would carry the selection into a
        /// neighbouring control.
        /// </summary>
        public bool MoveSelection(int step)
        {
            if (_rows.Count == 0) return false;
            int want = Math.Max(0, Math.Min(_rows.Count - 1, (_selected < 0 ? 0 : _selected) + step));
            if (want != _selected)
            {
                _selected = want;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            EnsureVisible(want);
            return true;
        }

        /// <summary>
        /// Where to put a context menu called from the keyboard: at the left edge of the row,
        /// halfway down it. There is no mouse here, and the menu has to come out at the row in
        /// question — not where the cursor happened to be left.
        /// </summary>
        public Point RowMenuPoint(int idx)
        {
            int top = HeaderHeight + idx * RowHeight - ScrollY + RowHeight / 2;
            return new Point(LeftX, Math.Max(0, Math.Min(Height - 1, top)));
        }

        void EnsureVisible(int idx)
        {
            int top = HeaderHeight + idx * RowHeight - _scroll;
            if (top < HeaderHeight) _scroll += top - HeaderHeight;
            else if (top + RowHeight > ViewH) _scroll += top + RowHeight - ViewH;
            _scrollTarget = _scrollCurrent = _scroll;
            if (_scroller != null) _scroller.SyncPosition(_scroll);
            ClampScroll();
        }

        // ------------------------------------------------------------------ drawing

        int[] ComputeWidths()
        {
            int[] w = new int[_columns.Count];
            int fixedTotal = 0, flexCount = 0;
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Width > 0) { w[i] = Sc(_columns[i].Width); fixedTotal += w[i]; }
                else flexCount++;
            }
            int available = Width - LeftX - PadRight - fixedTotal;
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Width == 0)
                {
                    int minW = i == 0 ? Sc(220) : Sc(80);
                    w[i] = Math.Max(minW, available / Math.Max(1, flexCount));
                }
            }
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            Theme.Smooth(g);

            int[] widths = ComputeWidths();
            int rowH = RowHeight;
            int pillH = Sc(Theme.RowPillH);

            int scrollLeft = LeftX + widths[0];
            int scrollWidth = Math.Max(0, Width - PadRight - scrollLeft);

            // While the column grips are fading in and out, the timer pulls Invalidate only for
            // the header rectangle — the list body does not fall into the clip and is discarded
            // anyway, so the whole pass over the rows below can be skipped.
            bool headerOnly = e.ClipRectangle.Bottom <= HeaderHeight;

            if (!headerOnly)
            {
                Region baseClip = g.Clip;

                // The rubber-band overshoot past the edge. We shift only the body of the list:
                // the header is pinned and must not travel with the rows.
                int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
                int slack = Sc(8) + Math.Abs(over);

                // The rectangles of the "+N" tails are collected anew on every full pass: the
                // rows have moved with the scroll, and yesterday's coordinates must not be
                // clicked.
                if (_countHit == null || _countHit.Length != _rows.Count)
                    _countHit = new Rectangle[_rows.Count];
                Array.Clear(_countHit, 0, _countHit.Length);

                if (_tagsHit == null || _tagsHit.Length != _rows.Count)
                    _tagsHit = new Rectangle[_rows.Count];
                Array.Clear(_tagsHit, 0, _tagsHit.Length);

                int first = Math.Max(0, (_scroll - slack) / rowH);
                int last = Math.Min(_rows.Count - 1, (_scroll + ViewH + slack) / rowH);

                // 1. Pass: row backgrounds (the selection and hover pills) across the full
                // width
                g.SetClip(new Rectangle(0, HeaderHeight, Width, Math.Max(0, ViewH - HeaderHeight)));
                for (int i = first; i <= last; i++)
                {
                    int top = HeaderHeight + i * rowH - _scroll - over;
                    if (top > ViewH) break;

                    float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                    if (entrance <= 0.001f) continue;

                    int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                    int topAnim = top + offsetY;

                    int pillW = Math.Max(0, Width - PillRightGap);
                    Rectangle pill = new Rectangle(0, topAnim + (rowH - pillH) / 2, pillW, pillH);
                    float hoverFactor = (_rowHoverFactors != null && i < _rowHoverFactors.Length) ? _rowHoverFactors[i] : (i == _hot ? 1f : 0f);

                    // On an already selected row the hover fill is not wanted — it has an
                    // outline of its own as it is, and a fill on top of that argued with the
                    // text.
                    if (i != _selected && hoverFactor > 0.001f)
                    {
                        Color c = Color.FromArgb((int)Math.Round(hoverFactor * Theme.RowHover.A * entrance), Theme.RowHover);
                        Theme.FillRound(g, pill, pillH / 2f, c);
                    }
                    if (i == _selected)
                    {
                        // Selection in ordinary light, outline only: a fill argued with the
                        // row's text, and the colour itself does not depend on the choice of
                        // accent — the accent is claimed by buttons, progress and switches.
                        RectangleF ring = RectangleF.Inflate(pill, -0.75f, -0.75f);
                        Color line = Color.FromArgb((int)Math.Round(0xC0 * entrance), Theme.Light);
                        using (GraphicsPath rp = Theme.Round(ring, pillH / 2f - 0.75f))
                        using (Pen pen = new Pen(line, 1.5f))
                            g.DrawPath(pen, rp);
                    }
                }

                // 2. Pass: the scrolling columns (1 through N-1), strictly clipped by the
                // scrollLeft and PadRight boundaries
                if (_columns.Count > 1 && scrollWidth > 0)
                {
                    g.SetClip(new Rectangle(scrollLeft, HeaderHeight, scrollWidth, Math.Max(0, ViewH - HeaderHeight)));

                    for (int i = first; i <= last; i++)
                    {
                        int top = HeaderHeight + i * rowH - _scroll - over;
                        if (top > ViewH) break;

                        float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                        if (entrance <= 0.001f) continue;

                        int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                        int topAnim = top + offsetY;
                        RowData row = _rows[i];
                        bool dim = ShowCheckboxes && !row.Checked;
                        bool bright = i == _selected;

                        for (int c = 1; c < _columns.Count && c < row.Cells.Length; c++)
                        {
                            int cx = ColX(widths, c);
                            if (cx < scrollLeft || cx >= Width - PadRight) continue;
                            PaintCell(g, widths, topAnim, rowH, entrance, dim, bright, row, c, false, i);
                        }

                        foreach (CellMark m in row.Marks)
                        {
                            if (m.Column >= 1)
                            {
                                int mx = ColX(widths, m.Column);
                                if (mx < scrollLeft || mx >= Width - PadRight) continue;
                                PaintMark(g, widths, topAnim, rowH, m);
                            }
                        }
                    }
                }

                // 3. Pass: the pinned name column (0) and the gutters on the left (0 ..
                // scrollLeft)
                {
                    int col0W = Math.Min(scrollLeft, Width - PadRight);
                    g.SetClip(new Rectangle(0, HeaderHeight, col0W, Math.Max(0, ViewH - HeaderHeight)));

                    for (int i = first; i <= last; i++)
                    {
                        int top = HeaderHeight + i * rowH - _scroll - over;
                        if (top > ViewH) break;

                        float entrance = (_rowEntrance != null && i < _rowEntrance.Length) ? _rowEntrance[i] : 1.0f;
                        if (entrance <= 0.001f) continue;

                        int offsetY = (int)Math.Round((1.0f - entrance) * Sc(18));
                        int topAnim = top + offsetY;
                        RowData row = _rows[i];
                        bool dim = ShowCheckboxes && !row.Checked;
                        bool bright = i == _selected;

                        if (ShowCheckboxes) PaintCheckbox(g, topAnim, rowH, row.Checked);
                        if (ShowPinIndicator && (row.Pinned || i == _hot))
                            PaintPin(g, topAnim, rowH, i == _pinHot, row.Pinned);
                        if (ShowPlayButton && row.CanPlay)
                            PaintPlay(g, topAnim, rowH, i == _playHot,
                                      Playing && SetEntry.SameSet(PlayingTag, row.Tag));

                        if (_columns.Count > 0 && row.Cells.Length > 0)
                        {
                            PaintCell(g, widths, topAnim, rowH, entrance, dim, bright, row, 0, i == _countHot, i);
                        }

                        foreach (CellMark m in row.Marks)
                        {
                            if (m.Column == 0) PaintMark(g, widths, topAnim, rowH, m);
                        }
                    }
                }

                g.Clip = baseClip;

                // There is no divider between the pinned ones and the rest any more: the pinned
                // are visible by their star as it is, and a line inside the list argued with
                // the line under the header — at their intersection with the vertical divider
                // an extra light pixel appeared.

                // 5. A thin vertical divider between the pinned column and the scrolling area
                if (_scrollX > 0 && _columns.Count > 1)
                {
                    using (Pen divPen = new Pen(Theme.Hairline))
                        g.DrawLine(divPen, scrollLeft - 1, HeaderHeight, scrollLeft - 1, Height);
                }

                // The bottom rows dissolve slightly into the background — a thin band, not the
                // former third of a screen. On glass we kill it entirely, see the comment on
                // FadeBottom.
                if (FadeBottom && !Glass.Enabled && ContentHeight > Height)
                {
                    int fadeH = Sc(36);
                    int fadeW = Math.Max(0, Width - PillRightGap);
                    Rectangle fr = new Rectangle(0, Height - fadeH, fadeW, fadeH);
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new Rectangle(fr.X, fr.Y - 1, fr.Width, fr.Height + 2),
                        Color.FromArgb(0, Surface), Surface, LinearGradientMode.Vertical))
                        g.FillRectangle(lb, fr);
                }
            }

            PaintHeader(g, widths);

            if (!headerOnly)
            {
                // The bar under the rows has a lane of its own. A Clip does not help here: the
                // text is drawn by TextRenderer past GDI+ and it ignores clipping, so we simply
                // paint the bottom strip over with the background and lay the bar on top.
                if (HBarSpace > 0)
                    Chrome.PaintBase(this, g, new Rectangle(0, Height - HBarSpace, Width, HBarSpace), Surface);

                PaintFadingBar(g, BarRect(), _barFade, _draggingBar, true);
                PaintFadingBar(g, HBarRect(), _hbarFade, _draggingHBar, false);
            }
        }

        void PaintFadingBar(Graphics g, Rectangle bar, ScrollFade fade, bool dragging, bool vertical)
        {
            Chrome.PaintFadingBar(g, bar, dragging ? 1f : fade.Alpha, dragging ? 1f : fade.Thick, vertical, Sc(4));
        }

        void PaintCell(Graphics g, int[] widths, int topAnim, int rowH, float entrance, bool dim, bool bright, RowData row, int c, bool countHot, int rowIndex)
        {
            Column col = _columns[c];
            Font f = col.Font ?? Theme.FBody;
            Color color = dim ? Theme.TextDim
                              : (col.Color ?? (c == 0 || bright ? Theme.Text : Theme.TextDim));
            if (entrance < 1.0f) color = Color.FromArgb((int)Math.Round(color.A * entrance), color);

            // A version under an expanded row is indented in the name column, and right in that
            // indent sits a short rail: only on child rows and only next to the text rather
            // than across the whole row — like the "|" before a name in a file tree.
            bool childHere = row.ChildRow && col.Id == "Set";
            int indent = childHere ? Sc(20) : 0;
            int x = ColX(widths, c);

            if (childHere)
            {
                Rectangle rail = new Rectangle(x + Sc(6), topAnim + Sc(4), Sc(2), rowH - Sc(8));
                Color railColor = Color.FromArgb((int)Math.Round(120 * entrance), Theme.TextDim);
                g.FillRectangle(Theme.GetBrush(railColor), rail);
            }
            int maxRight = Width - PadRight;
            int cellX = x + indent;
            int cellW = Math.Max(0, widths[c] - Sc(10) - indent);
            if (cellX + cellW > maxRight) cellW = Math.Max(0, maxRight - cellX);
            if (cellW <= 0 && cellX >= maxRight) return;
            Rectangle cr = new Rectangle(cellX, topAnim, cellW, rowH);
            // The last argument is the left boundary of the clickable zone: a scrolling column
            // can slide under the pinned first one, where the drawing is cut off by the clip,
            // but a mouse hit has to be cut off by us.
            if (col.Chips)
                PaintChips(g, cr, row.Cells[c], color, rowIndex, rowIndex == _hot, rowIndex == _tagsHot,
                           c >= 1 ? LeftX + widths[0] : 0);
            else
            {
                string cellText = row.Cells[c];
                int plusIdx = !col.Right ? CountAt(cellText) : -1;
                if (plusIdx > 0)
                {
                    string mainText = cellText.Substring(0, plusIdx);
                    string countText = cellText.Substring(plusIdx + CountSep.Length);

                    // Under the cursor the tail lightens — otherwise nobody would guess it can
                    // be clicked to expand the versions.
                    Color countColor = countHot ? Theme.Text : Theme.TextDim;
                    if (entrance < 1.0f) countColor = Color.FromArgb((int)Math.Round(countColor.A * entrance), countColor);

                    Size countSz = TextRenderer.MeasureText(countText, f);
                    Size mainSz = TextRenderer.MeasureText(mainText, f);

                    int countX = cr.X + mainSz.Width - Sc(4);
                    if (countX + countSz.Width <= cr.Right)
                    {
                        Rectangle mainR = new Rectangle(cr.X, cr.Y, mainSz.Width, cr.Height);
                        Chrome.DrawText(g, mainText, f, mainR, color, Chrome.CellLeft);

                        Rectangle countR = new Rectangle(countX, cr.Y, Math.Max(0, cr.Right - countX), cr.Height);
                        Chrome.DrawText(g, countText, f, countR, countColor, Chrome.CellLeft);
                        RememberCount(rowIndex, countX, cr.Y, countSz.Width, cr.Height);
                    }
                    else
                    {
                        int maxMainW = Math.Max(0, cr.Width - countSz.Width - Sc(4));
                        Rectangle mainR = new Rectangle(cr.X, cr.Y, maxMainW, cr.Height);
                        Chrome.DrawText(g, mainText, f, mainR, color, Chrome.CellLeft);

                        int cX = cr.X + maxMainW + Sc(4);
                        if (cX < cr.Right)
                        {
                            Rectangle countR = new Rectangle(cX, cr.Y, Math.Max(0, cr.Right - cX), cr.Height);
                            Chrome.DrawText(g, countText, f, countR, countColor, Chrome.CellLeft);
                            RememberCount(rowIndex, cX, cr.Y, Math.Min(countSz.Width, cr.Right - cX), cr.Height);
                        }
                    }
                }
                else
                {
                    Chrome.DrawText(g, row.Cells[c], f, cr, color,
                                   col.Right ? Chrome.CellRight : Chrome.CellLeft);
                }
            }
        }

        int ColX(int[] widths, int col)
        {
            if (col <= 0) return LeftX;
            int x = LeftX + widths[0] - _scrollX;
            for (int c = 1; c < col; c++) x += widths[c];
            return x;
        }

        // A heading and the mark in the data below it always share one and the same rectangle —
        // the left boundary of their own column, with no creeping into the neighbouring one.
        Rectangle ColSlot(int[] widths, int top, int rowH, int col)
        {
            int x = ColX(widths, col);
            int maxRight = Width - PadRight;
            int w = Math.Max(0, widths[col] - Sc(10));
            if (x + w > maxRight) w = Math.Max(0, maxRight - x);
            return new Rectangle(x, top, w, rowH);
        }

        /// <summary>A square checkbox in the gutter to the left of the first column.</summary>
        void PaintCheckbox(Graphics g, int top, int rowH, bool on)
        {
            float cs = Sc(16);
            RectangleF cb = new RectangleF(PadX, top + (rowH - cs) / 2f, cs, cs);
            if (on)
            {
                Theme.FillRound(g, cb, Sc(4), Theme.Light);
                Icons.Draw(g, Glyph.Check, RectangleF.Inflate(cb, -Sc(3), -Sc(3)), Theme.OnLight, 1.6f);
            }
            else
            {
                Theme.DrawRound(g, cb, Sc(4), Theme.TextDim, 1.3f);
            }
        }

        /// <summary>
        /// The "listen" triangle, in the same palette as the star: muted grey at rest, the
        /// accent colour on the playing row as on a pinned star. We do not yet single out hover
        /// with a look of its own.
        /// </summary>
        void PaintPlay(Graphics g, int top, int rowH, bool hot, bool playing)
        {
            RectangleF r = PlayRect(top, rowH);

            // While a row is sounding and the cursor is not on it, a live pulse takes the
            // glyph's place: it is visible that this row is the one playing. Under the cursor
            // the pause comes back, or there would be nothing to stop it with.
            if (playing && !hot)
            {
                RectangleF pr = RectangleF.Inflate(r, -Sc(5), -Sc(6));
                PlayPulse.Paint(g, pr, Theme.Light);
                return;
            }

            Color ink = playing ? Theme.Light : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);
            // A smaller inset than the star's: the triangle/pause itself is drawn smaller than
            // its box (the glyph's own proportions), and with the star's inset it would look
            // noticeably smaller than the star at the same button size.
            Icons.Draw(g, playing ? Glyph.Pause : Glyph.Play,
                       RectangleF.Inflate(r, -Sc(1), -Sc(1)), ink, 1.5f);
        }

        /// <summary>
        /// The pin star. On a pinned row it is visible permanently (otherwise how would one
        /// know a project is pinned without hovering over every row), on the rest while the
        /// cursor is somewhere on the row, so a long list does not shimmer.
        /// </summary>
        void PaintPin(Graphics g, int top, int rowH, bool hot, bool pinned)
        {
            RectangleF r = PinRect(top, rowH);
            Color ink = pinned ? Theme.Light : (hot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
            Icons.Draw(g, pinned ? Glyph.StarFill : Glyph.Star,
                       RectangleF.Inflate(r, -Sc(4), -Sc(4)), ink, 1.3f);
        }

        /// <summary>
        /// Tags as pills, by the same device as in the details panel — only smaller. In one
        /// lane, and if not everything fits into it, in two: a table row holds exactly two
        /// pills by height, and more is not needed. Whatever does not fit into the second
        /// either is collapsed into an ellipsis: a pill cut in half would read as broken layout
        /// rather than as "there are more tags than are visible".
        ///
        /// The pills themselves are the edit button: they are clicked to open the tag editor.
        /// In an empty cell there is nothing to click, so with the cursor on the row a "+"
        /// appears — keeping it permanently in every row would mean sowing the whole table with
        /// pluses, and most projects have no tags.
        /// </summary>
        const string AddTagsLabel = "Add tags";

        void PaintChips(Graphics g, Rectangle cell, string joined, Color textColor,
                        int rowIndex, bool rowHot, bool hot, int clipLeft)
        {
            // A smaller type size than the rest of the row's text: at eleven the pills come out
            // 21 px, two lanes eat almost the whole row, and the pills of neighbouring rows end
            // up as far from each other as the two lanes within one — the tags stop reading as
            // the tags of one project.
            Font f = Theme.FMini;
            int vgap = Sc(3);

            // The pill height comes from the font rather than from what is left of the row:
            // text that needs 17 px will not fit into a pill of 16, and the descenders run into
            // the edges. Two pills with a gap take 39 px out of 53, and the rest of the row
            // becomes the margins above and below by itself (the block is centred below).
            int h = Math.Min(Chrome.PillHeight(f, 4), (cell.Height - vgap) / 2);

            string[] tags = string.IsNullOrEmpty(joined)
                ? new string[0]
                : joined.Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

            if (tags.Length == 0)
            {
                if (!rowHot) return;

                // A tag and a caption, as in the details panel — a bare plus did not say what
                // it would add. The glyph and the text share one box the height of a pill: the
                // glyph stands in its centre, the text by the same rule as in the pills
                // (Chrome.PillTop), so the letters and the glyph are aligned with each other.
                Color ink = hot ? Theme.Text : Theme.TextDim;
                // The tag is fitted into a square, and it is itself wide and low (14×10 in the
                // source), so across the width it takes the whole square and across the height
                // two thirds.
                int icon = Math.Max(Sc(12), h - Sc(4));
                int iconGap = Sc(6);
                int textW = TextRenderer.MeasureText(AddTagsLabel, f, new Size(short.MaxValue, h),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                int hintW = icon + iconGap + textW;
                if (hintW > cell.Width) return;

                int hintY = cell.Y + (cell.Height - h) / 2;
                Icons.Draw(g, Glyph.Tag,
                           new RectangleF(cell.X, hintY + (h - icon) / 2f, icon, icon), ink, 1.2f);
                // NoClipping in Chrome.PillText: the rectangle here is exactly to the measure
                // of the text, and without it the tail of a "g" would be cut off by its own
                // box.
                Chrome.DrawText(g, AddTagsLabel, f,
                                new Rectangle(cell.X + icon + iconGap, hintY + Chrome.PillTop(g, f, h), textW, h),
                                ink, Chrome.PillText);
                RememberTags(rowIndex, new Rectangle(cell.X - Sc(3), cell.Y, hintW + Sc(6), cell.Height), clipLeft);
                return;
            }

            int gap = Sc(5);
            int padX = Sc(8);

            int[] chipW = new int[tags.Length];
            int total = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                chipW[i] = TextRenderer.MeasureText(tags[i], f, new Size(short.MaxValue, h),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + padX * 2;
                total += chipW[i] + (i > 0 ? gap : 0);
            }

            // We start a second lane only when one really did not suffice: there is no point
            // tearing the row apart vertically for a couple of short tags. And if even the
            // first tag does not fit, the second lane has all the less to show — only an
            // ellipsis.
            int lines = (total <= cell.Width || chipW[0] > cell.Width) ? 1 : 2;
            int top = cell.Y + (cell.Height - (lines * h + (lines - 1) * vgap)) / 2;

            Color fill = Color.FromArgb(hot ? 0x3D : 0x22, 0xFF, 0xFF, 0xFF);
            Color chipInk = hot ? Theme.Text : textColor;
            int textTop = Chrome.PillTop(g, f, h);

            int next = 0, right = cell.X;
            for (int line = 0; line < lines; line++)
            {
                int x = cell.X;
                int y = top + line * (h + vgap);

                while (next < tags.Length && x + chipW[next] <= cell.Right)
                {
                    Rectangle chip = new Rectangle(x, y, chipW[next], h);
                    Theme.FillRound(g, chip, h / 2f, fill);
                    Chrome.DrawText(g, tags[next], f,
                                    new Rectangle(chip.X, chip.Y + textTop, chip.Width, chip.Height),
                                    chipInk, Chrome.PillText);
                    x += chipW[next] + gap;
                    next++;
                }

                if (line == lines - 1 && next < tags.Length)
                {
                    int dot = Sc(3), ew = Chrome.DotsWidth(dot);
                    int ex = Math.Min(x, cell.Right - ew);
                    if (ex >= cell.X)
                    {
                        Chrome.DrawDots(g, new Rectangle(ex, y, ew, h), chipInk, dot);
                        x = ex + ew + gap;
                    }
                }

                right = Math.Max(right, Math.Min(cell.Right, x - gap));
            }

            if (right > cell.X)
                RememberTags(rowIndex, new Rectangle(cell.X, cell.Y, right - cell.X, cell.Height), clipLeft);
        }

        void PaintMark(Graphics g, int[] widths, int top, int rowH, CellMark m)
        {
            if (m.Column < 0 || m.Column >= _columns.Count || string.IsNullOrEmpty(m.Text)) return;

            Rectangle slot = ColSlot(widths, top, rowH, m.Column);
            if (slot.Width <= 0) return;

            int yOffset = 0;
            // The tick character (Segoe UI/Emoji) sits below the line's optical centre because
            // of the font metrics
            if (m.Text.IndexOfAny(new char[] { '\u2714', '\u2713', '\u2705' }) >= 0)
                yOffset = -Sc(2);

            // The coloured strip at the right edge is gone: the text itself is already
            // coloured, and the strip only doubled the same message in the rightmost column.
            Chrome.DrawText(g, m.Text, Theme.FBody, new Rectangle(slot.Left, top + yOffset, slot.Width, rowH),
                            m.Color, Chrome.CellRight);
        }

        void PaintHeader(Graphics g, int[] widths)
        {
            Chrome.PaintBase(this, g, new Rectangle(0, 0, Width, HeaderHeight), Surface);

            int scrollLeft = LeftX + widths[0];
            int scrollWidth = Math.Max(0, Width - PadRight - scrollLeft);

            // 1. Drawing the headings of the scrolling columns (1 through N-1)
            if (_columns.Count > 1 && scrollWidth > 0)
            {
                Region oldClip = g.Clip;
                g.SetClip(new Rectangle(scrollLeft, 0, scrollWidth, HeaderHeight));

                for (int c = 1; c < _columns.Count; c++)
                {
                    int cx = ColX(widths, c);
                    if (cx < scrollLeft || cx >= Width - PadRight) continue;
                    PaintHeaderCell(g, widths, c);
                }

                g.Clip = oldClip;
            }

            // 2. The pinned area of the header (column 0 and the left gutter)
            {
                Region oldClip = g.Clip;
                int head0W = Math.Min(scrollLeft, Width - PadRight);
                g.SetClip(new Rectangle(0, 0, head0W, HeaderHeight));

                if (ShowPinIndicator && ShowHeaderPin)
                {
                    RectangleF hp = PinRect(0, HeaderHeight);
                    Color ink = PinnedFirst ? Theme.Light
                              : (_headPinHot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
                    Icons.Draw(g, PinnedFirst ? Glyph.StarFill : Glyph.Star,
                               RectangleF.Inflate(hp, -Sc(4), -Sc(4)), ink, 1.3f);
                }

                if (_columns.Count > 0)
                {
                    PaintHeaderCell(g, widths, 0);
                }

                g.Clip = oldClip;
            }

            // 3. A thin vertical divider in the header
            if (_scrollX > 0 && _columns.Count > 1)
            {
                using (Pen divPen = new Pen(Theme.Hairline))
                    g.DrawLine(divPen, scrollLeft - 1, 0, scrollLeft - 1, HeaderHeight);
            }

            // 4. The line under the header: without it the headings are set in the same size
            // and colour as the body, and the header is not separated from the list at all.
            if (ShowHeaderRule && _columns.Count > 0)
            {
                using (Pen rule = new Pen(Theme.Hairline))
                    g.DrawLine(rule, PadX, HeaderHeight - 1, Width - PadRight, HeaderHeight - 1);
            }

            PaintDropMark(g, widths);
            PaintGrip(g, widths);
        }

        void PaintHeaderCell(Graphics g, int[] widths, int c)
        {
            bool active = c == SortColumn;
            Column col = _columns[c];

            Rectangle hr = ColSlot(widths, 0, HeaderHeight, c);
            if (hr.Width <= 0) return;

            Size tsz = TextRenderer.MeasureText(col.Title, Theme.FLabel);
            if (active)
            {
                // The triangle is drawn as a separate element, or it gets clipped along with
                // the text in a narrow column.
                int arrow = Sc(14);
                RectangleF ar = col.Right
                    ? new RectangleF(hr.Right - arrow, (HeaderHeight - arrow) / 2f, arrow, arrow)
                    : new RectangleF(Math.Min(hr.X + tsz.Width + Sc(6), hr.Right - arrow),
                                     (HeaderHeight - arrow) / 2f, arrow, arrow);
                Icons.Draw(g, SortDescending ? Glyph.SortDown : Glyph.SortUp, ar, Theme.Text, 1f);
                hr.Width = Math.Max(0, hr.Width - arrow - Sc(4));
            }

            // The heading being dragged fades out: it is "in hand", while where it will land is
            // shown by the vertical bar below.
            Color ink = active ? Theme.Text : Theme.TextDim;
            if (c == _dragCol) ink = Color.FromArgb(ink.A / 3, ink);

            Chrome.DrawText(g, col.Title, Theme.FLabel, hr, ink,
                            col.Right ? Chrome.CellRight : Chrome.CellLeft);
        }

        /// <summary>
        /// Where the column will land if released now — a vertical bar on the boundary between
        /// headings, as in Explorer. Drawn only while dragging.
        /// </summary>
        void PaintDropMark(Graphics g, int[] widths)
        {
            if (_dragCol < 0 || _dropAt < 0) return;
            if (_dropAt == _dragCol || _dropAt == _dragCol + 1) return;   // put it back

            int x = _dropAt == 0 ? LeftX : (_dropAt < _columns.Count ? ColX(widths, _dropAt) : ColX(widths, _columns.Count - 1) + widths[_columns.Count - 1]);
            if (x > Width - PadRight) x = Width - PadRight;

            int top = Sc(10), bottom = HeaderHeight - Sc(10);
            using (Pen p = new Pen(Theme.Text, Sc(2)))
                g.DrawLine(p, x, top, x, bottom);
        }

        /// <summary>
        /// Thin grips on every boundary between headings — they all appear at once as soon as
        /// the cursor enters the header, so that it is immediately visible where anything can
        /// be dragged rather than groping for the boundaries blindly. The rest of the time the
        /// header is clean.
        /// </summary>
        void PaintGrip(Graphics g, int[] widths)
        {
            if (_gripAlpha <= 0.001f || !ColumnsConfigurable) return;

            int top = Sc(13), bottom = HeaderHeight - Sc(13);
            if (bottom <= top) return;

            int a = (int)(180 * Math.Min(1f, Math.Max(0f, _gripAlpha)));
            using (Pen p = new Pen(Color.FromArgb(a, Theme.TextDim)))
            {
                if (_columns.Count > 0)
                {
                    int x0 = LeftX + widths[0];
                    if (x0 < Width - PadRight)
                        g.DrawLine(p, x0, top, x0, bottom);
                }
                for (int c = 1; c < _columns.Count; c++)
                {
                    if (_columns[c].Width > 0)
                    {
                        int x = ColX(widths, c);
                        if (x >= LeftX + widths[0] - Sc(2) && x < Width - PadRight)
                            g.DrawLine(p, x, top, x, bottom);
                    }
                }
            }
        }
    }
}
