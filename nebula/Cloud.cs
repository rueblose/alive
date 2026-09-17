using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager.Nebula
{
    /// <summary>An axis of the cloud: a value, its scale, the edge labels — and whether it is
    /// on at all.</summary>
    public sealed class Axis
    {
        public Metric M;

        /// <summary>A channel that is off takes no part in the cloud (see
        /// CloudView.Coord/ColorOf) but remembers its value: switch it back on and it is
        /// already chosen.</summary>
        public bool On = true;

        public Range R = new Range();
        public string LoText = "";
        public string HiText = "";

        public void Fit(List<SetEntry> sets, Metric m)
        {
            M = m;
            R = Range.Of(sets, m);
            LoText = HiText = "";

            List<SetEntry> ok = new List<SetEntry>();
            foreach (SetEntry s in sets) if (!double.IsNaN(m.Value(s))) ok.Add(s);
            if (ok.Count == 0) return;

            ok.Sort(delegate (SetEntry a, SetEntry b) { return m.Value(a).CompareTo(m.Value(b)); });
            int lo = m.Categorical || ok.Count < 20 ? 0 : (int)(ok.Count * 0.02);
            int hi = m.Categorical || ok.Count < 20 ? ok.Count - 1 : (int)(ok.Count * 0.98);
            if (hi >= ok.Count) hi = ok.Count - 1;
            LoText = m.Text(ok[lo]);
            HiText = m.Text(ok[hi]);
        }

        /// <summary>The channel is off — the scale is not computed and the edge is not
        /// labelled.</summary>
        public void Clear(Metric m)
        {
            M = m;
            R = new Range();
            LoText = HiText = "";
        }
    }

    /// <summary>Ready camera positions — a free angle by default and three orthographic views
    /// onto the cube, as in CAD.</summary>
    public enum CameraPreset { Angle, Front, Side, Top }

    /// <summary>
    /// The cloud itself: six values of a set turn into three coordinates, plus the size,
    /// density and colour of a dot.
    ///
    /// It is drawn not with GDI+ but into a pixel buffer of our own. The reason is simple:
    /// every dot has a soft glow, and a PathGradientBrush over a thousand dots per frame is a
    /// thousand brushes per frame and tens of milliseconds. Our own buffer adds the glows
    /// together (as light does add up) and lies in premultiplied alpha — exactly the format
    /// GDI+ puts onto the window's glass with a single DrawImage and no recomputation.
    /// </summary>
    public sealed class CloudView : Control
    {
        struct Node
        {
            public int Index;              // a row in _sets
            public float TX, TY, TZ;       // where the dot is heading
            public float X, Y, Z;          // where it is now — the cloud rebuilds itself smoothly
            public float Size;             // 0..1
            public float Alpha;            // 0..1
            public int Rgb;
            public float Phase;            // the shimmer phase
            public float SX, SY, SR;       // the screen position after projection
            public float Depth;
        }

        List<SetEntry> _sets = new List<SetEntry>();
        Node[] _nodes = new Node[0];

        public readonly Axis X = new Axis();
        public readonly Axis Y = new Axis();
        public readonly Axis Z = new Axis();
        public readonly Axis SizeCh = new Axis();
        public readonly Axis AlphaCh = new Axis();
        public readonly Axis ColorCh = new Axis();

        // The camera. Angles in radians, distance in the same units as the [-1,1] cube.
        float _yaw = 0.72f, _pitch = 0.34f, _zoom = 1f;
        float _panX, _panY;
        int _lastEdgeX = -1, _lastEdgeY = -1, _lastEdgeZ = -1;
        const float CamDist = 3.4f;
        const float TopPitch = (float)(Math.PI / 2); // the tilt limit for both the Top preset and a manual drag — exactly 90 degrees (PI/2), with no skew

        // An orthographic preset switches perspective off: k = 1 always, with no division by
        // depth (see Project) — so that "Front/Side/Top" give a true parallel projection, as in
        // CAD, rather than a perspective view from the same angle.
        bool _ortho;
        public CameraPreset Preset { get; private set; }

        public bool Spin = true;
        float _spinPhase;

        // 0 — a dot is almost a solid disc with a thin antialiased rim; 1 — a soft glow across
        // the whole radius. See Splat().
        float _softness = 0.04f;
        public float Softness
        {
            get { return _softness; }
            set
            {
                float v = value < 0f ? 0f : (value > 1f ? 1f : value);
                if (Math.Abs(v - _softness) < 0.001f) return;
                _softness = v;
                Invalidate();
            }
        }

        // The radius in logical pixels (before Sc()) for a dot with the smallest and with the
        // largest value of the Size channel (a set with no data in that channel sits a quarter
        // of the scale up from the minimum — see Rebuild, the same fallback as always for "no
        // data", only in pixels rather than as a fraction). The values do not recompute the
        // geometry — only the radius Node.Size unfolds into pixels with when drawing
        // (DrawPoints), so moving the slider does not trigger a Rebuild.
        float _minR = 1f, _maxR = 16f;
        public float MinPointRadius
        {
            get { return _minR; }
            set
            {
                float v = value < 0f ? 0f : (value > 60f ? 60f : value);
                if (Math.Abs(v - _minR) < 0.01f) return;
                _minR = v;
                Invalidate();
            }
        }
        public float MaxPointRadius
        {
            get { return _maxR; }
            set
            {
                float v = value < 0f ? 0f : (value > 120f ? 120f : value);
                if (Math.Abs(v - _maxR) < 0.01f) return;
                _maxR = v;
                Invalidate();
            }
        }

        // The same for the Fade channel, only in alpha (0..1) rather than pixels — and here the
        // values ARE used already in Rebuild (not in DrawPoints, as with size): alpha does not
        // depend on DPI, so there is no point deferring it to pixels.
        float _minA = 0.08f, _maxA = 1f;
        public float MinAlpha
        {
            get { return _minA; }
            set
            {
                float v = value < 0f ? 0f : (value > 1f ? 1f : value);
                if (Math.Abs(v - _minA) < 0.001f) return;
                _minA = v;
                Refade();
            }
        }
        public float MaxAlpha
        {
            get { return _maxA; }
            set
            {
                float v = value < 0f ? 0f : (value > 1f ? 1f : value);
                if (Math.Abs(v - _maxA) < 0.001f) return;
                _maxA = v;
                Refade();
            }
        }

        int[] _buf = new int[0];
        Bitmap _bmp;
        int _bw, _bh;

        int _hover = -1, _selected = -1;
        Point _dragFrom;
        bool _dragging, _panning;
        bool _moved;                       // whether it was dragged — so this is not counted as a click

        readonly Timer _timer = new Timer();
        float _settle;                     // how much longer the rebuild has to animate

        public event EventHandler HoverChanged;
        public event EventHandler SelectionChanged;
        public event EventHandler OpenRequested;

        public CloudView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Cross;

            // It opens on the XY view (Front) — flat, with no rotation, so that the first look
            // at the cloud is a clear readable chart rather than a random angle.
            SetPreset(CameraPreset.Front);

            _timer.Interval = 16;
            _timer.Tick += delegate { OnFrame(); };
            _timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                if (_bmp != null) _bmp.Dispose();
            }
            base.Dispose(disposing);
        }

        int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }
        float ScF(float v) { return v * (DeviceDpi / 96f); }

        // ------------------------------------------------------------------- data

        public List<SetEntry> Sets { get { return _sets; } }

        public SetEntry Hovered { get { return At(_hover); } }
        public SetEntry Selected { get { return At(_selected); } }

        SetEntry At(int i) { return i >= 0 && i < _sets.Count ? _sets[i] : null; }

        public void SetData(List<SetEntry> sets)
        {
            SetEntry keep = Selected;
            _sets = sets ?? new List<SetEntry>();
            Rebuild(false);

            _selected = -1;
            if (keep != null)
                for (int i = 0; i < _sets.Count; i++)
                    if (_sets[i].Path == keep.Path) { _selected = i; break; }
            _hover = -1;
            Invalidate();
        }

        public void SetChannels(Metric x, bool xOn, Metric y, bool yOn, Metric z, bool zOn,
                                Metric size, bool sizeOn, Metric alpha, bool alphaOn, Metric color, bool colorOn)
        {
            X.M = x; X.On = xOn;
            Y.M = y; Y.On = yOn;
            Z.M = z; Z.On = zOn;
            SizeCh.M = size; SizeCh.On = sizeOn;
            AlphaCh.M = alpha; AlphaCh.On = alphaOn;
            ColorCh.M = color; ColorCh.On = colorOn;
            Rebuild(true);
            Invalidate();
        }

        /// <summary>
        /// Recomputing all six channels. animate=true — the dots do not jump to their new
        /// places but fly to them over half a second: otherwise changing the value on an axis
        /// looks like the picture being swapped, and the connection between "before" and
        /// "after" is lost.
        /// </summary>
        public void Rebuild(bool animate)
        {
            if (X.M == null) return;

            // A channel that is off does not compute its scale at all — it does not need one,
            // and on categories like Key an extra pass over every set costs noticeable
            // milliseconds.
            if (X.On) X.Fit(_sets, X.M); else X.Clear(X.M);
            if (Y.On) Y.Fit(_sets, Y.M); else Y.Clear(Y.M);
            if (Z.On) Z.Fit(_sets, Z.M); else Z.Clear(Z.M);
            if (SizeCh.On) SizeCh.Fit(_sets, SizeCh.M); else SizeCh.Clear(SizeCh.M);
            if (AlphaCh.On) AlphaCh.Fit(_sets, AlphaCh.M); else AlphaCh.Clear(AlphaCh.M);
            if (ColorCh.On) ColorCh.Fit(_sets, ColorCh.M); else ColorCh.Clear(ColorCh.M);

            Node[] old = _nodes;
            Node[] fresh = new Node[_sets.Count];

            for (int i = 0; i < _sets.Count; i++)
            {
                SetEntry s = _sets[i];
                Node n = new Node();
                n.Index = i;

                double jitter = Metrics.Hash01(s.Path);
                n.TX = Coord(X, s, jitter, 0);
                n.TY = Coord(Y, s, jitter, 1);
                n.TZ = Coord(Z, s, jitter, 2);

                // A size or fade channel that is off gives every dot one and the same look —
                // not "no data" (that is a dull grey somewhere in the middle of the scale) but
                // exactly neutral, because this is not a gap in the data but a decision to
                // remove the channel altogether.
                if (SizeCh.On)
                {
                    double sz = SizeCh.R.Norm(SizeCh.M.Value(s));
                    n.Size = double.IsNaN(sz) ? 0.25f : (float)sz;
                }
                else n.Size = 0.40f;

                n.Alpha = AlphaOf(s);

                n.Rgb = ColorOf(s) & 0xFFFFFF;
                n.Phase = (float)(jitter * Math.PI * 2);

                // A dot that already lived in the cloud starts from its previous place.
                if (animate && i < old.Length && old[i].Index == i)
                {
                    n.X = old[i].X; n.Y = old[i].Y; n.Z = old[i].Z;
                }
                else if (animate)
                {
                    n.X = 0; n.Y = 0; n.Z = 0;
                }
                else
                {
                    n.X = n.TX; n.Y = n.TY; n.Z = n.TZ;
                }
                fresh[i] = n;
            }

            _nodes = fresh;
            if (animate) _settle = 1f;
        }

        /// <summary>A value → a coordinate in the [-1,1] cube. With no data, as on a channel
        /// that is off, it is the centre with a scatter, so that such sets do not stick
        /// together into one dot and it is visible how many of them there are.</summary>
        static float Coord(Axis a, SetEntry s, double jitter, int seed)
        {
            double t = a.On ? a.R.Norm(a.M.Value(s)) : double.NaN;
            if (double.IsNaN(t))
                return (float)((((jitter * 7.13 + seed * 0.37) % 1.0) - 0.5) * 0.12);
            // A small scatter: with whole-number values (24 tracks, 120 BPM) the dots otherwise
            // lie exactly on top of each other and the density does not read.
            double j = (((jitter * 13.7 + seed * 2.31) % 1.0) - 0.5) * 0.022;
            return (float)((t - 0.5) * 2.0 + j);
        }

        // Colour switched off is not "no data" (dull grey) but the same light neutral tone that
        // highlights the rest of the interface: the channel is not keeping quiet about a
        // problem, it has simply been turned off by decision.
        static readonly int OffRgb = Theme.Light.ToArgb();

        int ColorOf(SetEntry s)
        {
            if (!ColorCh.On) return OffRgb;

            Metric m = ColorCh.M;
            double v = m.Value(s);
            Color c;
            if (m.Color == ColorMode.Key) c = Palette.Key(s.ScaleRoot, s.ScaleIndex);
            else if (m.Color == ColorMode.Classes) c = double.IsNaN(v) ? Color.FromArgb(0x7E, 0x7E, 0x88) : Palette.Class((int)v);
            else c = Palette.Sample(_gradient, ColorCh.R.Norm(v));
            return c.ToArgb();
        }

        Gradient _gradient = Palette.Gradients[0];

        /// <summary>Which of the named LAB gradients colours continuous values (BPM, Modified,
        /// Project size, …). Has no effect on categories (Key/Scale/…).</summary>
        public Gradient ColorGradient
        {
            get { return _gradient; }
            set
            {
                if (value == null || value == _gradient) return;
                _gradient = value;
                Recolor();
            }
        }

        /// <summary>Recolour the cloud without rebuilding the geometry: changing the palette
        /// touches neither the coordinates nor the size — only Rgb — and must not trigger the
        /// fly-out animation a full Rebuild does.</summary>
        void Recolor()
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                SetEntry s = _sets[_nodes[i].Index];
                _nodes[i].Rgb = ColorOf(s) & 0xFFFFFF;
            }
            Invalidate();
        }

        /// <summary>A fade channel that is off is not "no data" (dull) but exactly as opaque a
        /// dot as at the brightest end of a channel that is on.</summary>
        float AlphaOf(SetEntry s)
        {
            if (!AlphaCh.On) return 0.82f;
            double al = AlphaCh.R.Norm(AlphaCh.M.Value(s));
            return double.IsNaN(al) ? 0.30f : (float)(_minA + (_maxA - _minA) * al);
        }

        /// <summary>Like Recolor(), only for alpha: moving the MinAlpha/MaxAlpha sliders
        /// touches neither the coordinates nor the colour, and must not trigger the fly-out
        /// animation.</summary>
        void Refade()
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                SetEntry s = _sets[_nodes[i].Index];
                _nodes[i].Alpha = AlphaOf(s);
            }
            Invalidate();
        }

        // ------------------------------------------------------------------- camera

        public void ResetView()
        {
            SetPreset(CameraPreset.Front);
        }

        /// <summary>
        /// A free angle (perspective) or one of the three orthographic views onto the cube —
        /// square on the axes, with no perspective convergence. Zoom and pan are reset too: a
        /// preset means "show me exactly like this" rather than "turn what has already been
        /// shifted".
        /// </summary>
        public void SetPreset(CameraPreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case CameraPreset.Front: _yaw = 0f; _pitch = 0f; _ortho = true; break;
                case CameraPreset.Side: _yaw = (float)(Math.PI / 2); _pitch = 0f; _ortho = true; break;
                case CameraPreset.Top: _yaw = 0f; _pitch = TopPitch; _ortho = true; break;
                default: _yaw = 0.72f; _pitch = 0.34f; _ortho = false; break;
            }
            _zoom = 1f; _panX = _panY = 0;
            _lastEdgeX = _lastEdgeY = _lastEdgeZ = -1;
            Invalidate();
        }

        void OnFrame()
        {
            bool need = false;

            if (_settle > 0.001f)
            {
                _settle *= 0.88f;
                for (int i = 0; i < _nodes.Length; i++)
                {
                    _nodes[i].X += (_nodes[i].TX - _nodes[i].X) * 0.14f;
                    _nodes[i].Y += (_nodes[i].TY - _nodes[i].Y) * 0.14f;
                    _nodes[i].Z += (_nodes[i].TZ - _nodes[i].Z) * 0.14f;
                }
                if (_settle <= 0.001f)
                    for (int i = 0; i < _nodes.Length; i++)
                    {
                        _nodes[i].X = _nodes[i].TX; _nodes[i].Y = _nodes[i].TY; _nodes[i].Z = _nodes[i].TZ;
                    }
                need = true;
            }

            if (Spin && !_dragging)
            {
                _yaw += 0.0016f;
                need = true;
            }

            // The shimmer runs always: a cloud of dead dots looks like a picture rather than a
            // living scene. The step is small, so the frame is cheap.
            _spinPhase += 0.028f;
            if (_spinPhase > 1000f) _spinPhase -= 1000f;
            need = true;

            if (need && Visible && Width > 0 && Height > 0) Invalidate();
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, Theme.Backdrop);
            Theme.Smooth(g);

            int w = Width, h = Height;
            if (w < 8 || h < 8) return;

            float scale = Math.Min(w, h) * 0.33f * _zoom;
            float cx = w / 2f + _panX, cy = h / 2f + _panY;
            float cosY = (float)Math.Cos(_yaw), sinY = (float)Math.Sin(_yaw);
            float cosP = (float)Math.Cos(_pitch), sinP = (float)Math.Sin(_pitch);

            DrawFrame(g, cx, cy, scale, cosY, sinY, cosP, sinP);
            DrawPoints(g, w, h, cx, cy, scale, cosY, sinY, cosP, sinP);
            DrawMarks(g);
            DrawAxisLabels(g, cx, cy, scale, cosY, sinY, cosP, sinP);
        }

        void Project(float x, float y, float z, float cx, float cy, float scale,
                     float cosY, float sinY, float cosP, float sinP,
                     out float sx, out float sy, out float depth)
        {
            float rx = x * cosY + z * sinY;
            float rz = -x * sinY + z * cosY;
            float ry = y * cosP - rz * sinP;
            float rz2 = y * sinP + rz * cosP;

            // An orthographic preset removes the division by depth entirely — the dots do not
            // converge towards the centre with distance, as in perspective, but run parallel.
            // That is also the same k that gives a dot its radius and haze (DrawPoints), so in
            // orthographic mode a dot's size and brightness stop depending on depth too — which
            // is how a true parallel projection should be.
            float k = _ortho ? 1f : CamDist / (CamDist + rz2);
            sx = cx + rx * scale * k;
            sy = cy - ry * scale * k;
            depth = k;
        }

        /// <summary>The floor and edges of the cube — only so that rotation reads. The lines
        /// are almost invisible on purpose: the scene is about the dots, not about the
        /// grid.</summary>
        void DrawFrame(Graphics g, float cx, float cy, float scale,
                       float cosY, float sinY, float cosP, float sinP)
        {
            using (Pen floorPen = new Pen(Color.FromArgb(14, 255, 255, 255), 1f))
            using (Pen edgePen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
            {
                for (int i = 0; i <= 8; i++)
                {
                    float t = -1f + i * 0.25f;
                    Line(g, floorPen, t, -1, -1, t, -1, 1, cx, cy, scale, cosY, sinY, cosP, sinP);
                    Line(g, floorPen, -1, -1, t, 1, -1, t, cx, cy, scale, cosY, sinY, cosP, sinP);
                }

                for (int i = 0; i < 4; i++)
                {
                    float a = (i & 1) == 0 ? -1 : 1;
                    float b = (i & 2) == 0 ? -1 : 1;
                    Line(g, edgePen, -1, a, b, 1, a, b, cx, cy, scale, cosY, sinY, cosP, sinP);
                    Line(g, edgePen, a, -1, b, a, 1, b, cx, cy, scale, cosY, sinY, cosP, sinP);
                    Line(g, edgePen, a, b, -1, a, b, 1, cx, cy, scale, cosY, sinY, cosP, sinP);
                }
            }
        }

        void Line(Graphics g, Pen p, float x1, float y1, float z1, float x2, float y2, float z2,
                  float cx, float cy, float scale, float cosY, float sinY, float cosP, float sinP)
        {
            float ax, ay, ad, bx, by, bd;
            Project(x1, y1, z1, cx, cy, scale, cosY, sinY, cosP, sinP, out ax, out ay, out ad);
            Project(x2, y2, z2, cx, cy, scale, cosY, sinY, cosP, sinP, out bx, out by, out bd);
            g.DrawLine(p, ax, ay, bx, by);
        }

        void DrawPoints(Graphics g, int w, int h, float cx, float cy, float scale,
                        float cosY, float sinY, float cosP, float sinP)
        {
            EnsureBuffer(w, h);
            Array.Clear(_buf, 0, _buf.Length);

            float baseR = ScF(_minR);
            float sizeR = ScF(Math.Max(0f, _maxR - _minR));
            // Ordinary perspective (not ortho) increases k for dots closer to the camera —
            // without headroom a dot at the maximum slider setting would also swell past that
            // very maximum when in the foreground.
            float maxR = ScF(Math.Max(_maxR * 2.2f, 34f));

            for (int i = 0; i < _nodes.Length; i++)
            {
                float sx, sy, k;
                Project(_nodes[i].X, _nodes[i].Y, _nodes[i].Z, cx, cy, scale, cosY, sinY, cosP, sinP,
                        out sx, out sy, out k);

                float r = (baseR + sizeR * _nodes[i].Size) * k;
                if (r > maxR) r = maxR;

                // Distant dots are dimmer — without this the cube looks like a flat blob.
                float fog = 0.42f + 0.58f * Math.Min(1f, Math.Max(0f, (k - 0.6f) / 0.75f));
                float twinkle = 0.90f + 0.10f * (float)Math.Sin(_spinPhase + _nodes[i].Phase);
                float a = _nodes[i].Alpha * fog * twinkle;

                _nodes[i].SX = sx; _nodes[i].SY = sy; _nodes[i].SR = r; _nodes[i].Depth = k;

                if (i == _hover || i == _selected) a = Math.Min(1f, a * 1.6f + 0.25f);
                Splat(w, h, sx, sy, r, _nodes[i].Rgb, a);
            }

            Blit(g, w, h);
        }

        void EnsureBuffer(int w, int h)
        {
            if (_bw == w && _bh == h && _bmp != null) return;
            _bw = w; _bh = h;
            _buf = new int[w * h];
            if (_bmp != null) _bmp.Dispose();
            _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        /// <summary>
        /// One glowing dot. A (1-t²)² falloff gives a soft edge without a single GDI+ call,
        /// while saturating addition gives light: where dots have overlapped it is brighter.
        /// The colour is already premultiplied by alpha, so the buffer goes onto the window as
        /// it is.
        /// </summary>
        void Splat(int w, int h, float cx, float cy, float r, int rgb, float alpha)
        {
            if (alpha <= 0.004f || r < 0.4f) return;
            if (cx < -r || cy < -r || cx > w + r || cy > h + r) return;

            int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r);
            int y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
            if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
            if (x1 >= w) x1 = w - 1; if (y1 >= h) y1 = h - 1;

            float inv = 1f / (r * r);
            int cr = (rgb >> 16) & 255, cg = (rgb >> 8) & 255, cb = rgb & 255;

            // The core is a solid fill, the halo a smooth falloff to zero at the edge. Where
            // the core ends and the falloff begins is decided by Softness: 0 leaves almost no
            // halo (a disc with a thin antialiased rim), 1 gives almost the whole radius over
            // to the falloff (a soft glow, as the default used to be). core is the fraction of
            // the radius where the dot is still solid.
            float core = 1f - (0.06f + 0.79f * _softness);
            float fadeSpan = Math.Max(0.0001f, 1f - core);

            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5f - cy;
                float dy2 = dy * dy;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    float t = (dx * dx + dy2) * inv;
                    if (t >= 1f) continue;

                    float d = (float)Math.Sqrt(t);
                    float k = d <= core ? 1f : (1f - d) / fadeSpan;
                    if (k <= 0f) continue;
                    int ai = (int)(alpha * k * k * 255f);
                    if (ai <= 0) continue;

                    int i = row + x;
                    int px = _buf[i];
                    int pa = (px >> 24) & 255, pr = (px >> 16) & 255, pg = (px >> 8) & 255, pb = px & 255;

                    pa += ai; if (pa > 255) pa = 255;
                    pr += cr * ai / 255; if (pr > 255) pr = 255;
                    pg += cg * ai / 255; if (pg > 255) pg = 255;
                    pb += cb * ai / 255; if (pb > 255) pb = 255;

                    _buf[i] = (pa << 24) | (pr << 16) | (pg << 8) | pb;
                }
            }
        }

        void Blit(Graphics g, int w, int h)
        {
            BitmapData d = _bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
                                         PixelFormat.Format32bppPArgb);
            try
            {
                if (d.Stride == w * 4)
                {
                    Marshal.Copy(_buf, 0, d.Scan0, w * h);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(_buf, y * w, (IntPtr)(d.Scan0.ToInt64() + (long)y * d.Stride), w);
                }
            }
            finally { _bmp.UnlockBits(d); }

            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(_bmp, 0, 0);
            g.InterpolationMode = old;
        }

        /// <summary>A ring and a name on the dot under the cursor and on the selected
        /// one.</summary>
        void DrawMarks(Graphics g)
        {
            if (_selected >= 0 && _selected < _nodes.Length && _selected != _hover)
                Ring(g, _nodes[_selected], Color.FromArgb(150, 255, 255, 255), false);

            if (_hover >= 0 && _hover < _nodes.Length)
            {
                Ring(g, _nodes[_hover], Color.FromArgb(230, 255, 255, 255), true);

                SetEntry s = _sets[_nodes[_hover].Index];
                float r = Math.Max(_nodes[_hover].SR, ScF(6f));
                int tx = (int)(_nodes[_hover].SX + r + Sc(10));
                int ty = (int)(_nodes[_hover].SY - Sc(9));
                Size sz = TextRenderer.MeasureText(s.Name, Theme.FTitle);
                if (tx + sz.Width > Width - Sc(8)) tx = (int)(_nodes[_hover].SX - r - Sc(10)) - sz.Width;

                Chrome.DrawText(g, s.Name, Theme.FTitle,
                                new Rectangle(tx, ty, sz.Width + Sc(4), Sc(20)), Theme.Text, Chrome.Left);
            }
        }

        void Ring(Graphics g, Node n, Color color, bool thick)
        {
            float r = Math.Max(n.SR, ScF(5f)) + ScF(5f);
            using (Pen p = new Pen(color, thick ? ScF(1.6f) : ScF(1.1f)))
                g.DrawEllipse(p, n.SX - r, n.SY - r, r * 2, r * 2);
        }

        /// <summary>
        /// The axis labels. The edge to label is chosen by the picture rather than by number:
        /// of the four parallel ones we take the one whose midpoint is furthest from the centre
        /// of the scene — it is always outside the cloud and the label does not drown among the
        /// dots.
        /// </summary>
        void DrawAxisLabels(Graphics g, float cx, float cy, float scale,
                            float cosY, float sinY, float cosP, float sinP)
        {
            DrawAxis(g, X, 0, cx, cy, scale, cosY, sinY, cosP, sinP);
            DrawAxis(g, Y, 1, cx, cy, scale, cosY, sinY, cosP, sinP);
            DrawAxis(g, Z, 2, cx, cy, scale, cosY, sinY, cosP, sinP);
        }

        void DrawAxis(Graphics g, Axis a, int dim, float cx, float cy, float scale,
                      float cosY, float sinY, float cosP, float sinP)
        {
            if (a.M == null) return;

            // For X (dim == 0) and Z (dim == 2) the axes are always tied to the floor of the
            // cube (y = -1), so that the labels of the horizontal scales are always below the
            // cube and never climb onto the ceiling. For Y (dim == 1) one of the 4 vertical
            // pillars is chosen (the leftmost).
            const float yFloor = -1f;

            int count = dim == 1 ? 4 : 2;
            int bestIdx = 0;
            float bestScore = -float.MaxValue;

            float bestAx = 0, bestAy = 0, bestBx = 0, bestBy = 0, bestLen = 0;
            float lastAx = 0, lastAy = 0, lastBx = 0, lastBy = 0, lastLen = 0;
            float lastScore = -float.MaxValue;

            int lastIdx = dim == 0 ? _lastEdgeX : (dim == 1 ? _lastEdgeY : _lastEdgeZ);

            for (int i = 0; i < count; i++)
            {
                float x1, y1, z1, x2, y2, z2;
                GetCandidate(dim, i, yFloor, out x1, out y1, out z1, out x2, out y2, out z2);

                float p1x, p1y, d1, p2x, p2y, d2;
                Project(x1, y1, z1, cx, cy, scale, cosY, sinY, cosP, sinP, out p1x, out p1y, out d1);
                Project(x2, y2, z2, cx, cy, scale, cosY, sinY, cosP, sinP, out p2x, out p2y, out d2);

                float dx = p2x - p1x, dy = p2y - p1y;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 1f) continue;

                float mx = (p1x + p2x) * 0.5f - cx;
                float my = (p1y + p2y) * 0.5f - cy;

                // For horizontal edges it matters more to be at the bottom of the screen (+my).
                // For vertical edges it matters more to be at the left of the screen (-mx). A
                // smooth continuous score with no abrupt jumps:
                float score = (Math.Abs(dx) * my - Math.Abs(dy) * mx) / len;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                    bestAx = p1x; bestAy = p1y; bestBx = p2x; bestBy = p2y; bestLen = len;
                }

                if (i == lastIdx)
                {
                    lastScore = score;
                    lastAx = p1x; lastAy = p1y; lastBx = p2x; lastBy = p2y; lastLen = len;
                }
            }

            if (bestScore <= -float.MaxValue / 2f) return;

            // Hysteresis: we do not switch edge until a new candidate beats the current one by
            // a confident margin (16 px). That rules out chatter and flicker while spinning
            // completely.
            float ax, ay, bx, by, lenChosen;
            int chosenIdx;

            if (lastIdx >= 0 && lastLen >= 1f && bestScore <= lastScore + ScF(16f))
            {
                chosenIdx = lastIdx;
                ax = lastAx; ay = lastAy; bx = lastBx; by = lastBy; lenChosen = lastLen;
            }
            else
            {
                chosenIdx = bestIdx;
                ax = bestAx; ay = bestAy; bx = bestBx; by = bestBy; lenChosen = bestLen;
            }

            if (dim == 0) _lastEdgeX = chosenIdx;
            else if (dim == 1) _lastEdgeY = chosenIdx;
            else _lastEdgeZ = chosenIdx;

            float nx = (bx - ax) / lenChosen, ny = (by - ay) / lenChosen;

            // The outward normal vector of the edge (away from the centre of the scene): for
            // the bottom axis px = 0, py = 1 (straight down) -> labels below the axis. For the
            // left axis px = -1, py = 0 (straight left) -> labels to the left of the axis.
            float mx2 = (ax + bx) / 2f, my2 = (ay + by) / 2f;
            float px = -ny, py = nx;
            if ((mx2 - cx) * px + (my2 - cy) * py < 0) { px = -px; py = -py; }

            string title = (dim == 0 ? "X · " : dim == 1 ? "Y · " : "Z · ") + (a.On ? a.M.Title : "Off");
            Size szTitle = TextRenderer.MeasureText(title, Theme.FLabel);
            float gap = ScF(6f);

            float cxTi = mx2 + px * (gap + HalfSpan(szTitle, px, py));
            float cyTi = my2 + py * (gap + HalfSpan(szTitle, px, py));

            if (a.On)
            {
                Size szLo = TextRenderer.MeasureText(a.LoText, Theme.FBadge);
                Size szHi = TextRenderer.MeasureText(a.HiText, Theme.FBadge);

                // LoText at point a (the minimum): we push it outward along the normal p by
                // (gap + Rp) and shift it inward along the edge by Rn, so the label starts at
                // the corner rather than flying off outside
                float cxLo = ax + px * (gap + HalfSpan(szLo, px, py)) + nx * HalfSpan(szLo, nx, ny);
                float cyLo = ay + py * (gap + HalfSpan(szLo, px, py)) + ny * HalfSpan(szLo, nx, ny);

                // HiText at point b (the maximum): we push it outward along the normal p by
                // (gap + Rp) and shift it inward along the edge by -Rn, so the label ends at
                // the corner
                float cxHi = bx + px * (gap + HalfSpan(szHi, px, py)) - nx * HalfSpan(szHi, nx, ny);
                float cyHi = by + py * (gap + HalfSpan(szHi, px, py)) - ny * HalfSpan(szHi, nx, ny);

                // If the edge is predominantly horizontal (bottom/top), we check whether the
                // Title runs into LoText or HiText:
                if (Math.Abs(px) < 0.5f)
                {
                    float leftLo = cxLo - szLo.Width / 2f, rightLo = cxLo + szLo.Width / 2f;
                    float leftHi = cxHi - szHi.Width / 2f, rightHi = cxHi + szHi.Width / 2f;
                    float minEdgeX = Math.Min(rightLo, rightHi), maxEdgeX = Math.Max(leftLo, leftHi);
                    float leftTi = cxTi - szTitle.Width / 2f, rightTi = cxTi + szTitle.Width / 2f;

                    if (leftTi < minEdgeX + ScF(8f) || rightTi > maxEdgeX - ScF(8f))
                        cyTi += py * (szTitle.Height + ScF(2f));
                }
                // Likewise for a predominantly vertical edge (left/right):
                else if (Math.Abs(py) < 0.5f)
                {
                    float topLo = cyLo - szLo.Height / 2f, bottomLo = cyLo + szLo.Height / 2f;
                    float topHi = cyHi - szHi.Height / 2f, bottomHi = cyHi + szHi.Height / 2f;
                    float minEdgeY = Math.Min(bottomLo, bottomHi), maxEdgeY = Math.Max(topLo, topHi);
                    float topTi = cyTi - szTitle.Height / 2f, bottomTi = cyTi + szTitle.Height / 2f;

                    if (topTi < minEdgeY + ScF(8f) || bottomTi > maxEdgeY - ScF(8f))
                        cxTi += px * (szTitle.Width + ScF(4f));
                }

                Label(g, a.LoText, Theme.FBadge, Theme.TextDim, cxLo, cyLo);
                Label(g, a.HiText, Theme.FBadge, Theme.TextDim, cxHi, cyHi);
            }

            Label(g, title, Theme.FLabel, a.On ? Theme.Text : Theme.TextDim, cxTi, cyTi);
        }

        static float HalfSpan(Size sz, float vx, float vy)
        {
            return (sz.Width * Math.Abs(vx) + sz.Height * Math.Abs(vy)) * 0.5f;
        }

        static void GetCandidate(int dim, int i, float yFloor,
                                 out float x1, out float y1, out float z1,
                                 out float x2, out float y2, out float z2)
        {
            if (dim == 0) // X
            {
                float z = i == 0 ? -1f : 1f;
                x1 = -1f; x2 = 1f; y1 = y2 = yFloor; z1 = z2 = z;
            }
            else if (dim == 2) // Z
            {
                float x = i == 0 ? -1f : 1f;
                x1 = x2 = x; y1 = y2 = yFloor; z1 = -1f; z2 = 1f;
            }
            else // Y (the pillars)
            {
                float x = (i == 1 || i == 2) ? 1f : -1f;
                float z = (i == 2 || i == 3) ? 1f : -1f;
                x1 = x2 = x; y1 = -1f; y2 = 1f; z1 = z2 = z;
            }
        }

        /// <summary>A caption centred on a dot, squeezed within the cloud's bounds: at the
        /// edges of the scene the label otherwise slides under the panel or off the
        /// window.</summary>
        void Label(Graphics g, string text, Font font, Color color, float x, float y)
        {
            if (string.IsNullOrEmpty(text)) return;
            Size sz = TextRenderer.MeasureText(text, font);
            int left = (int)(x - sz.Width / 2f), top = (int)(y - sz.Height / 2f);
            if (left < Sc(2)) left = Sc(2);
            if (top < Sc(2)) top = Sc(2);
            if (left + sz.Width > Width - Sc(2)) left = Width - Sc(2) - sz.Width;
            if (top + sz.Height > Height - Sc(2)) top = Height - Sc(2) - sz.Height;
            Chrome.DrawText(g, text, font, new Rectangle(left, top, sz.Width + Sc(2), sz.Height),
                            color, Chrome.Left);
        }

        // --------------------------------------------------------- mouse and keys

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            _dragFrom = e.Location;
            _moved = false;
            if (e.Button == MouseButtons.Left) _dragging = true;
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) _panning = true;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging || _panning)
            {
                int dx = e.X - _dragFrom.X, dy = e.Y - _dragFrom.Y;
                if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _moved = true;
                _dragFrom = e.Location;

                if (_dragging)
                {
                    // Inverted: drag to the right and the scene turns to the left, as though
                    // you were grabbing the object itself rather than swinging the camera
                    // around it.
                    _yaw -= dx * 0.0075f;
                    _pitch -= dy * 0.0075f;
                    if (_pitch > TopPitch) _pitch = TopPitch;
                    if (_pitch < -TopPitch) _pitch = -TopPitch;
                }
                else
                {
                    _panX += dx; _panY += dy;
                }
                Invalidate();
                base.OnMouseMove(e);
                return;
            }

            int hit = Pick(e.X, e.Y);
            if (hit != _hover)
            {
                _hover = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Cross;
                if (HoverChanged != null) HoverChanged(this, EventArgs.Empty);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !_moved)
            {
                int hit = Pick(e.X, e.Y);
                if (hit != _selected)
                {
                    _selected = hit;
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                    Invalidate();
                }
            }
            _dragging = _panning = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _selected >= 0 && OpenRequested != null)
                OpenRequested(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover >= 0)
            {
                _hover = -1;
                if (HoverChanged != null) HoverChanged(this, EventArgs.Empty);
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float step = e.Delta > 0 ? 1.12f : 1f / 1.12f;
            _zoom *= step;
            if (_zoom < 0.35f) _zoom = 0.35f;
            if (_zoom > 6f) _zoom = 6f;
            Invalidate();
            base.OnMouseWheel(e);
        }

        /// <summary>The nearest dot under the cursor. Of overlapping ones the closer to the
        /// camera wins — aiming at the topmost is the logical thing.</summary>
        int Pick(int mx, int my)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            float reach = ScF(9f);

            for (int i = 0; i < _nodes.Length; i++)
            {
                float dx = _nodes[i].SX - mx, dy = _nodes[i].SY - my;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                float r = Math.Max(_nodes[i].SR, reach);
                if (d > r) continue;

                float score = d / r - _nodes[i].Depth * 0.35f;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        public void Select(SetEntry s)
        {
            int i = -1;
            if (s != null)
                for (int k = 0; k < _sets.Count; k++) if (_sets[k] == s) { i = k; break; }
            if (i == _selected) return;
            _selected = i;
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            Invalidate();
        }

        protected override bool IsInputKey(Keys key) { return true; }
    }
}
