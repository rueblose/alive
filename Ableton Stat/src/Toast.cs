using System;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Короткое сообщение в углу содержимого: показали, подержали пару секунд, погасили.
    /// Ничего не спрашивает и ответа не ждёт, поэтому это контрол на форме, а не диалог:
    /// модальное окно ради «идёт запуск» перекрыло бы работу ровно там, где прерывать её
    /// незачем, и требовало бы закрыть себя руками.
    /// </summary>
    public sealed class Toast : GlassControl
    {
        string _text = "";
        float _alpha;
        bool _fadingOut;
        readonly Timer _hideTimer = new Timer();

        int InnerPad { get { return Sc(16); } }
        int DotSize { get { return Sc(6); } }
        int DotGap { get { return Sc(10); } }
        int TextLeft { get { return InnerPad + DotSize + DotGap; } }

        public Toast()
        {
            Visible = false;
            // Мышь сквозь него проходит к тому, что под ним: сообщение появляется само,
            // поверх плиток, и ловить клики, которых ему не адресовали, ему нечего.
            Enabled = false;
            Font = Theme.FBody;

            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                _fadingOut = true;
                AnimEngine.Register(this);
            };
        }

        /// <summary>Показать текст и погасить через ms миллисекунд.</summary>
        public void Post(string text, int ms)
        {
            _text = text ?? "";
            _fadingOut = false;
            Visible = true;
            BringToFront();

            _hideTimer.Stop();
            _hideTimer.Interval = Math.Max(1, ms);
            _hideTimer.Start();

            AnimEngine.Register(this);
            Invalidate();
        }

        /// <summary>
        /// Встать в левый нижний угол переданной области, с отступом от нижнего края —
        /// впритык к футеру плашка выглядела бы приклеенной к нему, а не отдельным
        /// плавающим сообщением. Размер по ширине — по самому тексту: сообщение короткое
        /// и заранее неизвестное, а коробка фиксированной ширины либо обрезала бы его,
        /// либо зияла пустотой.
        /// </summary>
        public void PlaceIn(Rectangle content)
        {
            Size sz = TextRenderer.MeasureText(_text, Font, new Size(Sc(420), Sc(40)),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            int w = Math.Min(content.Width, TextLeft + sz.Width + InnerPad);
            int h = Sc(44);
            int margin = Sc(18);
            int y = Math.Max(content.Top, content.Bottom - h - margin);
            SetBounds(content.Left, y, w, h);
        }

        public override bool OnAnimTick()
        {
            if (IsDisposed) return false;

            float target = _fadingOut ? 0f : 1f;
            float d = target - _alpha;
            if (Math.Abs(d) <= 0.01f)
            {
                _alpha = target;
                Invalidate();
                if (_alpha <= 0f) { Visible = false; return false; }
                // Полностью показан — дальше плашка неподвижна до срабатывания
                // _hideTimer; тикать впустую незачем.
                return false;
            }

            _alpha += d * 0.25f;
            Invalidate();
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hideTimer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            // Полностью непрозрачная подложка — без стекла и вообще без прозрачности.
            // Пробовал через PaintGlassSurface: у него что сама заливка, что уголки
            // прямоугольника снаружи скругления — настоящая акриловая дыра в окне, а
            // она показывает не соседние плитки программы, а то, что реально ЗА окном
            // на рабочем столе, — и это ПОСТОЯННО, а не только на входе/выходе. Стоит
            // там чему-то шевельнуться (другое окно, курсор), плашка на глазах меняет
            // цвет, хотя сама ничего не анимирует. Появление и исчезновение делаем не
            // альфой, а перетеканием цвета между фоном и цветом плашки — снаружи тот же
            // мягкий переход, а пиксель остаётся честно непрозрачным всегда.
            Color baseBg = Theme.Bg;
            g.FillRectangle(Theme.GetBrush(baseBg), new Rectangle(0, 0, Width, Height));
            if (_alpha <= 0.01f) return;

            RectangleF card = new RectangleF(0, 0, Width, Height);
            float r = Sc(Theme.CardR);
            Theme.FillRound(g, card, r, Theme.Interpolate(baseBg, Theme.SurfacePressed, _alpha));
            Theme.DrawRound(g, RectangleF.Inflate(card, -0.5f, -0.5f), r - 0.5f,
                            Theme.Interpolate(baseBg, Color.FromArgb(28, 255, 255, 255), _alpha), 1f);

            // Точка слева — единственное, что отличает плашку от обычной кнопки этого
            // приложения: у кнопок такой метки нет ни у одной.
            RectangleF dot = new RectangleF(InnerPad, Height / 2f - DotSize / 2f, DotSize, DotSize);
            g.FillEllipse(Theme.GetBrush(Theme.Interpolate(baseBg, Theme.Light, _alpha)), dot);

            Chrome.DrawText(g, _text, Font,
                new Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - InnerPad), Height),
                Theme.Interpolate(baseBg, Theme.Text, _alpha), Chrome.Left);
        }
    }
}
