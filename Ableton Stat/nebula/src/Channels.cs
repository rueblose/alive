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
    /// Одна величина сета, которую можно подставить в любой из шести каналов облака.
    /// Величина всегда число: положение по оси, размер, прозрачность и цвет считаются
    /// из него одинаково. NaN означает «у этого сета такого нет» — не ноль: у сета из
    /// Live 10 нет тональности вовсе, и ставить её в начало шкалы было бы враньём.
    /// </summary>
    public sealed class Metric
    {
        public string Id = "";
        public string Title = "";

        /// <summary>Шкала логарифмическая. Нужна там, где хвост тянется на порядки:
        /// размеры папок и число ссылок на сэмплы отличаются в тысячи раз.</summary>
        public bool Log;

        /// <summary>Значение — номер класса, а не количество: тональность, лад, полка.
        /// В цвете такие раскрашиваются палитрой, а не градиентом.</summary>
        public bool Categorical;

        /// <summary>Как красить, когда величина стоит в канале цвета.</summary>
        public ColorMode Color = ColorMode.Ramp;

        public MetricValue Value;
        public MetricText Text;
    }

    public enum ColorMode
    {
        Ramp,      // непрерывный градиент
        Classes,   // палитра по номеру класса
        Key        // круг квинт: тональность → оттенок
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

        // ------------------------------------------------------------------ каталог

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

            // Тональность как число — это номер ноты, а не «сколько». По оси она даёт
            // двенадцать плоскостей, в цвете — круг квинт (см. Palette.Key).
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

        // ------------------------------------------------------------ преобразования

        /// <summary>Дни от эпохи. Дата как число — чтобы шкала считалась как у любой другой величины.</summary>
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

        /// <summary>«12.3.5» → 12.0305: версии должны сравниваться по частям, а не по строке.</summary>
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

        /// <summary>Имя как число: первые шесть знаков в базе 40, чтобы ось «A→Z» была
        /// действительно алфавитной, а не случайной.</summary>
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

        /// <summary>Устойчивое число 0..1 из строки — рассыпать точки там, где своей
        /// величины нет. Своё, а не GetHashCode: тот не обещает одинаковый результат
        /// между запусками, и облако прыгало бы при каждом старте.</summary>
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

        // Полки нумеруем по первому появлению: имена папок заранее неизвестны, а цвет
        // класса должен быть один и тот же весь сеанс.
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

        public static string PlaceName(int index)
        {
            return index >= 0 && index < _placeNames.Count ? _placeNames[index] : "";
        }
    }

    /// <summary>
    /// Шкала одной величины по текущему набору сетов. Края берутся не по минимуму и
    /// максимуму, а по 2-му и 98-му процентилю: один проект-монстр на 40 ГБ иначе
    /// сплющивает все остальные в точку у нуля.
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
            // У категорий процентили ни к чему: классы должны попадать на шкалу все,
            // включая единственный сет в редком ладу.
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

        /// <summary>0..1 с обрезкой по краям. NaN на входе — NaN на выходе.</summary>
        public double Norm(double raw)
        {
            if (double.IsNaN(raw) || Empty) return double.NaN;
            double v = Log ? Math.Log10(1.0 + Math.Max(0, raw)) : raw;
            double t = (v - Lo) / (Hi - Lo);
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }

        /// <summary>Значение на краю шкалы — для подписей у осей.</summary>
        public double At(double t)
        {
            double v = Lo + (Hi - Lo) * t;
            return Log ? Math.Pow(10, v) - 1.0 : v;
        }
    }

    /// <summary>Именованный градиент непрерывной величины — набор опорных цветов,
    /// между которыми Palette.Sample идёт через LAB, а не напрямую по RGB.</summary>
    public sealed class Gradient
    {
        public string Id = "";
        public string Title = "";
        public Color[] Stops;
    }

    public static class Palette
    {
        // Пять градиентов на выбор. Опорные цвета — обычный sRGB (как их видno в любом
        // редакторе), а смешивает их между собой уже Sample() через LAB: перегон через
        // RGB напрямую между двумя насыщенными, но разными по светлоте цветами (скажем,
        // тёмно-фиолетовым и жёлтым) даёт грязную серую просадку посередине — глаз видит
        // готовую примесь, а не то, что было задумано. LAB устроен так, что светлота (L)
        // меняется по прямой независимо от цветности, и середина остаётся чистым цветом,
        // а не серой.
        public static readonly Gradient[] Gradients =
        {
            new Gradient { Id = "nebula", Title = "Nebula", Stops = new[]
            {
                Color.FromArgb(0x4C, 0x63, 0xFF), Color.FromArgb(0x9B, 0x5C, 0xFF),
                Color.FromArgb(0xEE, 0x5F, 0xC8), Color.FromArgb(0xFF, 0x77, 0x6B),
                Color.FromArgb(0xFF, 0xC8, 0x4D)
            } },
            new Gradient { Id = "viridis", Title = "Viridis", Stops = new[]
            {
                Color.FromArgb(0x44, 0x01, 0x54), Color.FromArgb(0x3B, 0x52, 0x8B),
                Color.FromArgb(0x21, 0x90, 0x8D), Color.FromArgb(0x5D, 0xC9, 0x63),
                Color.FromArgb(0xFD, 0xE7, 0x25)
            } },
            new Gradient { Id = "plasma", Title = "Plasma", Stops = new[]
            {
                Color.FromArgb(0x0D, 0x08, 0x87), Color.FromArgb(0x7E, 0x03, 0xA8),
                Color.FromArgb(0xCC, 0x47, 0x78), Color.FromArgb(0xF8, 0x94, 0x41),
                Color.FromArgb(0xF0, 0xF9, 0x21)
            } },
            new Gradient { Id = "inferno", Title = "Inferno", Stops = new[]
            {
                Color.FromArgb(0x00, 0x00, 0x04), Color.FromArgb(0x57, 0x10, 0x6E),
                Color.FromArgb(0xBC, 0x37, 0x54), Color.FromArgb(0xF9, 0x8C, 0x0A),
                Color.FromArgb(0xFC, 0xFF, 0xA4)
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
        // sRGB -> линейный свет -> XYZ (D65) -> CIELAB, и обратно. Формулы учебные
        // (тот же путь, что в любом руководстве по цветовым моделям), нужны здесь
        // только затем, чтобы смешивать между двумя опорными цветами по кратчайшей
        // дороге для глаза, а не по кратчайшей дороге для чисел R,G,B.

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
        /// Цвет тональности. Оттенок идёт по кругу квинт, а не по хроматической
        /// гамме: соседние по кругу тональности — родственные, и на облаке они
        /// оказываются соседними по цвету. Мажор светлее и мягче, минор — глубже.
        /// </summary>
        public static Color Key(int root, int scaleIndex)
        {
            if (root < 0 || root > 11) return Color.FromArgb(0x7E, 0x7E, 0x88);
            int fifth = (root * 7) % 12;
            float hue = fifth * 30f;
            bool minor = scaleIndex == 1 || scaleIndex == 5 || scaleIndex == 2;   // minor / phrygian / dorian
            return FromHsv(hue, minor ? 0.80f : 0.58f, minor ? 0.82f : 1.0f);
        }

        /// <summary>Цвет класса: оттенки раскладываются золотым углом, чтобы соседние
        /// номера не сливались.</summary>
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
    /// Квадратный чекбокс рядом со строкой канала — та же отрисовка, что и у галочек
    /// в RowListView, просто отдельным контролом: включает и выключает канал, не трогая
    /// выбор величины в выпадающем списке рядом с ним, чтобы вернуть канал можно было,
    /// не выбирая величину заново.
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
    /// Четыре пресета камеры в одной пилюле, каждый — маленький изометрический куб:
    /// на «Angle» все три грани ровные (свободный угол), у «Front/Side/Top» одна грань
    /// светится — та, что нормальна к оси, вдоль которой в этом виде смотрит камера.
    /// Не радио-кнопка: клик применяет вид, но не запоминается подсветкой — стоит
    /// потом покрутить мышью, и «Front» больше не будет правдой ни для одной иконки.
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
        /// Изометрический куб из трёх ромбов, сходящихся в одной точке — школьная
        /// проекция, но она моментально читается как «куб», а не абстрактный
        /// шестиугольник. Ромбы делят правильный шестиугольник линиями к его центру
        /// через одну вершину.
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

            PointF[] top = { v0, v1, o, v5 };     // смотрит вдоль Y — «Top»
            PointF[] right = { v1, v2, v3, o };   // смотрит вдоль X — «Side»
            PointF[] left = { o, v3, v4, v5 };    // смотрит вдоль Z — «Front»

            int dimA = hot ? 70 : 46;
            Color dim = Color.FromArgb(dimA, 255, 255, 255);
            Color lit = Theme.Interpolate(Theme.Light, Color.White, hot ? 1f : 0.4f);
            Color edge = Color.FromArgb(hot ? 170 : 110, 255, 255, 255);

            Color topFill = kind == CameraPreset.Top ? lit : dim;
            Color rightFill = kind == CameraPreset.Side ? lit : dim;
            Color leftFill = kind == CameraPreset.Front ? lit : dim;
            // Angle: ни одна грань не выделена — куб «просто куб», вид со свободного угла.

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
