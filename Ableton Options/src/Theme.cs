using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AbletonOptions
{
    public static class Theme
    {
        // Матовое стекло поверх размытого фона: панели полупрозрачные, поэтому
        // почти все цвета заданы с альфой.
        public static readonly Color Scrim       = Color.FromArgb(0x9E, 0x10, 0x10, 0x12);
        public static readonly Color CardFill    = Color.FromArgb(0xC6, 0x18, 0x18, 0x1B);
        public static readonly Color CardBorder  = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        public static readonly Color RowIdle     = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        public static readonly Color RowHover    = Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF);
        public static readonly Color RowOn       = Color.FromArgb(0x14, 0x0A, 0x84, 0xFF);
        public static readonly Color FieldFill   = Color.FromArgb(0x40, 0x00, 0x00, 0x00);
        public static readonly Color FieldBorder = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
        public static readonly Color Separator   = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

        public static readonly Color Text        = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5);
        public static readonly Color TextDim     = Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA3);
        public static readonly Color TextFaint   = Color.FromArgb(0xFF, 0x6C, 0x6C, 0x75);
        public static readonly Color Mono        = Color.FromArgb(0xFF, 0x7F, 0xB0, 0xE8);

        // Системные цвета macOS — то, что читается как «эппловский» интерфейс.
        public static readonly Color Blue   = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF);
        public static readonly Color Green  = Color.FromArgb(0xFF, 0x30, 0xD1, 0x58);
        public static readonly Color Orange = Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A);
        public static readonly Color Red    = Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A);
        public static readonly Color Grey   = Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93);
        public static readonly Color Purple = Color.FromArgb(0xFF, 0xBF, 0x5A, 0xF2);

        static string _uiFamily;

        static string UiFamily
        {
            get
            {
                if (_uiFamily == null)
                {
                    _uiFamily = "Segoe UI";
                    using (InstalledFontCollection c = new InstalledFontCollection())
                        foreach (FontFamily f in c.Families)
                            if (f.Name == "Segoe UI Variable Text") { _uiFamily = f.Name; break; }
                }
                return _uiFamily;
            }
        }

        public static Font UI(float size, FontStyle style)
        {
            return new Font(UiFamily, size, style, GraphicsUnit.Point);
        }

        public static Font Code(float size)
        {
            return new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        // --- кэш шрифтов: 160 карточек не должны плодить объекты на каждую отрисовку
        public static readonly Font FTitle   = UI(10.5f, FontStyle.Regular);
        public static readonly Font FBody    = UI(9.25f, FontStyle.Regular);
        public static readonly Font FSmall   = UI(8.25f, FontStyle.Regular);
        public static readonly Font FBadge   = UI(7.75f, FontStyle.Regular);
        public static readonly Font FMono    = Code(8.75f);
        public static readonly Font FHead    = UI(15f, FontStyle.Regular);
        public static readonly Font FButton  = UI(9.25f, FontStyle.Regular);
        public static readonly Font FLabel   = UI(8.5f, FontStyle.Regular);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color fill)
        {
            if (fill.A == 0) return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, RectangleF r, float radius, Color line, float width)
        {
            if (line.A == 0) return;
            RectangleF rr = new RectangleF(r.X + width / 2f, r.Y + width / 2f,
                                           r.Width - width, r.Height - width);
            using (GraphicsPath p = Round(rr, radius))
            using (Pen pen = new Pen(line, width))
                g.DrawPath(pen, p);
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static Color Accent(Stat s)
        {
            switch (s)
            {
                case Stat.Doc: return Blue;
                case Stat.New12: return Green;
                case Stat.Dev: return Grey;
                case Stat.Legacy: return Red;
            }
            return Grey;
        }
    }

    /// <summary>Панель с двойной буферизацией и прозрачным фоном (под ней — размытая подложка).</summary>
    public class GlassPanel : Panel
    {
        public GlassPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }
}
