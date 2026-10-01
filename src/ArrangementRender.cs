using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    public sealed class RenderOptions
    {
        public bool ShowNames;        // the track name column on the left
        public bool ShowRuler;        // the bar ruler along the top
        public int MinLane = 2;
        public int MaxLane = 26;
        public float Dpi = 1f;
    }

    /// <summary>
    /// Draws the arrangement the way it looks in Live: tracks as rows from top to bottom, time
    /// from left to right, a clip as a rectangle in its own colour. A midi clip's body is muted
    /// and the notes are drawn over it — otherwise an empty clip is indistinguishable from a
    /// dense one in the preview.
    /// </summary>
    public static class ArrangementRender
    {
        // Notes are not drawn without limit: large sets have tens of thousands of them, and the
        // eye is satisfied with a portion — otherwise the preview takes noticeably longer to
        // open than it needs to.
        const int MaxNotesDrawn = 60000;
        const int MaxLoopRepeats = 512;

        /// <summary>The whole arrangement fitted into area — the thumbnails on Home and the
        /// picture in the panel.</summary>
        public static void Draw(Graphics g, Rectangle area, Arrangement a, RenderOptions o)
        {
            if (a == null || a.Tracks.Count == 0) return;
            if (o == null) o = new RenderOptions();

            int n = a.Tracks.Count;
            int gap = area.Height / n >= 8 ? Px(o, 2) : 1;
            int rulerH = RulerHeight(o);

            int laneH = (area.Height - rulerH - gap * (n - 1)) / n;
            if (laneH > Px(o, o.MaxLane)) laneH = Px(o, o.MaxLane);
            if (laneH < Px(o, o.MinLane)) laneH = Math.Max(1, Px(o, o.MinLane));

            // The name column is only worth having when the rows are tall enough to hold a
            // name: otherwise it would simply eat a fifth of the width.
            int nameW = laneH >= Px(o, 9) ? NameWidth(area, o) : 0;

            // Few tracks — centre the block rather than pushing it to the top.
            int totalH = n * laneH + gap * (n - 1);
            int top = area.Y + rulerH + Math.Max(0, (area.Height - rulerH - totalH) / 2);

            Rectangle plot = new Rectangle(area.X + nameW, top,
                                           Math.Max(1, area.Width - nameW), Math.Max(1, totalH));
            double px = plot.Width / Math.Max(a.End, 4);
            PaintBody(g, plot, a, o, px, laneH, gap, plot.X, plot.Y);
            PaintText(g, area, plot, nameW, rulerH, a, o, px, laneH, gap, plot.X, plot.Y);
        }

        /// <summary>
        /// The arrangement at a scale of its own instead of fitted: pxPerBeat across, laneH down,
        /// the tracks moved left by scrollX and up by scrollY pixels — what the full-screen
        /// preview scrolls and zooms. A block of tracks lower than the area is centred, as the
        /// fitted picture centres it.
        ///
        /// In two layers. DrawView puts down the clips and the grid; DrawViewText the names in
        /// their column and the bar numbers on the ruler. The view keeps the first as a picture
        /// and lays the second straight onto the screen: GDI text drawn into a picture copies
        /// the whole picture out and back for every line — 20 ms of a 22 ms frame.
        /// </summary>
        public static void DrawView(Graphics g, Rectangle area, Arrangement a, RenderOptions o,
                                    double pxPerBeat, int laneH, double scrollX, int scrollY)
        {
            if (a == null || a.Tracks.Count == 0) return;
            Rectangle plot = Plot(area, o);
            PaintBody(g, plot, a, o, pxPerBeat, laneH, Gap(laneH, o), plot.X - scrollX, ViewTop(plot, a, o, laneH, scrollY));
        }

        public static void DrawViewText(Graphics g, Rectangle area, Arrangement a, RenderOptions o,
                                        double pxPerBeat, int laneH, double scrollX, int scrollY)
        {
            if (a == null || a.Tracks.Count == 0) return;
            Rectangle plot = Plot(area, o);
            PaintText(g, area, plot, plot.X - area.X, RulerHeight(o), a, o, pxPerBeat, laneH, Gap(laneH, o),
                      plot.X - scrollX, ViewTop(plot, a, o, laneH, scrollY));
        }

        /// <summary>Where the first track's top falls in the view.</summary>
        static int ViewTop(Rectangle plot, Arrangement a, RenderOptions o, int laneH, int scrollY)
        {
            int totalH = LanesHeight(a.Tracks.Count, laneH, o);
            return totalH < plot.Height ? plot.Y + (plot.Height - totalH) / 2 : plot.Y - scrollY;
        }

        // Measured with TextRenderer.MeasureText: a digit in FBadge needs 19px of height — the
        // old 15 (and even 20 less the offset) were just under that, hence the clipped tops of
        // the digits.
        public static int RulerHeight(RenderOptions o) { return o.ShowRuler ? Px(o, 24) : 0; }

        public static int NameWidth(Rectangle area, RenderOptions o)
        {
            return o.ShowNames && area.Width > Px(o, 420) ? Math.Min(Px(o, 170), area.Width / 5) : 0;
        }

        /// <summary>Where the tracks go in DrawView: the area less the name column and the
        /// ruler.</summary>
        public static Rectangle Plot(Rectangle area, RenderOptions o)
        {
            int nameW = NameWidth(area, o), rulerH = RulerHeight(o);
            return new Rectangle(area.X + nameW, area.Y + rulerH,
                                 Math.Max(1, area.Width - nameW), Math.Max(1, area.Height - rulerH));
        }

        public static int Gap(int laneH, RenderOptions o) { return laneH >= 8 ? Px(o, 2) : 1; }

        public static int LanesHeight(int tracks, int laneH, RenderOptions o)
        {
            return tracks * laneH + Gap(laneH, o) * Math.Max(0, tracks - 1);
        }

        /// <summary>
        /// The clips, their notes and the grid, whatever the layout: x0 is where beat 0 falls
        /// and y0 the first track's top. Nothing leaves plot — with the view scrolled, a clip
        /// does not run under the names.
        /// </summary>
        static void PaintBody(Graphics g, Rectangle plot, Arrangement a, RenderOptions o,
                              double pxPerBeat, int laneH, int gap, double x0, int y0)
        {
            int n = a.Tracks.Count;
            int step = laneH + gap;
            int first = Math.Max(0, (plot.Y - y0) / Math.Max(1, step));
            int blockTop = Math.Max(plot.Y, y0);
            int blockBottom = Math.Min(plot.Bottom, y0 + n * laneH + gap * (n - 1));

            GraphicsState saved = g.Save();
            g.SetClip(plot);
            Grid(g, plot, o, Math.Max(a.End, 4), pxPerBeat, a.BarBeats > 0 ? a.BarBeats : 4, x0, blockTop, blockBottom);

            int notesDrawn = 0;
            for (int i = first; i < n; i++)
            {
                TrackLane t = a.Tracks[i];
                int y = y0 + i * step;
                if (y > plot.Bottom) break;

                // A group has no clips of its own — we show only a strip on the left in its
                // colour, so the structure is visible without the empty row looking like a
                // failure.
                if (t.IsGroup && t.Clips.Count == 0)
                {
                    if (t.Color >= 0)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(0x66, LiveColors.Get(t.Color))))
                            g.FillRectangle(b, plot.X, y + laneH / 2, Math.Max(2, Px(o, 3)), Math.Max(1, laneH / 6 + 1));
                    continue;
                }

                foreach (ClipBlock c in t.Clips)
                {
                    float cx0 = (float)(x0 + c.Start * pxPerBeat);
                    float cx1 = (float)(x0 + c.End * pxPerBeat);
                    if (cx1 < plot.X || cx0 > plot.Right) continue;
                    RectangleF rect = new RectangleF(cx0, y, Math.Max(1f, cx1 - cx0), laneH);

                    Color col = LiveColors.Get(c.Color >= 0 ? c.Color : t.Color);
                    if (c.Disabled) col = Desaturate(col);

                    bool hasNotes = c.IsMidi && c.Notes != null && c.Notes.Count > 0;
                    int bodyAlpha = c.Disabled ? 0x50 : hasNotes ? 0x59 : 0xEE;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(bodyAlpha, col)))
                        g.FillRectangle(b, rect);

                    if (hasNotes && notesDrawn < MaxNotesDrawn)
                        notesDrawn += DrawNotes(g, rect, x0, plot, c, col, pxPerBeat, c.Disabled ? 0x90 : 0xFF);
                }
            }

            g.Restore(saved);
        }

        /// <summary>
        /// The names in their column beside plot and the bar numbers on the ruler above it. GDI
        /// text keeps to a Graphics clip only when asked (PreserveGraphicsClipping): so a
        /// half-scrolled name stays out of the ruler and a number out of the names.
        /// </summary>
        static void PaintText(Graphics g, Rectangle area, Rectangle plot, int nameW, int rulerH,
                              Arrangement a, RenderOptions o, double pxPerBeat, int laneH, int gap,
                              double x0, int y0)
        {
            int n = a.Tracks.Count;
            int step = laneH + gap;
            int first = Math.Max(0, (plot.Y - y0) / Math.Max(1, step));
            GraphicsState saved = g.Save();
            if (nameW > 0)
            {
                g.SetClip(new Rectangle(area.X, plot.Y, nameW, plot.Height));
                for (int i = first; i < n; i++)
                {
                    int y = y0 + i * step;
                    if (y > plot.Bottom) break;
                    DrawName(g, new Rectangle(area.X, y, nameW - Px(o, 8), laneH), a.Tracks[i], laneH, o);
                }
            }
            if (rulerH > 0)
            {
                g.SetClip(new Rectangle(plot.X, area.Y, Math.Max(0, area.Right - plot.X), rulerH));
                Ruler(g, area, plot, o, Math.Max(a.End, 4), pxPerBeat, a.BarBeats > 0 ? a.BarBeats : 4, x0, rulerH);
            }
            g.Restore(saved);
        }

        // ------------------------------------------------------------------- notes

        /// <summary>A midi clip's notes over its body; origin is where beat 0 falls, and what
        /// lies outside plot is not drawn.</summary>
        static int DrawNotes(Graphics g, RectangleF rect, double origin, Rectangle plot, ClipBlock c,
                             Color col, double pxPerBeat, int alpha)
        {
            int lo = c.MinPitch, hi = c.MaxPitch;
            int span = Math.Max(hi - lo + 1, 6);
            float inner = Math.Max(2f, rect.Height - 1f);
            float noteH = Math.Max(1f, inner / span);

            double loopLen = c.LoopLength;
            bool loop = c.LoopOn && loopLen > 0.0001;
            double contentAtStart = c.LoopStart + c.StartRelative;
            int repeats = 1;
            if (loop)
            {
                repeats = (int)Math.Ceiling((c.Length + (contentAtStart - c.LoopStart)) / loopLen) + 1;
                if (repeats > MaxLoopRepeats) repeats = MaxLoopRepeats;
                if (repeats < 1) repeats = 1;
            }

            List<RectangleF> boxes = new List<RectangleF>();
            for (int k = 0; k < repeats; k++)
            {
                double shift = c.Start - contentAtStart + k * (loop ? loopLen : 0);
                for (int i = 0; i < c.Notes.Count; i++)
                {
                    NoteEvent nt = c.Notes[i];
                    double s = nt.Time + shift;
                    double e = s + Math.Max(nt.Duration, 0.05);
                    if (e <= c.Start || s >= c.End) continue;
                    if (s < c.Start) s = c.Start;
                    if (e > c.End) e = c.End;

                    float x0 = (float)(origin + s * pxPerBeat);
                    float x1 = (float)(origin + e * pxPerBeat);
                    if (x1 < plot.X || x0 > plot.Right) continue;    // scrolled out of the view
                    float w = Math.Max(1f, x1 - x0);
                    float y = rect.Bottom - (nt.Pitch - lo + 1) * noteH - 0.5f;
                    if (y < rect.Y) y = rect.Y;
                    boxes.Add(new RectangleF(x0, y, w, noteH));
                    if (boxes.Count >= MaxNotesDrawn) break;
                }
                if (boxes.Count >= MaxNotesDrawn) break;
            }

            if (boxes.Count == 0) return 0;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, col)))
                g.FillRectangles(b, boxes.ToArray());
            return boxes.Count;
        }

        // ------------------------------------------------------------ grid and names

        /// <summary>
        /// How far apart the bar lines stand: whole bars, doubled until they are at least 56
        /// screen points apart — a bar each zoomed in, every eighth on a whole song.
        /// </summary>
        static double GridStep(RenderOptions o, double pxPerBeat, double bar)
        {
            double minStep = Px(o, 56) / Math.Max(0.0001, pxPerBeat);
            double step = bar;
            while (step < minStep) step *= 2;
            return step;
        }

        /// <summary>The first line's number at or left of plot's edge — a scrolled view starts
        /// there rather than at the song's start.</summary>
        static int FirstStep(Rectangle plot, double x0, double step, double pxPerBeat)
        {
            return Math.Max(0, (int)Math.Floor((plot.X - x0) / (step * pxPerBeat)));
        }

        static void Grid(Graphics g, Rectangle plot, RenderOptions o, double end, double pxPerBeat,
                         double bar, double x0, int top, int bottom)
        {
            // The beats are counted from the line's number rather than added up: a 7/8 bar is
            // 3.5 beats, and a sum drifts.
            double step = GridStep(o, pxPerBeat, bar);
            using (Pen p = new Pen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)))
                for (int k = Math.Max(1, FirstStep(plot, x0, step, pxPerBeat)); k * step < end; k++)
                {
                    float x = (float)(x0 + k * step * pxPerBeat);
                    if (x > plot.Right) break;
                    g.DrawLine(p, x, top, x, bottom);
                }
        }

        static void Ruler(Graphics g, Rectangle area, Rectangle plot, RenderOptions o, double end,
                          double pxPerBeat, double bar, double x0, int rulerH)
        {
            double step = GridStep(o, pxPerBeat, bar);
            int rTop = area.Y + Px(o, 2);
            int rHeight = Math.Max(1, rulerH - Px(o, 2));
            // NoClipping is a safeguard: if on a particular font or DPI a digit still comes out
            // a pixel taller than calculated, let it spill into the empty background rather
            // than be cut off at the rectangle's edge. The ruler's strip is the clip — see
            // PaintText.
            TextFormatFlags rulerFlags = Chrome.Left | TextFormatFlags.NoClipping | TextFormatFlags.PreserveGraphicsClipping;
            for (int k = FirstStep(plot, x0, step, pxPerBeat); k * step < end; k++)
            {
                double beat = k * step;
                float x = (float)(x0 + beat * pxPerBeat);
                if (x + 2 < plot.X) continue;        // scrolled in under the names
                if (x > area.Right) break;
                // A number that does not fit whole is left out: at the right edge "61" came
                // out as "6".
                string label = ((int)Math.Round(beat / bar) + 1).ToString();
                if (x + 2 + TextRenderer.MeasureText(label, Theme.FBadge).Width > area.Right) break;
                Chrome.DrawText(g, label, Theme.FBadge,
                    new Rectangle((int)x + 2, rTop, Px(o, 44), rHeight), Theme.TextDim, rulerFlags);
            }
        }

        static void DrawName(Graphics g, Rectangle r, TrackLane t, int laneH, RenderOptions o)
        {
            if (laneH < Px(o, 9) || r.Width <= 0) return;
            // A row can be exactly the height of the font, so captions get slightly more room
            // than the track itself: otherwise the descenders of the letters are shaved off.
            int indent = t.Indent * Px(o, 8);
            Rectangle box = new Rectangle(r.X + indent, r.Y - Px(o, 2),
                                          Math.Max(0, r.Width - indent), laneH + Px(o, 4));
            Color c = t.Color >= 0 ? Blend(LiveColors.Get(t.Color), Theme.Text, 0.55f) : Theme.TextDim;
            Chrome.DrawText(g, t.Name, Theme.FBadge, box, c, Chrome.Left | TextFormatFlags.PreserveGraphicsClipping);
        }

        // ---------------------------------------------------------------- utilities

        static int Px(RenderOptions o, int v) { return (int)Math.Round(v * o.Dpi); }

        static Color Desaturate(Color c)
        {
            int grey = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
            return Color.FromArgb(c.A, (c.R + grey) / 2, (c.G + grey) / 2, (c.B + grey) / 2);
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(255,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>A finished picture — the preview in the detail panel is redrawn on every
        /// frame.</summary>
        public static Bitmap ToBitmap(Arrangement a, int width, int height, RenderOptions o)
        {
            if (width <= 0 || height <= 0) return null;
            Bitmap bmp = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Draw(g, new Rectangle(0, 0, width, height), a, o);
            }
            return bmp;
        }
    }
}
