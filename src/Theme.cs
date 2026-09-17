using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Единая система оформления: цвета, кегли, скругления и отступы. Значения сняты
    /// с макета (1475×898 при 96 dpi) — там же, где макет задаёт число, оно здесь и стоит,
    /// а не подобрано на глаз. Всё остальное окно строится из этих же величин, чтобы
    /// элементы были одного роста и не разъезжались.
    /// </summary>
    public static class Theme
    {
        /// <summary>
        /// Включена ли плавная вертикальная прокрутка (доводка таймером).
        /// При false прокрутка во всех списках и панелях происходит мгновенно.
        /// </summary>
        public static bool SmoothScroll;

        // ------------------------------------------------------------------- цвета
        public static readonly Color Bg      = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1D);

        /// <summary>
        /// Насколько плотен фон окна поверх размытия. Гасить обои этим значением —
        /// плохая идея: размытие пропадает раньше, чем текст перестаёт розоветь (края
        /// букв у GDI наследуют альфу фона и мешаются с тем, что за окном). Темнота
        /// живёт в <see cref="Glass.AccentTint"/> — слое ПОД содержимым окна, а здесь
        /// остаётся ровно столько плотности, чтобы фон не выглядел дырявым.
        /// </summary>
        public static int GlassAlpha = 0x0f;

        /// <summary>
        /// Фон окна: тот же цвет, но со стеклом — полупрозрачный. Всё, что лежит прямо
        /// на окне (а не внутри карточки), должно заливаться именно им, иначе окно
        /// окажется дырявым в одних местах и глухим в других.
        /// </summary>
        public static Color Backdrop
        {
            get { return Glass.Enabled ? Color.FromArgb(GlassAlpha, Bg) : Bg; }
        }

        // Карточки, пилюли — опорные непрозрачные цвета. Используются как есть там, где
        // стекло невозможно (всплывающее меню — отдельное окно без своей альфы), и как
        // запасной вариант, если Glass.Enabled вдруг окажется false.
        public static readonly Color Surface        = Color.FromArgb(0xFF, 0x28, 0x28, 0x2A);
        public static readonly Color SurfacePressed = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3E);

        /// <summary>Прежние имена — теперь просто синонимы: непрозрачно вообще всё.</summary>
        public static readonly Color SolidSurface = Surface;
        public static readonly Color SolidPressed = SurfacePressed;

        /// <summary>
        /// Чем залита карточка панели подробностей — та же формула, что в
        /// DetailPanel.PaintCard (PaintGlassSurface с GlassAlpha): на стекле почти
        /// прозрачный тёмный тон, без стекла — сплошной Surface. Контрол, лежащий на
        /// карточке, должен красить фон под своими скруглениями этим, а не Backdrop:
        /// иначе в непрозрачном режиме углы уходят в фон окна и пилюлю обводит прямоугольник.
        /// </summary>
        public static Color CardFill
        {
            get { return Glass.Enabled ? Color.FromArgb(GlassAlpha, Bg) : Surface; }
        }

        // Степень «начинки» у стеклянных карточек/кнопок (см. PaintGlassSurface) —
        // по нарастающей для покоя/наведения/нажатия-выбора, тем же тёмным тоном, что
        // и сам фон окна, просто плотнее. Числа не пропорция альфы к состоянию флэт-версии
        // (0x28→0x32→0x3A) — та шкала имеет смысл только для непрозрачного, тут же даже
        // «нажатое» состояние должно оставаться стеклом, а не залипать в сплошной цвет.
        public const int GlassSurfaceAlpha        = 0x14;   // ~8%, состояние покоя
        public const int GlassSurfaceHotAlpha     = 0x86;   // наведение: на размытом фоне 0x49 почти не читался
        public const int GlassSurfacePressedAlpha = 0xb0;   // ~23%, нажатие / выбрано

        public static readonly Color Sunken  = Color.FromArgb(0xFF, 0x15, 0x15, 0x19);  // поле поиска
        // Главная кнопка. Плоский 0xCACACB рядом с обводочными кнопками выглядел
        // выключенным, поэтому теперь это вертикальный градиент: LightTop сверху,
        // Light снизу, LightPressed — при нажатии.
        public static readonly Color Light        = Color.FromArgb(0xFF, 0xCA, 0xCA, 0xCB);
        public static readonly Color LightTop     = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF4);
        public static readonly Color LightPressed = Color.FromArgb(0xFF, 0x9E, 0x9E, 0xA2);

        public static readonly Color Text     = Color.FromArgb(0xFF, 0xE9, 0xE9, 0xEB);

        // Вторичный текст поднят с 0x6B: на стекле он лежит на фоне, в который уже
        // подмешано размытие, и тёмно-серый читался на нём грязным пятном.
        public static readonly Color TextDim = Glass.Enabled
            ? Color.FromArgb(0xFF, 0x91, 0x91, 0x96)
            : Color.FromArgb(0xFF, 0x6B, 0x6B, 0x6D);
        public static readonly Color OnLight  = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1D);

        public static readonly Color Green = Color.FromArgb(0xFF, 0x61, 0xE1, 0x70);
        public static readonly Color Red   = Color.FromArgb(0xFF, 0xE1, 0x61, 0x61);

        public static readonly Color RowHover = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

        // Линейки непрозрачные. Полупрозрачная линия на пересечении с другой такой же
        // складывается сама с собой и даёт заметно более светлый пиксель на стыке —
        // ровно это и было видно там, где вертикальный разделитель встречал шапку.
        // Цвет подобран так, чтобы совпадать с прежним 15%-белым поверх Bg.
        public static readonly Color Hairline = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3D);

        // ---------------------------------------------------------------- размеры
        public const int Pad        = 30;   // поле окна слева/справа/сверху
        public const int ControlH   = 35;   // высота всех пилюль и полей в панели инструментов
        public const int IconSize   = 35;   // круглая кнопка-иконка
        public const int IconGap    = 10;
        public const int ContentY   = 98;   // где начинается содержимое под панелью инструментов
        public const int RowH       = 57;   // шаг строки таблицы
        public const int RowPillH   = 55;   // сама подсветка строки
        public const int CellPadX   = 24;   // отступ текста от края строки
        public const int PanelW     = 332;  // панель подробностей
        public const int PanelPad   = 16;
        // Радиусы вложены концентрически: внутренний = внешний − отступ, иначе угол
        // читается как две разные дуги рядом. 30 на окне 1475×950 выглядело как
        // телефон, а не как приложение.
        public const int WindowR    = 18;
        public const int CardR      = 14;
        public const int ThumbR     = 6;

        // ---------------------------------------------------------------- шрифты
        // Кегли подобраны замером: высота знаков и ширина строк совпадают с макетом
        // (строка таблицы — 13 pt, «F Phrygian» 93 px против 95 px в макете).
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

        /// <summary>Полужирный отдельным семейством: у GDI+ только два веса, и Bold слишком тяжёл.</summary>
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

        public static readonly Font FTitle       = UISemibold(13f);    // имя сета, заголовок панели
        public static readonly Font FBody        = UI(13f, FontStyle.Regular);
        public static readonly Font FButton      = UI(13f, FontStyle.Regular);     // кнопка — не подпись
        public static readonly Font FLabel       = UI(12f, FontStyle.Regular);     // шапка таблицы, подписи
        public static readonly Font FSmall       = UI(12f, FontStyle.Regular);
        public static readonly Font FBadge       = UI(11f, FontStyle.Regular);
        public static readonly Font FMini        = UI(9.5f, FontStyle.Regular);
        public static readonly Font FHead        = UISemibold(17f);
        public static readonly Font FDialogTitle = UISemibold(22f);

        /// <summary>
        /// Отступ от верха строки до базовой линии, в пикселях. TextRenderer сажает
        /// строку по верху коробки, а коробка у 13 и 9.5 пунктов разной высоты —
        /// без этой поправки подписи разного кегля стоят на разных линиях.
        /// </summary>
        public static int Baseline(Font f)
        {
            FontFamily fam = f.FontFamily;
            return (int)Math.Round(TextRenderer.MeasureText("Ag", f).Height
                                   * fam.GetCellAscent(f.Style) / (float)fam.GetLineSpacing(f.Style));
        }

        // ------------------------------------------------------------- рисование

        // Кэш перьев для PaintGlassSurface: цвета фиксированные, создаём один раз.
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
        /// Только верхняя дуга скруглённого прямоугольника — без боков и низа. Нужна
        /// для блика по верхнему краю карточки (в Figma это inner shadow со сдвигом
        /// по Y на 1px и нулевым блюром: свет ложится только там, где край смотрит
        /// вверх, а по бокам и снизу его не видно).
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

        /// <summary>Вариант DrawRound с уже готовым Pen — без аллокации на каждый кадр.</summary>
        static void DrawRoundCached(Graphics g, RectangleF r, float radius, Pen pen)
        {
            float w = pen.Width;
            RectangleF rr = new RectangleF(r.X + w / 2f, r.Y + w / 2f,
                                           r.Width - w, r.Height - w);
            using (GraphicsPath p = Round(rr, radius))
                g.DrawPath(pen, p);
        }

        /// <summary>
        /// Стеклянная карточка/кнопка/пилюля — тот же приём, что и у панели подробностей:
        /// почти прозрачная заливка тёмным тоном окна, тонкая обводка 1px белым 4% и
        /// блик по верхнему краю 1px белым 8% (в Figma — inner shadow, offset Y=1, blur 0).
        /// fillAlpha — степень «начинки», см. GlassSurfaceAlpha/Hot/Pressed.
        ///
        /// SourceCopy на заливке не косметика: без него альфа складывается поверх уже
        /// написанного фона control'а (Chrome.PaintBase рисуется раньше), и середина
        /// выходит заметно плотнее собственных краёв — тот же шов, что ловили на пилюлях
        /// полей ввода. Без стекла (Glass.Enabled=false) заливка сплошная — так же, как
        /// и раньше, до всего этого эксперимента.
        /// </summary>
        /// <summary>
        /// Реально ли ЭТО окно сейчас акриловое — не то же самое, что Glass.Enabled
        /// (поддержка самой ОС). У диалогов вроде NotesDialog стекло выключено осознанно
        /// (см. GlassDialog.UseGlass — там живёт настоящий многострочный TextBox, а
        /// акрил ломает нативные дочерние окна), и DWM ничего не размывает позади них.
        /// PaintGlassSurface должен об этом знать: SourceCopy ниже придуман для окна,
        /// чей итоговый альфа-канал реально читает DWM, а на обычном непрозрачном окне
        /// он просто стирает подсветку, оставляя после себя цвет фона — пилюли выходят
        /// невидимыми (см. репорт с NotesDialog).
        /// </summary>
        public static bool IsBlurred(Control owner)
        {
            if (!Glass.Enabled || owner == null) return false;
            GlassDialog gd = owner.FindForm() as GlassDialog;
            return gd == null || gd.UseGlass;
        }

        /// <summary>
        /// Стеклянная обводка — тонкий контур 1px белым 4% и блик по верхнему краю 1px белым 8%.
        /// Точно такая же обводка, как у карточек, диалогов и окна свойств (DetailPanel).
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

            // Обводка и блик держат постоянную альфу, пока заливка не упадёт ниже
            // состояния покоя. У Quiet-кнопок покоя нет: без этого затухания контур
            // висел на полной силе весь хвост анимации и «отлипал» рывком в конце.
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
        /// Облегчённая настройка Graphics при прокрутке: без HighQuality
        /// PixelOffsetMode, который замедляет все GDI+ операции.
        ///
        /// Но и не Default: при нём GDI+ кладёт фигуру на полпикселя ниже и правее, чем
        /// сказано в прямоугольнике (замерено: пилюля 8..31 рисуется сплошной с 9 по 31
        /// и бледной строкой на 32). На прокрутке всё уезжало на полпикселя и вставало
        /// обратно, когда список замирал. Half даёт ту же точность, что HighQuality,
        /// без его цены.
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
