using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace AbletonManager
{
    /// <summary>
    /// The home page: projects as tiles with arrangement previews, like DaVinci Resolve's start
    /// screen. The point is not analytics but recognition — a set comes back to mind faster
    /// from the picture of its arrangement than from a name in a table.
    /// </summary>
    public sealed class HomeView : GlassControl
    {
        public ProjectIndex Index;
        public ArrangementLoader Loader;

        public event Action<SetEntry> Activated;       // double click — open in Live
        public event Action<SetEntry> PlayRequested;
        public event Action<SetEntry> RevealRequested;
        public event Action<SetEntry> DetailsRequested; // right click → go to the set on the Sets tab
        public event Action<SetEntry> RescueRequested;  // right click → the rescue helper
        public event Action<SetEntry> NotesRequested;   // a click on the tags glyph — the tag editor
        public event Action NewProjectRequested;       // the first card in Recent

        public SetFilter SetFilter;

        /// <summary>The filter from the shared search field — by set name and folder
        /// name.</summary>
        public string Filter = "";

        /// <summary>One tile per folder rather than per version of a set. See the
        /// settings.</summary>
        public bool GroupByFolder = true;

        /// <summary>
        /// Pinned ones to the top of the list. The same switch as the star in the table header:
        /// the view differs but the question is one, and keeping two settings for it would mean
        /// pinned on one tab and not on the other.
        /// </summary>
        public bool PinnedFirst;
        public event Action PinnedFirstToggled;

        /// <summary>
        /// The summary above the list. It stands inside the scroll, so it is not a control but
        /// a painter: we ask it for a layout ourselves and forward the mouse ourselves — see
        /// OverviewPanel.
        /// </summary>
        public readonly OverviewPanel Overview = new OverviewPanel();

        /// <summary>The user collapsed the summary or switched the span in it — worth
        /// remembering.</summary>
        public event Action OverviewStateChanged;

        const int PinnedMax = 16;

        // The frame around the picture inside a preview, and the picture inside the frame — at
        // the bottom the frame runs flush with the text (with no inset of its own), so the
        // resulting gap from t.Thumb to the real edge of the picture differs on the right, the
        // top and the bottom. The play button has to be inset from the picture itself rather
        // than from the frame — otherwise its visible inset is there on one edge and absent on
        // the other.
        const int ThumbInset = 8;
        const int PicInset = 4;

        // We keep previews as finished pictures of a fixed size rather than as parsed
        // arrangements: a large set has hundreds of thousands of notes, and a dozen of those in
        // memory is tens of megabytes. A picture, on the other hand, can simply be scaled to a
        // tile of any size, so there is no need to re-read the set when the window changes.
        const int ThumbW = 512, ThumbH = 288;

        /// <summary>
        /// A cap on the pictures in memory. One is 512×288×4 = 576 KB, and without it a library
        /// of a thousand projects is half a gigabyte: "Show all" gets scrolled to the end, and
        /// every tile seen would stay in memory forever.
        ///
        /// The cap has to be NO SMALLER than the tiles in the band OnPaint manages to request
        /// (the visible ones plus a screen above and below). Otherwise the band does not fit
        /// into itself: tiles displace each other, the next repaint requests them again, and
        /// the "loaded — displaced — loaded" circle jams the UI thread so badly that the mouse
        /// wheel stops scrolling. This only shows up on a wide window with a large library, so
        /// we compute it from the layout rather than as a number.
        /// </summary>
        int _thumbBudget = MinThumbs;
        const int MinThumbs = 64;

        readonly Dictionary<string, Bitmap> _thumbs =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        // When a picture was last drawn: we choose who to displace by this counter. Visible
        // tiles are marked on every frame, so what gets displaced is always something that went
        // off the edge long ago.
        readonly Dictionary<string, int> _thumbUsed =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int _thumbClock;

        // Pre-scaled previews: a DrawImage to the tile size on every frame while scrolling is
        // an expensive operation (20 rescales per frame with HQ interpolation). Here we keep
        // already-scaled copies and drop them when the window is resized.
        readonly Dictionary<string, Bitmap> _scaledThumbs =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        Size _scaledSize;

        /// <summary>true while the scroll is animating — OnPaint then uses simplified rendering
        /// (PaintGlassSurfaceFast, NearestNeighbor, skipping decorative elements such as
        /// outlines and highlights).</summary>
        bool _scrolling { get { return _scroller != null && _scroller.IsActive; } }

        // We run the preview queue ourselves: the loader gives its answers to every subscriber
        // at once and does not know which of them we actually need. We keep several in flight —
        // parsing a set is pure CPU, and on one thread two dozen tiles take over three seconds
        // to fill.
        const int MaxInFlight = 3;

        readonly List<string> _queue = new List<string>();
        readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _rendering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _diskLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sealed class Tile
        {
            public SetEntry Set;
            public Rectangle Bounds;      // without the scroll
            public Rectangle Thumb;
            public Rectangle Play;
            public Rectangle Pin;
            public Rectangle TagBox;      // the tags glyph at the top left of the preview
            public string TagText = "";   // the tags joined by ", "; empty means no glyph
            public bool HasPlay;
            public bool IsNewProject;     // the first card in Recent — "New Live Set"
            public string Subtitle;

            public float AnimX;
            public float AnimY;
            public float TargetX;
            public float TargetY;
            public float Alpha = 1f;
            public float TargetAlpha = 1f;
            public bool Leaving;
        }

        sealed class Header
        {
            public string Text;
            public string Note;
            public Rectangle Bounds;
            public int NoteShift;
            public Rectangle Star;      // empty means the heading has no star

            public float AnimY;
            public float TargetY;
            public float Alpha = 1f;
            public float TargetAlpha = 1f;
            public bool Leaving;
        }

        readonly List<Tile> _tiles = new List<Tile>();
        readonly List<Tile> _leavingTiles = new List<Tile>();
        readonly List<Header> _heads = new List<Header>();
        readonly List<Header> _leavingHeads = new List<Header>();
        bool _layoutTransitioning;

        int _scroll, _contentHeight;
        float _scrollTarget, _scrollCurrent;
        readonly SmoothScroller _scroller;
        bool _draggingBar;
        int _dragOffset;
        int _hot = -1;
        bool _playHot, _pinHot;

        /// <summary>The cursor is on the heading's star — it lights up.</summary>
        bool _headStarHot;

        /// <summary>The cursor is on the tags glyph of the _hot tile — the tags themselves then
        /// pop up under it.</summary>
        bool _tagsHot;
        SetEntry _selected;

        static readonly string[] Splashes = new string[]
        {
            "this one will finished",
            "how u name it?",
            "more OTT this time",
            "also try FL Studio!",
            "certified 8-bar loop",
            "another Untitled.als",
            "try \"dice\" instead",
            "Fmin again, isn't it?",
            "hardest part is naming it",
            "who needs sleep anyway?",
            "tip: automate everything",
            "+1 step to unemployment",
            "drop first, intro never",
            "u already replaced by AI",
            "today we finish a track",
            "nitepunk pls dm",
            "AD place for sale!",
            "dev Telegram: @RueBlose",
            "trust your ears, not meters",
            "fresh project, same chords",
            "you finished the old ones?",
            "give me flowjob",
            "gas gas gas",
            "i hate midtempo",
            "LUFSMAXXING+MOOGING",
            "no red meter = bad mix"
        };
        static readonly Random _splashRnd = new Random();
        int _splashIndex = _splashRnd.Next(Splashes.Length);

        void PickNextSplash()
        {
            if (Splashes.Length <= 1) return;
            _splashIndex = (_splashIndex + _splashRnd.Next(1, Splashes.Length)) % Splashes.Length;
        }

        Rectangle BarRect()
        {
            if (_contentHeight <= Height) return Rectangle.Empty;
            int track = Height - Sc(10);
            int h = Math.Max(Sc(40), (int)(track * (float)Height / _contentHeight));
            int max = Math.Max(1, _contentHeight - Height);
            int y = Sc(5) + (int)((track - h) * (_scroll / (float)max));
            return new Rectangle(Width - Sc(8), y, Sc(4), h);
        }

        ScrollFade _barFade;

        float[] _tileHoverFactors;
        float[] _tileEntrance;
        int _entranceStartTick;
        float _playHoverFactor;
        float _pinHoverFactor;

        public void TriggerEntrance()
        {
            _tileEntrance = new float[_tiles.Count];
            _entranceStartTick = Environment.TickCount;
            AnimEngine.Register(this);
            Invalidate();
        }

        public override bool OnAnimTick()
        {
            bool parentStill = base.OnAnimTick();
            bool anim = false;

            if (_tileHoverFactors == null || _tileHoverFactors.Length != _tiles.Count)
                _tileHoverFactors = new float[_tiles.Count];

            for (int i = 0; i < _tiles.Count; i++)
            {
                float target = (i == _hot) ? 1.0f : 0.0f;
                float diff = target - _tileHoverFactors[i];
                if (Math.Abs(diff) > 0.01f)
                {
                    _tileHoverFactors[i] += diff * 0.35f;
                    anim = true;
                }
                else
                {
                    _tileHoverFactors[i] = target;
                }
            }

            if (_layoutTransitioning)
            {
                float speed = 0.28f;
                bool anyMoved = false;

                for (int i = 0; i < _tiles.Count; i++)
                {
                    Tile t = _tiles[i];
                    float dx = t.TargetX - t.AnimX;
                    float dy = t.TargetY - t.AnimY;
                    float da = t.TargetAlpha - t.Alpha;

                    if (Math.Abs(dx) > 0.5f) { t.AnimX += dx * speed; anyMoved = true; }
                    else t.AnimX = t.TargetX;

                    if (Math.Abs(dy) > 0.5f) { t.AnimY += dy * speed; anyMoved = true; }
                    else t.AnimY = t.TargetY;

                    if (Math.Abs(da) > 0.005f) { t.Alpha += da * speed; anyMoved = true; }
                    else t.Alpha = t.TargetAlpha;
                }

                for (int i = _leavingTiles.Count - 1; i >= 0; i--)
                {
                    Tile lt = _leavingTiles[i];
                    float da = lt.TargetAlpha - lt.Alpha;
                    if (Math.Abs(da) > 0.005f)
                    {
                        lt.Alpha += da * speed;
                        anyMoved = true;
                    }
                    else
                    {
                        lt.Alpha = 0f;
                        _leavingTiles.RemoveAt(i);
                    }
                }

                for (int i = 0; i < _heads.Count; i++)
                {
                    Header h = _heads[i];
                    float dy = h.TargetY - h.AnimY;
                    float da = h.TargetAlpha - h.Alpha;

                    if (Math.Abs(dy) > 0.5f) { h.AnimY += dy * speed; anyMoved = true; }
                    else h.AnimY = h.TargetY;

                    if (Math.Abs(da) > 0.005f) { h.Alpha += da * speed; anyMoved = true; }
                    else h.Alpha = h.TargetAlpha;
                }

                for (int i = _leavingHeads.Count - 1; i >= 0; i--)
                {
                    Header lh = _leavingHeads[i];
                    float da = lh.TargetAlpha - lh.Alpha;
                    if (Math.Abs(da) > 0.005f)
                    {
                        lh.Alpha += da * speed;
                        anyMoved = true;
                    }
                    else
                    {
                        lh.Alpha = 0f;
                        _leavingHeads.RemoveAt(i);
                    }
                }

                if (anyMoved) anim = true;
                else _layoutTransitioning = false;
            }

            int now = Environment.TickCount;
            if (_tileEntrance == null || _tileEntrance.Length != _tiles.Count)
                _tileEntrance = new float[_tiles.Count];

            for (int i = 0; i < _tiles.Count; i++)
            {
                if (_tileEntrance[i] >= 1.0f) continue;
                int delay = Math.Min(i, 24) * 20;
                int elapsed = now - _entranceStartTick - delay;
                if (elapsed > 0)
                {
                    float diff = 1.0f - _tileEntrance[i];
                    if (diff > 0.005f)
                    {
                        _tileEntrance[i] += diff * 0.25f;
                        anim = true;
                    }
                    else
                    {
                        _tileEntrance[i] = 1.0f;
                    }
                }
                else
                {
                    anim = true;
                }
            }

            float targetPlay = _playHot ? 1.0f : 0.0f;
            float diffPlay = targetPlay - _playHoverFactor;
            if (Math.Abs(diffPlay) > 0.01f)
            {
                _playHoverFactor += diffPlay * 0.35f;
                anim = true;
            }
            else _playHoverFactor = targetPlay;

            float targetPin = _pinHot ? 1.0f : 0.0f;
            float diffPin = targetPin - _pinHoverFactor;
            if (Math.Abs(diffPin) > 0.01f)
            {
                _pinHoverFactor += diffPin * 0.35f;
                anim = true;
            }
            else _pinHoverFactor = targetPin;

            if (anim) Invalidate();
            return parentStill || anim;
        }

        /// <summary>The "New Live Set" card is selected — it has no SetEntry of its
        /// own.</summary>
        bool _newSelected;

        /// <summary>What is currently loaded into the player (not necessarily playing — see
        /// Playing) — the same device as PlayingTag in RowListView: we compare by the SetEntry
        /// reference rather than by path.</summary>
        public object PlayingTag
        {
            get { return _playingTag; }
            set { if (!ReferenceEquals(_playingTag, value)) { _playingTag = value; SyncPulse(); } }
        }
        object _playingTag;

        /// <summary>The player is really sounding right now rather than paused.</summary>
        public bool Playing
        {
            get { return _playing; }
            set { if (_playing != value) { _playing = value; SyncPulse(); } }
        }
        bool _playing;

        /// <summary>
        /// While it sounds, we subscribe the tile to the shared pulse so the bars move. We
        /// repaint not the whole screen but the corner of the preview: the playing tile travels
        /// with the scroll, so the rectangle is recomputed on every tick.
        /// </summary>
        void SyncPulse()
        {
            if (_playing && _playingTag != null) PlayPulse.Attach(this, PulseRect);
            else PlayPulse.Detach(this);
            Invalidate();
        }

        Rectangle PulseRect()
        {
            if (_playingTag == null) return Rectangle.Empty;
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile t = _tiles[i];
                if (t.Set == null || !ReferenceEquals(t.Set, _playingTag)) continue;
                if (!t.HasPlay) return Rectangle.Empty;   // the pulse lives in the play button
                int dx = (int)Math.Round(t.AnimX) - t.Bounds.X;
                int dy = (int)Math.Round(t.AnimY) - t.Bounds.Y - (_scroll + over);
                return new Rectangle(t.Play.X + dx, t.Play.Y + dy, t.Play.Width, t.Play.Height);
            }
            return Rectangle.Empty;
        }

        public HomeView()
        {
            Cursor = Cursors.Default;
            Surface = Theme.Backdrop;
            _barFade = new ScrollFade(this, BarRect);
            _scroller = new SmoothScroller(this,
                delegate (int s) { _scroll = s; _scrollCurrent = s; ClampScroll(); },
                delegate { return Math.Max(0, _contentHeight - Height); });

            // The summary was collapsed — the tiles do not fly in again but move up into the
            // freed space: it is visible that it was the space that moved while the list stayed
            // the same.
            Overview.LayoutChanged += delegate { RebuildTransition(); };
            Overview.Repaint += delegate { Invalidate(); };
            Overview.StateChanged += delegate
            {
                if (OverviewStateChanged != null) OverviewStateChanged();
            };
        }

        // ------------------------------------------------------------------ content

        public void Rebuild() { Rebuild(true); }

        /// <summary>
        /// animate=false — rebuild without starting the tile entrance again. Typing in the
        /// search needs it: there the rebuild happens on every letter, and the grid kept
        /// "flying in" from below instead of simply being filtered.
        /// </summary>
        public void Rebuild(bool animate)
        {
            BuildLayout(animate);
            Invalidate();
        }

        /// <summary>
        /// A smooth rebuild animation: the tiles glide to their new places, the surplus
        /// disappear and the new ones fade in. Used when sets are pinned and unpinned.
        /// </summary>
        public void RebuildTransition()
        {
            BuildLayoutTransition();
            Invalidate();
        }

        /// <summary>How many project tiles are on screen right now — for the counter in the
        /// header. Kept apart from VisibleSets(), which assembles a list: the counter is asked
        /// for on every repaint of the window, and building a list for it would be
        /// pointless.</summary>
        public int VisibleCount
        {
            get
            {
                int n = 0;
                foreach (Tile t in _tiles) if (t.Set != null) n++;
                return n;
            }
        }

        /// <summary>
        /// The sets in the order they lie on screen: the pinned first — in pin order rather
        /// than by date — then the recent. The player needs it: "next" in the mini transport
        /// has to go where the eye goes, not into some internal order of its own.
        /// </summary>
        public event EventHandler SelectionChanged;

        public SetEntry Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                _newSelected = false;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Moving the selection from the keyboard. Horizontally simply by tile order;
        /// vertically geometrically, into the nearest tile of the neighbouring row under the
        /// same X. A jump "by the grid width" will not do here: the rows are of different
        /// lengths and separated by section headings, and at the junction of Recent and All it
        /// would miss.
        ///
        /// true even when there is nowhere to move — at the edge of the grid we swallow the key
        /// all the same, or it goes into focus navigation and the selection drives out of the
        /// catalog.
        /// </summary>
        public bool MoveSelection(int dx, int dy)
        {
            List<int> cells = new List<int>();
            for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Set != null) cells.Add(i);
            if (cells.Count == 0) return false;

            int cur = -1;
            if (_selected != null)
                for (int k = 0; k < cells.Count; k++)
                    if (ReferenceEquals(_tiles[cells[k]].Set, _selected)) { cur = k; break; }

            if (cur < 0) { SelectTile(cells[0]); return true; }

            if (dx != 0)
            {
                SelectTile(cells[Math.Max(0, Math.Min(cells.Count - 1, cur + dx))]);
                return true;
            }

            Rectangle from = _tiles[cells[cur]].Bounds;
            int best = -1;
            long bestScore = long.MaxValue;
            foreach (int i in cells)
            {
                Rectangle b = _tiles[i].Bounds;
                if (dy > 0 ? b.Y <= from.Y : b.Y >= from.Y) continue;
                // The nearest row first, and only within it the nearest column.
                long score = (long)Math.Abs(b.Y - from.Y) * 100000 + Math.Abs(b.X - from.X);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) SelectTile(best);
            return true;
        }

        void SelectTile(int i)
        {
            Selected = _tiles[i].Set;
            EnsureTileVisible(_tiles[i].Bounds);
        }

        /// <summary>Selecting a tile programmatically — from the random-set die, for instance.
        /// Unlike a plain assignment to Selected, it also scrolls to it.</summary>
        public void Select(SetEntry s)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (!ReferenceEquals(_tiles[i].Set, s)) continue;
                SelectTile(i);
                return;
            }
            Selected = s;
        }

        void EnsureTileVisible(Rectangle b)
        {
            int pad = Sc(16);
            float target = _scroll;
            if (b.Y - pad < target) target = b.Y - pad;
            else if (b.Bottom + pad > target + Height) target = b.Bottom + pad - Height;
            if (target == _scroll) return;

            _scroll = (int)Math.Max(0, Math.Min(_contentHeight - Height, target));
            _scrollTarget = _scrollCurrent = _scroll;
            if (_scroller != null) _scroller.SyncPosition(_scroll);
            ClampScroll();
            Invalidate();
        }

        /// <summary>The menu of the selected tile — for the context menu key on the
        /// keyboard.</summary>
        public bool ShowMenuForSelected()
        {
            if (_selected == null) return false;
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (!ReferenceEquals(_tiles[i].Set, _selected)) continue;
                Rectangle b = _tiles[i].Bounds;
                ShowMenu(_selected, new Point(b.X + Sc(28), b.Y - _scroll + Sc(28)));
                return true;
            }
            return false;
        }

        public List<SetEntry> VisibleSets()
        {
            List<SetEntry> result = new List<SetEntry>();
            foreach (Tile t in _tiles) if (t.Set != null) result.Add(t.Set);
            return result;
        }

        bool Matches(SetEntry s)
        {
            if (SetFilter != null && !SetFilter.Matches(s)) return false;

            string q = (Filter ?? "").Trim();
            if (q.Length == 0) return true;
            if (s.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            if (s.Place.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;

            // A render is the same "project name" for somebody searching by sound rather than
            // by a set's name. The same selection as the preview uses (without Samples).
            foreach (string r in s.RenderNames)
                if (r.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            return false;
        }

        List<SetEntry> Pinned()
        {
            List<SetEntry> result = new List<SetEntry>();
            if (Index == null) return result;

            // We go by the list of pins rather than by every set: the pin order is the order on
            // screen and must not jump about.
            foreach (string path in HomeStore.Pins)
            {
                foreach (SetEntry s in Index.Sets)
                    if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Matches(s)) result.Add(s);
                        break;
                    }
            }
            return result;
        }

        /// <summary>
        /// One list instead of two sections. The former "Pinned" and "Recent" showed the same
        /// thing as two heaps, and a pinned project vanished from the stream of recent ones —
        /// it had to be hunted for by eye elsewhere on the screen. Now the stream is one, and
        /// pinning either lifts a project to the top or remains merely a mark on the tile.
        /// </summary>
        List<SetEntry> Projects()
        {
            List<SetEntry> all = new List<SetEntry>();
            if (Index == null) return all;

            foreach (SetEntry s in Index.Sets)
                if (!s.IsBackup && Matches(s)) all.Add(s);

            if (GroupByFolder) all = ProjectIndex.CollapseByFolder(all);

            List<SetEntry> pins = Pinned();

            // A version was pinned and then a fresh one saved next to it — the old one goes
            // under the collapsed row, and the pin would silently stop working. We bring it
            // back.
            foreach (SetEntry p in pins)
                if (!all.Contains(p)) all.Add(p);

            all.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });
            if (!PinnedFirst || pins.Count == 0) return all;

            // To the top — in pin order: it must not jump about with the edit date.
            List<SetEntry> ordered = new List<SetEntry>(pins);
            foreach (SetEntry s in all)
                if (!pins.Contains(s)) ordered.Add(s);
            return ordered;
        }

        // ------------------------------------------------------------------ layout

        int Gap { get { return Sc(16); } }   // S4 — the same step as between property rows

        void BuildLayout() { BuildLayout(true); }

        void ComputeLayout()
        {
            int gap = Gap;
            int target = Sc(250);
            int cols = Math.Min(8, Math.Max(1, (Width + gap) / (target + gap)));
            int tileW = (Width - gap * (cols - 1)) / cols;
            int thumbH = (int)(tileW * 0.56f);
            int tileH = thumbH + Sc(68);

            int bandRows = Height * 3 / Math.Max(1, tileH + gap) + 2;
            _thumbBudget = Math.Max(MinThumbs, cols * bandRows);

            int y = 0;

            Overview.Index = Index;
            Overview.Dpi = DeviceDpi / 96f;
            y = Overview.Layout(Width, y) + Sc(28);

            List<SetEntry> projects = Projects();
            string note = Index != null && Index.Sets.Count == 0 ? "nothing indexed yet" : "";
            y = Section("Projects", note, projects, y, cols, tileW, tileH, thumbH, gap);

            _contentHeight = y;
            ClampScroll();
        }

        void BuildLayout(bool animate)
        {
            _leavingTiles.Clear();
            _leavingHeads.Clear();
            _layoutTransitioning = false;
            _tiles.Clear();
            _heads.Clear();
            _hot = -1; _playHot = _pinHot = _tagsHot = false;
            _pinHoverFactor = _playHoverFactor = 0f;
            if (Width <= 0) return;

            ComputeLayout();

            _tileHoverFactors = new float[_tiles.Count];
            _tileEntrance = new float[_tiles.Count];
            if (animate)
            {
                _entranceStartTick = Environment.TickCount;
                AnimEngine.Register(this);
            }
            else
            {
                for (int i = 0; i < _tileEntrance.Length; i++) _tileEntrance[i] = 1f;
            }
        }

        static bool IsSameTile(Tile a, Tile b)
        {
            if (a == null || b == null) return false;
            if (a.IsNewProject || b.IsNewProject) return a.IsNewProject && b.IsNewProject;
            if (a.Set != null && b.Set != null)
                return string.Equals(a.Set.Path, b.Set.Path, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        void BuildLayoutTransition()
        {
            if (Width <= 0) return;
            if (_tiles.Count == 0)
            {
                BuildLayout(true);
                return;
            }

            List<Tile> oldTiles = new List<Tile>(_tiles);
            foreach (Tile lt in _leavingTiles) oldTiles.Add(lt);
            List<Header> oldHeads = new List<Header>(_heads);
            foreach (Header lh in _leavingHeads) oldHeads.Add(lh);

            _leavingTiles.Clear();
            _leavingHeads.Clear();
            _tiles.Clear();
            _heads.Clear();

            _hot = -1; _playHot = _pinHot = _tagsHot = false;
            _pinHoverFactor = _playHoverFactor = 0f;
            _layoutTransitioning = true;
            ComputeLayout();

            _tileHoverFactors = new float[_tiles.Count];
            _tileEntrance = new float[_tiles.Count];
            for (int i = 0; i < _tileEntrance.Length; i++) _tileEntrance[i] = 1f;

            bool[] usedOld = new bool[oldTiles.Count];

            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile nt = _tiles[i];
                int match = -1;
                for (int k = 0; k < oldTiles.Count; k++)
                {
                    if (usedOld[k]) continue;
                    if (IsSameTile(nt, oldTiles[k])) { match = k; break; }
                }

                if (match >= 0)
                {
                    usedOld[match] = true;
                    Tile ot = oldTiles[match];
                    nt.AnimX = ot.AnimX;
                    nt.AnimY = ot.AnimY;
                    nt.Alpha = ot.Alpha;
                    nt.TargetX = nt.Bounds.X;
                    nt.TargetY = nt.Bounds.Y;
                    nt.TargetAlpha = 1.0f;
                }
                else
                {
                    nt.AnimX = nt.Bounds.X;
                    nt.AnimY = nt.Bounds.Y;
                    nt.TargetX = nt.Bounds.X;
                    nt.TargetY = nt.Bounds.Y;
                    nt.Alpha = 0.0f;
                    nt.TargetAlpha = 1.0f;
                }
            }

            for (int k = 0; k < oldTiles.Count; k++)
            {
                if (!usedOld[k] && oldTiles[k].Alpha > 0.01f)
                {
                    Tile ot = oldTiles[k];
                    ot.Leaving = true;
                    ot.TargetAlpha = 0.0f;
                    ot.TargetX = ot.AnimX;
                    ot.TargetY = ot.AnimY;
                    _leavingTiles.Add(ot);
                }
            }

            bool[] usedHeads = new bool[oldHeads.Count];
            for (int i = 0; i < _heads.Count; i++)
            {
                Header nh = _heads[i];
                int match = -1;
                for (int k = 0; k < oldHeads.Count; k++)
                {
                    if (usedHeads[k]) continue;
                    if (string.Equals(nh.Text, oldHeads[k].Text, StringComparison.OrdinalIgnoreCase))
                    {
                        match = k; break;
                    }
                }

                if (match >= 0)
                {
                    usedHeads[match] = true;
                    Header oh = oldHeads[match];
                    nh.AnimY = oh.AnimY;
                    nh.Alpha = oh.Alpha;
                    nh.TargetY = nh.Bounds.Y;
                    nh.TargetAlpha = 1.0f;
                }
                else
                {
                    nh.AnimY = nh.Bounds.Y;
                    nh.TargetY = nh.Bounds.Y;
                    nh.Alpha = 0.0f;
                    nh.TargetAlpha = 1.0f;
                }
            }

            for (int k = 0; k < oldHeads.Count; k++)
            {
                if (!usedHeads[k] && oldHeads[k].Alpha > 0.01f)
                {
                    Header oh = oldHeads[k];
                    oh.Leaving = true;
                    oh.TargetAlpha = 0.0f;
                    oh.TargetY = oh.AnimY;
                    _leavingHeads.Add(oh);
                }
            }

            AnimEngine.Register(this);
        }

        int Section(string title, string note, List<SetEntry> sets, int y, int cols,
                    int tileW, int tileH, int thumbH, int gap)
        {
            Header h = new Header();
            h.Text = title;
            h.Note = note;
            h.Bounds = new Rectangle(0, y, Width, Sc(38));
            int titleW = title.Length > 0 ? TextRenderer.MeasureText(title, Theme.FHead).Width + Sc(12) : 0;

            // The star right after the name and the note after it: otherwise they run into each
            // other.
            int star = Sc(26);
            h.Star = new Rectangle(titleW, h.Bounds.Y + (h.Bounds.Height - star) / 2, star, star);
            h.NoteShift = titleW + star + Sc(6);

            h.AnimY = h.TargetY = h.Bounds.Y;
            h.Alpha = h.TargetAlpha = 1f;
            _heads.Add(h);
            y += h.Bounds.Height + Sc(16);

            // The first tile is always "New Live Set": starting a set from here has to be
            // closer at hand than finding one among those already started.
            Tile nt = new Tile();
            nt.IsNewProject = true;
            nt.Bounds = new Rectangle(0, y, tileW, tileH);
            nt.AnimX = nt.TargetX = nt.Bounds.X;
            nt.AnimY = nt.TargetY = nt.Bounds.Y;
            nt.Alpha = nt.TargetAlpha = 1f;
            _tiles.Add(nt);
            int slot = 1;

            for (int i = 0; i < sets.Count; i++)
            {
                int col = (slot + i) % cols, row = (slot + i) / cols;
                Rectangle b = new Rectangle(col * (tileW + gap), y + row * (tileH + gap), tileW, tileH);

                Tile t = new Tile();
                t.Set = sets[i];
                t.Bounds = b;
                t.AnimX = t.TargetX = b.X;
                t.AnimY = t.TargetY = b.Y;
                t.Alpha = t.TargetAlpha = 1f;
                t.Thumb = new Rectangle(b.X, b.Y, b.Width, thumbH);
                t.HasPlay = sets[i].HasRenders;

                string date = t.Set.Modified == default(DateTime)
                    ? "" : t.Set.Modified.ToLocalTime().ToString("yyyy-MM-dd");
                string place = t.Set.Place;
                t.Subtitle = place.Length > 0 ? date + "   ·   " + place : date;

                // We count the insets from the real edge of the picture (Thumb minus the frame
                // minus the picture's inset inside the frame) rather than from the preview
                // frame — on the right and at the bottom the frame has different internal
                // insets, and measured from the frame the button would sit crooked.
                int picRight = Sc(ThumbInset + PicInset);
                int picBottom = Sc(PicInset);
                int picTop = Sc(ThumbInset + PicInset);
                int margin = Sc(8);

                int btn = Sc(34);
                t.Play = new Rectangle(t.Thumb.Right - picRight - btn - margin,
                                       t.Thumb.Bottom - picBottom - btn - margin, btn, btn);
                int pin = Sc(28);
                t.Pin = new Rectangle(t.Thumb.Right - picRight - margin - pin, t.Thumb.Y + picTop + margin, pin, pin);

                // The tags glyph mirrors the star: on the left the picture is set off from the
                // frame exactly as it is on the right, so the inset is the same picRight.
                t.TagText = ProjectMeta.JoinTags(ProjectMeta.TagsOf(sets[i].ProjectDir));
                t.TagBox = new Rectangle(t.Thumb.X + picRight + margin, t.Thumb.Y + picTop + margin, pin, pin);
                _tiles.Add(t);
            }

            int total = slot + sets.Count;   // slot — that very first tile
            int rows = (total + cols - 1) / cols;
            return y + rows * tileH + (rows - 1) * gap + Sc(18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DropScaledThumbs();
            // Without animation: a resize is only new geometry for the same set of tiles, not
            // new content. With BuildLayout(true) the whole grid replayed its fade-in at the
            // slightest thing, including the mini player appearing and disappearing at the
            // bottom of the window (which changes HomeView's height and arrives here).
            BuildLayout(false);
        }

        void DropScaledThumbs()
        {
            foreach (Bitmap b in _scaledThumbs.Values) if (b != null) b.Dispose();
            _scaledThumbs.Clear();
            _scaledSize = Size.Empty;
        }

        void ClampScroll()
        {
            int max = Math.Max(0, _contentHeight - Height);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_contentHeight <= Height) return;
            _scroller.OnMouseWheel(e.Delta, Sc(90));
            _barFade.Ping();
            base.OnMouseWheel(e);
        }

        // ------------------------------------------------------------------- mouse

        int TileAt(Point p, out bool onPlay, out bool onPin, out bool onTags)
        {
            onPlay = onPin = onTags = false;
            // Along with the rubber-band overshoot: during the bounce the tiles move away while
            // hits were counted at the old place — the star fired where it no longer was.
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            Point q = new Point(p.X, p.Y + _scroll + over);
            for (int i = 0; i < _tiles.Count; i++)
            {
                Tile t = _tiles[i];
                if (t.Alpha < 0.3f) continue;
                int dx = (int)Math.Round(t.AnimX) - t.Bounds.X;
                int dy = (int)Math.Round(t.AnimY) - t.Bounds.Y;
                Rectangle curBounds = new Rectangle(t.Bounds.X + dx, t.Bounds.Y + dy, t.Bounds.Width, t.Bounds.Height);
                if (!curBounds.Contains(q)) continue;
                Rectangle curPlay = new Rectangle(t.Play.X + dx, t.Play.Y + dy, t.Play.Width, t.Play.Height);
                Rectangle curPin = new Rectangle(t.Pin.X + dx, t.Pin.Y + dy, t.Pin.Width, t.Pin.Height);
                onPlay = t.HasPlay && curPlay.Contains(q);
                onPin = curPin.Contains(q);
                onTags = !string.IsNullOrEmpty(t.TagText)
                      && new Rectangle(t.TagBox.X + dx, t.TagBox.Y + dy, t.TagBox.Width, t.TagBox.Height).Contains(q);
                return i;
            }
            return -1;
        }

        /// <summary>
        /// A window point in content coordinates. Along with the rubber-band overshoot: during
        /// the bounce everything moves away while hits were counted at the old place.
        /// </summary>
        Point Content(Point p)
        {
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            return new Point(p.X, p.Y + _scroll + over);
        }

        /// <summary>Is the cursor on the heading's star? There is one heading, so there is
        /// nothing to search.</summary>
        bool OnHeadStar(Point p)
        {
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            Point q = new Point(p.X, p.Y + _scroll + over);
            foreach (Header h in _heads)
                if (!h.Star.IsEmpty && h.Alpha > 0.3f && h.Star.Contains(q)) return true;
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _barFade.SetHot(e.X >= Width - Sc(22));

            if (_draggingBar)
            {
                Rectangle bar = BarRect();
                int track = Height - Sc(10);
                int max = Math.Max(1, _contentHeight - Height);
                float t = (e.Y - _dragOffset - Sc(5)) / (float)Math.Max(1, track - bar.Height);
                _scrollTarget = _scrollCurrent = _scroll = (int)Math.Round(t * max);
                if (_scroller != null) _scroller.SyncPosition(_scroll);
                ClampScroll();
                Invalidate();
                return;
            }

            if (Math.Abs(Environment.TickCount - _lastPinTime) < 400)
            {
                int dist = Math.Abs(e.X - _lastPinPos.X) + Math.Abs(e.Y - _lastPinPos.Y);
                if (dist < Sc(8))
                {
                    if (_hot >= 0)
                    {
                        _hot = -1; _playHot = _pinHot = _tagsHot = false;
                        Cursor = Cursors.Default;
                        Invalidate();
                    }
                    base.OnMouseMove(e);
                    return;
                }
            }

            if (Overview.MouseMove(Content(e.Location))) Invalidate();

            bool starHot = OnHeadStar(e.Location);
            if (starHot != _headStarHot) { _headStarHot = starHot; Invalidate(); }

            bool play, pin, tags;
            int hot = TileAt(e.Location, out play, out pin, out tags);

            if (hot != _hot || play != _playHot || pin != _pinHot || tags != _tagsHot)
            {
                bool isNewHot = hot >= 0 && hot < _tiles.Count && _tiles[hot].IsNewProject;
                bool wasNewHot = _hot >= 0 && _hot < _tiles.Count && _tiles[_hot].IsNewProject;
                if (isNewHot && !wasNewHot)
                    PickNextSplash();

                _hot = hot; _playHot = play; _pinHot = pin; _tagsHot = tags;
                Cursor = hot >= 0 || starHot ? Cursors.Hand : Cursors.Default;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (Overview.MouseLeave()) Invalidate();
            if (_headStarHot) { _headStarHot = false; Invalidate(); }
            if (_hot >= 0)
            {
                _hot = -1; _playHot = _pinHot = _tagsHot = false;
                Cursor = Cursors.Default;
                AnimEngine.Register(this);
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        Point _lastPinPos;
        int _lastPinTime;

        // A right click on a tile opens its menu — see OnMouseDown.
        protected override bool WantsRightClick { get { return true; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                Rectangle bar = BarRect();
                if (!bar.IsEmpty && (bar.Contains(e.Location) || e.X >= Width - Sc(12)))
                {
                    _draggingBar = true;
                    _dragOffset = e.Y - bar.Y;
                    if (_dragOffset < 0 || _dragOffset > bar.Height) _dragOffset = bar.Height / 2;
                    return;
                }
            }

            if (e.Button == MouseButtons.Left && Overview.MouseDown(Content(e.Location))) return;

            if (e.Button == MouseButtons.Left && OnHeadStar(e.Location))
            {
                PinnedFirst = !PinnedFirst;
                Invalidate();                      // the glyph changes at once
                if (PinnedFirstToggled != null) PinnedFirstToggled();
                else RebuildTransition();          // there is nobody to rebuild it — we do it ourselves
                return;
            }

            bool play, pin, tags;
            int hit = TileAt(e.Location, out play, out pin, out tags);

            if (e.Button == MouseButtons.Right)
            {
                if (hit >= 0 && !_tiles[hit].IsNewProject) ShowMenu(_tiles[hit].Set, e.Location);
                return;
            }

            // A click past the tiles clears the selection — otherwise there is no getting rid
            // of it without choosing something else.
            if (hit < 0)
            {
                if (_selected != null || _newSelected)
                {
                    _newSelected = false;
                    Selected = null;
                    Invalidate();
                }
                return;
            }

            // "New Live Set" behaves like the other cards: a single click only selects, and the
            // action itself is on a double click. It used to fire straight away, and on a field
            // of tiles that look alike one behaved unlike the rest — people launched Live with
            // it by accident, simply by clicking wide.
            if (_tiles[hit].IsNewProject)
            {
                _selected = null;
                _newSelected = true;
                Invalidate();
                return;
            }

            SetEntry s = _tiles[hit].Set;
            if (play) { if (PlayRequested != null) PlayRequested(s); return; }
            if (tags) { if (NotesRequested != null) NotesRequested(s); return; }
            if (pin)
            {
                _lastPinTime = Environment.TickCount;
                _lastPinPos = e.Location;
                _hot = -1; _playHot = _pinHot = _tagsHot = false;
                _pinHoverFactor = _playHoverFactor = 0f;
                if (_tileHoverFactors != null)
                {
                    for (int i = 0; i < _tileHoverFactors.Length; i++) _tileHoverFactors[i] = 0f;
                }
                HomeStore.TogglePin(s.Path);
                RebuildTransition();
                return;
            }

            Selected = s;
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingBar)
            {
                _draggingBar = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            bool play, pin, tags;
            int hit = TileAt(e.Location, out play, out pin, out tags);
            // The tags glyph already opened the editor on the first click — the second we
            // simply swallow, or on top of the editor it would also launch the project in Live.
            if (hit >= 0 && tags) { base.OnMouseDoubleClick(e); return; }
            if (hit >= 0 && (play || pin))
            {
                OnMouseDown(e);
                base.OnMouseDoubleClick(e);
                return;
            }
            if (Math.Abs(Environment.TickCount - _lastPinTime) < SystemInformation.DoubleClickTime + 150)
                return;

            if (hit >= 0)
            {
                if (_tiles[hit].IsNewProject)
                {
                    if (NewProjectRequested != null) NewProjectRequested();
                }
                else if (Activated != null) Activated(_tiles[hit].Set);
            }
            base.OnMouseDoubleClick(e);
        }

        void ShowMenu(SetEntry s, Point at)
        {
            ContextMenuStrip m = DarkMenu.Create();

            ToolStripMenuItem open = new ToolStripMenuItem("Open in Live");
            open.ShortcutKeyDisplayString = "Enter";
            open.Click += delegate { if (Activated != null) Activated(s); };
            m.Items.Add(open);

            if (s.HasRenders)
            {
                ToolStripMenuItem play = new ToolStripMenuItem("Play render");
                play.ShortcutKeyDisplayString = "Space";
                play.Click += delegate { if (PlayRequested != null) PlayRequested(s); };
                m.Items.Add(play);
            }

            ToolStripMenuItem pin = new ToolStripMenuItem(
                HomeStore.IsPinned(s.Path) ? "Unpin" : "Pin project");
            pin.Checked = HomeStore.IsPinned(s.Path);
            pin.ShortcutKeyDisplayString = "Q";
            pin.Click += delegate
            {
                _lastPinTime = Environment.TickCount;
                _lastPinPos = at;
                _hot = -1; _playHot = _pinHot = _tagsHot = false;
                _pinHoverFactor = _playHoverFactor = 0f;
                if (_tileHoverFactors != null)
                {
                    for (int i = 0; i < _tileHoverFactors.Length; i++) _tileHoverFactors[i] = 0f;
                }
                HomeStore.TogglePin(s.Path);
                RebuildTransition();
            };
            m.Items.Add(pin);

            ToolStripMenuItem notes = new ToolStripMenuItem(
                ProjectMeta.HasAnything(s.ProjectDir)
                    ? "Tags and notes…"
                    : "Add tags or a note…");
            notes.ShortcutKeyDisplayString = "Ctrl+T";
            notes.Click += delegate { if (NotesRequested != null) NotesRequested(s); };
            m.Items.Add(notes);

            ToolStripMenuItem details = new ToolStripMenuItem("Show details");
            details.Click += delegate { if (DetailsRequested != null) DetailsRequested(s); };
            m.Items.Add(details);

            ToolStripMenuItem rescue = new ToolStripMenuItem("Rescue project…");
            rescue.ShortcutKeyDisplayString = "Ctrl+R";
            rescue.Click += delegate { if (RescueRequested != null) RescueRequested(s); };
            m.Items.Add(rescue);

            ToolStripMenuItem reveal = new ToolStripMenuItem("Show in Explorer");
            reveal.ShortcutKeyDisplayString = "Shift+Enter";
            reveal.Click += delegate { if (RevealRequested != null) RevealRequested(s); };
            m.Items.Add(reveal);

            m.Show(this, at);
        }

        // ------------------------------------------------------------------ previews

        void Want(string path)
        {
            if (Loader == null || string.IsNullOrEmpty(path)) return;
            if (_thumbs.ContainsKey(path) || _queued.Contains(path)
                || _rendering.Contains(path) || _diskLoading.Contains(path)) return;

            // A finished picture from the previous run is the cheapest route: a couple of
            // milliseconds against a hundred for a full parse of the set.
            if (ThumbCache.Has(path)) { LoadFromDisk(path); return; }

            Arrangement cached = Loader.Cached(path);
            if (cached != null) { StoreThumb(cached); return; }

            _queued.Add(path);
            _queue.Add(path);
            PumpQueue();
        }

        void LoadFromDisk(string path)
        {
            _diskLoading.Add(path);
            string p = path;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool known;
                Bitmap bmp = ThumbCache.Load(p, out known);
                try
                {
                    if (IsDisposed || !IsHandleCreated) { if (bmp != null) bmp.Dispose(); return; }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _diskLoading.Remove(p);
                        if (!known)
                        {
                            // The entry is gone or corrupt — we go the usual way.
                            if (bmp != null) bmp.Dispose();
                            Want(p);
                            return;
                        }
                        if (_thumbs.ContainsKey(p)) { if (bmp != null) bmp.Dispose(); return; }
                        PutThumb(p, bmp);
                        Invalidate();
                    });
                }
                catch { if (bmp != null) bmp.Dispose(); }
            });
        }

        void PumpQueue()
        {
            while (_inFlight.Count < MaxInFlight && _queue.Count > 0)
            {
                string p = _queue[0];
                _queue.RemoveAt(0);
                if (_thumbs.ContainsKey(p) || _rendering.Contains(p)) { _queued.Remove(p); continue; }
                _inFlight.Add(p);
                Loader.Request(p);
            }
        }

        /// <summary>The loader's answer; it arrives on the UI thread.</summary>
        public void OnArrangement(Arrangement a)
        {
            if (IsDisposed) return;

            // Parsing always returns an object with Path filled in — even when the set did not
            // read — so that is what we close our request by. Answers to other requests (the
            // detail panel, the full-screen preview) arrive here too: since the set is already
            // parsed, it would be a shame not to make a tile out of it.
            if (a != null && !string.IsNullOrEmpty(a.Path))
            {
                _inFlight.Remove(a.Path);
                _queued.Remove(a.Path);
                StoreThumb(a);
            }

            PumpQueue();
            Invalidate();
        }

        // --------------------------------------------------- storing the pictures

        /// <summary>Mark that a picture has just been shown — it is not a candidate for
        /// displacement.</summary>
        void TouchThumb(string path)
        {
            _thumbUsed[path] = ++_thumbClock;
        }

        void PutThumb(string path, Bitmap bmp)
        {
            _thumbs[path] = bmp;
            TouchThumb(path);
            TrimThumbs();
        }

        /// <summary>
        /// We count and displace only entries with a picture: "the arrangement is empty" and
        /// "the set does not read" are null and take no memory at all, while throwing such an
        /// entry away would mean parsing a hopeless set again on every scroll.
        /// </summary>
        void TrimThumbs()
        {
            int heavy = 0;
            foreach (Bitmap held in _thumbs.Values) if (held != null) heavy++;

            while (heavy > _thumbBudget)
            {
                string oldest = null;
                int best = int.MaxValue;
                foreach (KeyValuePair<string, int> kv in _thumbUsed)
                {
                    if (kv.Value >= best) continue;
                    Bitmap held;
                    if (!_thumbs.TryGetValue(kv.Key, out held) || held == null) continue;
                    best = kv.Value; oldest = kv.Key;
                }
                if (oldest == null) break;

                Bitmap b;
                if (_thumbs.TryGetValue(oldest, out b) && b != null) b.Dispose();
                _thumbs.Remove(oldest);
                _thumbUsed.Remove(oldest);
                heavy--;

                Bitmap s;
                if (_scaledThumbs.TryGetValue(oldest, out s))
                {
                    if (s != null) s.Dispose();
                    _scaledThumbs.Remove(oldest);
                }
            }
        }

        void StoreThumb(Arrangement a)
        {
            if (a == null || string.IsNullOrEmpty(a.Path)) return;
            if (_thumbs.ContainsKey(a.Path) || _rendering.Contains(a.Path)) return;

            _queued.Remove(a.Path);

            if (a.Error != null || !a.HasContent)
            {
                // An empty arrangement we remember to disk too: that answer costs exactly as
                // much as a picture. A read error only in memory: it can be temporary (the
                // drive was switched off), and writing it down would mean declaring the set
                // empty until its next edit.
                if (a.Error == null) ThumbCache.SaveEmptyAsync(a.Path);
                PutThumb(a.Path, null);
                return;
            }

            _rendering.Add(a.Path);
            string path = a.Path;
            Arrangement arr = a;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap bmp = null;
                try
                {
                    RenderOptions o = new RenderOptions();
                    o.Dpi = 1f;                 // we draw into a fixed size and scale afterwards
                    o.MaxLane = 8;
                    o.ShowRuler = false;
                    bmp = ArrangementRender.ToBitmap(arr, ThumbW, ThumbH, o);
                    if (bmp != null) ThumbCache.Save(path, bmp);
                }
                catch { }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _rendering.Remove(path);
                            if (!_thumbs.ContainsKey(path))
                            {
                                PutThumb(path, bmp);
                                Invalidate();
                            }
                            else
                            {
                                if (bmp != null) bmp.Dispose();
                            }
                        });
                    }
                    else
                    {
                        if (bmp != null) bmp.Dispose();
                    }
                }
                catch
                {
                    if (bmp != null) bmp.Dispose();
                }
            });
        }

        void DropThumbs()
        {
            foreach (Bitmap b in _thumbs.Values) if (b != null) b.Dispose();
            _thumbs.Clear();
            _thumbUsed.Clear();
            DropScaledThumbs();
            _queued.Clear();
            _queue.Clear();
            _rendering.Clear();
            _inFlight.Clear();
            _diskLoading.Clear();
        }

        Bitmap GetScaledThumb(string path, Bitmap src, Size targetSize)
        {
            if (src == null || targetSize.Width <= 0 || targetSize.Height <= 0) return null;

            // Dropping the cache when the tile size changes (a window resize)
            if (_scaledSize != targetSize)
            {
                DropScaledThumbs();
                _scaledSize = targetSize;
            }

            Bitmap result;
            if (_scaledThumbs.TryGetValue(path, out result)) return result;

            // We scale once, and DrawImage then draws 1:1 with no resampling
            result = new Bitmap(targetSize.Width, targetSize.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics tg = Graphics.FromImage(result))
            {
                tg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                tg.DrawImage(src, 0, 0, targetSize.Width, targetSize.Height);
            }
            _scaledThumbs[path] = result;
            return result;
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, e.ClipRectangle, Surface);
            if (_scrolling)
                Theme.SmoothFast(g);
            else
                Theme.Smooth(g);

            if (_tiles.Count == 0 && _heads.Count == 0)
            {
                Chrome.DrawText(g, "Nothing indexed yet",
                    Theme.FBody, new Rectangle(0, Sc(40), Width, Sc(30)), Theme.TextDim, Chrome.Left);
                return;
            }

            // The rubber-band overshoot past the edge — everything moves except the scrollbar.
            int over = _scroller != null ? (int)Math.Round(_scroller.Overscroll) : 0;
            int scroll = _scroll + over;

            Overview.Paint(g, this, scroll);
            foreach (Header h in _heads) PaintHeader(g, h, scroll);
            foreach (Header lh in _leavingHeads) PaintHeader(g, lh, scroll);

            int bufferTop = -Height;
            int bufferBottom = Height * 2;

            for (int i = 0; i < _leavingTiles.Count; i++)
            {
                PaintOneTile(g, _leavingTiles[i], -1, true, scroll, bufferTop, bufferBottom);
            }

            for (int i = 0; i < _tiles.Count; i++)
            {
                PaintOneTile(g, _tiles[i], i, false, scroll, bufferTop, bufferBottom);
            }

            // The tiles at the top and bottom edges dissolve slightly into the background — a
            // thin band, three times shorter than the first attempt. Not needed on glass: there
            // the edge is blurred by the window backdrop as it is.
            if (!Glass.Enabled && _contentHeight > Height)
            {
                int fadeH = Sc(36);

                // At the top only if it really has been scrolled: right at the top the fade
                // would dim the Overview heading, and there is nothing to hide, nothing is cut
                // off there.
                if (scroll > 0)
                {
                    Rectangle top = new Rectangle(0, 0, Width, fadeH);
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new Rectangle(top.X, top.Y - 1, top.Width, top.Height + 2),
                        Surface, Color.FromArgb(0, Surface), LinearGradientMode.Vertical))
                        g.FillRectangle(lb, top);
                }

                Rectangle bottom = new Rectangle(0, Height - fadeH, Width, fadeH);
                using (LinearGradientBrush lb = new LinearGradientBrush(
                    new Rectangle(bottom.X, bottom.Y - 1, bottom.Width, bottom.Height + 2),
                    Color.FromArgb(0, Surface), Surface, LinearGradientMode.Vertical))
                    g.FillRectangle(lb, bottom);
            }

            // The scrollbar over the grid: Sc(4) at rest, Sc(8) under the cursor, fading a
            // second after the last scroll.
            Chrome.PaintFadingBar(g, BarRect(), _draggingBar ? 1f : _barFade.Alpha,
                                  _draggingBar ? 1f : _barFade.Thick, true, Sc(4));
        }

        void PaintHeader(Graphics g, Header h, int scroll)
        {
            if (h.Alpha <= 0.005f) return;
            int hy = (int)Math.Round(h.AnimY) - scroll;
            Rectangle r = new Rectangle(h.Bounds.X, hy, h.Bounds.Width, h.Bounds.Height);
            if (r.Bottom < 0 || r.Top > Height) return;
            Color textC = h.Alpha < 0.99f ? Color.FromArgb((int)Math.Round(Theme.Text.A * h.Alpha), Theme.Text) : Theme.Text;
            Color noteC = h.Alpha < 0.99f ? Color.FromArgb((int)Math.Round(Theme.TextDim.A * h.Alpha), Theme.TextDim) : Theme.TextDim;
            if (h.Text.Length > 0)
                Chrome.DrawText(g, h.Text, Theme.FHead, r, textC,
                                Chrome.Left | TextFormatFlags.NoClipping);

            if (!h.Star.IsEmpty)
            {
                // The same glyph and the same three states as the star in the table header.
                Color ink = PinnedFirst ? Theme.Light
                          : (_headStarHot ? Theme.Text : Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93));
                if (h.Alpha < 0.99f) ink = Color.FromArgb((int)Math.Round(ink.A * h.Alpha), ink);
                RectangleF sr = new RectangleF(h.Star.X, hy + (h.Star.Y - h.Bounds.Y),
                                               h.Star.Width, h.Star.Height);
                Icons.Draw(g, PinnedFirst ? Glyph.StarFill : Glyph.Star,
                           RectangleF.Inflate(sr, -Sc(4), -Sc(4)), ink, 1.3f);
            }
            if (h.Note.Length > 0)
            {
                Chrome.DrawText(g, h.Note, Theme.FLabel,
                    new Rectangle(r.X + h.NoteShift, r.Y, Math.Max(0, r.Width - h.NoteShift), r.Height),
                    noteC, Chrome.Left | TextFormatFlags.NoClipping);
            }
        }

        void PaintOneTile(Graphics g, Tile t, int index, bool isLeaving, int scroll, int bufferTop, int bufferBottom)
        {
            int curX = (int)Math.Round(t.AnimX);
            int curY = (int)Math.Round(t.AnimY);
            Rectangle b = new Rectangle(curX, curY - scroll, t.Bounds.Width, t.Bounds.Height);

            // A tile far outside visibility (more than one screen from the edge) — skipped
            // entirely
            if (b.Bottom < bufferTop || b.Top > bufferBottom) return;

            if (b.Bottom < 0 || b.Top > Height)
            {
                if (t.Set != null)
                {
                    if (_thumbs.ContainsKey(t.Set.Path)) TouchThumb(t.Set.Path);
                    else Want(t.Set.Path);
                }
                return;
            }

            // The entrance animation (if a full one was started) is multiplied by the tile's
            // transparency
            float entrance = (_tileEntrance != null && index >= 0 && index < _tileEntrance.Length) ? _tileEntrance[index] : 1.0f;
            float totalAlpha = entrance * t.Alpha;
            if (totalAlpha <= 0.001f) return;

            int offsetY = (entrance < 1.0f) ? (int)Math.Round((1.0f - entrance) * Sc(30)) : 0;
            Rectangle animB = new Rectangle(b.X, b.Y + offsetY, b.Width, b.Height);

            bool isHot = !isLeaving && (index == _hot);
            float hover = (!isLeaving && _tileHoverFactors != null && index >= 0 && index < _tileHoverFactors.Length) ? _tileHoverFactors[index] : (isHot ? 1f : 0f);
            if (t.IsNewProject) PaintNewProjectTile(g, animB, isHot, hover, totalAlpha);
            else PaintTile(g, t, animB, isHot, hover, totalAlpha);
        }

        /// <summary>
        /// The card backing — always the full one, with outline and highlight.
        ///
        /// I tried drawing it simplified while scrolling (PaintGlassSurfaceFast, with no
        /// outline and no highlight — those are two GraphicsPaths per tile per frame): there is
        /// a saving, but it is visible to the eye. Cards on a moving grid lose their edges and
        /// get them back the moment the scroll comes to rest — that reads as flicker rather
        /// than as smoothness. So no: two extra paths per tile are cheaper than a twitching
        /// picture.
        /// </summary>
        void PaintCard(Graphics g, RectangleF r, float radius, int alpha)
        {
            Theme.PaintGlassSurface(this, g, r, radius, alpha);
        }

        /// <summary>The card for creating a new set: a soft dashed contour with no background
        /// and a neat centred plus in the Apple style.</summary>
        void PaintNewProjectTile(Graphics g, Rectangle b, bool hot, float hoverFactor, float entrance)
        {
            float cardR = Sc(Theme.CardR);

            // A soft dashed contour along the card's rounded outline, with no background and no
            // solid stroke
            int borderAlpha = _newSelected ? 0xFF : (int)Math.Round((0x22 + 0x2A * hoverFactor) * entrance);
            Color dashColor = _newSelected ? Theme.Light : Color.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF);
            float dashWidth = _newSelected ? 1.5f : 1.2f;
            if (borderAlpha > 0)
            {
                using (Pen dashPen = new Pen(dashColor, dashWidth))
                {
                    dashPen.DashStyle = DashStyle.Custom;
                    dashPen.DashPattern = new float[] { 5f, 4f };
                    using (GraphicsPath path = Theme.Round(b, cardR))
                    {
                        g.DrawPath(dashPen, path);
                    }
                }
            }

            Color ink = hoverFactor > 0.01f ? Theme.Interpolate(Theme.TextDim, Color.White, hoverFactor) : Theme.TextDim;
            if (entrance < 1.0f) ink = Color.FromArgb((int)Math.Round(ink.A * entrance), ink);

            string label = "New Live Set";
            int discDiam = Sc(44);
            int labelH = Sc(24);
            int gap = Sc(12);

            int groupH = discDiam + gap + labelH;
            float discY = b.Y + (b.Height - groupH) / 2f;
            float discX = b.X + (b.Width - discDiam) / 2f;

            // A frosted glass circle under the plus glyph
            RectangleF discRect = new RectangleF(discX, discY, discDiam, discDiam);
            Color discBg = Color.FromArgb((int)Math.Round((0x12 + 0x1E * hoverFactor) * entrance), 0xFF, 0xFF, 0xFF);
            Color discBorder = Color.FromArgb((int)Math.Round((0x1E + 0x28 * hoverFactor) * entrance), 0xFF, 0xFF, 0xFF);

            using (Brush discBrush = new SolidBrush(discBg))
                g.FillEllipse(discBrush, discRect);
            using (Pen discPen = new Pen(discBorder, 1f))
                g.DrawEllipse(discPen, discRect);

            // The plus glyph inside the circle
            int iconSize = Sc(20);
            RectangleF iconBox = new RectangleF(b.X + (b.Width - iconSize) / 2f, discY + (discDiam - iconSize) / 2f, iconSize, iconSize);
            Icons.Draw(g, Glyph.Plus, iconBox, ink, 1.8f);

            // The caption under the circle
            Rectangle textR = new Rectangle(b.X, (int)(discY + discDiam + gap), b.Width, labelH);
            Chrome.DrawText(g, label, Theme.FTitle, textR, ink, Chrome.Center | TextFormatFlags.NoClipping);

            // A hover subtitle in the Minecraft/Terraria spirit, with producer humour
            if (hoverFactor > 0.02f)
            {
                string splash = (_splashIndex >= 0 && _splashIndex < Splashes.Length) ? Splashes[_splashIndex] : Splashes[0];
                int padX = Sc(12);
                int subY = textR.Bottom + Sc(4);
                Rectangle subR = new Rectangle(b.X + padX, subY, b.Width - padX * 2, Sc(44));
                Color subTarget = Theme.Interpolate(Theme.TextDim, Theme.Text, 0.35f);
                Color subColor = Theme.Interpolate(Theme.Bg, subTarget, hoverFactor * entrance);
                TextFormatFlags flags = TextFormatFlags.HorizontalCenter |
                                        TextFormatFlags.Top |
                                        TextFormatFlags.WordBreak |
                                        TextFormatFlags.NoPrefix |
                                        TextFormatFlags.NoClipping;
                Chrome.DrawText(g, splash, Theme.FLabel, subR, subColor, flags);
            }
        }

        void PaintTile(Graphics g, Tile t, Rectangle b, bool hot, float hoverFactor, float entrance)
        {
            bool selected = _selected != null && ReferenceEquals(_selected, t.Set);
            int alpha = (int)Math.Round(Theme.Lerp(Theme.GlassSurfaceAlpha, Theme.GlassSurfaceHotAlpha, hoverFactor) * entrance);
            float cardR = Sc(Theme.CardR);

            PaintCard(g, b, cardR, alpha);

            // Hover lights up the contour: on glass the fill barely changes, and without this
            // the card did not respond to the cursor at all.
            if (!selected && hoverFactor > 0.01f)
                Theme.DrawRound(g, b, cardR,
                    Color.FromArgb((int)Math.Round(0x3C * hoverFactor * entrance), 255, 255, 255), 1.2f);
            if (selected)
            {
                // Selection in ordinary light rather than the accent: the accent is claimed by
                // buttons, progress and switches, while the outline of the active element does
                // not depend on the choice of theme.
                RectangleF ring = RectangleF.Inflate(b, -0.75f, -0.75f);
                Color line = Color.FromArgb((int)Math.Round(0xD0 * entrance), Theme.Light);
                using (GraphicsPath rp = Theme.Round(ring, cardR - 0.75f))
                using (Pen pen = new Pen(line, 1.5f))
                    g.DrawPath(pen, rp);
            }

            // Everything inside a tile is counted from its own rectangle: that has already
            // accounted for the scroll, the rubber band and the transition animation's offset.
            int dx = b.X - t.Bounds.X;
            int dy = b.Y - t.Bounds.Y;
            Rectangle thumb = new Rectangle(t.Thumb.X + dx, t.Thumb.Y + dy, t.Thumb.Width, t.Thumb.Height);
            Rectangle inner = Rectangle.Inflate(thumb, -Sc(ThumbInset), -Sc(ThumbInset));
            inner.Height = thumb.Height - Sc(ThumbInset);   // at the bottom the picture runs flush with the text
            Color innerBg = entrance < 0.99f ? Color.FromArgb((int)Math.Round(255 * entrance), Theme.Bg) : Theme.Bg;
            Theme.FillRound(g, inner, Sc(Theme.ThumbR), innerBg);

            Bitmap art;
            bool known = _thumbs.TryGetValue(t.Set.Path, out art);
            if (!known)
            {
                Want(t.Set.Path);
                Color readC = entrance < 0.99f ? Color.FromArgb((int)Math.Round(Theme.TextDim.A * entrance), Theme.TextDim) : Theme.TextDim;
                Chrome.DrawText(g, "reading…", Theme.FSmall, inner,
                                readC, Chrome.Center);
            }
            else if (art == null)
            {
                Color emptyText = Color.FromArgb((int)Math.Round(0xFF * entrance), 0x80, 0x80, 0x84);
                Chrome.DrawText(g, "no arrangement", Theme.FSmall, inner,
                                emptyText, Chrome.Center);
            }
            else
            {
                // The picture was shown — meaning it is not a candidate for displacement, see
                // MaxThumbs.
                TouchThumb(t.Set.Path);
                Rectangle fit = Rectangle.Inflate(inner, -Sc(PicInset), -Sc(PicInset));
                if (fit.Width > 0 && fit.Height > 0)
                {
                    // We use the pre-scaled copy: DrawImage 1:1 with no resampling
                    Bitmap scaled = GetScaledThumb(t.Set.Path, art, fit.Size);
                    Bitmap bmp = scaled ?? art;
                    if (entrance < 0.99f)
                    {
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ColorMatrix cm = new ColorMatrix();
                            cm.Matrix33 = Math.Max(0f, Math.Min(1f, entrance));
                            ia.SetColorMatrix(cm);
                            g.DrawImage(bmp, fit, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                    {
                        if (scaled != null)
                            g.DrawImage(scaled, fit.X, fit.Y);
                        else
                            g.DrawImage(art, fit);
                    }
                }
            }

            // The name and the date under the preview — with room from the tile edges and from
            // each other, but the pair of lines stays closer to the preview than to the middle
            // of the empty bottom.
            int textY = thumb.Bottom + Sc(4);
            Rectangle nameR = new Rectangle(b.X + Sc(16), textY, b.Width - Sc(32), Sc(26));
            Color nameC = entrance < 0.99f ? Color.FromArgb((int)Math.Round(Theme.Text.A * entrance), Theme.Text) : Theme.Text;
            Chrome.DrawText(g, t.Set.Name, Theme.FTitle, nameR, nameC,
                            Chrome.Left | TextFormatFlags.NoClipping);

            Rectangle dateR = new Rectangle(b.X + Sc(16), nameR.Bottom + Sc(4), b.Width - Sc(32), Sc(22));
            Color dateC = entrance < 0.99f ? Color.FromArgb((int)Math.Round(Theme.TextDim.A * entrance), Theme.TextDim) : Theme.TextDim;
            Chrome.DrawText(g, t.Subtitle ?? "", Theme.FLabel, dateR, dateC,
                            Chrome.Left | TextFormatFlags.NoClipping);

            // The listen button — only if there is a render next to the project.
            bool isPlaying = Playing && PlayingTag != null && ReferenceEquals(PlayingTag, t.Set);
            if (t.HasPlay)
            {
                Rectangle pb = new Rectangle(t.Play.X + dx, t.Play.Y + dy, t.Play.Width, t.Play.Height);
                bool ph = hot && _playHot;
                Color btnBg = ph ? Color.White : Theme.Light;
                if (entrance < 0.99f) btnBg = Color.FromArgb((int)Math.Round(btnBg.A * entrance), btnBg);
                Theme.FillRound(g, pb, pb.Height / 2f, btnBg);

                Color iconC = entrance < 0.99f ? Color.FromArgb((int)Math.Round(Theme.OnLight.A * entrance), Theme.OnLight) : Theme.OnLight;
                if (isPlaying && !ph)
                    PlayPulse.Paint(g, RectangleF.Inflate(pb, -Sc(9), -Sc(9)), iconC);
                else
                    Icons.Draw(g, isPlaying ? Glyph.Pause : Glyph.Play,
                               RectangleF.Inflate(pb, -Sc(9), -Sc(9)), iconC, 1.4f);
            }

            // The star: always visible on a pinned one, under the cursor on the rest.
            bool pinned = HomeStore.IsPinned(t.Set.Path);
            if (pinned || hot)
            {
                Rectangle nb = new Rectangle(t.Pin.X + dx, t.Pin.Y + dy, t.Pin.Width, t.Pin.Height);
                bool nh = hot && _pinHot;
                if (nh) Theme.PaintGlassSurface(this, g, nb, nb.Height / 2f, (int)Math.Round(Theme.GlassSurfacePressedAlpha * entrance));
                Color starC = pinned ? Theme.Light : (nh ? Theme.Text : Theme.TextDim);
                if (entrance < 0.99f) starC = Color.FromArgb((int)Math.Round(starC.A * entrance), starC);
                Icons.Draw(g, pinned ? Glyph.StarFill : Glyph.Star, RectangleF.Inflate(nb, -Sc(6), -Sc(6)),
                           starC, 1.2f);
            }

            PaintTileTags(g, t, dx, dy, thumb, hot, entrance);
        }

        /// <summary>
        /// A tag at the top left of the preview — "this project has tags". Always visible, like
        /// a pinned star: otherwise one learns about the tags only by hovering over every tile
        /// in turn. The tags themselves do not fit into a tile, so with the cursor on the glyph
        /// they pop up as a strip under it — whatever does not fit is cut off with an ellipsis.
        /// </summary>
        void PaintTileTags(Graphics g, Tile t, int dx, int dy, Rectangle thumb, bool hot, float entrance)
        {
            if (string.IsNullOrEmpty(t.TagText)) return;

            Rectangle tb = new Rectangle(t.TagBox.X + dx, t.TagBox.Y + dy, t.TagBox.Width, t.TagBox.Height);
            bool th = hot && _tagsHot;
            if (th) Theme.PaintGlassSurface(this, g, tb, tb.Height / 2f, (int)Math.Round(Theme.GlassSurfacePressedAlpha * entrance));

            Color ink = th ? Theme.Text : Theme.Light;
            if (entrance < 0.99f) ink = Color.FromArgb((int)Math.Round(ink.A * entrance), ink);
            Icons.Draw(g, Glyph.Tag, RectangleF.Inflate(tb, -Sc(6), -Sc(6)), ink, 1.2f);

            if (!th) return;

            string[] tags = t.TagText.Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            if (tags.Length == 0) return;

            // The pills lie directly on the picture with no shared backing under them, so each
            // is opaque in itself and outlined with a hairline: over a light arrangement a
            // translucent pill would not read at all.
            Font f = Theme.FBadge;
            int chipH = Chrome.PillHeight(f), gap = Sc(4), chipPadX = Sc(10);
            int right = thumb.Right - Sc(ThumbInset + PicInset);
            int x = tb.X, y = tb.Bottom + Sc(4);

            for (int i = 0; i < tags.Length; i++)
            {
                int w = TextRenderer.MeasureText(tags[i], f, new Size(short.MaxValue, chipH),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + chipPadX * 2;

                if (x + w > right)
                {
                    // The very first tag is wider than the cell — we cut it ourselves, or in
                    // place of the only tag there would be a lone ellipsis about nothing.
                    if (i == 0 && right - x > chipH)
                        PaintTagChip(g, new Rectangle(x, y, right - x, chipH), tags[i], f, true);
                    else if (x + chipH <= right)
                        PaintTagDots(g, new Rectangle(x, y, chipH, chipH));
                    break;
                }

                PaintTagChip(g, new Rectangle(x, y, w, chipH), tags[i], f);
                x += w + gap;
            }
        }

        /// <summary>The "there are more tags" pill — the same one, only with dots instead of a
        /// word.</summary>
        void PaintTagDots(Graphics g, Rectangle chip)
        {
            PaintTagPill(g, chip);
            Chrome.DrawDots(g, chip, Theme.Text, Sc(3));
        }

        /// <summary>The pill backing: dense, with a hairline outline — it goes over the
        /// preview.</summary>
        void PaintTagPill(Graphics g, Rectangle chip)
        {
            float r = chip.Height / 2f;
            Theme.FillRound(g, chip, r, Color.FromArgb(0xEE, 0x3A, 0x3A, 0x3D));
            Theme.DrawRound(g, chip, r, Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF), 1f);
        }

        /// <summary>One tag pill over the preview.</summary>
        void PaintTagChip(Graphics g, Rectangle chip, string text, Font f, bool clipped = false)
        {
            PaintTagPill(g, chip);
            Chrome.DrawText(g, text, f,
                            new Rectangle(chip.X, chip.Y + Chrome.PillTop(g, f, chip.Height),
                                          chip.Width, chip.Height),
                            Theme.Text, clipped ? Chrome.PillTextClipped : Chrome.PillText);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PlayPulse.Detach(this);
                if (_barFade != null) _barFade.Dispose();
                if (_scroller != null) _scroller.Dispose();
                Overview.Dispose();
                DropThumbs();
            }
            base.Dispose(disposing);
        }
    }
}
