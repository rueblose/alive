using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager
{
    public static class Chrome
    {
        /// <summary>
        /// Фон контрола. Окно плоское, поэтому достаточно залить цветом той поверхности,
        /// на которой контрол лежит: у панели инструментов это фон окна, у кнопки внутри
        /// карточки — цвет карточки. Без этого скруглённые углы обводятся чужим цветом.
        /// </summary>
        public static void PaintBase(Control c, Graphics g, Color surface)
        {
            PaintBase(c, g, c.ClientRectangle, surface);
        }

        /// <summary>
        /// То же, но в два слоя — для контрола, который лежит не на голом окне, а на
        /// карточке. На стекле карточка это фон окна плюс полупрозрачная накладка;
        /// повторяем обе, иначе контрол вырежет в карточке дырку своей плотности.
        /// </summary>
        public static void PaintBase(Control c, Graphics g, Rectangle clip, Color surface, Color overlay)
        {
            PaintBase(c, g, clip, surface);
            if (overlay.A == 0) return;
            clip.Intersect(c.ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            g.FillRectangle(Theme.GetBrush(overlay), clip);
        }

        public static void PaintBase(Control c, Graphics g, Rectangle clip, Color surface)
        {
            clip.Intersect(c.ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;

            // Полупрозрачный фон имеет смысл только там, где DWM реально читает альфу
            // окна. У диалога с UseGlass=false её никто не читает, и записанная
            // SourceCopy прозрачность превращалась в чёрный прямоугольник вокруг пилюли
            // (раньше его прятал region-клип контрола, теперь клипа нет).
            if (surface.A < 255 && !Theme.IsBlurred(c)) surface = Color.FromArgb(255, surface);

            // Полупрозрачная поверхность — это стекло, и её надо записать в буфер как
            // есть, а не подмешать поверх. Причин две: буфер WinForms переиспользуется
            // между кадрами, так что альфа копилась бы от кадра к кадру; и DWM показывает
            // размытие ровно по той альфе, что в итоге лежит в пикселе.
            CompositingMode old = g.CompositingMode;
            if (surface.A < 255) g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(Theme.GetBrush(surface), clip);
            g.CompositingMode = old;
        }

        public static readonly TextFormatFlags Left =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags Right =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags Center =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping;

        public static readonly TextFormatFlags CellLeft =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        public static readonly TextFormatFlags CellRight =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        public static readonly TextFormatFlags CellCenter =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix;

        public static readonly TextFormatFlags Wrap =
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // ------------------------------------------------------------------ системный «дзынь»

        const int WM_SETCURSOR = 0x0020;
        const int WM_MOUSEMOVE = 0x0200;
        const short HTERROR = -2;

        /// <summary>
        /// Съесть системный звук при клике мимо модального окна. Зовётся из WndProc
        /// любого окна, которое может оказаться владельцем диалога.
        ///
        /// Клик по окну, выключенному модальным диалогом, не приходит к нему ни одним
        /// мышиным сообщением — систему это интересует раньше. Но одно сообщение всё же
        /// доходит: WM_SETCURSOR, где в младшем слове lParam лежит HTERROR, а в старшем —
        /// код нажатой кнопки (проверено журналом сообщений владельца: 0x0020 с lParam
        /// 0x0201FFFE на нажатие левой). Именно на этой паре DefWindowProc и зовёт
        /// MessageBeep — так это описано и в документации WM_SETCURSOR. Не отдаём
        /// сообщение дальше: звука нет, а всё остальное поведение системы на месте —
        /// диалог по-прежнему остаётся впереди и не закрывается.
        ///
        /// Взамен звука подсвечиваем обводку самого диалога: отказ должно быть видно.
        /// </summary>
        public static bool SwallowBlockedClick(ref Message m)
        {
            if (m.Msg != WM_SETCURSOR) return false;

            int l = m.LParam.ToInt32();
            if ((short)(l & 0xFFFF) != HTERROR) return false;
            if (((l >> 16) & 0xFFFF) == WM_MOUSEMOVE) return false;   // beep'ает только нажатие

            GlassDialog blocking = Form.ActiveForm as GlassDialog;
            if (blocking != null) blocking.Flash();

            m.Result = (IntPtr)1;      // TRUE — «обработано», DefWindowProc не зовём
            return true;
        }

        public static void Chevron(Graphics g, float cx, float cy, float size, Color color)
        {
            Icons.Draw(g, Glyph.ChevronDown,
                       new RectangleF(cx - size, cy - size, size * 2, size * 2), color, 1.5f);
        }

        /// <summary>
        /// GDI (TextRenderer) центрирует однострочный текст по высоте box'а шрифта, а не
        /// по видимым чернилам — у Segoe UI Variable это стабильно сдвигает текст на
        /// 1.5–2 px ниже геометрического центра (замерено VCenterCal на реальном рендере).
        /// В высокой строке таблицы это незаметно, а в невысокой пилюле — заметно и
        /// выглядит криво. Поправка на VerticalCenter — во всех остальных случаях сдвиг
        /// не нужен.
        /// </summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color,
                                    TextFormatFlags flags)
        {
            if ((flags & TextFormatFlags.VerticalCenter) != 0)
                rect.Y -= (int)Math.Round(2 * (g.DpiY / 96f));
            TextRenderer.DrawText(g, text, font, rect, color, flags);
        }
    }

    public interface IAnimatable
    {
        bool OnAnimTick();
    }

    public static class AnimEngine
    {
        static readonly System.Windows.Forms.Timer _timer;
        static readonly List<IAnimatable> _active = new List<IAnimatable>();

        static AnimEngine()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 16;
            _timer.Tick += OnTick;
        }

        public static void Register(IAnimatable anim)
        {
            if (anim == null) return;
            if (!_active.Contains(anim))
            {
                _active.Add(anim);
                if (!_timer.Enabled) _timer.Start();
            }
        }

        static void OnTick(object sender, EventArgs e)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!_active[i].OnAnimTick())
                        _active.RemoveAt(i);
                }
                catch { _active.RemoveAt(i); }
            }
            if (_active.Count == 0)
                _timer.Stop();
        }
    }

    /// <summary>
    /// Пульс «сейчас играет»: три столбика, которые шевелятся, пока идёт звук.
    /// Общий на всё приложение — один таймер на 90 мс вместо таймера у каждого списка,
    /// и перерисовывается не весь список, а только прямоугольник самого значка
    /// (подписчик отдаёт его функцией, потому что играющая строка ездит с прокруткой).
    /// </summary>
    public static class PlayPulse
    {
        static readonly System.Windows.Forms.Timer _timer;
        static readonly List<Control> _subs = new List<Control>();
        static readonly List<Func<Rectangle>> _rects = new List<Func<Rectangle>>();

        static PlayPulse()
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 90;
            _timer.Tick += delegate
            {
                for (int i = _subs.Count - 1; i >= 0; i--)
                {
                    Control c = _subs[i];
                    if (c.IsDisposed || !c.IsHandleCreated) { _subs.RemoveAt(i); _rects.RemoveAt(i); continue; }
                    Rectangle r = _rects[i]();
                    if (r.Width > 0 && r.Height > 0) c.Invalidate(Rectangle.Inflate(r, 2, 2));
                }
                if (_subs.Count == 0) _timer.Stop();
            };
        }

        public static void Attach(Control c, Func<Rectangle> rect)
        {
            if (c == null || rect == null) return;
            int i = _subs.IndexOf(c);
            if (i >= 0) { _rects[i] = rect; }
            else { _subs.Add(c); _rects.Add(rect); }
            if (!_timer.Enabled) _timer.Start();
        }

        public static void Detach(Control c)
        {
            int i = _subs.IndexOf(c);
            if (i < 0) return;
            _subs.RemoveAt(i);
            _rects.RemoveAt(i);
            if (_subs.Count == 0) _timer.Stop();
        }

        /// <summary>Высота столбика номер i, 0.25..1. Фаза общая — от часов, без своего состояния.</summary>
        public static float Level(int i)
        {
            double t = Environment.TickCount / 260.0 + i * 0.7;
            return 0.28f + 0.72f * (float)((Math.Sin(t) * 0.5 + 0.5) * (0.55 + 0.45 * (Math.Sin(t * 1.7 + i) * 0.5 + 0.5)));
        }

        /// <summary>Три столбика по нижнему краю прямоугольника.</summary>
        public static void Paint(Graphics g, RectangleF r, Color color)
        {
            float barW = r.Width / 5f;
            for (int i = 0; i < 3; i++)
            {
                float h = Math.Max(1.5f, r.Height * Level(i));
                RectangleF b = new RectangleF(r.X + i * barW * 2f, r.Bottom - h, barW, h);
                Theme.FillRound(g, b, barW / 2f, color);
            }
        }
    }

    /// <summary>
    /// Оверлейная полоса прокрутки: тонкая в покое, толще под курсором, гаснет через
    /// секунду после последней прокрутки. Держит только свои две доли (видимость и
    /// толщину) — саму геометрию и цвет рисует владелец, потому что у списка и у сетки
    /// плиток полосы стоят по-разному.
    /// </summary>
    public sealed class ScrollFade : IDisposable
    {
        readonly Control _owner;
        readonly Func<Rectangle> _rect;
        readonly System.Windows.Forms.Timer _t = new System.Windows.Forms.Timer();
        int _lastPing;
        float _alpha, _thick;
        bool _hot;

        const int HoldMs = 900;

        public ScrollFade(Control owner, Func<Rectangle> rect)
        {
            _owner = owner;
            _rect = rect;
            _t.Interval = 30;
            _t.Tick += Step;
        }

        /// <summary>Насколько полоса видна, 0..1.</summary>
        public float Alpha { get { return _alpha; } }

        /// <summary>Насколько полоса «толстая», 0..1 — от покоя к наведению.</summary>
        public float Thick { get { return _thick; } }

        /// <summary>Была прокрутка — показать полосу и завести отсчёт до затухания.</summary>
        public void Ping()
        {
            _lastPing = Environment.TickCount;
            if (!_t.Enabled) _t.Start();
            InvalidateBar();
        }

        /// <summary>Курсор у самой полосы: она не гаснет и становится толще.</summary>
        public void SetHot(bool value)
        {
            if (_hot == value) return;
            _hot = value;
            Ping();
        }

        void Step(object sender, EventArgs e)
        {
            if (_owner.IsDisposed) { _t.Stop(); return; }

            bool want = _hot || Environment.TickCount - _lastPing < HoldMs;
            float ta = want ? 1f : 0f;
            float tt = _hot ? 1f : 0f;

            _alpha += (ta - _alpha) * (ta > _alpha ? 0.40f : 0.13f);
            _thick += (tt - _thick) * 0.30f;

            if (Math.Abs(ta - _alpha) < 0.01f && Math.Abs(tt - _thick) < 0.01f)
            {
                _alpha = ta; _thick = tt;
                _t.Stop();
            }
            InvalidateBar();
        }

        void InvalidateBar()
        {
            if (!_owner.IsHandleCreated || _owner.IsDisposed) return;
            Rectangle r = _rect();
            if (r.Width > 0 && r.Height > 0) _owner.Invalidate(Rectangle.Inflate(r, 8, 8));
        }

        public void Dispose() { _t.Stop(); _t.Dispose(); }
    }

    /// <summary>База для контролов: без мигания, с фоном своей поверхности и плавной анимацией hover/press.</summary>
    public class GlassControl : Control, IAnimatable
    {
        protected bool Hot, Pressed;
        public float HoverFactor;
        public float PressFactor;

        /// <summary>Цвет того, на чём лежит контрол — им заливается фон под скруглениями.</summary>
        public Color Surface = Theme.Backdrop;

        /// <summary>Накладка поверх фона, если контрол лежит на карточке. См. Chrome.PaintBase.</summary>
        public Color SurfaceOverlay = Color.Transparent;

        protected void PaintSurface(Graphics g)
        {
            Chrome.PaintBase(this, g, ClientRectangle, Surface, SurfaceOverlay);
        }

        protected virtual float PillRadius { get { return -1f; } }

        public GlassControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.FButton;
            ForeColor = Theme.Text;
        }

        protected int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        /// <summary>
        /// Насколько контрол «продавлен» при нажатии — на столько пикселей всё его
        /// содержимое рисуется внутрь. Тактильность, которую дают Apple-контролы,
        /// это она, а не анимация цвета.
        /// </summary>
        protected float PressInset { get { return PressFactor * Sc(2); } }

        // Регион-клипа здесь больше нет. Он был однобитным, и сглаженный край пилюли
        // срезался по нему ступеньками — заметно на любой заливной кнопке (Open in Live,
        // Apply). Фон под скруглениями пишет PaintBase, так что регион не держал ничего,
        // кроме этих ступенек.

        protected override void OnMouseEnter(EventArgs e) { Hot = true; AnimEngine.Register(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; AnimEngine.Register(this); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; AnimEngine.Register(this); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; AnimEngine.Register(this); base.OnMouseUp(e); }

        public virtual bool OnAnimTick()
        {
            if (IsDisposed) return false;

            float targetHover = Hot ? 1.0f : 0.0f;
            float targetPress = Pressed ? 1.0f : 0.0f;

            float dh = targetHover - HoverFactor;
            float dp = targetPress - PressFactor;

            if (Math.Abs(dh) < 0.01f && Math.Abs(dp) < 0.01f)
            {
                HoverFactor = targetHover;
                PressFactor = targetPress;
                Invalidate();
                return false;
            }

            // Вход быстрый, возврат ленивый — симметричные скорости читаются как
            // «интерфейс думает». Числа — доля остатка за кадр (16 мс):
            // наведение ~110 мс вход / ~260 мс выход, нажатие ~55 / ~210.
            HoverFactor += dh * (dh > 0f ? 0.45f : 0.18f);
            PressFactor += dp * (dp > 0f ? 0.70f : 0.22f);
            Invalidate();
            return true;
        }
    }

    /// <summary>Кнопка-пилюля. Обычная — на цвете поверхности, главная — светлая заливка.</summary>
    public class GlassButton : GlassControl
    {
        public bool Primary;
        public bool Quiet;   // без заливки, как текстовая ссылка
        public bool Checked;

        protected override float PillRadius { get { return Height / 2f; } }

        public GlassButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        public void FitToText(int hPadding)
        {
            Size s = TextRenderer.MeasureText(Text, Font);
            Width = s.Width + Sc(hPadding) * 2;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);

            if (!Enabled)
            {
                using (GraphicsPath path = Theme.Round(r, Height / 2f))
                using (Pen pen = new Pen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 1f))
                    g.DrawPath(pen, path);

                Color disabledText = Color.FromArgb(0xFF, 0x48, 0x48, 0x4C);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), disabledText, Chrome.Center);
                return;
            }

            Color text;

            if (Primary)
            {
                RectangleF pr = RectangleF.Inflate(r, -PressInset, -PressInset);
                float rad = pr.Height / 2f;

                // Вертикальный градиент со светом сверху — то же, что у ручки тумблера:
                // кнопка читается как приподнятая, а значит нажимаемая. Плоская заливка
                // выглядела выключенной рядом с обводочными кнопками.
                Color top = Theme.Interpolate(Theme.LightTop, Color.White, HoverFactor);
                Color bottom = Theme.Interpolate(Theme.Light, Theme.LightTop, HoverFactor);
                if (PressFactor > 0.01f)
                {
                    top = Theme.Interpolate(top, Theme.LightPressed, PressFactor);
                    bottom = Theme.Interpolate(bottom, Theme.LightPressed, PressFactor);
                }

                using (GraphicsPath path = Theme.Round(pr, rad))
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                           new RectangleF(pr.X, pr.Y - 1f, pr.Width, pr.Height + 2f),
                           top, bottom, LinearGradientMode.Vertical))
                    g.FillPath(lgb, path);

                // Блик по верхней дуге — 1px белым, гаснет при нажатии.
                using (Pen hi = new Pen(Color.FromArgb((int)Math.Round(0x66 * (1f - PressFactor)), 255, 255, 255), 1f))
                using (GraphicsPath tp = Theme.RoundTop(RectangleF.Inflate(pr, -0.5f, -0.5f), rad - 0.5f))
                    g.DrawPath(hi, tp);

                text = Theme.OnLight;
                Chrome.DrawText(g, Text, Font,
                    new Rectangle((int)Math.Round(pr.X), (int)Math.Round(pr.Y),
                                  (int)Math.Round(pr.Width), (int)Math.Round(pr.Height)),
                    text, Chrome.Center);
                return;
            }
            else if (Quiet)
            {
                // Обычная кнопка сливается с покоем задолго до HoverFactor==0 (её alpha
                // идёт от 0x14, не от нуля). У Quiet заливка идёт от нуля, поэтому хвост
                // виден вдвое дольше и ховер «залипает». Сдвигаем шкалу на ту же долю.
                float rest = Theme.GlassSurfaceAlpha / (float)Theme.GlassSurfaceHotAlpha;
                float shown = Math.Max(0f, (HoverFactor - rest) / (1f - rest));
                if (shown > 0.001f)
                {
                    int alpha = (int)Math.Round(shown * Theme.GlassSurfaceHotAlpha);
                    Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
                }
                text = Theme.Interpolate(Theme.TextDim, Theme.Text, shown);
            }
            else
            {
                int baseAlpha = Checked || PressFactor > 0.5f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceAlpha;
                int targetAlpha = HoverFactor > 0.001f ? Theme.GlassSurfaceHotAlpha : baseAlpha;
                int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, HoverFactor));
                if (PressFactor > 0.01f)
                    alpha = (int)Math.Round(Theme.Lerp(alpha, Theme.GlassSurfacePressedAlpha, PressFactor));

                RectangleF pr = RectangleF.Inflate(r, -PressInset, -PressInset);
                Theme.PaintGlassSurface(this, g, pr, pr.Height / 2f, alpha);
                text = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
            }

            Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, Chrome.Center);
        }
    }

    /// <summary>
    /// Кнопка фильтров: значок ползунков, подпись и число включённых условий через точку.
    /// Отдельный контрол, потому что значок и счётчик должны стоять на своих местах
    /// независимо от длины подписи.
    /// </summary>
    public class FiltersButton : GlassControl
    {
        public int Count;

        protected override float PillRadius { get { return Height / 2f; } }

        public FiltersButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
            Font = Theme.FButton;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            int baseAlpha = Theme.GlassSurfaceAlpha;
            int targetAlpha = PressFactor > 0.01f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha;
            int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, Math.Max(HoverFactor, PressFactor)));

            Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);

            float box = Sc(17);
            Color iconColor = Theme.Interpolate(Theme.TextDim, Theme.Light, HoverFactor);
            Icons.Draw(g, Glyph.Filters,
                       new RectangleF(Sc(15), (Height - box) / 2f, box, box), iconColor, 1.5f);

            string text = Text;
            if (Count > 0) text += " · " + Count;
            Color textColor = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
            Chrome.DrawText(g, text, Font,
                new Rectangle(Sc(15) + (int)box + Sc(8), 0, Width - Sc(15) - (int)box - Sc(16), Height),
                textColor, Chrome.Left);
        }
    }

    /// <summary>Круглая кнопка со значком — панель инструментов и кнопки окна.</summary>
    public class IconButton : GlassControl
    {
        public Glyph Icon = Glyph.Close;
        public bool Danger;          // закрытие окна краснеет под курсором
        public bool Quiet;           // без подложки: только значок, светлеющий под курсором
        public float IconScale = 0.46f;

        /// <summary>Провернуть значок на пол-оборота при нажатии — для кубика.</summary>
        public bool SpinOnClick;
        float _spin;                 // текущий угол, градусы
        int _spinFrom;

        protected override float PillRadius { get { return Height / 2f; } }

        public IconButton()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Size = new Size(Theme.IconSize, Theme.IconSize);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (SpinOnClick)
            {
                _spinFrom = (int)Math.Round(_spin) + 180;
                _spin = _spinFrom;
                AnimEngine.Register(this);
            }
            base.OnClick(e);
        }

        public override bool OnAnimTick()
        {
            bool more = base.OnAnimTick();
            if (_spin > 0.5f)
            {
                // Доводим угол к нулю по той же экспоненте, что и ховер: кубик
                // проворачивается и встаёт на место, а не крутится вечно.
                _spin += (0f - _spin) * 0.22f;
                Invalidate();
                return true;
            }
            if (_spin != 0f) { _spin = 0f; Invalidate(); }
            return more;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            if (Danger && HoverFactor > 0.001f)
            {
                Color bg = Color.FromArgb((int)Math.Round(HoverFactor * 255), Theme.Red);
                Theme.FillRound(g, r, Height / 2f, bg);
            }
            else if (!Quiet)
            {
                int baseAlpha = Theme.GlassSurfaceAlpha;
                int targetAlpha = PressFactor > 0.01f ? Theme.GlassSurfacePressedAlpha : Theme.GlassSurfaceHotAlpha;
                int alpha = (int)Math.Round(Theme.Lerp(baseAlpha, targetAlpha, Math.Max(HoverFactor, PressFactor)));
                Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
            }

            float box = Width * IconScale;
            // У значка ход вдвое короче: он и так мельче пилюли, и полный отступ
            // съедал бы заметную долю самого рисунка.
            float inset = PressInset * 0.5f;
            RectangleF ir = new RectangleF((Width - box) / 2f + inset, (Height - box) / 2f + inset,
                                           box - inset * 2, box - inset * 2);
            Color ink = Danger ? Theme.Interpolate(Theme.Light, Color.White, HoverFactor)
                      : (Quiet ? Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor) : Theme.Light);

            if (_spin > 0.5f)
            {
                System.Drawing.Drawing2D.Matrix old = g.Transform;
                g.TranslateTransform(Width / 2f, Height / 2f);
                g.RotateTransform(_spin);
                g.TranslateTransform(-Width / 2f, -Height / 2f);
                Icons.Draw(g, Icon, ir, ink, Math.Max(1.4f, Width / 24f));
                g.Transform = old;
                return;
            }
            Icons.Draw(g, Icon, ir, ink, Math.Max(1.4f, Width / 24f));
        }
    }

    /// <summary>Пилюля-переключатель: выбор из двух состояний или тумблер (IsSwitch).</summary>
    public class PillToggle : GlassControl
    {
        bool _checked;

        public bool IsSwitch;
        float _thumbPos;
        float _targetPos;
        System.Windows.Forms.Timer _animTimer;

        public event EventHandler CheckedChanged;

        protected override float PillRadius { get { return Height / 2f; } }

        public PillToggle()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
            Font = Theme.FButton;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _targetPos = _checked ? 1f : 0f;
                if (IsSwitch && IsHandleCreated && Visible)
                {
                    StartSwitchAnim();
                }
                else
                {
                    _thumbPos = _targetPos;
                    Invalidate();
                }
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        void StartSwitchAnim()
        {
            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Interval = 15;
                _animTimer.Tick += delegate
                {
                    float diff = _targetPos - _thumbPos;
                    if (Math.Abs(diff) < 0.04f)
                    {
                        _thumbPos = _targetPos;
                        _animTimer.Stop();
                    }
                    else
                    {
                        _thumbPos += diff * 0.35f;
                    }
                    Invalidate();
                };
            }
            if (!_animTimer.Enabled) _animTimer.Start();
        }

        public void FitToText() { Width = TextRenderer.MeasureText(Text, Font).Width + Sc(32); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            OnClick(e);
            base.OnDoubleClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null)
            {
                _animTimer.Dispose();
                _animTimer = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);

            if (IsSwitch)
            {
                PaintAppleSwitch(g, r);
                return;
            }

            if (!Enabled)
            {
                using (GraphicsPath path = Theme.Round(r, Height / 2f))
                using (Pen pen = new Pen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 1f))
                    g.DrawPath(pen, path);

                Color disabledText = Color.FromArgb(0xFF, 0x48, 0x48, 0x4C);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), disabledText, Chrome.Center);
                return;
            }

            if (_checked)
            {
                Color fill = Theme.Interpolate(Theme.Light, Color.White, HoverFactor);
                Theme.FillRound(g, r, Height / 2f, fill);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                                Theme.OnLight, Chrome.Center);
            }
            else
            {
                int alpha = (int)Math.Round(Theme.Lerp(Theme.GlassSurfaceAlpha, Theme.GlassSurfaceHotAlpha, HoverFactor));
                if (PressFactor > 0.01f)
                    alpha = (int)Math.Round(Theme.Lerp(alpha, Theme.GlassSurfacePressedAlpha, PressFactor));
                Theme.PaintGlassSurface(this, g, r, Height / 2f, alpha);
                Color text = Theme.Interpolate(Theme.TextDim, Theme.Text, HoverFactor);
                Chrome.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, Chrome.Center);
            }
        }

        void PaintAppleSwitch(Graphics g, RectangleF r)
        {
            float trackW = Math.Min(Width, Sc(38));
            float trackH = Math.Min(Height, Sc(22));
            float trackX = r.X + (r.Width - trackW) / 2f;
            float trackY = r.Y + (r.Height - trackH) / 2f;
            RectangleF track = new RectangleF(trackX, trackY, trackW, trackH);
            float radius = trackH / 2f;

            float t = (_animTimer != null && _animTimer.Enabled) ? _thumbPos : (_checked ? 1f : 0f);

            // 1. Ложе трека:
            // OFF: утопленный тёмный стеклянный трек (Theme.Sunken + деликатная полупрозрачность)
            // ON: обычный светлый (Theme.Light)
            Color offSunken = Theme.Sunken;
            Color offOverlay = Color.FromArgb((int)(0x14 + 0x0E * HoverFactor), 0xFF, 0xFF, 0xFF);

            using (GraphicsPath trackPath = Theme.Round(track, radius))
            {
                if (!Enabled)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)))
                        g.FillPath(b, trackPath);
                }
                else
                {
                    if (t < 0.999f)
                    {
                        using (Brush b = new SolidBrush(offSunken)) g.FillPath(b, trackPath);
                        using (Brush b = new SolidBrush(offOverlay)) g.FillPath(b, trackPath);
                    }
                    if (t > 0.001f)
                    {
                        int alpha = (int)(255 * t);
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Theme.Light)))
                            g.FillPath(b, trackPath);

                        if (HoverFactor > 0.01f)
                        {
                            using (Brush b = new SolidBrush(Color.FromArgb((int)(0x20 * HoverFactor * t), 255, 255, 255)))
                                g.FillPath(b, trackPath);
                        }
                    }
                }
            }

            // 2. Стеклянный контур ложа с бликом по верхнему краю (в стилистике PaintGlassBorder)
            if (Enabled)
            {
                int borderAlpha = (int)(0x26 * (1f - t) + 0x30 * t + 0x10 * HoverFactor);
                using (Pen pen = new Pen(Color.FromArgb(borderAlpha, 255, 255, 255), 1f))
                using (GraphicsPath path = Theme.Round(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f), radius - 0.5f))
                {
                    g.DrawPath(pen, path);
                }

                int hiAlpha = (int)(0x1A * (1f - t) + 0x38 * t + 0x10 * HoverFactor);
                using (Pen hiPen = new Pen(Color.FromArgb(hiAlpha, 255, 255, 255), 1f))
                using (GraphicsPath topPath = Theme.RoundTop(new RectangleF(track.X + 1f, track.Y + 1f, track.Width - 2f, track.Height - 2f), radius - 1f))
                {
                    g.DrawPath(hiPen, topPath);
                }
            }
            else
            {
                using (Pen pen = new Pen(Color.FromArgb(0x12, 255, 255, 255), 1f))
                using (GraphicsPath path = Theme.Round(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1f, track.Height - 1f), radius - 0.5f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 3. Ручка (knob): тактильная, с мягким градиентом, серебристая в OFF, белая в ON
            float inset = Sc(2);
            float knobDiam = trackH - inset * 2;
            float offX = trackX + inset;
            float onX = trackX + trackW - inset - knobDiam;
            float knobX = Theme.Lerp(offX, onX, t);
            float knobY = trackY + inset;

            RectangleF knobRect = new RectangleF(knobX, knobY, knobDiam, knobDiam);

            if (Enabled)
            {
                // Мягкая диффузная тень под ручкой
                RectangleF s1 = new RectangleF(knobX - 0.5f, knobY + 0.5f, knobDiam + 1f, knobDiam + 1f);
                using (Brush b1 = new SolidBrush(Color.FromArgb(0x18, 0, 0, 0))) g.FillEllipse(b1, s1);
                RectangleF s2 = new RectangleF(knobX, knobY + 1f, knobDiam, knobDiam);
                using (Brush b2 = new SolidBrush(Color.FromArgb(0x35, 0, 0, 0))) g.FillEllipse(b2, s2);

                Color knobTop = Theme.Interpolate(Color.FromArgb(255, 0xE8, 0xE8, 0xEB), Color.FromArgb(255, 0xFF, 0xFF, 0xFF), t);
                Color knobBottom = Theme.Interpolate(Color.FromArgb(255, 0xC6, 0xC6, 0xCA), Color.FromArgb(255, 0xEE, 0xEE, 0xF2), t);

                if (knobRect.Width > 0 && knobRect.Height > 0)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(knobRect, knobTop, knobBottom, LinearGradientMode.Vertical))
                    {
                        g.FillEllipse(lgb, knobRect);
                    }
                }
                else
                {
                    using (Brush b = new SolidBrush(knobTop)) g.FillEllipse(b, knobRect);
                }

                using (Pen knobBorder = new Pen(Color.FromArgb(0x22, 0, 0, 0), 1f))
                {
                    g.DrawEllipse(knobBorder, knobRect.X, knobRect.Y, knobRect.Width, knobRect.Height);
                }
                using (Pen knobHi = new Pen(Color.FromArgb(0x50, 255, 255, 255), 1f))
                {
                    g.DrawArc(knobHi, knobRect.X + 0.5f, knobRect.Y + 0.5f, knobRect.Width - 1f, knobRect.Height - 1f, 200, 140);
                }
            }
            else
            {
                Color disKnob = Color.FromArgb(0x60, 255, 255, 255);
                using (Brush b = new SolidBrush(disKnob))
                    g.FillEllipse(b, knobRect);
            }
        }
    }

    /// <summary>Сегментированный переключатель: обводка и подвижная пилюля внутри.</summary>
    public class Segmented : GlassControl
    {
        string[] _items = new string[0];
        int _index;
        int _hotIndex = -1;

        public event EventHandler SelectedChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Height = Sc(Theme.ControlH);
            Cursor = Cursors.Hand;
            Font = Theme.FButton;
        }

        public void SetItems(params string[] items)
        {
            _items = items;
            int w = 0;
            foreach (string s in items) w += Math.Max(Sc(60), TextRenderer.MeasureText(s, Font).Width + Sc(36));
            Width = w;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_index == value) return;
                _index = value; Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        RectangleF SegRect(int i)
        {
            float w = (Width - 1f) / Math.Max(1, _items.Length);
            return new RectangleF(0.5f + i * w, 0.5f, w, Height - 1f);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = -1;
            for (int i = 0; i < _items.Length; i++) if (SegRect(i).Contains(e.Location)) idx = i;
            if (idx != _hotIndex) { _hotIndex = idx; AnimEngine.Register(this); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotIndex = -1; AnimEngine.Register(this); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < _items.Length; i++)
                if (SegRect(i).Contains(e.Location)) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            MouseEventArgs me = e as MouseEventArgs;
            if (me != null)
            {
                for (int i = 0; i < _items.Length; i++)
                    if (SegRect(i).Contains(me.Location)) SelectedIndex = i;
            }
            base.OnDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassBorder(g, r, Height / 2f);

            for (int i = 0; i < _items.Length; i++)
            {
                if (i == _index)
                {
                    RectangleF sr = SegRect(i);
                    Theme.DrawRound(g, sr, sr.Height / 2f, Color.FromArgb(115, 255, 255, 255), 1f);
                }
            }

            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF sr = SegRect(i);
                bool sel = i == _index;
                Color textColor = sel ? Theme.Text : (i == _hotIndex ? Theme.Text : Theme.TextDim);
                Rectangle textRect = new Rectangle((int)Math.Round(sr.X), 0, (int)Math.Round(sr.Width), Height);
                Chrome.DrawText(g, _items[i], Font, textRect, textColor, Chrome.Center);
            }
        }
    }

    /// <summary>Переключатель вида иконками без подложки и обводки.</summary>
    public class IconToggle : GlassControl
    {
        Glyph[] _glyphs = new Glyph[0];
        int _index;

        public event EventHandler SelectedChanged;

        public IconToggle()
        {
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Height = Sc(Theme.ControlH);
            Cursor = Cursors.Hand;
        }

        public void SetGlyphs(params Glyph[] glyphs)
        {
            _glyphs = glyphs;
            int itemW = Sc(20);
            int gap = Sc(8);
            Width = _glyphs.Length * itemW + Math.Max(0, _glyphs.Length - 1) * gap;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_index == value) return;
                _index = value; Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        RectangleF IconDrawRect(int i)
        {
            int iconSize = Sc(20);
            int itemW = Sc(20);
            int gap = Sc(8);
            float totalW = _glyphs.Length * itemW + Math.Max(0, _glyphs.Length - 1) * gap;
            float startX = (Width - totalW) / 2f;
            float x = startX + i * (itemW + gap);
            float y = (Height - iconSize) / 2f;
            return new RectangleF(x, y, iconSize, iconSize);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_glyphs.Length > 0)
                SelectedIndex = (_index + 1) % _glyphs.Length;
            base.OnMouseDown(e);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            if (_glyphs.Length > 0)
                SelectedIndex = (_index + 1) % _glyphs.Length;
            base.OnDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            for (int i = 0; i < _glyphs.Length; i++)
            {
                RectangleF r = IconDrawRect(i);
                bool sel = i == _index;
                Color col = sel ? Theme.Text : (Hot ? Color.FromArgb(210, 210, 210) : Theme.TextDim);
                float stroke = 2.0f;
                Icons.Draw(g, _glyphs[i], r, col, stroke);
            }
        }
    }

    /// <summary>
    /// Поле ввода — единственное настоящее нативное окно во всём интерфейсе, и на
    /// стеклянном окне оно ведёт себя не как остальные контролы. Edit рисует свой фон
    /// обычной GDI-кистью, а у кисти цвет — это COLORREF, где под альфу байта просто
    /// нет: в поверхность уходит ноль. Ноль в альфе для DWM значит «здесь дырка», и
    /// сквозь поле начинает светить акриловый слой.
    ///
    /// Первая версия чинила это ПОСЛЕ штатного WM_PAINT: пусть Edit нарисует дыру в
    /// настоящем окне, а мы следом залатаем альфу поверх. В спокойном состоянии
    /// разницы не видно, но при перетаскивании окна DWM успевает считать кадр ровно
    /// в момент между «дыра нарисована» и «альфа залатана» — и мелькает прозрачный
    /// прямоугольник. Лечится только атомарно: Edit печатает себя в наш офскрин-буфер
    /// (WM_PRINTCLIENT, а не WM_PAINT), там же получает альфу 0xFF, и на настоящее
    /// окно попадает уже готовый, беспроблемный кадр одним BitBlt — сырую дыру
    /// снаружи не увидит никто и никогда, а не «обычно не увидит».
    /// </summary>
    /// <summary>
    /// Текстовое поле ввода на базе невидимого нативного TextBox.
    /// Полностью устраняет артефакты фонового прямоугольника UxTheme на акриловом стекле.
    /// </summary>
    public class GlassTextBox : TextBox
    {
        public string Cue = "";

        /// <summary>
        /// Второй источник того же системного «дзынь»: однострочное поле ввода не умеет
        /// вставить перевод строки, и на Enter (а заодно на Escape) сам edit-control
        /// зовёт MessageBeep из своего WM_CHAR. Съедаем именно символ — KeyDown до
        /// подписчиков доходит как раньше, так что Enter по-прежнему что-то делает,
        /// просто молча.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_CHAR = 0x0102;
            if (m.Msg == WM_CHAR)
            {
                int ch = m.WParam.ToInt32();
                if (ch == 13 || ch == 10 || ch == 27) return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Поле ввода: утопленная пилюля, отрисовка через GDI+ с поддержкой выделения и каретки.</summary>
    public class FieldBox : GlassControl
    {
        public readonly GlassTextBox Box = new GlassTextBox();
        public string Glyph;      // необязательный значок слева
        public bool ShowClear;    // крестик справа, пока в поле есть текст — очищает его

        public string Cue
        {
            get { return Box.Cue; }
            set { Box.Cue = value ?? ""; Invalidate(); }
        }

        bool _focused, _clearHot, _caretVisible, _dragSelecting;
        int _dragStartIdx;
        Rectangle _clearRect;
        Timer _timer = new Timer();

        protected override float PillRadius { get { return Height / 2f; } }

        public FieldBox()
        {
            Height = Theme.ControlH;
            Box.Size = new Size(0, 0);
            Box.Location = new Point(-100, -100);
            Controls.Add(Box);

            _timer.Interval = 500;
            _timer.Tick += delegate { _caretVisible = !_caretVisible; Invalidate(); };

            Box.GotFocus += delegate { _focused = true; _caretVisible = true; _timer.Start(); Invalidate(); };
            Box.LostFocus += delegate { _focused = false; _caretVisible = false; _timer.Stop(); Invalidate(); };
            Box.TextChanged += delegate { UpdateLayout(); Invalidate(); };
            Box.KeyDown += delegate { Invalidate(); };
            Box.KeyUp += delegate { Invalidate(); };
            Box.Click += delegate { Invalidate(); };
        }

        bool HasClear { get { return ShowClear && Box.Text.Length > 0; } }

        protected override void OnResize(EventArgs e) { UpdateLayout(); base.OnResize(e); }

        void UpdateLayout()
        {
            int cs = Sc(20);
            _clearRect = new Rectangle(Width - Sc(26), (Height - cs) / 2, cs, cs);
        }

        Rectangle GetTextRect()
        {
            int left = string.IsNullOrEmpty(Glyph) ? Sc(16) : Sc(34);
            int right = HasClear ? Sc(30) : Sc(14);
            return new Rectangle(left, 0, Math.Max(10, Width - left - right), Height);
        }

        int GetCharIndexAt(Graphics g, int mouseX, Rectangle textRect, TextFormatFlags flags)
        {
            string txt = Box.Text;
            if (string.IsNullOrEmpty(txt) || mouseX <= textRect.Left) return 0;
            int bestIdx = txt.Length;
            int minDiff = int.MaxValue;

            for (int i = 0; i <= txt.Length; i++)
            {
                string sub = txt.Substring(0, i);
                int cx = (i == 0) ? textRect.Left + Sc(2) : textRect.Left + TextRenderer.MeasureText(g, sub, Font, textRect.Size, flags).Width - Sc(5);
                int diff = Math.Abs(mouseX - cx);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = HasClear && _clearRect.Contains(e.Location);
            if (hot != _clearHot) { _clearHot = hot; Cursor = hot ? Cursors.Hand : Cursors.Default; Invalidate(); }

            if (_dragSelecting && e.Button == MouseButtons.Left)
            {
                using (Graphics g = CreateGraphics())
                {
                    Rectangle tr = GetTextRect();
                    TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
                    int curIdx = GetCharIndexAt(g, e.X, tr, flags);
                    int start = Math.Min(_dragStartIdx, curIdx);
                    int len = Math.Abs(curIdx - _dragStartIdx);
                    Box.Select(start, len);
                    Invalidate();
                }
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_clearHot) { _clearHot = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (HasClear && _clearRect.Contains(e.Location))
            {
                Box.Clear();
                Box.Focus();
                return;
            }

            Box.Focus();
            using (Graphics g = CreateGraphics())
            {
                Rectangle tr = GetTextRect();
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
                _dragStartIdx = GetCharIndexAt(g, e.X, tr, flags);
                Box.SelectionStart = _dragStartIdx;
                Box.SelectionLength = 0;
                _dragSelecting = true;
                _caretVisible = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragSelecting = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            float insetY = Sc(1);
            RectangleF r = new RectangleF(0, insetY, Width, Height - insetY * 2);
            Theme.FillRound(g, r, r.Height / 2f, Theme.Sunken);
            if (_focused) Theme.DrawRound(g, r, r.Height / 2f, Theme.SurfacePressed, 1f);

            if (!string.IsNullOrEmpty(Glyph))
                Chrome.DrawText(g, Glyph, Font, new Rectangle(Sc(12), 0, Sc(20), Height),
                               Theme.TextDim, Chrome.Center);

            if (HasClear)
                Icons.Draw(g, AbletonManager.Glyph.Close, RectangleF.Inflate(_clearRect, -Sc(5), -Sc(5)),
                          _clearHot ? Theme.Text : Theme.TextDim, 1.4f);

            Rectangle textRect = GetTextRect();
            string txt = Box.Text;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

            if (string.IsNullOrEmpty(txt))
            {
                if (!string.IsNullOrEmpty(Cue))
                    Chrome.DrawText(g, Cue, Font, textRect, Theme.TextDim, flags);
            }
            else
            {
                int selStart = Box.SelectionStart;
                int selLen = Box.SelectionLength;

                if (_focused && selLen > 0 && selStart < txt.Length)
                {
                    int selEnd = Math.Min(selStart + selLen, txt.Length);
                    string preText = txt.Substring(0, selStart);
                    string selText = txt.Substring(selStart, selEnd - selStart);

                    int x1 = selStart == 0 ? textRect.Left + Sc(2) : textRect.Left + TextRenderer.MeasureText(g, preText, Font, textRect.Size, flags).Width - Sc(5);
                    int selWidth = TextRenderer.MeasureText(g, selText, Font, textRect.Size, flags).Width - Sc(4);

                    int cy = (Height - Sc(20)) / 2;
                    using (SolidBrush selBrush = new SolidBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x5E)))
                        g.FillRectangle(selBrush, x1, cy, Math.Max(4, selWidth), Sc(20));
                }

                Chrome.DrawText(g, txt, Font, textRect, Theme.Text, flags);
            }

            if (_focused && _caretVisible && Box.SelectionLength == 0)
            {
                int curPos = Math.Min(Box.SelectionStart, txt.Length);
                int cx;
                if (curPos == 0)
                {
                    cx = textRect.Left + Sc(2);
                }
                else
                {
                    string sub = txt.Substring(0, curPos);
                    Size sz = TextRenderer.MeasureText(g, sub, Font, textRect.Size, flags);
                    cx = textRect.Left + sz.Width - Sc(5);
                }

                int cy = (Height - Sc(18)) / 2;
                using (Pen p = new Pen(Theme.Text, 1.5f))
                    g.DrawLine(p, cx, cy, cx, cy + Sc(18));
            }
        }
    }

    /// <summary>Выпадающий список: пилюля и тёмное меню.</summary>
    public class DropField : GlassControl
    {
        readonly List<string> _items = new List<string>();
        int _index = -1;

        public event EventHandler SelectedChanged;
        public string Label;

        protected override float PillRadius { get { return Height / 2f; } }

        public DropField() { Height = Theme.ControlH; Cursor = Cursors.Hand; Font = Theme.FButton; }

        public void SetItems(IEnumerable<string> items, int index)
        {
            _items.Clear();
            _items.AddRange(items);
            _index = index;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_index == value) return;
                _index = value; Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return _index >= 0 && _index < _items.Count ? _items[_index] : ""; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_items.Count == 0) return;

            ContextMenuStrip menu = DarkMenu.Create();
            for (int i = 0; i < _items.Count; i++)
            {
                int captured = i;
                ToolStripMenuItem mi = new ToolStripMenuItem(_items[i]);
                mi.Checked = i == _index;
                mi.Click += delegate { SelectedIndex = captured; };
                menu.Items.Add(mi);
            }
            menu.Closed += delegate { Pressed = false; Invalidate(); };
            menu.Show(this, new Point(0, Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintSurface(g);
            Theme.Smooth(g);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            Theme.PaintGlassSurface(this, g, r, Height / 2f, Hot ? Theme.GlassSurfaceHotAlpha : Theme.GlassSurfaceAlpha);

            int x = Sc(16);
            if (!string.IsNullOrEmpty(Label))
            {
                Size ls = TextRenderer.MeasureText(Label, Theme.FLabel);
                Chrome.DrawText(g, Label, Theme.FLabel, new Rectangle(x, 0, ls.Width, Height),
                                Theme.TextDim, Chrome.Left);
                x += ls.Width + Sc(8);
            }

            Rectangle tr = new Rectangle(x, 0, Math.Max(10, Width - x - Sc(32)), Height);
            Chrome.DrawText(g, SelectedItem, Font, tr, Theme.Text, Chrome.Left);
            Chrome.Chevron(g, Width - Sc(17), Height / 2f, Sc(6), Theme.TextDim);
        }
    }

    public static class DarkMenu
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.BackColor = Theme.SolidSurface;
            m.ForeColor = Theme.Text;
            m.Font = Theme.FButton;
            m.ShowImageMargin = false;
            m.DropShadowEnabled = true;
            m.Opening += delegate { RoundCorners(m); };
            return m;
        }

        /// <summary>
        /// Скругляем меню тем же радиусом, что у компактных карточек в остальном
        /// интерфейсе — иначе на фоне скруглённого всюду оно торчит острыми углами.
        /// Ставим на Opening: к этому моменту меню уже разложено по своим пунктам
        /// и Width/Height окончательные.
        /// </summary>
        static void RoundCorners(ContextMenuStrip m)
        {
            float scale = m.DeviceDpi / 96f;
            float r = 10f * scale;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, m.Width, m.Height), r))
                m.Region = new Region(p);
        }

        class DarkColors : ProfessionalColorTable
        {
            // Меню — отдельное окно без стекла, поэтому цвета здесь только непрозрачные.
            public override Color MenuItemSelected { get { return Theme.SolidPressed; } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
            public override Color MenuItemBorder { get { return MenuItemSelected; } }
            public override Color ToolStripDropDownBackground { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientBegin { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.SolidSurface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.SolidSurface; } }
            public override Color MenuBorder { get { return Theme.SolidPressed; } }
        }

        class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Theme.Text;
                base.OnRenderItemText(e);
            }

            /// <summary>
            /// Своя галочка вместо системной — штатная рисует приподнятый квадрат под
            /// цвета Windows и на тёмном фоне почти не видна.
            /// </summary>
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                RectangleF box = RectangleF.Inflate(r, -r.Width * 0.16f, -r.Height * 0.16f);
                Icons.Draw(e.Graphics, Glyph.Check, box, Theme.Text, 1.5f);
            }
        }
    }

    /// <summary>Полоса перемотки трека для мини-транспорта в футере в стиле Apple Music.</summary>
    public sealed class SeekSlider : GlassControl
    {
        float _progress;
        bool _drag;

        public event Action<float> Seeked;

        public SeekSlider()
        {
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        public float Progress
        {
            get { return _progress; }
            set
            {
                float v = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(v - _progress) < 0.001f) return;
                _progress = v;
                Invalidate();
            }
        }

        RectangleF Track
        {
            get
            {
                int trackH = Sc(5);
                float padX = Sc(4);
                // Ряд желобка целочисленный: на половине пикселя сглаживание размазывает
                // крайние строки, и сыгранная часть выглядит подрезанной снизу.
                return new RectangleF(padX, (Height - trackH) / 2, Math.Max(Sc(20), Width - padX * 2), trackH);
            }
        }

        void Grab(int x)
        {
            RectangleF t = Track;
            float p = (x - t.X) / Math.Max(1f, t.Width);
            Progress = p;
            if (Seeked != null) Seeked(Progress);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _drag = true;
            Grab(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) Grab(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, Surface);
            Theme.Smooth(g);

            RectangleF t = Track;
            float r = t.Height / 2f;

            // Фоновый желобок с закруглёнными концами
            Color trackBg = Hot || _drag
                ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

            Theme.FillRound(g, t, r, trackBg);

            // Сыгранная часть — тот же скруглённый желобок, только короче. Клипа по
            // GraphicsPath тут не было нужно: форма и так лежит внутри желобка, а
            // Region однобитный и срезал сглаженный край ровной ступенькой.
            float doneW = t.Width * _progress;
            if (doneW > 0f)
                Theme.FillRound(g, new RectangleF(t.X, t.Y, doneW, t.Height), r,
                                Hot || _drag ? Color.White : Theme.Text);

            // Ручка появляется только под курсором: в покое полоса читается как
            // ровная линия прогресса, а тянуть её всё равно можно — курсор подскажет.
            if (Hot || _drag)
            {
                float knob = Sc(11);
                float kx = t.X + doneW;
                float ky = t.Y + t.Height / 2f;
                Theme.FillRound(g, new RectangleF(kx - knob / 2f, ky - knob / 2f, knob, knob),
                                knob / 2f, Color.White);
            }
        }
    }

    /// <summary>
    /// Кликабельное название сета в мини-транспорте.
    /// При наведении плавно подсвечивается белым, меняет курсор на руку, по клику переходит к сету.
    /// </summary>
    public sealed class PlayerSetLink : GlassControl
    {
        string _text = "";

        public PlayerSetLink()
        {
            Cursor = Cursors.Hand;
            Height = Theme.ControlH;
        }

        public string SetName
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value ?? "";
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // Фон обязателен: без него контрол оставляет на стекле непрозрачный
            // прямоугольник — свой BackColor, унаследованный от окна.
            PaintSurface(g);
            if (string.IsNullOrEmpty(_text)) return;
            Theme.Smooth(g);

            Color col = Theme.Interpolate(Theme.TextDim, Color.White, HoverFactor);
            Font font = Theme.FTitle;

            Chrome.DrawText(g, _text, font, ClientRectangle, col,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Вертикальный регулятор громкости в виде всплывающей капсулы над кнопкой громкости.
    /// </summary>
    public sealed class VolumePopupControl : GlassControl
    {
        float _value = 0.5f;
        bool _dragging;

        public event EventHandler ValueChanged;

        public VolumePopupControl()
        {
            Cursor = Cursors.Hand;
            Visible = false;
        }

        public float Value
        {
            get { return _value; }
            set
            {
                float v = Math.Max(0f, Math.Min(1f, value));
                if (Math.Abs(v - _value) < 0.001f) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        void UpdateFromY(int y)
        {
            float padY = Sc(14);
            float trackH = Height - padY * 2;
            if (trackH <= 0f) return;
            float v = 1f - (y - padY) / trackH;
            Value = Math.Max(0f, Math.Min(1f, v));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                UpdateFromY(e.Y);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
                UpdateFromY(e.Y);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Delta != 0)
            {
                int steps = e.Delta / 120;
                if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
                float newVal = (float)Math.Round((_value + steps * 0.05f) / 0.05f) * 0.05f;
                Value = Math.Max(0f, Math.Min(1f, newVal));
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float cornerR = Width / 2f;

            using (GraphicsPath path = Theme.Round(rect, cornerR))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF4, 0x1A, 0x1A, 0x1D)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
                    g.DrawPath(p, path);
            }

            float padY = Sc(14);
            float trackH = Height - padY * 2;
            float tx = Width / 2f;
            float trackW = Sc(5);
            float r = trackW / 2f;
            float knobY = padY + (1f - _value) * trackH;

            // Фоновый серый трек
            RectangleF fullTrack = new RectangleF(tx - r, padY, trackW, trackH);
            Theme.FillRound(g, fullTrack, r, Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

            // Заполненная белая часть снизу до ручки
            if (knobY < padY + trackH)
            {
                RectangleF playedRect = new RectangleF(tx - r, knobY, trackW, (padY + trackH) - knobY);
                Theme.FillRound(g, playedRect, r, Color.White);
            }

            // Белая круглая ручка
            float knobR = Sc(6);
            Theme.FillRound(g, new RectangleF(tx - knobR, knobY - knobR, knobR * 2, knobR * 2), knobR, Color.White);
        }
    }
}
