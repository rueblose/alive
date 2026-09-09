using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager.Nebula
{
    /// <summary>Ось облака: величина, её шкала, подписи краёв — и включена ли она вообще.</summary>
    public sealed class Axis
    {
        public Metric M;

        /// <summary>Выключенный канал не участвует в облаке (см. CloudView.Coord/ColorOf),
        /// но помнит свою величину: включили обратно — она уже выбрана.</summary>
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

        /// <summary>Канал выключен — шкала не считается, а край не подписан.</summary>
        public void Clear(Metric m)
        {
            M = m;
            R = new Range();
            LoText = HiText = "";
        }
    }

    /// <summary>Готовые положения камеры — свободный угол по умолчанию и три
    /// ортографических вида на куб, как в CAD.</summary>
    public enum CameraPreset { Angle, Front, Side, Top }

    /// <summary>
    /// Само облако: шесть величин сета превращаются в три координаты, размер, плотность
    /// и цвет точки.
    ///
    /// Рисуется не средствами GDI+, а своим буфером пикселей. Причина простая: у каждой
    /// точки мягкое свечение, а PathGradientBrush на тысяче точек в кадре — это тысяча
    /// кистей на кадр и десятки миллисекунд. Свой буфер складывает свечения сложением
    /// (как свет и складывается), лежит в предумноженной альфе — ровно в том формате,
    /// который GDI+ кладёт на стекло окна одним DrawImage без пересчёта.
    /// </summary>
    public sealed class CloudView : Control
    {
        struct Node
        {
            public int Index;              // строка в _sets
            public float TX, TY, TZ;       // куда точка едет
            public float X, Y, Z;          // где сейчас — облако перестраивается плавно
            public float Size;             // 0..1
            public float Alpha;            // 0..1
            public int Rgb;
            public float Phase;            // фаза мерцания
            public float SX, SY, SR;       // экран после проекции
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

        // Камера. Углы в радианах, дистанция в тех же единицах, что и куб [-1,1].
        float _yaw = 0.72f, _pitch = 0.34f, _zoom = 1f;
        float _panX, _panY;
        int _lastEdgeX = -1, _lastEdgeY = -1, _lastEdgeZ = -1;
        const float CamDist = 3.4f;
        const float TopPitch = (float)(Math.PI / 2); // предел наклона и у Top-пресета, и у ручного тяни — ровно 90 градусов (PI/2) без перекоса

        // Ортографический пресет отключает перспективу: k = 1 всегда, без деления по
        // глубине (см. Project) — так «Front/Side/Top» дают настоящую параллельную
        // проекцию, как в CAD, а не перспективный вид под тем же углом.
        bool _ortho;
        public CameraPreset Preset { get; private set; }

        public bool Spin = true;
        float _spinPhase;

        // 0 — точка почти сплошной диск с тонкой сглаженной кромкой; 1 — мягкое
        // свечение во весь радиус. См. Splat().
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

        // Радиус в логических пикселях (до Sc()) у точки с наименьшим и с наибольшим
        // значением канала Size (сет без данных в этом канале садится на четверть шкалы
        // вверх от минимума — см. Rebuild, тот же запасной вариант, что и всегда у «нет
        // данных», просто в пикселях, а не в доле). Значения не пересчитывают геометрию —
        // только то, каким радиусом Node.Size разворачивается в пиксели при отрисовке
        // (DrawPoints), поэтому смена ползунка не запускает Rebuild.
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

        // То же самое для канала Fade, только в альфе (0..1), а не в пикселях — и здесь
        // значения ЗАДЕЙСТВОВАНЫ уже в Rebuild (не в DrawPoints, как у размера): альфа не
        // зависит от DPI, откладывать её в пиксели незачем.
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
        bool _moved;                       // тащили ли — чтобы не считать это кликом

        readonly Timer _timer = new Timer();
        float _settle;                     // сколько ещё анимировать перестроение

        public event EventHandler HoverChanged;
        public event EventHandler SelectionChanged;
        public event EventHandler OpenRequested;

        public CloudView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Cross;

            // Открывается на виде XY (Front) — плоско, без вращения, чтобы первый
            // взгляд на облако был понятным читаемым графиком, а не случайным углом.
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

        // ------------------------------------------------------------------- данные

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
        /// Пересчёт всех шести каналов. animate=true — точки не прыгают на новые места,
        /// а разлетаются по ним за полсекунды: иначе смена величины на оси выглядит как
        /// подмена картинки, и связь между «было» и «стало» теряется.
        /// </summary>
        public void Rebuild(bool animate)
        {
            if (X.M == null) return;

            // Выключенный канал шкалу не считает вовсе — она ему не нужна, а на
            // категориях вроде Key лишний проход по всем сетам стоит заметных мс.
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

                // Выключенный канал размера/прозрачности даёт всем точкам один и тот же
                // вид — не «нет данных» (это тусклый серый где-то посередине шкалы), а
                // ровно нейтральный, потому что тут не пропуск в данных, а решение убрать
                // канал целиком.
                if (SizeCh.On)
                {
                    double sz = SizeCh.R.Norm(SizeCh.M.Value(s));
                    n.Size = double.IsNaN(sz) ? 0.25f : (float)sz;
                }
                else n.Size = 0.40f;

                n.Alpha = AlphaOf(s);

                n.Rgb = ColorOf(s) & 0xFFFFFF;
                n.Phase = (float)(jitter * Math.PI * 2);

                // Точка, уже жившая в облаке, стартует с прежнего места.
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

        /// <summary>Величина → координата в кубе [-1,1]. Без данных, как и на выключенном
        /// канале, — центр со сдвигом, чтобы такие сеты не слипались в одну точку и было
        /// видно, сколько их.</summary>
        static float Coord(Axis a, SetEntry s, double jitter, int seed)
        {
            double t = a.On ? a.R.Norm(a.M.Value(s)) : double.NaN;
            if (double.IsNaN(t))
                return (float)((((jitter * 7.13 + seed * 0.37) % 1.0) - 0.5) * 0.12);
            // Небольшой разброс: у целых величин (24 дорожки, 120 BPM) точки иначе
            // ложатся ровно друг на друга и плотность не читается.
            double j = (((jitter * 13.7 + seed * 2.31) % 1.0) - 0.5) * 0.022;
            return (float)((t - 0.5) * 2.0 + j);
        }

        // Выключенный цвет — не «нет данных» (тускло-серый), а тот же светлый нейтральный
        // тон, что даёт подсветку остальным элементам интерфейса: канал не молчит о
        // проблеме, он просто отключён по решению.
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

        /// <summary>Какой из именованных LAB-градиентов красит непрерывные величины
        /// (BPM, Modified, Project size, …). На категориях (Key/Scale/…) не влияет.</summary>
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

        /// <summary>Перекрасить облако без пересборки геометрии: смена палитры не
        /// трогает ни координаты, ни размер — только Rgb, и не должна дёргать анимацию
        /// разлёта точек, которую делает полный Rebuild.</summary>
        void Recolor()
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                SetEntry s = _sets[_nodes[i].Index];
                _nodes[i].Rgb = ColorOf(s) & 0xFFFFFF;
            }
            Invalidate();
        }

        /// <summary>Выключенный канал прозрачности — не «нет данных» (тускло), а ровно
        /// такая же непрозрачная точка, как у самого яркого конца включённой шкалы.</summary>
        float AlphaOf(SetEntry s)
        {
            if (!AlphaCh.On) return 0.82f;
            double al = AlphaCh.R.Norm(AlphaCh.M.Value(s));
            return double.IsNaN(al) ? 0.30f : (float)(_minA + (_maxA - _minA) * al);
        }

        /// <summary>Как Recolor(), только для альфы: сдвиг ползунков MinAlpha/MaxAlpha не
        /// трогает ни координаты, ни цвет, и не должен дёргать анимацию разлёта точек.</summary>
        void Refade()
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                SetEntry s = _sets[_nodes[i].Index];
                _nodes[i].Alpha = AlphaOf(s);
            }
            Invalidate();
        }

        // ------------------------------------------------------------------- камера

        public void ResetView()
        {
            SetPreset(CameraPreset.Front);
        }

        /// <summary>
        /// Угол свободный (перспектива) или один из трёх ортографических видов на куб —
        /// ровно по осям, без перспективного схождения. Zoom и pan тоже сбрасываются:
        /// пресет — это «покажи мне ровно вот так», а не «поверни то, что уже сдвинуто».
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

            // Мерцание идёт всегда: облако из мёртвых точек выглядит картинкой, а не
            // живой сценой. Шаг мелкий, поэтому кадр дешёвый.
            _spinPhase += 0.028f;
            if (_spinPhase > 1000f) _spinPhase -= 1000f;
            need = true;

            if (need && Visible && Width > 0 && Height > 0) Invalidate();
        }

        // ------------------------------------------------------------------ отрисовка

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

            // Ортографический пресет убирает деление по глубине целиком — точки не
            // сходятся к центру с удалением, как в перспективе, а идут параллельно.
            // Заодно это тот же k, что даёт радиус и туман точке (DrawPoints), поэтому
            // на ортографике размер и яркость точки тоже перестают зависеть от глубины —
            // так и должно быть у настоящей параллельной проекции.
            float k = _ortho ? 1f : CamDist / (CamDist + rz2);
            sx = cx + rx * scale * k;
            sy = cy - ry * scale * k;
            depth = k;
        }

        /// <summary>Пол и рёбра куба — только чтобы вращение читалось. Линии почти
        /// невидимы намеренно: сцена про точки, а не про сетку.</summary>
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
            // Обычная перспектива (не орто) увеличивает k у точек ближе камеры — без
            // запаса точка на максимальном ползунке ещё и на переднем плане раздулась
            // бы за пределы им же заданного максимума.
            float maxR = ScF(Math.Max(_maxR * 2.2f, 34f));

            for (int i = 0; i < _nodes.Length; i++)
            {
                float sx, sy, k;
                Project(_nodes[i].X, _nodes[i].Y, _nodes[i].Z, cx, cy, scale, cosY, sinY, cosP, sinP,
                        out sx, out sy, out k);

                float r = (baseR + sizeR * _nodes[i].Size) * k;
                if (r > maxR) r = maxR;

                // Дальние точки тусклее — без этого куб выглядит плоским пятном.
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
        /// Одна светящаяся точка. Спад (1-t²)² даёт мягкий край без единого вызова
        /// GDI+, а сложение с насыщением — свет: там, где точки наложились, ярче.
        /// Цвет уже предумножен на альфу, поэтому буфер кладётся на окно как есть.
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

            // Ядро — сплошная заливка, ореол — плавный спад до нуля на краю. Где кончается
            // ядро и начинается спад, решает Softness: 0 почти не оставляет ореола (диск с
            // тонкой сглаженной кромкой), 1 отдаёт под спад почти весь радиус (мягкое
            // свечение, как раньше по умолчанию). core — доля радиуса, где точка ещё сплошная.
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

        /// <summary>Кольцо и имя у точки под курсором и у выбранной.</summary>
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
        /// Подписи осей. Ребро для подписи выбирается по картинке, а не по номеру:
        /// из четырёх параллельных берём то, чья середина дальше от центра сцены, —
        /// оно всегда снаружи облака и подпись не тонет в точках.
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

            // Для X (dim == 0) и Z (dim == 2) оси всегда привязаны к полу куба (y = -1),
            // чтобы подписи горизонтальных шкал всегда были снизу куба и никогда не лезли на потолок.
            // Для Y (dim == 1) выбирается один из 4 вертикальных столбов (крайний левый).
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

                // Для горизонтальных ребер важнее быть снизу экрана (+my).
                // Для вертикальных ребер важнее быть слева экрана (-mx).
                // Плавная непрерывная оценка без резких скачков:
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

            // Гистерезис: не переключаем ребро, пока новый кандидат не опередит текущий
            // с уверенным отрывом (16 px). Это на 100% исключает дребезг и мелькание при вращении (spin).
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

            // Вектор внешней нормали к ребру (в сторону от центра сцены):
            // Для нижней оси: px = 0, py = 1 (строго вниз) -> подписи снизу оси.
            // Для левой оси: px = -1, py = 0 (строго влево) -> подписи слева от оси.
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

                // LoText у точки a (минимум): отодвигаем наружу по нормали p на (gap + Rp)
                // и смещаем внутрь ребра на Rn, чтобы подпись начиналась от угла, а не вылетала наружу
                float cxLo = ax + px * (gap + HalfSpan(szLo, px, py)) + nx * HalfSpan(szLo, nx, ny);
                float cyLo = ay + py * (gap + HalfSpan(szLo, px, py)) + ny * HalfSpan(szLo, nx, ny);

                // HiText у точки b (максимум): отодвигаем наружу по нормали p на (gap + Rp)
                // и смещаем внутрь ребра на -Rn, чтобы подпись заканчивалась у угла
                float cxHi = bx + px * (gap + HalfSpan(szHi, px, py)) - nx * HalfSpan(szHi, nx, ny);
                float cyHi = by + py * (gap + HalfSpan(szHi, px, py)) - ny * HalfSpan(szHi, nx, ny);

                // Если ребро преимущественно горизонтальное (снизу/сверху), проверяем,
                // не наползает ли Title на LoText или HiText:
                if (Math.Abs(px) < 0.5f)
                {
                    float leftLo = cxLo - szLo.Width / 2f, rightLo = cxLo + szLo.Width / 2f;
                    float leftHi = cxHi - szHi.Width / 2f, rightHi = cxHi + szHi.Width / 2f;
                    float minEdgeX = Math.Min(rightLo, rightHi), maxEdgeX = Math.Max(leftLo, leftHi);
                    float leftTi = cxTi - szTitle.Width / 2f, rightTi = cxTi + szTitle.Width / 2f;

                    if (leftTi < minEdgeX + ScF(8f) || rightTi > maxEdgeX - ScF(8f))
                        cyTi += py * (szTitle.Height + ScF(2f));
                }
                // Аналогично для преимущественно вертикального ребра (слева/справа):
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
            else // Y (столбы)
            {
                float x = (i == 1 || i == 2) ? 1f : -1f;
                float z = (i == 2 || i == 3) ? 1f : -1f;
                x1 = x2 = x; y1 = -1f; y2 = 1f; z1 = z2 = z;
            }
        }

        /// <summary>Надпись по центру точки, вжатая в границы облака: у краёв сцены
        /// подпись иначе уезжает под панель или за окно.</summary>
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

        // --------------------------------------------------------------- мышь и клавиши

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
                    // Инвертировано: тащишь вправо — сцена доворачивается влево, как будто
                    // хватаешь сам объект, а не крутишь камеру вокруг него.
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

        /// <summary>Ближайшая точка под курсором. Из наложившихся выигрывает та, что
        /// ближе к камере, — целиться логично в верхнюю.</summary>
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
