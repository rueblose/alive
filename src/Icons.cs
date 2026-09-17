using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;

namespace AbletonManager
{
    public enum Glyph
    {
        Folder, Refresh, Settings, Minimize, Maximize, Close, CloseFullscreen,
        Filters, Magnifier, ChevronDown, SortUp, SortDown, Check,
        Play, Pause,
        Volume0, VolumeLow, VolumeHigh,
        Volume = VolumeHigh, Mute = Volume0,
        Volume1_50 = VolumeLow, Volume51_100 = VolumeHigh,
        Star, StarFill, Plus,
        NextSet, PrevSet, NextTrack, PrevTrack, OpenPlaylist, ViewList,
        Note, Tag, Nebula, Keyboard, Calendar, HiddenBtnsOpen, HiddenBtnsClose,
        // Грани кубика идут подряд: MainForm берёт случайную как Dice1 + n.
        Dice1, Dice2, Dice3, Dice4, Dice5, Dice6, Dice = Dice1
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

                case Glyph.Settings:
                    DrawSvg(g, p, null, r, 24, 24, () => {
                        g.DrawPath(p, GetSvgPath("M20.3499 8.92293L19.9837 8.7192C19.9269 8.68756 19.8989 8.67169 19.8714 8.65524C19.5983 8.49165 19.3682 8.26564 19.2002 7.99523C19.1833 7.96802 19.1674 7.93949 19.1348 7.8831C19.1023 7.82677 19.0858 7.79823 19.0706 7.76998C18.92 7.48866 18.8385 7.17515 18.8336 6.85606C18.8331 6.82398 18.8332 6.79121 18.8343 6.72604L18.8415 6.30078C18.8529 5.62025 18.8587 5.27894 18.763 4.97262C18.6781 4.70053 18.536 4.44993 18.3462 4.23725C18.1317 3.99685 17.8347 3.82534 17.2402 3.48276L16.7464 3.1982C16.1536 2.85658 15.8571 2.68571 15.5423 2.62057C15.2639 2.56294 14.9765 2.56561 14.6991 2.62789C14.3859 2.69819 14.0931 2.87351 13.5079 3.22396L13.5045 3.22555L13.1507 3.43741C13.0948 3.47091 13.0665 3.48779 13.0384 3.50338C12.7601 3.6581 12.4495 3.74365 12.1312 3.75387C12.0992 3.7549 12.0665 3.7549 12.0013 3.7549C11.9365 3.7549 11.9024 3.7549 11.8704 3.75387C11.5515 3.74361 11.2402 3.65759 10.9615 3.50224C10.9334 3.48658 10.9056 3.46956 10.8496 3.4359L10.4935 3.22213C9.90422 2.86836 9.60915 2.69121 9.29427 2.62057C9.0157 2.55807 8.72737 2.55634 8.44791 2.61471C8.13236 2.68062 7.83577 2.85276 7.24258 3.19703L7.23994 3.1982L6.75228 3.48124L6.74688 3.48454C6.15904 3.82572 5.86441 3.99672 5.6517 4.23614C5.46294 4.4486 5.32185 4.69881 5.2374 4.97018C5.14194 5.27691 5.14703 5.61896 5.15853 6.3027L5.16568 6.72736C5.16676 6.79166 5.16864 6.82362 5.16817 6.85525C5.16343 7.17499 5.08086 7.48914 4.92974 7.77096C4.9148 7.79883 4.8987 7.8267 4.86654 7.88237C4.83436 7.93809 4.81877 7.96579 4.80209 7.99268C4.63336 8.26452 4.40214 8.49186 4.12733 8.65572C4.10015 8.67193 4.0715 8.68752 4.01521 8.71871L3.65365 8.91908C3.05208 9.25245 2.75137 9.41928 2.53256 9.65669C2.33898 9.86672 2.19275 10.1158 2.10349 10.3882C2.00259 10.6939 2.00267 11.0378 2.00424 11.7255L2.00551 12.2877C2.00706 12.9708 2.00919 13.3122 2.11032 13.6168C2.19979 13.8863 2.34495 14.134 2.53744 14.3427C2.75502 14.5787 3.05274 14.7445 3.64974 15.0766L4.00808 15.276C4.06907 15.3099 4.09976 15.3266 4.12917 15.3444C4.40148 15.5083 4.63089 15.735 4.79818 16.0053C4.81625 16.0345 4.8336 16.0648 4.8683 16.1255C4.90256 16.1853 4.92009 16.2152 4.93594 16.2452C5.08261 16.5229 5.16114 16.8315 5.16649 17.1455C5.16707 17.1794 5.16658 17.2137 5.16541 17.2827L5.15853 17.6902C5.14695 18.3763 5.1419 18.7197 5.23792 19.0273C5.32287 19.2994 5.46484 19.55 5.65463 19.7627C5.86915 20.0031 6.16655 20.1745 6.76107 20.5171L7.25478 20.8015C7.84763 21.1432 8.14395 21.3138 8.45869 21.379C8.73714 21.4366 9.02464 21.4344 9.30209 21.3721C9.61567 21.3017 9.90948 21.1258 10.4964 20.7743L10.8502 20.5625C10.9062 20.5289 10.9346 20.5121 10.9626 20.4965C11.2409 20.3418 11.5512 20.2558 11.8695 20.2456C11.9015 20.2446 11.9342 20.2446 11.9994 20.2446C12.0648 20.2446 12.0974 20.2446 12.1295 20.2456C12.4484 20.2559 12.7607 20.3422 13.0394 20.4975C13.0639 20.5112 13.0885 20.526 13.1316 20.5519L13.5078 20.7777C14.0971 21.1315 14.3916 21.3081 14.7065 21.3788C14.985 21.4413 15.2736 21.4438 15.5531 21.3855C15.8685 21.3196 16.1657 21.1471 16.7586 20.803L17.2536 20.5157C17.8418 20.1743 18.1367 20.0031 18.3495 19.7636C18.5383 19.5512 18.6796 19.3011 18.764 19.0297C18.8588 18.7252 18.8531 18.3858 18.8417 17.7119L18.8343 17.2724C18.8332 17.2081 18.8331 17.1761 18.8336 17.1445C18.8383 16.8247 18.9195 16.5104 19.0706 16.2286C19.0856 16.2007 19.1018 16.1726 19.1338 16.1171C19.166 16.0615 19.1827 16.0337 19.1994 16.0068C19.3681 15.7349 19.5995 15.5074 19.8744 15.3435C19.9012 15.3275 19.9289 15.3122 19.9838 15.2818L19.9857 15.2809L20.3472 15.0805C20.9488 14.7472 21.2501 14.5801 21.4689 14.3427C21.6625 14.1327 21.8085 13.8839 21.8978 13.6126C21.9981 13.3077 21.9973 12.9658 21.9958 12.2861L21.9945 11.7119C21.9929 11.0287 21.9921 10.6874 21.891 10.3828C21.8015 10.1133 21.6555 9.86561 21.463 9.65685C21.2457 9.42111 20.9475 9.25526 20.3517 8.92378L20.3499 8.92293Z"));
                        g.DrawPath(p, GetSvgPath("M8.00033 12C8.00033 14.2091 9.79119 16 12.0003 16C14.2095 16 16.0003 14.2091 16.0003 12C16.0003 9.79082 14.2095 7.99996 12.0003 7.99996C9.79119 7.99996 8.00033 9.79082 8.00033 12Z"));
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
                    DrawSvg(g, p, null, r, 15, 2, () => {
                        g.DrawLine(p, 1f, 1f, 14f, 1f);
                    });
                    break;

                case Glyph.Maximize:
                    DrawSvg(g, p, null, r, 14, 14, () => {
                        g.DrawPath(p, GetSvgPath("M6 13H1V8M8 1H13V6"));
                    });
                    break;

                case Glyph.CloseFullscreen:
                    // Тот же холст 17x17, что и в файле, но с поправкой масштаба: сам
                    // Maximize нарисован на 14x14, и оба экспортированы по краям своей
                    // фигуры, а не в общий холст. Без contentScale эта иконка — второе
                    // состояние той же кнопки — выходила заметно тоньше первой.
                    DrawSvg(g, p, null, r, 17, 17, () => {
                        g.DrawPath(p, GetSvgPath("M6 16V11H1M11 1V6H16"));
                    }, false, 17f / 14f);
                    break;

                case Glyph.Close:
                    DrawSvg(g, p, null, r, 15, 15, () => {
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

                // Ярлык-бирка остриём вправо, с дыркой под шнурок — рисунок из src/icons/tag.svg.
                case Glyph.Tag:
                    DrawSvg(g, p, b, r, 14, 10, () => {
                        g.DrawPath(p, GetSvgPath("M1 3C1 1.89543 1.89543 1 3 1H9C9.62951 1 10.2223 1.29639 10.6 1.8L12.1 3.8C12.6333 4.51111 12.6333 5.48889 12.1 6.2L10.6 8.2C10.2223 8.70361 9.62951 9 9 9H3C1.89543 9 1 8.10457 1 7V3Z"));
                        g.FillEllipse(b, 7f, 4f, 2f, 2f);
                    });
                    break;

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

                // Бургер: три линии симметричны по высоте, поэтому одно и то же рисуем
                // и для открытого, и для закрытого попапа — переворачивать нечего.
                case Glyph.HiddenBtnsOpen:
                case Glyph.HiddenBtnsClose:
                    DrawSvg(g, p, null, r, 15, 12, () => {
                        g.DrawLine(p, 1f, 1f, 14f, 1f);
                        g.DrawLine(p, 1f, 6f, 14f, 6f);
                        g.DrawLine(p, 1f, 11f, 14f, 11f);
                    });
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
                    // Оптическая компенсация: треугольник направлен вправо, поэтому его
                    // визуальный центр масс смещён влево. Сдвигаем на 2.2f правее для идеального оптического баланса в круге.
                    DrawSvg(g, p, b, r, 17, 19, () => {
                        g.FillPath(b, GetSvgPath("M15.5347 8.39118C16.192 8.77783 16.192 9.7284 15.5347 10.115L1.50702 18.3666C0.840386 18.7588 -3.56578e-07 18.2781 -3.22771e-07 17.5047L3.98605e-07 1.00153C4.32412e-07 0.228114 0.840387 -0.252541 1.50702 0.139596L15.5347 8.39118Z"));
                    }, false, 0.72f, 2.2f);
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

                case Glyph.Volume0:    Volume(g, p, b, r, 0); break;
                case Glyph.VolumeLow:  Volume(g, p, b, r, 1); break;
                case Glyph.VolumeHigh: Volume(g, p, b, r, 2); break;

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

                case Glyph.ViewList:
                    DrawSvg(g, p, null, r, 20, 20, () => {
                        DrawRoundRect(g, p, 1f, 1f, 18f, 3.33333f, 1.66667f);
                        DrawRoundRect(g, p, 1f, 8.33334f, 18f, 3.33333f, 1.66667f);
                        DrawRoundRect(g, p, 1f, 15.6667f, 18f, 3.33333f, 1.66667f);
                    });
                    break;

                // src/icons/New Icons/Dice.svg: гранёный квадрат и пять точек — «кинуть кость»
                // для случайного выбора сета.
                case Glyph.Dice1:
                case Glyph.Dice2:
                case Glyph.Dice3:
                case Glyph.Dice4:
                case Glyph.Dice5:
                case Glyph.Dice6:
                    DrawSvg(g, p, b, r, 17, 17, () => {
                        DrawRoundRect(g, p, 1f, 1f, 15f, 15f, 2f);
                        foreach (PointF d in DiceDots(glyph - Glyph.Dice1 + 1))
                            FillDot(g, b, d.X, d.Y, 1.25f);
                    });
                    break;

                case Glyph.Keyboard:
                    DrawSvg(g, p, null, r, 22, 14, () => {
                        g.DrawPath(p, GetSvgPath("M17 10H18M8 10H14M5 10H4M4 7H18M4 4H18M1 9.8002V4.2002C1 3.08009 1 2.51962 1.21799 2.0918C1.40973 1.71547 1.71547 1.40974 2.0918 1.21799C2.51962 1 3.08009 1 4.2002 1H17.8002C18.9203 1 19.4796 1 19.9074 1.21799C20.2837 1.40974 20.5905 1.71547 20.7822 2.0918C21 2.5192 21 3.079 21 4.19691V9.80309C21 10.921 21 11.48 20.7822 11.9074C20.5905 12.2837 20.2837 12.5905 19.9074 12.7822C19.48 13 18.921 13 17.8031 13H4.19691C3.07899 13 2.5192 13 2.0918 12.7822C1.71547 12.5905 1.40973 12.2837 1.21799 11.9074C1 11.4796 1 10.9203 1 9.8002Z"));
                    });
                    break;

                case Glyph.Calendar:
                    DrawSvg(g, p, null, r, 18, 18, () => {
                        DrawRoundRect(g, p, 1f, 3f, 16f, 14f, 2.5f);
                        g.DrawLine(p, 1f, 7.5f, 17f, 7.5f);
                        g.DrawLine(p, 5.5f, 1f, 5.5f, 4f);
                        g.DrawLine(p, 12.5f, 1f, 12.5f, 4f);
                    });
                    break;

                case Glyph.Nebula:
                {
                    Image img = GetNebulaImage();
                    if (img != null)
                    {
                        InterpolationMode oldInterp = g.InterpolationMode;
                        PixelOffsetMode oldOffset = g.PixelOffsetMode;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        float size = Math.Min(w, h);
                        RectangleF dest = new RectangleF(cx - size / 2f, cy - size / 2f, size, size);
                        g.DrawImage(img, dest, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                        g.InterpolationMode = oldInterp;
                        g.PixelOffsetMode = oldOffset;
                    }
                    break;
                }
            }
        }

        static Image _nebulaImage;
        static Image GetNebulaImage()
        {
            if (_nebulaImage == null)
            {
                string[] candidates = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"src\icons\nebula icon.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\src\icons\nebula icon.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"src\nebula icon (1).png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\src\nebula icon (1).png")
                };
                foreach (string candidate in candidates)
                {
                    try
                    {
                        if (File.Exists(candidate))
                        {
                            _nebulaImage = Image.FromFile(candidate);
                            break;
                        }
                    }
                    catch { }
                }

                if (_nebulaImage == null)
                {
                    byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABMAAAATCAYAAAByUDbMAAAACXBIWXMAAAsTAAALEwEAmpwYAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAOdEVYdFNvZnR3YXJlAEZpZ21hnrGWYwAAA4VJREFUeAF1k09MHUUcx78zO7v79r1neECplQqstkn/xKR4qF6MhXhRE9Oe8eDDJiZ6sZfaIzYxIaaHogev4EWP9tKLMWltYxNBU5o0wda0PAIt8KCwsLwH+3Znpr/ZfVD6b5Lv253Z+X3m+52Zx/CSVh7SpRMHlk59dOTGwQ7v+n5uhxXg0S3U5VX29qXgRTXi2YEPh7Vf28LoX8vou7tUhBV6+OToDErFPwFLUkUX9NSpMaB0nh0Zq+yu5bs7A8O6PFfFzetT6Pu/Ckwse/jp9ju4Nvs+oqQEqBBozAH1/8qIq1f07a96Xwj78oI+M/0Ao/PzKNmMBkixBv5ZLuHH8ZMYn32LBl1SjqDkMHroQy4Q8GzvU7BzF7W/uIiL4SrQSpA2GiuQLFJCurbQjct3T2Nu5T3q2Zk0fY0WyW7wm755prQDSzYwtL4AdEKBlkMX0+gk6D5SB6lIs67c/wATD04iiv0mUGTAjVkfTu5rw2FD5zZ9bKnpZDWCxyQYgWrMQl1zhMrBuhJYkwJ1yv3x4TsYOD6CnvZ/CURxNcVWLrR4NeDvjraKvQ+DPkXUotWAQ6dlCUULMkQEDKkgVDYC6WBNC7RYHIKbiA5BmpIEjaJS4/eBXpHTqgdcQjAFm5zZ3AAlXMtK3x1a3dUSh9sf4fihy9hTuJcBVC596iQTV+KYANM+JcsaM5uu4NC2uyIGJ5dSM/Tsu4NDb15FZ+sEHBbRJnsZaBvWMGBBu6h1JQWpprSmU1FwVQz3lU34r/2B7v3jaMnPg9O3bVCq2MBozxoumHQC42wGNEeTLdWUeTe/7YV59HT+jWJ+eidWJgLEbgqCeTYcRFAznFvyEuULTFJFkbSRSg1CrucQrXZDRnvIEUHSYicFIHEISMEapNiqFD+9MMkHx94woBEDSl3RlTBBQf0oLGKlcgy16lGoqKUJMa7sFAR6IqFwSn+7c2m5SH6gaIEBSjTdNeNuhW0IqwcRhx2Zs8RuimCJRYfAKs7nwz/vwIy7JOH9BAuk4nSCPHW5fcIpcOkA4o22zBWBNIFAIMlU/3N/9C9+eX1SQ/eTq0oKS+NmQEWFW+sdqK12Ia6XUiBLxGSMRr83+H0FT27W0+3X8pSfz+OzgrNZzjt1v+jW4dk15MQmHJKXWwu8wsqIffq788/WPgfb3cbP3jiR9+p+zsCsGsGiW3u/GZx82fzH5rqxRbquHXEAAAAASUVORK5CYII=");
                    using (MemoryStream ms = new MemoryStream(bytes))
                        _nebulaImage = Image.FromStream(ms);
                }
            }
            return _nebulaImage;
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

        /// <summary>Точки грани в системе координат кубика 17x17 — прямо из dice_N.svg.</summary>
        static PointF[] DiceDots(int face)
        {
            const float a = 5.5f, m = 8.5f, z = 11.5f;
            switch (face)
            {
                case 1: return new[] { new PointF(m, m) };
                case 2: return new[] { new PointF(m, a), new PointF(m, z) };
                case 3: return new[] { new PointF(a, a), new PointF(m, m), new PointF(z, z) };
                case 4: return new[] { new PointF(a, a), new PointF(a, z), new PointF(z, a), new PointF(z, z) };
                case 5: return new[] { new PointF(a, a), new PointF(a, z), new PointF(m, m),
                                       new PointF(z, a), new PointF(z, z) };
                default: return new[] { new PointF(a, a), new PointF(a, m), new PointF(a, z),
                                        new PointF(z, a), new PointF(z, m), new PointF(z, z) };
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

        // Громкость: залитый рупор плюс волны по уровню. Вьюбокс у всех трёх состояний
        // один (16x13) — иначе рупор прыгал бы вбок, когда волны появляются и исчезают.
        static void Volume(Graphics g, Pen p, SolidBrush b, RectangleF r, int waves)
        {
            DrawSvg(g, p, b, r, 16, 13, () => {
                GraphicsPath cone = GetSvgPath("M0.75 3.98493H3.75L7.75 0.750008V11.75L3.75 8.51509H0.75V3.98493Z");
                g.FillPath(b, cone);
                g.DrawPath(p, cone);
                if (waves > 0) g.DrawPath(p, GetSvgPath("M10.75 3.75001C10.75 3.75001 11.75 4.89077 11.75 6.25001C11.75 7.60925 10.75 8.75001 10.75 8.75001"));
                if (waves > 1) g.DrawPath(p, GetSvgPath("M13.25 1.75001C13.25 1.75001 14.75 3.80338 14.75 6.25001C14.75 8.69664 13.25 10.75 13.25 10.75"));
            });
        }

        static void DrawSvg(Graphics g, Pen p, SolidBrush b, RectangleF r, float vw, float vh, Action drawAction, bool flipX = false, float contentScale = 1.0f, float offsetX = 0f, float offsetY = 0f)
        {
            Matrix old = g.Transform;
            try
            {
                float baseScale = Math.Min(r.Width / vw, r.Height / vh);
                float scale = baseScale * contentScale;

                float dx = r.X + (r.Width - vw * scale) / 2f + offsetX * scale;
                float dy = r.Y + (r.Height - vh * scale) / 2f + offsetY * scale;

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
