using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// A summary of the library above the project list: tiles with numbers and a calendar of
    /// the work.
    ///
    /// Not a control but a painter. The panel has to travel with the tiles — it stands ABOVE
    /// them in one scrolling flow rather than as a separate header; a control of its own would
    /// have to be moved by hand behind somebody else's scroll, and it would still cover the
    /// tiles on an overshoot past the edge. So HomeView asks the panel for a height, hands it a
    /// piece of its own surface and forwards the mouse — just as it does with its section
    /// headings.
    ///
    /// The panel counts no data of its own: both the catalog and the history come from
    /// ProjectIndex, and the summary is recomputed only when the list there has been swapped (a
    /// scan publishes a new one — see ProjectIndex.Sets).
    /// </summary>
    public sealed class OverviewPanel
    {
        public ProjectIndex Index;

        /// <summary>Whether the panel is expanded. Collapsed, it is one line with a
        /// heading.</summary>
        public bool Open = true;

        public float Dpi = 1f;

        /// <summary>The height changed — time for the owner to recompute the layout.</summary>
        public event Action LayoutChanged;

        /// <summary>Something changed in appearance — a redraw is enough.</summary>
        public event Action Repaint;

        /// <summary>The user collapsed the panel — that is worth remembering.</summary>
        public event Action StateChanged;

        /// <summary>The panel's place without the scroll — like Bounds on a section
        /// heading.</summary>
        public Rectangle Bounds;

        int Sc(int v) { return (int)Math.Round(v * Dpi); }

        /// <summary>
        /// The value in a tile. A type size of its own rather than FTitle: the caption and the
        /// number have to read as a "small grey / large white" pair, and at thirteen they merge
        /// into two identical lines.
        /// </summary>
        static readonly Font FValue = Theme.UISemibold(15f);

        // ------------------------------------------------------------------ sizes

        const int CardGap = 8;
        const int Cols = 4;        // tiles per row
        const int CellGap = 3;     // between calendar cells
        const int CellMax = 14;

        /// <summary>
        /// The panel takes two thirds of the width. Across the full width a tile stretched into
        /// a six-to-one strip: the caption presses against the left edge, half a card of
        /// emptiness on the right, and the whole block reads as having come apart. The right
        /// third is deliberately free — something will turn up for it, and until then air is
        /// better than stretching.
        /// </summary>
        int ContentWidth(int width)
        {
            return width <= Sc(560) ? width : Math.Max(Sc(560), width * 2 / 3);
        }

        // ---------------------------------------------------------------- layout

        Rectangle _chevron, _title, _grid, _splash;

        /// <summary>The whole heading row is clickable — the panel collapses by it.</summary>
        Rectangle _toggle;
        bool _headHot;
        readonly List<Rectangle> _cards = new List<Rectangle>();
        readonly List<string> _cardLabel = new List<string>();
        readonly List<string> _cardValue = new List<string>();

        DateTime _gridFrom, _gridTo;
        int _cell, _gridMax;

        /// <summary>
        /// Row heights come from the fonts themselves rather than being assigned as numbers. A
        /// box shorter than the line is not "denser", it is letters clipped top and bottom:
        /// with VerticalCenter, GDI centres the line in the box and shaves off whatever does
        /// not fit. The first version of the panel shaved the tails off "y" and "j" in the
        /// captions in exactly that way.
        /// </summary>
        int _labH, _valH, _headH, _monthH;

        void MeasureRows()
        {
            _labH = TextRenderer.MeasureText("Ag", Theme.FBadge).Height;
            _valH = TextRenderer.MeasureText("Ag", FValue).Height;
            _monthH = TextRenderer.MeasureText("Ag", Theme.FMini).Height;
            _headH = Math.Max(Sc(30), TextRenderer.MeasureText("Ag", Theme.FHead).Height + Sc(8));
        }

        /// <summary>How much air there is between a month caption and the first cell beneath
        /// it.</summary>
        int MonthGap { get { return Sc(7); } }

        /// <summary>
        /// There is no gap of its own between the caption and the number: a measured line
        /// already carries the font's internal leading above and below it, and any inset on top
        /// of that spreads the pair across a third of a tile — it stops reading as one.
        /// </summary>
        int CardHeight { get { return Sc(6) + _labH + _valH + Sc(6); } }

        int Step { get { return _cell + Sc(CellGap); } }

        int _gridW;

        /// <summary>
        /// Fit the cell to the allotted width and return the width of the resulting grid — the
        /// whole panel is aligned by it. The grid's own place is set later, in LayoutGrid: it
        /// depends on how much the tiles above it took.
        /// </summary>
        int PrepareGrid(int avail)
        {
            _gridTo = DateTime.Today;
            _gridFrom = _gridTo.AddDays(-364);

            int gap = Sc(CellGap);
            int cols = Weeks(_gridFrom, _gridTo);
            _cell = Math.Max(Sc(7), Math.Min(Sc(CellMax), (avail - gap * (cols - 1)) / Math.Max(1, cols)));
            _gridW = cols * Step - gap;

            // In a really narrow window the cell hits its lower limit and the grid spills past
            // the edge. We then align to the window: a panel that has slid off is worse than
            // edges that do not meet.
            return Math.Min(avail, _gridW);
        }

        /// <summary>
        /// Lay the panel out across the width and return its bottom edge. Recomputed on every
        /// rebuild of the grid — like everything else in HomeView.
        /// </summary>
        public int Layout(int width, int top)
        {
            _cards.Clear();
            _cardLabel.Clear();
            _cardValue.Clear();

            MeasureRows();

            // The width is set by the calendar. The cell is a whole number, and the grid is
            // almost always slightly narrower than the space allotted — the remainder of the
            // division has nowhere to go. Align the tiles to the available width and their
            // right edge misses the last column by a dozen pixels, and that shows.
            int w = PrepareGrid(ContentWidth(width));
            int y = top;
            int headH = _headH;

            int chev = Sc(18);
            _chevron = new Rectangle(0, y + (headH - chev) / 2, chev, chev);
            _title = new Rectangle(_chevron.Right + Sc(8), y, Sc(240), headH);

            int titleW = TextRenderer.MeasureText("Overview", Theme.FHead).Width;
            _toggle = new Rectangle(0, y, _title.X + titleW + Sc(10), headH);

            if (!Open)
            {
                Bounds = new Rectangle(0, top, w, headH);
                return Bounds.Bottom;
            }

            y += headH + Sc(14);
            y = LayoutCards(w, y);
            y += Sc(24);
            y = LayoutGrid(y);

            _splash = new Rectangle(0, y + Sc(12), w,
                                    TextRenderer.MeasureText("Ag", Theme.FLabel).Height);
            y = _splash.Bottom;

            Bounds = new Rectangle(0, top, w, y - top);
            return Bounds.Bottom;
        }

        int LayoutCards(int w, int y)
        {
            Stats st = Ensure();

            string[] labels = { "Active days", "Streak", "Record", "Peak hour" };
            string[] values =
            {
                Num(st.ActiveDays), Days(st.Streak), Days(st.Record), Hour(st.PeakHour),
            };

            int gap = Sc(CardGap);
            int ch = CardHeight;

            // The bounds are counted from the edge rather than as "tile width × number": with
            // integer division the last tile would swallow the remainder, and its right edge
            // would fall a couple of pixels short of the end of the calendar.
            for (int i = 0; i < labels.Length; i++)
            {
                int col = i % Cols;
                int x = col * (w + gap) / Cols;
                int right = (col + 1) * (w + gap) / Cols - gap;
                _cards.Add(new Rectangle(x, y + (i / Cols) * (ch + gap), right - x, ch));
                _cardLabel.Add(labels[i]);
                _cardValue.Add(values[i]);
            }

            int rows = (labels.Length + Cols - 1) / Cols;
            return y + rows * ch + (rows - 1) * gap;
        }

        /// <summary>
        /// A calendar of the last year: a column is a week, a row a day of the week, as on
        /// GitHub. The span is exactly one and is not selectable — the span switch was removed,
        /// and with it the second form of the calendar, which only the month needed.
        /// </summary>
        int LayoutGrid(int y)
        {
            _grid = new Rectangle(0, y + _monthH + MonthGap, _gridW, GridRows * Step - Sc(CellGap));

            _gridMax = 1;
            if (Index != null)
                for (DateTime d = _gridFrom; d <= _gridTo; d = d.AddDays(1))
                {
                    int n = Index.History.SavesOn(d);
                    if (n > _gridMax) _gridMax = n;
                }

            return _grid.Bottom;
        }

        const int GridRows = 7;

        static int Weeks(DateTime from, DateTime to)
        {
            return (int)((to - from.AddDays(-Weekday(from))).TotalDays) / 7 + 1;
        }

        /// <summary>Monday is zero: the week starts with it rather than with Sunday.</summary>
        static int Weekday(DateTime d)
        {
            int w = (int)d.DayOfWeek;      // 0 is Sunday
            return w == 0 ? 6 : w - 1;
        }

        /// <summary>The Monday of the first column — every cell is counted from it.</summary>
        DateTime FirstCol { get { return _gridFrom.AddDays(-Weekday(_gridFrom)); } }

        /// <summary>Which day stands in this cell. The inverse is CellOf.</summary>
        DateTime DayAt(int col, int row) { return FirstCol.AddDays(col * 7 + row); }

        void CellOf(DateTime day, out int col, out int row)
        {
            int n = (int)(day - FirstCol).TotalDays;
            col = n / 7; row = n % 7;
        }

        // ---------------------------------------------------------------- drawing

        public void Paint(Graphics g, Control owner, int scroll)
        {
            int dy = -scroll;
            if (owner != null && (Bounds.Bottom + dy < 0 || Bounds.Y + dy > owner.Height)) return;

            PaintChevron(g, RectangleF.Inflate(Shift(_chevron, dy), -Sc(3), -Sc(3)),
                         _headHot ? Theme.Text : Theme.TextDim);
            Chrome.DrawText(g, "Overview", Theme.FHead, Shift(_title, dy), Theme.Text, Chrome.Left);

            if (!Open) return;

            for (int i = 0; i < _cards.Count; i++) PaintCard(g, owner, Shift(_cards[i], dy), i);
            PaintGrid(g, dy);
            Chrome.DrawText(g, _hover.Length > 0 ? _hover : Footer(), Theme.FLabel,
                            Shift(_splash, dy), Theme.TextDim, Chrome.CellLeft);
        }

        /// <summary>
        /// A collapsed panel is the same chevron turned a quarter. There is no separate "right"
        /// glyph in the set, and there is no point making one for a single state.
        /// </summary>
        void PaintChevron(Graphics g, RectangleF box, Color ink)
        {
            if (Open) { Icons.Draw(g, Glyph.ChevronDown, box, ink, 1.5f); return; }

            System.Drawing.Drawing2D.GraphicsState saved = g.Save();
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-90f);
            g.TranslateTransform(-cx, -cy);
            Icons.Draw(g, Glyph.ChevronDown, box, ink, 1.5f);
            g.Restore(saved);
        }

        static Rectangle Shift(Rectangle r, int dy)
        {
            return new Rectangle(r.X, r.Y + dy, r.Width, r.Height);
        }

        /// <summary>
        /// The caption and the number inside a tile — WITH clipping, without fail (CellLeft,
        /// not Left): Chrome.Left has NoClipping baked in, and a long value like "F Phrygian"
        /// drew straight over the neighbouring card, past its own frame.
        /// </summary>
        void PaintCard(Graphics g, Control owner, Rectangle r, int i)
        {
            Theme.PaintGlassSurface(owner, g, r, Sc(10), Theme.GlassSurfaceAlpha);

            int pad = Sc(12);
            int inner = r.Width - pad * 2;
            Chrome.DrawText(g, _cardLabel[i], Theme.FBadge,
                            new Rectangle(r.X + pad, r.Y + Sc(6), inner, _labH),
                            Theme.TextDim, Chrome.CellLeft);
            Chrome.DrawText(g, _cardValue[i], FValue,
                            new Rectangle(r.X + pad, r.Y + Sc(6) + _labH, inner, _valH),
                            Theme.Text, Chrome.CellLeft);
        }

        /// <summary>
        /// Density instead of colour: the window's whole palette is grey, and the single
        /// coloured spot on the screen would read as an error rather than as a chart. An empty
        /// day is a shade lighter than the background, the densest almost white.
        /// </summary>
        Color Level(int saves)
        {
            if (saves <= 0) return Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

            // A square root rather than straight proportion: one day with 28 saves would squash
            // the whole rest of the scale into the palest level, and the calendar would read as
            // empty.
            int step = (int)(3.999 * Math.Sqrt(saves / (double)Math.Max(1, _gridMax)));
            int[] alpha = { 0x4C, 0x80, 0xB8, 0xF4 };
            return Color.FromArgb(alpha[Math.Max(0, Math.Min(3, step))], Theme.Light);
        }

        Bitmap _gridCache;
        string _gridKey;
        object _gridHist;

        /// <summary>
        /// The calendar is kept as a finished picture. There are close to four hundred cells,
        /// and each is a GraphicsPath with rounding; redrawing them per frame means assembling
        /// twenty thousand paths a second while scrolling. They change only with the range, the
        /// cell size and the history itself — and those are what we reset on.
        /// </summary>
        void PaintGrid(Graphics g, int dy)
        {
            if (Index == null || _grid.Width <= 0 || _grid.Height <= 0) return;

            // The date in the key is not decoration: the year window moves at every midnight,
            // and without it a program left running all night would show yesterday's picture.
            string key = _gridFrom.ToString("yyyyMMdd") + "|" + _cell
                       + "|" + _grid.Width + "x" + _grid.Height + "|" + _gridMax;
            if (_gridCache == null || _gridKey != key || !ReferenceEquals(_gridHist, Index.History))
            {
                if (_gridCache != null) _gridCache.Dispose();
                _gridCache = RenderGrid();
                _gridKey = key;
                _gridHist = Index.History;
            }
            g.DrawImageUnscaled(_gridCache, _grid.X, _grid.Y + dy);

            // The outline under the cursor goes over the picture: there is one of it and it
            // changes every frame.
            if (_hoverDay != default(DateTime))
            {
                int hc, hr;
                CellOf(_hoverDay, out hc, out hr);
                Theme.FillRound(g, Rectangle.Inflate(
                    new Rectangle(_grid.X + hc * Step, _grid.Y + dy + hr * Step, _cell, _cell),
                    Sc(1), Sc(1)), _cell * 0.3f, Theme.LightTop);
            }

            PaintMonths(g, dy);
        }

        /// <summary>
        /// Month captions over the columns where a month begins.
        ///
        /// We go RIGHT TO LEFT and skip the ones running into an already-drawn caption.
        /// Otherwise in the year view the first column is still December of the previous year
        /// while the second is already January: two captions one cell apart ran together into
        /// something like "Decan". Going right to left, what gets skipped is the stub on the
        /// left rather than the full month on the right.
        /// </summary>
        void PaintMonths(Graphics g, int dy)
        {
            List<int> cols = new List<int>();
            int lastMonth = -1;
            for (int c = 0; _grid.X + c * Step < _grid.Right; c++)
            {
                DateTime top = DayAt(c, 0);
                if (top.Month == lastMonth) continue;
                lastMonth = top.Month;
                cols.Add(c);
            }

            int keptX = int.MaxValue;
            for (int i = cols.Count - 1; i >= 0; i--)
            {
                string name = Months[DayAt(cols[i], 0).Month - 1];
                int x = _grid.X + cols[i] * Step;
                if (x + TextRenderer.MeasureText(name, Theme.FMini).Width + Sc(8) > keptX) continue;
                keptX = x;
                Chrome.DrawText(g, name, Theme.FMini,
                                new Rectangle(x, _grid.Y + dy - _monthH - MonthGap, Sc(40), _monthH),
                                Theme.TextDim, Chrome.Left);
            }
        }

        Bitmap RenderGrid()
        {
            Bitmap bmp = new Bitmap(_grid.Width, _grid.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            float r = _cell * 0.3f;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Theme.Smooth(g);
                for (DateTime day = _gridFrom; day <= _gridTo; day = day.AddDays(1))
                {
                    int col, row;
                    CellOf(day, out col, out row);
                    Theme.FillRound(g, new Rectangle(col * Step, row * Step, _cell, _cell),
                                    r, Level(Index.History.SavesOn(day)));
                }
            }
            return bmp;
        }

        static readonly string[] Months =
            { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        // ------------------------------------------------------------------- mouse

        string _hover = "";
        DateTime _hoverDay;

        /// <summary>The point already corrected for the scroll. true means a redraw is
        /// needed.</summary>
        public bool MouseMove(Point p)
        {
            bool was = _headHot;
            string wasHover = _hover;

            _headHot = _toggle.Contains(p);
            _hover = "";
            _hoverDay = default(DateTime);
            if (Open && Index != null && _grid.Contains(p))
            {
                DateTime day = DayAt((p.X - _grid.X) / Math.Max(1, Step),
                                     (p.Y - _grid.Y) / Math.Max(1, Step));
                if (day >= _gridFrom && day <= _gridTo)
                {
                    int n = Index.History.SavesOn(day);
                    _hoverDay = day;
                    _hover = (n == 0 ? "nothing saved" : n + (n == 1 ? " save" : " saves"))
                           + "  ·  " + Date(day);
                }
            }
            return was != _headHot || wasHover != _hover;
        }

        public bool MouseLeave()
        {
            if (!_headHot && _hover.Length == 0) return false;
            _headHot = false; _hover = ""; _hoverDay = default(DateTime);
            return true;
        }

        /// <summary>true means the click was ours and there is nowhere further to carry
        /// it.</summary>
        public bool MouseDown(Point p)
        {
            if (!_toggle.Contains(p)) return false;

            Open = !Open;
            if (StateChanged != null) StateChanged();
            if (LayoutChanged != null) LayoutChanged();
            else if (Repaint != null) Repaint();
            return true;
        }

        // ------------------------------------------------------------------ counting

        sealed class Stats
        {
            public int ActiveDays, Streak, Record, PeakHour;
            public long Disk;
        }

        Stats _stats;
        object _statsOf, _histOf;

        Stats Ensure()
        {
            if (Index == null) return _stats ?? (_stats = new Stats());
            if (_stats != null && ReferenceEquals(_statsOf, Index.Sets)
                               && ReferenceEquals(_histOf, Index.History)) return _stats;

            _statsOf = Index.Sets;
            _histOf = Index.History;
            _stats = Compute();
            return _stats;
        }

        Stats Compute()
        {
            Stats s = new Stats();
            Activity h = Index.History;
            s.ActiveDays = h.ActiveDays;
            s.Streak = h.CurrentStreak;
            s.Record = h.LongestStreak;
            s.PeakHour = h.PeakHour;

            // We count the weight by folders: a project has a dozen .als versions next to each
            // other, and by sets one and the same folder would be added ten times over.
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetEntry e in Index.Sets)
                if (!e.IsBackup && e.ProjectDir.Length > 0 && dirs.Add(e.ProjectDir))
                    s.Disk += e.ProjectSize;
            return s;
        }

        // ------------------------------------------------------------- formatting

        static string Days(int n) { return n == 0 ? "—" : n + "d"; }

        /// <summary>
        /// The date is always in English: the whole interface is English, and ToString without
        /// a culture takes the system one — and on a machine with a Russian locale a localised
        /// date turned up in the middle of "nothing saved · ".
        /// </summary>
        static string Date(DateTime d)
        {
            return d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }
        static string Hour(int h) { return h < 0 ? "—" : h.ToString("00") + ":00"; }

        static string Num(int n)
        {
            return n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ");
        }

        static string Bytes(long b)
        {
            if (b >= 1L << 40) return (b / (double)(1L << 40)).ToString("0.0") + " TB";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            return b / 1024 + " KB";
        }

        /// <summary>
        /// The line under the calendar: the weight of the library. The tiles no longer show it,
        /// while it takes up the spot under the cursor anyway — the day from the calendar
        /// appears there.
        /// </summary>
        string Footer()
        {
            Stats s = Ensure();
            return s.Disk > 0 ? Bytes(s.Disk) + " on disk" : "";
        }

        public void Dispose()
        {
            if (_gridCache != null) { _gridCache.Dispose(); _gridCache = null; }
        }
    }
}
