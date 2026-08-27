using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace AbletonManager
{
    public enum Glyph
    {
        Folder, Refresh, Minimize, Maximize, Close, CloseFullscreen,
        Filters, Magnifier, ChevronDown, SortUp, SortDown, Check,
        Play, Pause, Volume, Mute, Star, StarFill, Plus,
        NextSet, PrevSet, NextTrack, PrevTrack, OpenPlaylist, ViewTiles, ViewList,
        Note, Tag, Dice
    }

    public static class Icons
    {
        // Перья значков: цвет и толщина повторяются кадр за кадром (звёздочка и play в
        // каждой строке таблицы — это десятки Pen на кадр), а Pen — объект GDI+, и
        // создание его не бесплатно. Набор сочетаний невелик и конечен, поэтому просто
        // держим готовые. Рисуют значки только из потока интерфейса.
        static readonly Dictionary<long, Pen> _penCache = new Dictionary<long, Pen>();

        static Pen GetPen(Color color, float stroke)
        {
            long key = ((long)(uint)color.ToArgb() << 16) | (long)(ushort)(stroke * 100f);
            Pen p;
            if (!_penCache.TryGetValue(key, out p))
            {
                // Плавные подсветки дают промежуточные оттенки, и набор хоть и конечен,
                // но не крошечный. Потолок ставим на всякий случай; Dispose тут не зовём —
                // перо этого же кадра ещё может быть в работе, а брошенное освободит сборщик.
                if (_penCache.Count > 256) _penCache.Clear();

                p = new Pen(color, stroke);
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                _penCache[key] = p;
            }
            return p;
        }

        public static void Draw(Graphics g, Glyph glyph, RectangleF box, Color color, float stroke)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Paint(g, glyph, box, GetPen(color, stroke), Theme.GetBrush(color));
            g.SmoothingMode = old;
        }

        static void Paint(Graphics g, Glyph glyph, RectangleF r, Pen p, SolidBrush b)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float cx = x + w / 2f, cy = y + h / 2f;

            switch (glyph)
            {
                case Glyph.Folder:
                    DrawSvg(g, p, null, r, 16, 14, () => {
                        g.DrawPath(p, GetSvgPath("M1 2.50012V10.6001C1 11.4402 1 11.86 1.16954 12.1808C1.31868 12.4631 1.55647 12.693 1.84917 12.8368C2.18159 13.0001 2.61698 13.0001 3.48645 13.0001H12.5134C13.3829 13.0001 13.8176 13.0001 14.15 12.8368C14.4427 12.693 14.6813 12.4632 14.8305 12.181C15 11.8601 15 11.4401 15 10.6L15 4.89996C15 4.05988 15 3.63984 14.8305 3.31897C14.6813 3.03673 14.4429 2.80742 14.1502 2.66361C13.8174 2.50012 13.3822 2.50012 12.511 2.50012H7.99992M1 2.50012H7.99992M1 2.50012C1 1.6717 1.69644 1.00012 2.55554 1.00012H5.41346C5.79393 1.00012 5.98461 1.00012 6.16364 1.04157C6.32236 1.07831 6.47382 1.13907 6.613 1.22131C6.76993 1.31405 6.90467 1.44398 7.17354 1.70325L7.99992 2.50012"));
                    });
                    break;

                case Glyph.Refresh:
                    DrawSvg(g, p, null, r, 14, 16, () => {
                        g.DrawPath(p, GetSvgPath("M5.06299 10.75H1.31299V14.5M8.06299 4.75H11.813V1M1.00024 5.50255C1.42076 4.46175 2.12482 3.55978 3.0324 2.89917C3.93998 2.23856 5.01563 1.84564 6.1353 1.76538C7.25498 1.68512 8.37404 1.92055 9.36657 2.44496C10.3591 2.96937 11.1839 3.7619 11.7486 4.73209M12.1262 9.9978C11.7056 11.0386 11.0016 11.9406 10.094 12.6012C9.18641 13.2618 8.11186 13.6542 6.99219 13.7345C5.87251 13.8147 4.75254 13.5793 3.76001 13.0549C2.76748 12.5305 1.9421 11.7381 1.37744 10.7679"));
                    });
                    break;

                case Glyph.OpenPlaylist:
                    DrawSvg(g, p, b, r, 15, 14, () => {
                        g.DrawPath(p, GetSvgPath("M7 2.8L14 2.8"));
                        g.DrawLine(p, 1f, 7.8f, 14f, 7.8f);
                        g.DrawLine(p, 1f, 12.8f, 14f, 12.8f);
                        g.FillPath(b, GetSvgPath("M0.811523 0.447462C0.941478 0.377985 1.09902 0.385338 1.22168 0.466993L4.22168 2.46699C4.33296 2.54118 4.40039 2.66626 4.40039 2.8C4.40039 2.93374 4.33296 3.05882 4.22168 3.13301L1.22168 5.13301C1.09902 5.21466 0.941478 5.22202 0.811523 5.15254C0.68146 5.08293 0.599609 4.94752 0.599609 4.8V0.800001C0.599609 0.652483 0.68146 0.51707 0.811523 0.447462Z"));
                    });
                    break;

                case Glyph.Minimize:
                    DrawSvg(g, p, null, r, 16, 16, () => {
                        g.DrawLine(p, 1f, 8f, 15f, 8f);
                    });
                    break;

                case Glyph.Maximize:
                    DrawSvg(g, p, null, r, 16, 16, () => {
                        g.DrawPath(p, GetSvgPath("M5.82887 13.8333H1V9.25M9.69197 1H14.5208V5.58333"));
                    });
                    break;

                case Glyph.CloseFullscreen:
                    DrawSvg(g, p, null, r, 16, 16, () => {
                        g.DrawPath(p, GetSvgPath("M4.6631 14.2666V10.6H0.8M10.9536 0.8V4.4666H14.8166"));
                    });
                    break;

                case Glyph.Close:
                    DrawSvg(g, p, null, r, 16, 16, () => {
                        g.DrawPath(p, GetSvgPath("M14 1L7.50919 7.49999L14 14L7.5 7.50919L1.00001 14L7.49081 7.49999L1.00001 1L7.5 7.4908L14 1Z"));
                    });
                    break;

                case Glyph.Plus:
                    g.DrawLine(p, cx, y + h * 0.14f, cx, r.Bottom - h * 0.14f);
                    g.DrawLine(p, x + w * 0.14f, cy, r.Right - w * 0.14f, cy);
                    break;

                // Листок с текстом: рамка и три строки разной длины — последняя короче,
                // иначе на мелком размере читается как просто заштрихованный квадрат.
                case Glyph.Note:
                {
                    RectangleF box = new RectangleF(x + w * 0.16f, y + h * 0.10f, w * 0.68f, h * 0.80f);
                    using (GraphicsPath gp = Theme.Round(box, w * 0.12f)) g.DrawPath(p, gp);
                    float lx = box.Left + w * 0.12f, rx = box.Right - w * 0.12f;
                    g.DrawLine(p, lx, box.Top + box.Height * 0.30f, rx, box.Top + box.Height * 0.30f);
                    g.DrawLine(p, lx, box.Top + box.Height * 0.53f, rx, box.Top + box.Height * 0.53f);
                    g.DrawLine(p, lx, box.Top + box.Height * 0.76f,
                                  lx + (rx - lx) * 0.55f, box.Top + box.Height * 0.76f);
                    break;
                }

                // Ярлык-бирка: пятиугольник остриём влево и дырка под шнурок. Пропорции
                // подобраны под мелкий размер (15 px в панели сведений): у более острого
                // угла и крупной дырки на нём вместо бирки читалась стрелка.
                case Glyph.Tag:
                {
                    float top = y + h * 0.20f, bot = r.Bottom - h * 0.20f;
                    g.DrawLines(p, new PointF[] {
                        new PointF(x + w * 0.06f, cy),
                        new PointF(x + w * 0.30f, top),
                        new PointF(r.Right - w * 0.08f, top),
                        new PointF(r.Right - w * 0.08f, bot),
                        new PointF(x + w * 0.30f, bot),
                        new PointF(x + w * 0.06f, cy) });
                    g.DrawEllipse(p, x + w * 0.30f, cy - h * 0.05f, w * 0.10f, h * 0.10f);
                    break;
                }

                case Glyph.Filters:
                    DrawSvg(g, p, null, r, 20, 13, () => {
                        g.DrawPath(p, GetSvgPath("M12 9.5H19M1 9.5H3M3 9.5C3 10.8807 4.11929 12 5.5 12C6.88071 12 8 10.8807 8 9.5C8 8.11929 6.88071 7 5.5 7C4.11929 7 3 8.11929 3 9.5ZM18 3.5H19M1 3.5H8M14.5 6C13.1193 6 12 4.88071 12 3.5C12 2.11929 13.1193 1 14.5 1C15.8807 1 17 2.11929 17 3.5C17 4.88071 15.8807 6 14.5 6Z"));
                    });
                    break;

                case Glyph.Magnifier:
                {
                    float rad = w * 0.32f;
                    g.DrawEllipse(p, x + w * 0.10f, y + h * 0.10f, rad * 2, rad * 2);
                    g.DrawLine(p, x + w * 0.10f + rad * 1.75f, y + h * 0.10f + rad * 1.75f,
                                  r.Right - w * 0.08f, r.Bottom - h * 0.08f);
                    break;
                }

                case Glyph.ChevronDown:
                    g.DrawLines(p, new PointF[] {
                        new PointF(cx - w * 0.26f, cy - h * 0.12f),
                        new PointF(cx, cy + h * 0.14f),
                        new PointF(cx + w * 0.26f, cy - h * 0.12f) });
                    break;

                case Glyph.SortUp:
                case Glyph.SortDown:
                {
                    float half = w * 0.30f, ht = h * 0.26f;
                    PointF[] tri = glyph == Glyph.SortUp
                        ? new PointF[] { new PointF(cx, cy - ht), new PointF(cx + half, cy + ht), new PointF(cx - half, cy + ht) }
                        : new PointF[] { new PointF(cx, cy + ht), new PointF(cx + half, cy - ht), new PointF(cx - half, cy - ht) };
                    g.FillPolygon(b, tri);
                    break;
                }

                case Glyph.Check:
                    g.DrawLines(p, new PointF[] {
                        new PointF(x + w * 0.14f, y + h * 0.52f),
                        new PointF(x + w * 0.40f, y + h * 0.76f),
                        new PointF(x + w * 0.88f, y + h * 0.22f) });
                    break;

                case Glyph.Play:
                    DrawSvg(g, p, b, r, 17, 19, () => {
                        g.FillPath(b, GetSvgPath("M15.5347 8.39118C16.192 8.77783 16.192 9.7284 15.5347 10.115L1.50702 18.3666C0.840386 18.7588 -3.56578e-07 18.2781 -3.22771e-07 17.5047L3.98605e-07 1.00153C4.32412e-07 0.228114 0.840387 -0.252541 1.50702 0.139596L15.5347 8.39118Z"));
                    }, false, 0.72f);
                    break;

                case Glyph.Pause:
                    DrawSvg(g, p, b, r, 15, 20, () => {
                        FillRoundRect(g, b, 0, 0, 5, 20, 1);
                        FillRoundRect(g, b, 10, 0, 5, 20, 1);
                    }, false, 0.72f);
                    break;

                case Glyph.NextSet:
                    DrawSvg(g, p, b, r, 9, 10, () => {
                        g.FillPath(b, GetSvgPath("M7.42652 4.12584C8.11232 4.50685 8.11233 5.49315 7.42652 5.87416L1.48564 9.17464C0.819112 9.54494 -6.0748e-07 9.06297 -5.74151e-07 8.30049L-2.85614e-07 1.69951C-2.52284e-07 0.937031 0.819111 0.455063 1.48564 0.825358L7.42652 4.12584Z"));
                        FillRoundRect(g, b, 7, 0, 2, 10, 1);
                    }, false, 0.72f);
                    break;

                case Glyph.PrevSet:
                    DrawSvg(g, p, b, r, 9, 10, () => {
                        g.FillPath(b, GetSvgPath("M7.42652 4.12584C8.11232 4.50685 8.11233 5.49315 7.42652 5.87416L1.48564 9.17464C0.819112 9.54494 -6.0748e-07 9.06297 -5.74151e-07 8.30049L-2.85614e-07 1.69951C-2.52284e-07 0.937031 0.819111 0.455063 1.48564 0.825358L7.42652 4.12584Z"));
                        FillRoundRect(g, b, 7, 0, 2, 10, 1);
                    }, true, 0.72f);
                    break;

                case Glyph.NextTrack:
                    DrawSvg(g, p, b, r, 13, 10, () => {
                        g.FillPath(b, GetSvgPath("M11.4265 4.12584C12.1123 4.50685 12.1123 5.49315 11.4265 5.87416L5.48564 9.17464C4.81911 9.54494 4 9.06297 4 8.30049L4 1.69951C4 0.937031 4.81911 0.455063 5.48564 0.825358L11.4265 4.12584Z"));
                        FillRoundRect(g, b, 0, 0, 2, 10, 1);
                    }, false, 0.72f);
                    break;

                case Glyph.PrevTrack:
                    DrawSvg(g, p, b, r, 13, 10, () => {
                        g.FillPath(b, GetSvgPath("M11.4265 4.12584C12.1123 4.50685 12.1123 5.49315 11.4265 5.87416L5.48564 9.17464C4.81911 9.54494 4 9.06297 4 8.30049L4 1.69951C4 0.937031 4.81911 0.455063 5.48564 0.825358L11.4265 4.12584Z"));
                        FillRoundRect(g, b, 0, 0, 2, 10, 1);
                    }, true, 0.72f);
                    break;

                case Glyph.Volume:
                    DrawSvg(g, p, b, r, 12, 17, () => {
                        g.FillPath(b, GetSvgPath("M0 5.04762C0 4.49534 0.447715 4.04762 1 4.04762H1.64597C1.87502 4.04762 2.09714 3.96898 2.27517 3.82486L5.3708 1.31887C6.02462 0.789594 7 1.25492 7 2.09612V14.9039C7 15.7451 6.02462 16.2104 5.3708 15.6811L2.27517 13.1751C2.09714 13.031 1.87502 12.9524 1.64597 12.9524H1C0.447715 12.9524 0 12.5047 0 11.9524V5.04762Z"));
                        g.DrawPath(p, GetSvgPath("M10.25 5C10.25 5 11.25 6.59707 11.25 8.5C11.25 10.4029 10.25 12 10.25 12"));
                    });
                    break;

                case Glyph.Mute:
                    DrawSvg(g, p, b, r, 14, 17, () => {
                        g.FillPath(b, GetSvgPath("M0 5.04762C0 4.49534 0.447716 4.04762 1 4.04762H1.38291C1.60296 4.04762 1.81686 3.97504 1.99147 3.84113L5.39145 1.23363C6.04927 0.729134 7 1.19814 7 2.02714V14.9729C7 15.8019 6.04926 16.2709 5.39144 15.7664L1.99147 13.1589C1.81686 13.025 1.60296 12.9524 1.38291 12.9524H1C0.447716 12.9524 0 12.5047 0 11.9524V5.04762Z"));
                        g.DrawPath(p, GetSvgPath("M10 12L13 5M13 12L10 5"));
                    });
                    break;

                case Glyph.Star:
                    DrawSvg(g, p, null, r, 19, 19, () => {
                        g.DrawPath(p, GetSvgPath("M0.886083 7.68279C0.615124 7.42002 0.762309 6.94498 1.1288 6.89941L6.32186 6.25349C6.47123 6.23491 6.60097 6.13655 6.66397 5.99332L8.85434 1.01361C9.00892 0.662175 9.48535 0.662108 9.63993 1.01355L11.8303 5.99321C11.8933 6.13645 12.0222 6.23507 12.1716 6.25365L17.3649 6.89941C17.7314 6.94498 17.8782 7.42016 17.6072 7.68293L13.7683 11.4065C13.6578 11.5136 13.6087 11.673 13.638 11.8277L14.6568 17.2064C14.7288 17.586 14.3435 17.8802 14.0215 17.6911L9.45834 15.0119C9.32709 14.9348 9.16763 14.9352 9.03638 15.0122L4.47276 17.6904C4.15072 17.8795 3.76477 17.586 3.83669 17.2064L4.8557 11.828C4.88501 11.6733 4.83597 11.5136 4.72554 11.4065L0.886083 7.68279Z"));
                    });
                    break;

                case Glyph.StarFill:
                    DrawSvg(g, p, b, r, 19, 19, () => {
                        g.FillPath(b, GetSvgPath("M0.886083 7.68279C0.615124 7.42002 0.762309 6.94498 1.1288 6.89941L6.32186 6.25349C6.47123 6.23492 6.60097 6.13655 6.66397 5.99332L8.85434 1.01361C9.00892 0.662175 9.48535 0.662108 9.63993 1.01355L11.8303 5.99321C11.8933 6.13645 12.0222 6.23507 12.1716 6.25365L17.3649 6.89941C17.7314 6.94498 17.8782 7.42016 17.6072 7.68293L13.7683 11.4065C13.6578 11.5136 13.6087 11.673 13.638 11.8277L14.6568 17.2064C14.7288 17.586 14.3435 17.8802 14.0215 17.6911L9.45834 15.0119C9.32709 14.9348 9.16763 14.9352 9.03638 15.0122L4.47276 17.6904C4.15072 17.8795 3.76477 17.586 3.83669 17.2064L4.8557 11.828C4.88501 11.6733 4.83597 11.5136 4.72554 11.4065L0.886083 7.68279Z"));
                    });
                    break;

                case Glyph.ViewTiles:
                    DrawSvg(g, p, null, r, 20, 20, () => {
                        DrawRoundRect(g, p, 1f, 1f, 7.33333f, 7.33333f, 3.66667f);
                        DrawRoundRect(g, p, 11.6667f, 1f, 7.33333f, 7.33333f, 3.66667f);
                        DrawRoundRect(g, p, 11.6667f, 11.6667f, 7.33333f, 7.33333f, 3.66667f);
                        DrawRoundRect(g, p, 1f, 11.6667f, 7.33333f, 7.33333f, 3.66667f);
                    });
                    break;

                case Glyph.ViewList:
                    DrawSvg(g, p, null, r, 20, 20, () => {
                        DrawRoundRect(g, p, 1f, 1f, 18f, 3.33333f, 1.66667f);
                        DrawRoundRect(g, p, 1f, 8.33334f, 18f, 3.33333f, 1.66667f);
                        DrawRoundRect(g, p, 1f, 15.6667f, 18f, 3.33333f, 1.66667f);
                    });
                    break;

                // New Icons/Dice.svg: гранёный квадрат и пять точек — «кинуть кость»
                // для случайного выбора сета.
                case Glyph.Dice:
                    DrawSvg(g, p, b, r, 20, 20, () => {
                        DrawRoundRect(g, p, 1f, 1f, 18f, 18f, 5f);
                        FillDot(g, b, 5.5f, 5.5f, 1.5f);
                        FillDot(g, b, 14.5f, 5.5f, 1.5f);
                        FillDot(g, b, 10f, 10f, 1.5f);
                        FillDot(g, b, 5.5f, 14.5f, 1.5f);
                        FillDot(g, b, 14.5f, 14.5f, 1.5f);
                    });
                    break;
            }
        }

        static void DrawRoundRect(Graphics g, Pen p, float x, float y, float w, float h, float rx)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, rx * 2, rx * 2, 180, 90);
                path.AddArc(x + w - rx * 2, y, rx * 2, rx * 2, 270, 90);
                path.AddArc(x + w - rx * 2, y + h - rx * 2, rx * 2, rx * 2, 0, 90);
                path.AddArc(x, y + h - rx * 2, rx * 2, rx * 2, 90, 90);
                path.CloseFigure();
                g.DrawPath(p, path);
            }
        }

        static void FillDot(Graphics g, SolidBrush b, float cx, float cy, float radius)
        {
            g.FillEllipse(b, cx - radius, cy - radius, radius * 2, radius * 2);
        }

        static void FillRoundRect(Graphics g, SolidBrush b, float x, float y, float w, float h, float rx)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, rx * 2, rx * 2, 180, 90);
                path.AddArc(x + w - rx * 2, y, rx * 2, rx * 2, 270, 90);
                path.AddArc(x + w - rx * 2, y + h - rx * 2, rx * 2, rx * 2, 0, 90);
                path.AddArc(x, y + h - rx * 2, rx * 2, rx * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        static void DrawSvg(Graphics g, Pen p, SolidBrush b, RectangleF r, float vw, float vh, Action drawAction, bool flipX = false, float contentScale = 1.0f)
        {
            Matrix old = g.Transform;
            try
            {
                float baseScale = Math.Min(r.Width / vw, r.Height / vh);
                float scale = baseScale * contentScale;

                float dx = r.X + (r.Width - vw * scale) / 2f;
                float dy = r.Y + (r.Height - vh * scale) / 2f;

                using (Matrix m = old.Clone())
                {
                    m.Translate(dx, dy);
                    if (flipX)
                    {
                        m.Translate(vw * scale, 0);
                        m.Scale(-scale, scale);
                    }
                    else
                    {
                        m.Scale(scale, scale);
                    }
                    g.Transform = m;
                    drawAction();
                }
            }
            finally
            {
                g.Transform = old;
            }
        }

        // ----------------------- SVG Parser -----------------------

        static readonly Dictionary<string, GraphicsPath> _svgCache = new Dictionary<string, GraphicsPath>();

        static GraphicsPath GetSvgPath(string d)
        {
            if (string.IsNullOrEmpty(d)) return null;
            GraphicsPath path;
            if (!_svgCache.TryGetValue(d, out path))
            {
                path = ParseSvg(d);
                _svgCache[d] = path;
            }
            return path;
        }

        static GraphicsPath ParseSvg(string d)
        {
            GraphicsPath path = new GraphicsPath();
            if (string.IsNullOrEmpty(d)) return path;

            int i = 0;
            char cmd = ' ';
            PointF current = new PointF(0, 0);
            PointF start = new PointF(0, 0);
            PointF lastPt = new PointF(0, 0);

            while (i < d.Length)
            {
                while (i < d.Length && char.IsWhiteSpace(d[i])) i++;
                if (i >= d.Length) break;

                if (char.IsLetter(d[i]))
                {
                    cmd = d[i];
                    i++;
                }

                if (cmd == 'Z' || cmd == 'z')
                {
                    path.CloseFigure();
                    current = start;
                    lastPt = current;
                    continue;
                }

                float[] args = ReadArgs(d, ref i);

                switch (cmd)
                {
                    case 'M':
                        for (int k = 0; k < args.Length; k += 2)
                        {
                            current = new PointF(args[k], args[k + 1]);
                            if (k == 0)
                            {
                                start = current;
                                path.StartFigure();
                                lastPt = current;
                            }
                            else
                            {
                                path.AddLine(lastPt, current);
                                lastPt = current;
                            }
                        }
                        cmd = 'L';
                        break;
                    case 'L':
                        for (int k = 0; k < args.Length; k += 2)
                        {
                            PointF pt = new PointF(args[k], args[k + 1]);
                            path.AddLine(current, pt);
                            current = pt;
                            lastPt = current;
                        }
                        break;
                    case 'H':
                        for (int k = 0; k < args.Length; k++)
                        {
                            PointF pt = new PointF(args[k], current.Y);
                            path.AddLine(current, pt);
                            current = pt;
                            lastPt = current;
                        }
                        break;
                    case 'V':
                        for (int k = 0; k < args.Length; k++)
                        {
                            PointF pt = new PointF(current.X, args[k]);
                            path.AddLine(current, pt);
                            current = pt;
                            lastPt = current;
                        }
                        break;
                    case 'C':
                        for (int k = 0; k < args.Length; k += 6)
                        {
                            PointF pt1 = new PointF(args[k], args[k + 1]);
                            PointF pt2 = new PointF(args[k + 2], args[k + 3]);
                            PointF pt3 = new PointF(args[k + 4], args[k + 5]);
                            path.AddBezier(current, pt1, pt2, pt3);
                            current = pt3;
                            lastPt = current;
                        }
                        break;
                }
            }
            return path;
        }

        static float[] ReadArgs(string d, ref int i)
        {
            List<float> args = new List<float>();
            while (i < d.Length)
            {
                while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
                if (i >= d.Length || char.IsLetter(d[i])) break;

                int start = i;
                if (d[i] == '-') i++;
                while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.' || d[i] == 'e' || d[i] == 'E' || (i > 0 && d[i - 1] == 'e' && (d[i] == '-' || d[i] == '+')))) i++;

                if (i > start)
                {
                    string num = d.Substring(start, i - start);
                    float val;
                    if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        args.Add(val);
                }
            }
            return args.ToArray();
        }
    }
}
