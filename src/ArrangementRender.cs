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
        public bool ShowClipNames;    // clip names inside the blocks
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

        public static readonly Color Ink = Color.FromArgb(0xD0, 0x10, 0x10, 0x12);

        public static void Draw(Graphics g, Rectangle area, Arrangement a, RenderOptions o)
        {
            if (a == null || a.Tracks.Count == 0) return;
            if (o == null) o = new RenderOptions();

            int n = a.Tracks.Count;
            int gap = area.Height / n >= 8 ? Px(o, 2) : 1;
            // Measured with TextRenderer.MeasureText: a digit in FBadge needs 19px of height —
            // the old 15 (and even 20 less the offset) were just under that, hence the clipped
            // tops of the digits.
            int rulerH = o.ShowRuler ? Px(o, 24) : 0;

            int laneH = (area.Height - rulerH - gap * (n - 1)) / n;
            if (laneH > Px(o, o.MaxLane)) laneH = Px(o, o.MaxLane);
            if (laneH < Px(o, o.MinLane)) laneH = Math.Max(1, Px(o, o.MinLane));

            // The name column is only worth having when the rows are tall enough to hold a
            // name: otherwise it would simply eat a fifth of the width.
            int nameW = 0;
            if (o.ShowNames && area.Width > Px(o, 420) && laneH >= Px(o, 9))
                nameW = Math.Min(Px(o, 170), area.Width / 5);

            // Few tracks — centre the block rather than pushing it to the top.
            int totalH = n * laneH + gap * (n - 1);
            int top = area.Y + rulerH + Math.Max(0, (area.Height - rulerH - totalH) / 2);

            Rectangle plot = new Rectangle(area.X + nameW, top,
                                           Math.Max(1, area.Width - nameW), Math.Max(1, totalH));

            double end = Math.Max(a.End, 4);
            double pxPerBeat = plot.Width / end;

            GridAndRuler(g, area, plot, o, end, pxPerBeat, rulerH);

            int notesDrawn = 0;
            for (int i = 0; i < n; i++)
            {
                TrackLane t = a.Tracks[i];
                int y = plot.Y + i * (laneH + gap);
                if (y > plot.Bottom) break;

                if (nameW > 0) DrawName(g, new Rectangle(area.X, y, nameW - Px(o, 8), laneH), t, laneH, o);

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
                    float x0 = (float)(plot.X + c.Start * pxPerBeat);
                    float x1 = (float)(plot.X + c.End * pxPerBeat);
                    if (x1 < plot.X || x0 > plot.Right) continue;
                    float w = Math.Max(1f, x1 - x0);
                    RectangleF rect = new RectangleF(x0, y, w, laneH);

                    Color col = LiveColors.Get(c.Color >= 0 ? c.Color : t.Color);
                    if (c.Disabled) col = Desaturate(col);

                    bool hasNotes = c.IsMidi && c.Notes != null && c.Notes.Count > 0;
                    int bodyAlpha = c.Disabled ? 0x50 : hasNotes ? 0x59 : 0xEE;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(bodyAlpha, col)))
                        g.FillRectangle(b, rect);

                    if (hasNotes && notesDrawn < MaxNotesDrawn)
                        notesDrawn += DrawNotes(g, rect, plot, c, col, pxPerBeat, c.Disabled ? 0x90 : 0xFF);

                    if (o.ShowClipNames && laneH >= Px(o, 13) && w >= Px(o, 44) && c.Name.Length > 0)
                        TextRenderer.DrawText(g, c.Name, Theme.FBadge,
                            Rectangle.Round(new RectangleF(rect.X + 3, rect.Y, rect.Width - 5, rect.Height)),
                            Ink, Chrome.Left);
                }
            }
        }

        // ------------------------------------------------------------------- notes

        static int DrawNotes(Graphics g, RectangleF rect, Rectangle plot, ClipBlock c,
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

                    float x0 = (float)(plot.X + s * pxPerBeat);
                    float x1 = (float)(plot.X + e * pxPerBeat);
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

        static void GridAndRuler(Graphics g, Rectangle area, Rectangle plot, RenderOptions o,
                                 double end, double pxPerBeat, int rulerH)
        {
            // Lines on the bar, but no denser than one per 56 screen points.
            double minStep = Px(o, 56) / Math.Max(0.0001, pxPerBeat);
            double step = 16;                                   // 4 bars
            while (step < minStep) step *= 2;

            using (Pen p = new Pen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)))
                for (double beat = step; beat < end; beat += step)
                {
                    float x = (float)(plot.X + beat * pxPerBeat);
                    g.DrawLine(p, x, plot.Y, x, plot.Bottom);
                }

            if (rulerH <= 0) return;
            int rTop = area.Y + Px(o, 2);
            int rHeight = Math.Max(1, rulerH - Px(o, 2));
            // NoClipping is a safeguard: if on a particular font or DPI a digit still comes out
            // a pixel taller than calculated, let it spill into the empty background rather
            // than be cut off at the rectangle's edge.
            TextFormatFlags rulerFlags = Chrome.Left | TextFormatFlags.NoClipping;
            for (double beat = 0; beat < end; beat += step)
            {
                float x = (float)(plot.X + beat * pxPerBeat);
                Chrome.DrawText(g, ((int)(beat / 4) + 1).ToString(), Theme.FBadge,
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
            Chrome.DrawText(g, t.Name, Theme.FBadge, box, c, Chrome.Left);
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
