using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// A single design system: colours, type sizes, corner radii and insets. The values are
    /// taken from the mockup (1475×898 at 96 dpi) — wherever the mockup sets a number, that
    /// number stands here rather than something picked by eye. The rest of the window is built
    /// from these same quantities so that elements are of one height and do not drift apart.
    /// </summary>
    public static class Theme
    {
        /// <summary>
        /// Whether smooth vertical scrolling is on (settling by timer). With false, scrolling
        /// in every list and panel is instant.
        /// </summary>
        public static bool SmoothScroll;

        // ------------------------------------------------------------------ colours
        public static readonly Color Bg      = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1D);

        /// <summary>
        /// How dense the window background is over the blur. Dimming the wallpaper with this
        /// value is a bad idea: the blur disappears before the text stops going pink (the edges
        /// of GDI letters inherit the background's alpha and mix with what is behind the
        /// window). The darkness lives in <see cref="Glass.AccentTint"/> — the layer UNDER the
        /// window's content — while exactly enough density is left here for the background not
        /// to look full of holes.
        /// </summary>
        public static int GlassAlpha = 0x0f;

        /// <summary>
        /// The window background: the same colour but, with glass, translucent. Everything
        /// lying directly on the window (rather than inside a card) has to be filled with
        /// precisely this, or the window ends up full of holes in some places and solid in
        /// others.
        /// </summary>
        public static Color Backdrop
        {
            get { return Glass.Enabled ? Color.FromArgb(GlassAlpha, Bg) : Bg; }
        }

        // Cards and pills — the opaque reference colours. Used as they are where glass is
        // impossible (a popup menu is a separate window with no alpha of its own), and as a
        // fallback should Glass.Enabled turn out to be false.
        public static readonly Color Surface        = Color.FromArgb(0xFF, 0x28, 0x28, 0x2A);
        public static readonly Color SurfacePressed = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3E);

        /// <summary>The former names are now merely synonyms: absolutely everything is
        /// opaque.</summary>
        public static readonly Color SolidSurface = Surface;
        public static readonly Color SolidPressed = SurfacePressed;

        /// <summary>
        /// What the detail panel's card is filled with — the same formula as in
        /// DetailPanel.PaintCard (PaintGlassSurface with GlassAlpha): on glass an almost
        /// transparent dark tone, without glass a solid Surface. A control lying on the card
        /// has to paint the background under its own rounding with this rather than with
        /// Backdrop: otherwise in opaque mode the corners fall through to the window background
        /// and a rectangle outlines the pill.
        /// </summary>
        public static Color CardFill
        {
            get { return Glass.Enabled ? Color.FromArgb(GlassAlpha, Bg) : Surface; }
        }

        // The degree of "filling" in glass cards and buttons (see PaintGlassSurface) —
        // increasing for rest / hover / press-or-selected, in the same dark tone as the window
        // background itself, only denser. The numbers are not the alpha proportions of the flat
        // version's states (0x28→0x32→0x3A) — that scale only makes sense for the opaque one,
        // whereas here even the "pressed" state has to stay glass rather than sticking to a
        // solid colour.
        public const int GlassSurfaceAlpha        = 0x14;   // ~8%, the resting state
        public const int GlassSurfaceHotAlpha     = 0x86;   // hover: on a blurred background 0x49 was barely legible
        public const int GlassSurfacePressedAlpha = 0xb0;   // ~23%, press / selected

        public static readonly Color Sunken  = Color.FromArgb(0xFF, 0x15, 0x15, 0x19);  // the search field
        // The primary button. A flat 0xCACACB next to the outlined buttons looked disabled, so
        // it is now a vertical gradient: LightTop on top, Light below, LightPressed while
        // pressed.
        public static readonly Color Light        = Color.FromArgb(0xFF, 0xCA, 0xCA, 0xCB);
        public static readonly Color LightTop     = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF4);
        public static readonly Color LightPressed = Color.FromArgb(0xFF, 0x9E, 0x9E, 0xA2);

        public static readonly Color Text     = Color.FromArgb(0xFF, 0xE9, 0xE9, 0xEB);

        // Secondary text was raised from 0x6B: on glass it sits on a background that already
        // has blur mixed into it, and dark grey read as a dirty smudge on it.
        public static readonly Color TextDim = Glass.Enabled
            ? Color.FromArgb(0xFF, 0x91, 0x91, 0x96)
            : Color.FromArgb(0xFF, 0x6B, 0x6B, 0x6D);
        public static readonly Color OnLight  = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1D);

        public static readonly Color Green = Color.FromArgb(0xFF, 0x61, 0xE1, 0x70);
        public static readonly Color Red   = Color.FromArgb(0xFF, 0xE1, 0x61, 0x61);

        public static readonly Color RowHover = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

        // The rules are opaque. A translucent line crossing another one just like it adds to
        // itself and gives a noticeably lighter pixel at the junction — which is exactly what
        // could be seen where the vertical divider met the header. The colour is picked to
        // match the former 15% white over Bg.
        public static readonly Color Hairline = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3D);

        // ------------------------------------------------------------------ sizes
        public const int Pad        = 30;   // the window margin left/right/top
        public const int ControlH   = 35;   // the height of every pill and field in the toolbar
        public const int IconSize   = 35;   // a round icon button
        public const int IconGap    = 10;
        public const int ContentY   = 98;   // where the content under the toolbar begins
        public const int RowH       = 57;   // the table row pitch
        public const int RowPillH   = 55;   // the row highlight itself
        public const int CellPadX   = 24;   // the text inset from the edge of a row
        public const int PanelW     = 332;  // the detail panel
        public const int PanelPad   = 16;
        // The radii are nested concentrically: inner = outer − inset, or the corner reads as
        // two different arcs side by side. 30 on a 1475×950 window looked like a phone rather
        // than an application.
        public const int WindowR    = 18;
        public const int CardR      = 14;
        public const int ThumbR     = 6;

        // ------------------------------------------------------------------ fonts The type
        // sizes were picked by measurement: glyph heights and line widths match the mockup (a
        // table row is 13 pt, "F Phrygian" 93 px against 95 px in the mockup).
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

        /// <summary>Semibold as a separate family: GDI+ has only two weights, and Bold is too
        /// heavy.</summary>
        public static Font UISemibold(float size)
        {
            foreach (string name in new string[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" })
                if (HasFamily(name)) return new Font(name, size, FontStyle.Regular, GraphicsUnit.Point);
            return new Font(UiFamily, size, FontStyle.Bold, GraphicsUnit.Point);
        }

        static bool HasFamily(string name)
        {
            using (InstalledFontCollection c = new InstalledFontCollection())
                foreach (FontFamily f in c.Families)
                    if (f.Name == name) return true;
            return false;
        }

        public static readonly Font FTitle       = UISemibold(13f);    // a set name, a panel heading
        public static readonly Font FBody        = UI(13f, FontStyle.Regular);
        public static readonly Font FButton      = UI(13f, FontStyle.Regular);     // a button, not a caption
        public static readonly Font FLabel       = UI(12f, FontStyle.Regular);     // the table header, captions
        public static readonly Font FSmall       = UI(12f, FontStyle.Regular);
        public static readonly Font FBadge       = UI(11f, FontStyle.Regular);
        public static readonly Font FMini        = UI(9.5f, FontStyle.Regular);
        public static readonly Font FHead        = UISemibold(17f);
        public static readonly Font FDialogTitle = UISemibold(22f);

        /// <summary>
        /// The offset from the top of a line to its baseline, in pixels. TextRenderer seats a
        /// line by the top of its box, and the box for 13 and for 9.5 points differs in height
        /// — without this correction captions of different sizes stand on different lines.
        /// </summary>
        public static int Baseline(Font f)
        {
            FontFamily fam = f.FontFamily;
            return (int)Math.Round(TextRenderer.MeasureText("Ag", f).Height
                                   * fam.GetCellAscent(f.Style) / (float)fam.GetLineSpacing(f.Style));
        }

        // ------------------------------------------------------------------ drawing

        // A pen cache for PaintGlassSurface: the colours are fixed, so we create them once.
        static readonly Pen _borderPen    = new Pen(Color.FromArgb(10, 255, 255, 255), 1.5f);
        static readonly Pen _highlightPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1.5f);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0f) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Only the top arc of a rounded rectangle — no sides and no bottom. Needed for the
        /// highlight along a card's top edge (in Figma it is an inner shadow offset by 1px in Y
        /// with zero blur: the light falls only where the edge faces upward, and is invisible
        /// at the sides and the bottom).
        /// </summary>
        public static GraphicsPath RoundTop(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (radius <= 0f) { p.AddLine(r.X, r.Y, r.Right, r.Y); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            return p;
        }

        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Max(0f, Math.Min(1f, t));
        }

        public static Color Interpolate(Color c1, Color c2, float factor)
        {
            float f = Math.Max(0f, Math.Min(1f, factor));
            int a = (int)Math.Round(c1.A + (c2.A - c1.A) * f);
            int r = (int)Math.Round(c1.R + (c2.R - c1.R) * f);
            int g = (int)Math.Round(c1.G + (c2.G - c1.G) * f);
            int b = (int)Math.Round(c1.B + (c2.B - c1.B) * f);
            return Color.FromArgb(a, r, g, b);
        }

        static readonly System.Collections.Generic.Dictionary<int, SolidBrush> _brushCache =
            new System.Collections.Generic.Dictionary<int, SolidBrush>();

        public static SolidBrush GetBrush(Color color)
        {
            int argb = color.ToArgb();
            SolidBrush b;
            if (!_brushCache.TryGetValue(argb, out b))
            {
                b = new SolidBrush(color);
                _brushCache[argb] = b;
            }
            return b;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color fill)
        {
            if (fill.A == 0) return;
            using (GraphicsPath p = Round(r, radius))
                g.FillPath(GetBrush(fill), p);
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

        /// <summary>A variant of DrawRound with a ready Pen — no allocation per
        /// frame.</summary>
        static void DrawRoundCached(Graphics g, RectangleF r, float radius, Pen pen)
        {
            float w = pen.Width;
            RectangleF rr = new RectangleF(r.X + w / 2f, r.Y + w / 2f,
                                           r.Width - w, r.Height - w);
            using (GraphicsPath p = Round(rr, radius))
                g.DrawPath(pen, p);
        }

        /// <summary>
        /// A glass card, button or pill — the same trick as the detail panel: an almost
        /// transparent fill in the window's dark tone, a thin 1px outline in 4% white and a
        /// highlight along the top edge in 1px 8% white (in Figma an inner shadow, offset Y=1,
        /// blur 0). fillAlpha is the degree of "filling", see GlassSurfaceAlpha/Hot/Pressed.
        ///
        /// SourceCopy on the fill is not cosmetic: without it the alpha adds over the control's
        /// already written background (Chrome.PaintBase draws earlier), and the middle comes
        /// out noticeably denser than its own edges — the same seam that was caught on the
        /// pills of the input fields. Without glass (Glass.Enabled=false) the fill is solid —
        /// just as it was before this whole experiment.
        /// </summary>
        /// <summary>
        /// Whether THIS window is really acrylic right now — which is not the same as
        /// Glass.Enabled (support in the OS itself). On dialogs like NotesDialog the glass is
        /// off deliberately (see GlassDialog.UseGlass — a real multi-line TextBox lives there,
        /// and acrylic breaks native child windows), and DWM blurs nothing behind them.
        /// PaintGlassSurface has to know that: the SourceCopy below was devised for a window
        /// whose final alpha channel DWM really reads, while on an ordinary opaque window it
        /// simply erases the highlight, leaving the background colour behind it — and the pills
        /// come out invisible (see the report from NotesDialog).
        /// </summary>
        public static bool IsBlurred(Control owner)
        {
            if (!Glass.Enabled || owner == null) return false;
            GlassDialog gd = owner.FindForm() as GlassDialog;
            return gd == null || gd.UseGlass;
        }

        /// <summary>
        /// A glass outline — a thin 1px contour in 4% white and a highlight along the top edge
        /// in 1px 8% white. Exactly the same outline as on the cards, the dialogs and the
        /// properties window (DetailPanel).
        /// </summary>
        public static void PaintGlassBorder(Graphics g, RectangleF r, float radius, float k = 1.0f)
        {
            if (k <= 0.001f) return;
            Pen borderPen = k < 0.999f
                ? new Pen(Color.FromArgb((int)Math.Round(10 * k), 255, 255, 255), 1.5f)
                : _borderPen;
            Pen highlightPen = k < 0.999f
                ? new Pen(Color.FromArgb((int)Math.Round(20 * k), 255, 255, 255), 1.5f)
                : _highlightPen;

            RectangleF border = RectangleF.Inflate(r, -0.5f, -0.5f);
            DrawRoundCached(g, border, Math.Max(0f, radius - 0.5f), borderPen);                           // 4%

            RectangleF hi = RectangleF.Inflate(r, -1.5f, -1.5f);
            using (GraphicsPath top = RoundTop(hi, Math.Max(0f, radius - 1.5f)))
                g.DrawPath(highlightPen, top);                                                            // 8%

            if (borderPen != _borderPen) borderPen.Dispose();
            if (highlightPen != _highlightPen) highlightPen.Dispose();
        }

        public static void PaintGlassSurface(Control owner, Graphics g, RectangleF r, float radius, int fillAlpha)
        {
            bool blurred = IsBlurred(owner);
            Color fill = blurred ? Color.FromArgb(fillAlpha, Bg) : Surface;
            CompositingMode old = g.CompositingMode;
            if (blurred) g.CompositingMode = CompositingMode.SourceCopy;
            FillRound(g, r, radius, fill);
            g.CompositingMode = old;

            // The outline and the highlight hold a constant alpha until the fill drops below
            // the resting state. Quiet buttons have no resting state: without this fade the
            // contour hung at full strength through the whole tail of the animation and
            // "unstuck" itself with a jerk at the end.
            float k = Math.Min(1f, fillAlpha / (float)GlassSurfaceAlpha);
            PaintGlassBorder(g, r, radius, k);
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        /// <summary>
        /// A lighter Graphics setup for scrolling: without the HighQuality PixelOffsetMode,
        /// which slows every GDI+ operation down.
        ///
        /// But not Default either: with it GDI+ puts a shape half a pixel lower and further
        /// right than the rectangle says (measured: a pill at 8..31 draws solid from 9 to 31
        /// and as a pale line at 32). While scrolling everything slid half a pixel and stood
        /// back when the list came to rest. Half gives the same accuracy as HighQuality without
        /// its price.
        /// </summary>
        public static void SmoothFast(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }
    }

}
