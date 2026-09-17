using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AbletonManager.Nebula
{
    public delegate double MetricValue(SetEntry s);
    public delegate string MetricText(SetEntry s);

    /// <summary>
    /// One value of a set that can be plugged into any of the cloud's six channels. The value
    /// is always a number: position along an axis, size, transparency and colour are all
    /// computed from it the same way. NaN means "this set does not have one" — not zero: a set
    /// from Live 10 has no key at all, and putting it at the start of the scale would be a lie.
    /// </summary>
    public sealed class Metric
    {
        public string Id = "";
        public string Title = "";

        /// <summary>The scale is logarithmic. Needed where the tail stretches over orders of
        /// magnitude: folder sizes and sample reference counts differ by thousands of
        /// times.</summary>
        public bool Log;

        /// <summary>The value is a class number rather than a quantity: key, scale, shelf. In
        /// colour these are painted with a palette rather than a gradient.</summary>
        public bool Categorical;

        /// <summary>How to colour it when the value sits in the colour channel.</summary>
        public ColorMode Color = ColorMode.Ramp;

        public MetricValue Value;
        public MetricText Text;
    }

    public enum ColorMode
    {
        Ramp,      // a continuous gradient
        Classes,   // a palette by class number
        Key        // the circle of fifths: key → hue
    }

    public static class Metrics
    {
        public static readonly List<Metric> All = Build();

        public static Metric ById(string id)
        {
            foreach (Metric m in All) if (m.Id == id) return m;
            return All[0];
        }

        public static int IndexOf(Metric m)
        {
            for (int i = 0; i < All.Count; i++) if (All[i] == m) return i;
            return 0;
        }

        public static string[] Titles()
        {
            string[] t = new string[All.Count];
            for (int i = 0; i < All.Count; i++) t[i] = All[i].Title;
            return t;
        }

        // ------------------------------------------------------------------ catalog

        static List<Metric> Build()
        {
            List<Metric> m = new List<Metric>();

            m.Add(Num("tracks", "Tracks",
                delegate (SetEntry s) { return s.Tracks > 0 ? s.Tracks : double.NaN; },
                delegate (SetEntry s) { return s.Tracks > 0 ? s.Tracks.ToString(CultureInfo.InvariantCulture) : "—"; }));

            m.Add(Num("plugins", "Plugins",
                delegate (SetEntry s) { return s.Plugins.Length; },
                delegate (SetEntry s) { return s.Plugins.Length.ToString(CultureInfo.InvariantCulture); }));

            m.Add(Num("bpm", "BPM",
                delegate (SetEntry s) { return s.Tempo > 0 ? s.Tempo : double.NaN; },
                delegate (SetEntry s) { return s.Tempo > 0 ? s.Tempo.ToString("0.##", CultureInfo.InvariantCulture) : "—"; }));

            // A key as a number is a note number, not a "how much". Along an axis it gives
            // twelve planes; in colour, the circle of fifths (see Palette.Key).
            Metric key = Num("key", "Key",
                delegate (SetEntry s) { return s.ScaleRoot >= 0 ? s.ScaleRoot : double.NaN; },
                delegate (SetEntry s) { return s.Key.Length > 0 ? s.Key : "—"; });
            key.Categorical = true;
            key.Color = ColorMode.Key;
            m.Add(key);

            Metric scale = Num("scale", "Scale",
                delegate (SetEntry s) { return s.ScaleIndex >= 0 ? s.ScaleIndex : double.NaN; },
                delegate (SetEntry s) { return s.ScaleIndex >= 0 ? Scales.ScaleName(s.ScaleIndex) : "—"; });
            scale.Categorical = true;
            scale.Color = ColorMode.Classes;
            m.Add(scale);

            m.Add(Log("projsize", "Project size",
                delegate (SetEntry s) { return s.ProjectSize > 0 ? s.ProjectSize : double.NaN; },
                delegate (SetEntry s) { return s.ProjectSize > 0 ? Bytes(s.ProjectSize) : "…"; }));

            m.Add(Log("setsize", "Set file size",
                delegate (SetEntry s) { return s.Size > 0 ? s.Size : double.NaN; },
                delegate (SetEntry s) { return Bytes(s.Size); }));

            m.Add(Log("files", "Sample refs",
                delegate (SetEntry s) { return s.TotalRefs; },
                delegate (SetEntry s) { return s.TotalRefs.ToString(CultureInfo.InvariantCulture); }));

            m.Add(Num("missfiles", "Missing files",
                delegate (SetEntry s) { return s.MissingFiles; },
                delegate (SetEntry s) { return s.MissingFiles.ToString(CultureInfo.InvariantCulture); }));

            m.Add(Num("missplugins", "Missing plugins",
                delegate (SetEntry s) { return s.MissingPlugins; },
                delegate (SetEntry s) { return s.MissingPlugins.ToString(CultureInfo.InvariantCulture); }));

            m.Add(Num("modified", "Modified",
                delegate (SetEntry s) { return Days(s.Modified); },
                delegate (SetEntry s) { return Date(s.Modified); }));

            m.Add(Num("created", "Created",
                delegate (SetEntry s) { return Days(s.Created); },
                delegate (SetEntry s) { return Date(s.Created); }));

            m.Add(Num("live", "Live version",
                delegate (SetEntry s) { return Version(s.ShortVersion); },
                delegate (SetEntry s) { return s.ShortVersion.Length > 0 ? s.ShortVersion : "—"; }));

            Metric place = Num("place", "Collection",
                delegate (SetEntry s) { return PlaceIndex(s.Place); },
                delegate (SetEntry s) { return s.Place.Length > 0 ? s.Place : "—"; });
            place.Categorical = true;
            place.Color = ColorMode.Classes;
            m.Add(place);

            m.Add(Num("name", "Name A→Z",
                delegate (SetEntry s) { return Alphabetical(s.Name); },
                delegate (SetEntry s) { return s.Name; }));

            m.Add(Num("scatter", "Scatter (random)",
                delegate (SetEntry s) { return Hash01(s.Path); },
                delegate (SetEntry s) { return "—"; }));

            return m;
        }

        static Metric Num(string id, string title, MetricValue v, MetricText t)
        {
            Metric m = new Metric();
            m.Id = id; m.Title = title; m.Value = v; m.Text = t;
            return m;
        }

        static Metric Log(string id, string title, MetricValue v, MetricText t)
        {
            Metric m = Num(id, title, v, t);
            m.Log = true;
            return m;
        }

        // ------------------------------------------------------------- conversions

        /// <summary>Days since the epoch. A date as a number — so the scale is computed like
        /// any other value's.</summary>
        static double Days(DateTime d)
        {
            if (d == default(DateTime) || d.Year < 1990) return double.NaN;
            return (d - new DateTime(1990, 1, 1)).TotalDays;
        }

        static string Date(DateTime d)
        {
            if (d == default(DateTime) || d.Year < 1990) return "—";
            return d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>"12.3.5" → 12.0305: versions have to compare part by part, not as
        /// strings.</summary>
        static double Version(string v)
        {
            if (string.IsNullOrEmpty(v)) return double.NaN;
            string[] parts = v.Split('.');
            double result = 0, weight = 1;
            for (int i = 0; i < parts.Length && i < 3; i++)
            {
                int n;
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) break;
                result += n * weight;
                weight /= 100.0;
            }
            return result > 0 ? result : double.NaN;
        }

        public static string Bytes(long b)
        {
            if (b <= 0) return "—";
            if (b >= 1073741824L) return (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (b >= 1048576L) return (b / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
            return (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        /// <summary>A name as a number: the first six characters in base 40, so that an "A→Z"
        /// axis is genuinely alphabetical rather than random.</summary>
        static double Alphabetical(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            double v = 0;
            for (int i = 0; i < 6; i++)
            {
                int c = 0;
                if (i < name.Length)
                {
                    char ch = char.ToLowerInvariant(name[i]);
                    if (ch >= 'a' && ch <= 'z') c = ch - 'a' + 1;
                    else if (ch >= '0' && ch <= '9') c = 27 + (ch - '0');
                    else c = 38;
                }
                v = v * 40 + c;
            }
            return v;
        }

        /// <summary>A stable 0..1 number out of a string — for scattering dots where there is
        /// no value of their own. Ours rather than GetHashCode: that one promises no
        /// consistency between runs, and the cloud would jump on every start.</summary>
        public static double Hash01(string s)
        {
            return (Hash(s) & 0xFFFFFF) / (double)0x1000000;
        }

        public static int Hash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < s.Length; i++) { h = (h ^ char.ToLowerInvariant(s[i])) * 16777619; }
                return h & 0x7FFFFFFF;
            }
        }

        // Shelves are numbered by first appearance: folder names are not known in advance,
        // while a class's colour has to stay the same for the whole session.
        static readonly Dictionary<string, int> _places =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> _placeNames = new List<string>();

        static double PlaceIndex(string place)
        {
            if (string.IsNullOrEmpty(place)) return double.NaN;
            int i;
            if (!_places.TryGetValue(place, out i))
            {
                i = _placeNames.Count;
                _places[place] = i;
                _placeNames.Add(place);
            }
            return i;
        }

    }

    /// <summary>
    /// The scale of one value over the current set of sets. The edges are taken not from the
    /// minimum and maximum but from the 2nd and 98th percentile: one 40 GB monster of a project
    /// would otherwise squash all the rest into a dot at zero.
    /// </summary>
    public sealed class Range
    {
        public double Lo, Hi;
        public bool Log;
        public bool Empty = true;

        public static Range Of(List<SetEntry> sets, Metric m)
        {
            Range r = new Range();
            r.Log = m.Log;

            List<double> v = new List<double>(sets.Count);
            foreach (SetEntry s in sets)
            {
                double d = m.Value(s);
                if (double.IsNaN(d)) continue;
                v.Add(m.Log ? Math.Log10(1.0 + Math.Max(0, d)) : d);
            }
            if (v.Count == 0) return r;

            v.Sort();
            r.Empty = false;
            // Percentiles are of no use for categories: every class has to land on the scale,
            // including the single set in a rare mode.
            if (m.Categorical || v.Count < 20)
            {
                r.Lo = v[0];
                r.Hi = v[v.Count - 1];
            }
            else
            {
                r.Lo = v[(int)(v.Count * 0.02)];
                r.Hi = v[(int)(v.Count * 0.98)];
            }
            if (r.Hi - r.Lo < 1e-9) { r.Lo -= 0.5; r.Hi += 0.5; }
            return r;
        }

        /// <summary>0..1 with clamping at the edges. NaN in — NaN out.</summary>
        public double Norm(double raw)
        {
            if (double.IsNaN(raw) || Empty) return double.NaN;
            double v = Log ? Math.Log10(1.0 + Math.Max(0, raw)) : raw;
            double t = (v - Lo) / (Hi - Lo);
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }

        /// <summary>The value at the edge of the scale — for the axis labels.</summary>
        public double At(double t)
        {
            double v = Lo + (Hi - Lo) * t;
            return Log ? Math.Pow(10, v) - 1.0 : v;
        }
    }

    /// <summary>A named gradient for a continuous value — a set of anchor colours between which
    /// Palette.Sample travels through LAB rather than straight through RGB.</summary>
    public sealed class Gradient
    {
        public string Id = "";
        public string Title = "";
        public Color[] Stops;
    }

    public static class Palette
    {
        // Five gradients to choose from. The anchor colours are ordinary sRGB (as they look in
        // any editor), and it is Sample() that mixes between them through LAB: going straight
        // through RGB between two saturated colours of differing lightness (say, dark violet
        // and yellow) gives a dirty grey sag in the middle — the eye sees an accidental blend
        // rather than what was intended. LAB is built so that lightness (L) changes along a
        // straight line independently of chroma, and the middle stays a clean colour instead of
        // a grey one.
        public static readonly Gradient[] Gradients =
        {
            new Gradient { Id = "nebula", Title = "Nebula", Stops = new[]
            {
                Color.FromArgb(0x4C, 0x63, 0xFF), Color.FromArgb(0x9B, 0x5C, 0xFF),
                Color.FromArgb(0xEE, 0x5F, 0xC8), Color.FromArgb(0xFF, 0x77, 0x6B),
                Color.FromArgb(0xFF, 0xC8, 0x4D)
            } },
            new Gradient { Id = "ocean", Title = "Ocean", Stops = new[]
            {
                Color.FromArgb(0x04, 0x12, 0x2B), Color.FromArgb(0x0B, 0x3D, 0x5C),
                Color.FromArgb(0x14, 0x7A, 0x96), Color.FromArgb(0x4F, 0xC6, 0xC0),
                Color.FromArgb(0xEA, 0xFC, 0xF7)
            } },
        };

        public static Gradient GradientById(string id)
        {
            foreach (Gradient g in Gradients) if (g.Id == id) return g;
            return Gradients[0];
        }

        public static int GradientIndexOf(Gradient g)
        {
            for (int i = 0; i < Gradients.Length; i++) if (Gradients[i] == g) return i;
            return 0;
        }

        public static string[] GradientTitles()
        {
            string[] t = new string[Gradients.Length];
            for (int i = 0; i < Gradients.Length; i++) t[i] = Gradients[i].Title;
            return t;
        }

        public static Color Sample(Gradient grad, double t)
        {
            if (double.IsNaN(t)) return Color.FromArgb(0x8A, 0x8A, 0x94);
            Color[] stops = grad.Stops;
            if (t < 0) t = 0; if (t > 1) t = 1;
            double x = t * (stops.Length - 1);
            int i = (int)x;
            if (i >= stops.Length - 1) return stops[stops.Length - 1];
            return LabLerp(stops[i], stops[i + 1], x - i);
        }

        // ---------------------------------------------------------------------- LAB
        //
        // sRGB -> linear light -> XYZ (D65) -> CIELAB, and back. The formulas are textbook ones
        // (the same path as in any guide to colour models) and are only here so that mixing
        // between two anchor colours takes the shortest road for the eye rather than the
        // shortest road for the numbers R, G and B.

        static double SrgbToLinear(double c)
        {
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double LinearToSrgb(double c)
        {
            if (c < 0) c = 0; if (c > 1) c = 1;
            return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        }

        const double LabDelta = 6.0 / 29.0;

        static double LabF(double t)
        {
            return t > LabDelta * LabDelta * LabDelta
                 ? Math.Pow(t, 1.0 / 3.0)
                 : t / (3 * LabDelta * LabDelta) + 4.0 / 29.0;
        }

        static double LabFInv(double t)
        {
            return t > LabDelta ? t * t * t : 3 * LabDelta * LabDelta * (t - 4.0 / 29.0);
        }

        static void RgbToLab(Color c, out double L, out double A, out double B)
        {
            double r = SrgbToLinear(c.R / 255.0);
            double g = SrgbToLinear(c.G / 255.0);
            double b = SrgbToLinear(c.B / 255.0);

            double x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
            double y = (r * 0.2126729 + g * 0.7151522 + b * 0.0721750) / 1.00000;
            double z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

            double fx = LabF(x), fy = LabF(y), fz = LabF(z);
            L = 116.0 * fy - 16.0;
            A = 500.0 * (fx - fy);
            B = 200.0 * (fy - fz);
        }

        static Color LabToColor(double L, double A, double B)
        {
            double fy = (L + 16.0) / 116.0;
            double fx = fy + A / 500.0;
            double fz = fy - B / 200.0;

            double x = LabFInv(fx) * 0.95047;
            double y = LabFInv(fy) * 1.00000;
            double z = LabFInv(fz) * 1.08883;

            double r = x * 3.2404542 + y * -1.5371385 + z * -0.4985314;
            double g = x * -0.9692660 + y * 1.8760108 + z * 0.0415560;
            double b = x * 0.0556434 + y * -0.2040259 + z * 1.0572252;

            return Color.FromArgb(255,
                (int)Math.Round(LinearToSrgb(r) * 255.0),
                (int)Math.Round(LinearToSrgb(g) * 255.0),
                (int)Math.Round(LinearToSrgb(b) * 255.0));
        }

        static Color LabLerp(Color a, Color b, double t)
        {
            double la, aa, ba, lb, ab, bb;
            RgbToLab(a, out la, out aa, out ba);
            RgbToLab(b, out lb, out ab, out bb);
            return LabToColor(la + (lb - la) * t, aa + (ab - aa) * t, ba + (bb - ba) * t);
        }

        /// <summary>
        /// The colour of a key. The hue follows the circle of fifths rather than the chromatic
        /// scale: keys adjacent on the circle are related, and in the cloud they end up
        /// adjacent in colour. Major is lighter and softer, minor is deeper.
        /// </summary>
        public static Color Key(int root, int scaleIndex)
        {
            if (root < 0 || root > 11) return Color.FromArgb(0x7E, 0x7E, 0x88);
            int fifth = (root * 7) % 12;
            float hue = fifth * 30f;
            bool minor = scaleIndex == 1 || scaleIndex == 5 || scaleIndex == 2;   // minor / phrygian / dorian
            return FromHsv(hue, minor ? 0.80f : 0.58f, minor ? 0.82f : 1.0f);
        }

        /// <summary>A class colour: hues are laid out by the golden angle so neighbouring
        /// numbers do not blend together.</summary>
        public static Color Class(int index)
        {
            if (index < 0) return Color.FromArgb(0x7E, 0x7E, 0x88);
            float hue = (index * 137.508f) % 360f;
            float sat = 0.62f + ((index % 3) * 0.09f);
            float val = 1.0f - ((index % 2) * 0.16f);
            return FromHsv(hue, sat, val);
        }

        public static Color FromHsv(float h, float s, float v)
        {
            h = ((h % 360f) + 360f) % 360f;
            int i = (int)(h / 60f) % 6;
            float f = h / 60f - (int)(h / 60f);
            float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            float r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
    }

    /// <summary>
    /// A square checkbox beside a channel row — the same drawing as the ticks in RowListView,
    /// just as a separate control: it switches a channel on and off without touching the value
    /// chosen in the dropdown next to it, so a channel can be brought back without picking its
    /// value again.
    /// </summary>
    public sealed class ChannelSwitch : GlassControl
    {
        bool _on = true;
        public event EventHandler CheckedChanged;

        public ChannelSwitch()
        {
            Cursor = Cursors.Hand;
            Size = new Size(20, 20);
        }

        public bool Checked
        {
            get { return _on; }
            set
            {
                if (_on == value) return;
                _on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            float cs = Math.Min(Width, Height) - 2f;
            RectangleF cb = new RectangleF((Width - cs) / 2f, (Height - cs) / 2f, cs, cs);

            if (_on)
            {
                Color fill = Theme.Interpolate(Theme.Light, Color.White, HoverFactor);
                Theme.FillRound(g, cb, cs * 0.28f, fill);
                Icons.Draw(g, Glyph.Check, RectangleF.Inflate(cb, -cs * 0.22f, -cs * 0.22f), Theme.OnLight, 1.5f);
            }
            else
            {
                Color line = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
                Theme.DrawRound(g, cb, cs * 0.28f, line, 1.3f);
            }
        }
    }

    /// <summary>
    /// Four camera presets in one pill, each a small isometric cube: on "Angle" all three faces
    /// are even (a free angle), while on "Front/Side/Top" one face glows — the one normal to
    /// the axis the camera looks along in that view. Not a radio button: a click applies the
    /// view but is not remembered by a highlight — spin the mouse afterwards and "Front" is no
    /// longer true for any of the icons.
    /// </summary>
    public sealed class CameraPresetBar : GlassControl
    {
        int _hot = -1;
        public event Action<CameraPreset> PresetClicked;

        public CameraPresetBar() { Cursor = Cursors.Hand; }

        RectangleF SlotRect(int i)
        {
            float inset = Sc(3);
            float w = (Width - inset * 2) / 4f;
            return new RectangleF(inset + i * w, inset, w, Height - inset * 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = -1;
            for (int i = 0; i < 4; i++) if (SlotRect(i).Contains(e.Location)) idx = i;
            if (idx != _hot) { _hot = idx; AnimEngine.Register(this); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; AnimEngine.Register(this); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < 4; i++)
                if (SlotRect(i).Contains(e.Location) && PresetClicked != null)
                    PresetClicked((CameraPreset)i);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.DrawRound(g, r, (Height - 1) / 2f, Color.FromArgb(46, 255, 255, 255), 1f);

            for (int i = 0; i < 4; i++)
            {
                RectangleF sr = SlotRect(i);
                bool hot = i == _hot;
                if (hot) Theme.FillRound(g, sr, sr.Height / 2f, Theme.RowHover);
                DrawCube(g, sr, (CameraPreset)i, hot);
            }
        }

        /// <summary>
        /// An isometric cube of three rhombi meeting at one point — a schoolbook projection,
        /// but it reads instantly as "a cube" rather than as an abstract hexagon. The rhombi
        /// divide a regular hexagon with lines to its centre through every other vertex.
        /// </summary>
        static void DrawCube(Graphics g, RectangleF box, CameraPreset kind, bool hot)
        {
            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f + box.Height * 0.05f;
            float s = box.Height * 0.30f;

            PointF o = new PointF(cx, cy);
            PointF v0 = new PointF(cx, cy - s);
            PointF v1 = new PointF(cx + s * 0.866f, cy - s * 0.5f);
            PointF v2 = new PointF(cx + s * 0.866f, cy + s * 0.5f);
            PointF v3 = new PointF(cx, cy + s);
            PointF v4 = new PointF(cx - s * 0.866f, cy + s * 0.5f);
            PointF v5 = new PointF(cx - s * 0.866f, cy - s * 0.5f);

            PointF[] top = { v0, v1, o, v5 };     // looks along Y — "Top"
            PointF[] right = { v1, v2, v3, o };   // looks along X — "Side"
            PointF[] left = { o, v3, v4, v5 };    // looks along Z — "Front"

            int dimA = hot ? 70 : 46;
            Color dim = Color.FromArgb(dimA, 255, 255, 255);
            Color lit = Theme.Interpolate(Theme.Light, Color.White, hot ? 1f : 0.4f);
            Color edge = Color.FromArgb(hot ? 170 : 110, 255, 255, 255);

            Color topFill = kind == CameraPreset.Top ? lit : dim;
            Color rightFill = kind == CameraPreset.Side ? lit : dim;
            Color leftFill = kind == CameraPreset.Front ? lit : dim;
            // Angle: no face is picked out — the cube is "just a cube", seen from a free angle.

            g.FillPolygon(Theme.GetBrush(topFill), top);
            g.FillPolygon(Theme.GetBrush(rightFill), right);
            g.FillPolygon(Theme.GetBrush(leftFill), left);

            using (Pen pen = new Pen(edge, 1f))
            {
                g.DrawPolygon(pen, top);
                g.DrawPolygon(pen, right);
                g.DrawPolygon(pen, left);
            }
        }
    }
}
